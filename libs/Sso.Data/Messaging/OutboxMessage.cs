namespace Sso.Data.Messaging;

/// <summary>
/// The transactional outbox (messaging.outbox_messages, ADR 0010): a change that wants something done later writes a
/// message in the same transaction (<see cref="Outbox.Enqueue"/>); the runner hands it on to RabbitMQ through
/// Wolverine. A payload carries ids, never secrets.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; init; }

    /// <summary>The name of the message (Sso.Messaging.Contracts.OutboxMessageTypes).</summary>
    public string MessageType { get; init; } = "";

    /// <summary>JSON in the web defaults (camelCase), stored as jsonb.</summary>
    public string Payload { get; init; } = "{}";

    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}
