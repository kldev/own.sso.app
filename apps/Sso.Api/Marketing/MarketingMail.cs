using System.ComponentModel.DataAnnotations;
using JetBrains.Annotations;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Sso.Api.Marketing;

/// <summary>
/// The SMTP server the marketing e-mails go through (ADR 0014): MailPit locally (no TLS, no login), a real
/// server in production. Section "Smtp"; the password only as the environment variable Smtp__Password. An empty Host
/// switches sending off - mailings wait as pending until it is set.
/// </summary>
// Set by the configuration binder (BindConfiguration).
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.Members)]
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    /// <summary>Empty = no sending (production until the SMTP secrets are set).</summary>
    public string? Host { get; init; }

    [Range(1, 65535)]
    public int Port { get; init; } = 587;

    /// <summary>None (MailPit), StartTls (587) or SslOnConnect (465) - MailKit's SecureSocketOptions.</summary>
    public SecureSocketOptions Security { get; init; } = SecureSocketOptions.StartTls;

    /// <summary>Empty = no login (MailPit).</summary>
    public string? Username { get; init; }

    /// <summary>A secret: environment variable Smtp__Password only.</summary>
    public string? Password { get; init; }

    [Required, EmailAddress]
    public string FromAddress { get; init; } = "hello@example.com";

    [Required]
    public string FromName { get; init; } = "Great SSO";

    /// <summary>Where replies go; empty = to FromAddress.</summary>
    [EmailAddress]
    public string? ReplyTo { get; init; }

    public bool Enabled => !string.IsNullOrWhiteSpace(Host);
}

/// <summary>How the sender works through the queue. Section "Marketing".</summary>
// Set by the configuration binder (BindConfiguration).
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.Members)]
public sealed class MarketingOptions
{
    public const string SectionName = "Marketing";

    /// <summary>How often the sender looks for pending recipients when the queue is empty.</summary>
    [Range(typeof(TimeSpan), "00:00:00.05", "01:00:00")]
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>A pause between two e-mails: a burst from one address looks like spam to the receiving servers.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:10:00")]
    public TimeSpan DelayBetweenEmails { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Recipients read per round.</summary>
    [Range(1, 500)]
    public int BatchSize { get; init; } = 20;

    /// <summary>Attempts per address before it is failed (a refused address fails at once).</summary>
    [Range(1, 20)]
    public int MaxAttempts { get; init; } = 3;

    /// <summary>The wait after a failed attempt.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "1.00:00:00")]
    public TimeSpan RetryAfter { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>One plain-text e-mail to one address.</summary>
public sealed record MarketingEmail(string To, string Subject, string Body);

/// <summary>Sends one e-mail; the tests replace it with a recorder.</summary>
public interface IMarketingMailSender
{
    Task SendAsync(MarketingEmail email, CancellationToken ct);
}

/// <summary>
/// The server refused the address itself (5xx on RCPT TO): another attempt would get the same answer.
/// </summary>
public sealed class PermanentMailException(string message, Exception inner)
    : Exception(message, inner);

/// <summary>SMTP with MailKit: one connection per e-mail - a few hundred e-mails a mailing at most.</summary>
public sealed class SmtpMarketingMailSender(IOptions<SmtpOptions> options) : IMarketingMailSender
{
    public async Task SendAsync(MarketingEmail email, CancellationToken ct)
    {
        var o = options.Value;
        var host =
            o.Host ?? throw new InvalidOperationException("Smtp:Host is not set - sending is off.");
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        mime.To.Add(MailboxAddress.Parse(email.To));
        if (!string.IsNullOrWhiteSpace(o.ReplyTo))
            mime.ReplyTo.Add(MailboxAddress.Parse(o.ReplyTo));
        mime.Subject = email.Subject;
        mime.Body = new TextPart("plain") { Text = email.Body };

        using var client = new SmtpClient();
        await client.ConnectAsync(host, o.Port, o.Security, ct);
        if (!string.IsNullOrEmpty(o.Username))
            await client.AuthenticateAsync(o.Username, o.Password ?? "", ct);
        try
        {
            await client.SendAsync(mime, ct);
        }
        catch (SmtpCommandException e)
            when (e.ErrorCode == SmtpErrorCode.RecipientNotAccepted && (int)e.StatusCode >= 500)
        {
            throw new PermanentMailException(
                $"The server refused {email.To}: {(int)e.StatusCode} {e.Message}",
                e
            );
        }
        await client.DisconnectAsync(true, ct);
    }
}
