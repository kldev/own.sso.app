using System.Text.Json;
using Sso.Messaging.Contracts;

namespace Sso.Data.Messaging;

/// <summary>
/// Writes integration events to the outbox in the caller's unit of work: the message is saved by the same
/// SaveChanges as the change it describes, and rolled back with it (ADR 0010).
/// </summary>
public static class Outbox
{
    public static OutboxMessage Enqueue<T>(SsoDbContext db, T message, DateTimeOffset now)
        where T : notnull
    {
        var row = new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            MessageType = OutboxMessageTypes.NameOf(message.GetType()),
            Payload = JsonSerializer.Serialize(
                message,
                message.GetType(),
                JsonSerializerOptions.Web
            ),
            OccurredAt = now,
        };
        db.OutboxMessages.Add(row);
        return row;
    }
}
