using Microsoft.EntityFrameworkCore;
using Sso.Data;
using Sso.Data.Marketing;

namespace Sso.Api.Marketing;

/// <summary>A due recipient with the text of its mailing.</summary>
public sealed record Outgoing(Guid Id, string Email, string Subject, string Body);

/// <summary>
/// The sender's queue (ADR 0014): due pending recipients, oldest first; a recipient is claimed (pending -> sending, a
/// conditional update - the second of two replicas finds it no longer pending) before the SMTP server is asked, and
/// marked sent or failed afterwards. A row left in sending by a crash stays there for a person to look at - sending it
/// again could e-mail the address twice. No actor and no audit: these are the sender's own state changes.
/// </summary>
public sealed class RecipientQueue(IDbContextFactory<SsoDbContext> factory, TimeProvider time)
{
    public async Task<List<Outgoing>> DueAsync(int batchSize, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var db = await factory.CreateDbContextAsync(ct);
        return await (
            from r in db.Recipients
            join m in db.Mailings on r.MailingId equals m.Id
            where r.Status == RecipientStatuses.Pending && r.NextAttemptAt <= now
            orderby r.NextAttemptAt, r.CreatedAt, r.Id
            select new Outgoing(r.Id, r.Email, m.Subject, m.Body)
        )
            .Take(batchSize)
            .ToListAsync(ct);
    }

    /// <summary>pending -> sending, once; false when another sender took it first or it is no longer pending.</summary>
    public async Task<bool> ClaimAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db
                .Recipients.Where(r => r.Id == id && r.Status == RecipientStatuses.Pending)
                .ExecuteUpdateAsync(
                    set =>
                        set.SetProperty(r => r.Status, RecipientStatuses.Sending)
                            .SetProperty(r => r.Attempts, r => r.Attempts + 1),
                    ct
                ) == 1;
    }

    /// <summary>The SMTP server accepted the e-mail: sending -> sent, with the time.</summary>
    public async Task SentAsync(Guid id, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db
            .Recipients.Where(r => r.Id == id && r.Status == RecipientStatuses.Sending)
            .ExecuteUpdateAsync(
                set =>
                    set.SetProperty(r => r.Status, RecipientStatuses.Sent)
                        .SetProperty(r => r.SentAt, now)
                        .SetProperty(r => r.LastError, (string?)null),
                ct
            );
    }

    /// <summary>
    /// Sending failed: back to pending after <paramref name="retryAfter"/>, or failed once the attempts reach
    /// <paramref name="maxAttempts"/> (or at once when <paramref name="permanent"/> - the server refused the address).
    /// </summary>
    public async Task FailedAsync(
        Guid id,
        string error,
        int maxAttempts,
        TimeSpan retryAfter,
        bool permanent,
        CancellationToken ct
    )
    {
        var next = time.GetUtcNow() + (retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter);
        var message = error.Length <= 1000 ? error : error[..1000];
        await using var db = await factory.CreateDbContextAsync(ct);
        await db
            .Recipients.Where(r => r.Id == id && r.Status == RecipientStatuses.Sending)
            .ExecuteUpdateAsync(
                set =>
                    set.SetProperty(
                            r => r.Status,
                            r =>
                                permanent || r.Attempts >= maxAttempts
                                    ? RecipientStatuses.Failed
                                    : RecipientStatuses.Pending
                        )
                        .SetProperty(r => r.LastError, message)
                        .SetProperty(r => r.NextAttemptAt, next),
                ct
            );
    }
}
