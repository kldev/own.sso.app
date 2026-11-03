using System.Collections.Concurrent;
using Sso.Api.Marketing;

namespace Sso.Tests;

/// <summary>
/// The marketing sender's SMTP in the tests: keeps what was sent; an address with "fail" fails every attempt, one with
/// "refused" is refused for good (as a 5xx on RCPT TO).
/// </summary>
public sealed class RecordingMailSender : IMarketingMailSender
{
    private ConcurrentQueue<MarketingEmail> Sent { get; } = new();

    public Task SendAsync(MarketingEmail email, CancellationToken ct)
    {
        if (email.To.Contains("refused"))
            throw new PermanentMailException(
                $"The server refused {email.To}: 550",
                new InvalidOperationException("550")
            );
        if (email.To.Contains("fail"))
            throw new InvalidOperationException("The SMTP server is away.");
        Sent.Enqueue(email);
        return Task.CompletedTask;
    }

    public IReadOnlyList<MarketingEmail> To(string address) =>
        Sent.Where(e => e.To == address).ToList();
}
