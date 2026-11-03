using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using Sso.Data;
using Sso.Data.Messaging;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Sso.Runner.Outbox;

/// <summary>
/// Hands messaging.outbox_messages on to Wolverine (ADR 0010). One transaction per batch: the rows are locked
/// (SELECT ... FOR UPDATE SKIP LOCKED - a second runner takes other rows), each message goes into Wolverine's durable
/// outbox in the same transaction and the row is marked processed - so a message leaves the outbox once, and
/// Wolverine sends it to RabbitMQ after the commit, retrying from its own storage while the broker is away.
/// </summary>
public sealed class OutboxRelay(
    IDbContextFactory<SsoDbContext> factory,
    IWolverineRuntime runtime,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxRelay> logger
)
{
    /// <summary>The header naming the outbox row a message came from.</summary>
    private const string OutboxIdHeader = "sso-outbox-id";

    /// <summary>One batch; the number of rows it looked at (0 = nothing pending).</summary>
    public async Task<int> RelayAsync(CancellationToken ct)
    {
        var settings = options.Value;
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var rows = await OutboxBatch.ClaimAsync(db, settings.BatchSize, settings.MaxAttempts, ct);
        if (rows.Count == 0)
            return 0;

        var context = new MessageContext(runtime);
        await context.EnlistInOutboxAsync(
            new DatabaseEnvelopeTransaction(
                (IMessageDatabase)runtime.Storage,
                (NpgsqlTransaction)transaction.GetDbTransaction()
            )
        );
        var published = await OutboxBatch.ProcessAsync(
            rows,
            timeProvider.GetUtcNow(),
            (row, message) =>
                context
                    .PublishAsync(
                        message,
                        new DeliveryOptions { Headers = { [OutboxIdHeader] = row.Id.ToString() } }
                    )
                    .AsTask()
        );
        if (published < rows.Count)
            logger.LogWarning(
                "{Unknown} outbox message(s) have a type this version does not know",
                rows.Count - published
            );

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await context.FlushOutgoingMessagesAsync();
        return rows.Count;
    }

    /// <summary>Processed rows past the retention; the number deleted.</summary>
    public async Task<int> DeleteProcessedAsync(CancellationToken ct)
    {
        var before = timeProvider.GetUtcNow() - options.Value.Retention;
        await using var db = await factory.CreateDbContextAsync(ct);
        return await OutboxBatch.DeleteProcessedAsync(db, before, ct);
    }
}
