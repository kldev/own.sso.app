using Microsoft.Extensions.Options;

namespace Sso.Api.Marketing;

/// <summary>
/// Sends the mailings' e-mails one address at a time (ADR 0014): reads due recipients (RecipientQueue), claims each
/// (pending -> sending, so a second replica never sends it again), sends it over SMTP with only that address in To,
/// and marks it sent - or failed, which becomes another attempt later or the end. The addresses never reach the log,
/// only the recipients' ids.
/// </summary>
public sealed class MailingSender(
    RecipientQueue queue,
    IMarketingMailSender mail,
    IOptions<SmtpOptions> smtp,
    IOptions<MarketingOptions> options,
    TimeProvider time,
    ILogger<MailingSender> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!smtp.Value.Enabled)
        {
            logger.LogWarning(
                "Marketing e-mails are not sent: Smtp:Host is not set - mailings stay pending"
            );
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var sent = 0;
            try
            {
                sent = await RoundAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // The database away: the rows stay as they are, the next round tries again.
                logger.LogError(e, "A round of the marketing sender failed");
            }

            if (sent == 0)
                await Task.Delay(options.Value.PollInterval, time, stoppingToken)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>One batch of due recipients; the number it looked at.</summary>
    private async Task<int> RoundAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var due = await queue.DueAsync(settings.BatchSize, ct);

        foreach (var (id, email, subject, body) in due)
        {
            if (!await queue.ClaimAsync(id, ct))
                continue;
            try
            {
                await mail.SendAsync(new MarketingEmail(email, subject, body), ct);
                await queue.SentAsync(id, ct);
                logger.LogInformation("Marketing e-mail {RecipientId} sent", id);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                var permanent = e is PermanentMailException;
                logger.LogWarning(
                    e,
                    "Marketing e-mail {RecipientId} failed (permanent: {Permanent})",
                    id,
                    permanent
                );
                await queue.FailedAsync(
                    id,
                    e.Message,
                    settings.MaxAttempts,
                    settings.RetryAfter,
                    permanent,
                    ct
                );
            }

            if (settings.DelayBetweenEmails > TimeSpan.Zero)
                await Task.Delay(settings.DelayBetweenEmails, time, ct);
        }
        return due.Count;
    }
}

public static class MarketingSetup
{
    /// <summary>The SMTP settings, the sender's settings and the background sender (ADR 0014).</summary>
    public static WebApplicationBuilder AddMarketingMail(this WebApplicationBuilder builder)
    {
        builder
            .Services.AddOptions<SmtpOptions>()
            .BindConfiguration(SmtpOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder
            .Services.AddOptions<MarketingOptions>()
            .BindConfiguration(MarketingOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddSingleton<IMarketingMailSender, SmtpMarketingMailSender>();
        builder.Services.AddSingleton<RecipientQueue>();
        builder.Services.AddHostedService<MailingSender>();
        return builder;
    }
}
