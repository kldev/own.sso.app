using Microsoft.EntityFrameworkCore;
using Sso.Messaging.Contracts;

namespace Sso.Data.Messaging;

/// <summary>
/// One batch of the outbox relay (ADR 0010), without the broker: <see cref="ClaimAsync"/> locks pending rows in order
/// (FOR UPDATE SKIP LOCKED - a second relay takes other rows) inside the caller's transaction, <see cref="ProcessAsync"/>
/// hands each known message to <c>publish</c> and marks the row processed, or counts a failed attempt for a type this
/// version does not know. The caller saves and commits; the runner publishes into Wolverine's outbox in the same
/// transaction.
/// </summary>
public static class OutboxBatch
{
    public static Task<List<OutboxMessage>> ClaimAsync(
        SsoDbContext db,
        int batchSize,
        int maxAttempts,
        CancellationToken ct
    ) =>
        db
            .OutboxMessages.FromSql(
                $"""
                SELECT * FROM messaging.outbox_messages
                WHERE processed_at IS NULL AND attempts < {maxAttempts}
                ORDER BY occurred_at, id
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """
            )
            .ToListAsync(ct);

    /// <summary>The number of rows published; the others got an error and another attempt later.</summary>
    public static async Task<int> ProcessAsync(
        IEnumerable<OutboxMessage> rows,
        DateTimeOffset now,
        Func<OutboxMessage, object, Task> publish
    )
    {
        var published = 0;
        foreach (var row in rows)
        {
            row.Attempts++;
            if (OutboxMessageTypes.Deserialize(row.MessageType, row.Payload) is not { } message)
            {
                row.LastError = $"Unknown message type {row.MessageType}.";
                continue;
            }
            await publish(row, message);
            row.ProcessedAt = now;
            row.LastError = null;
            published++;
        }
        return published;
    }

    /// <summary>Processed rows older than <paramref name="before"/>; the number deleted.</summary>
    public static Task<int> DeleteProcessedAsync(
        SsoDbContext db,
        DateTimeOffset before,
        CancellationToken ct
    ) =>
        db
            .OutboxMessages.Where(m => m.ProcessedAt != null && m.ProcessedAt < before)
            .ExecuteDeleteAsync(ct);
}
