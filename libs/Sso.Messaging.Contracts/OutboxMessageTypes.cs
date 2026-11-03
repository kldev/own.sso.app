using System.Collections.Concurrent;
using System.Text.Json;

namespace Sso.Messaging.Contracts;

/// <summary>
/// The messages of messaging.outbox_messages and the name each one is stored under (message_type), so a rename of a
/// class needs no data change. The payload is JSON in the web defaults (camelCase). Great SSO publishes no
/// integration event yet; a module that needs one registers it here (the tests register their own).
/// </summary>
public static class OutboxMessageTypes
{
    private static readonly ConcurrentDictionary<string, Type> ByName = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<Type, string> ByType = new();

    /// <summary>Registers <typeparamref name="T"/> under <paramref name="name"/> (lower case, digits, dashes).</summary>
    public static void Register<T>(string name)
    {
        ByName[name] = typeof(T);
        ByType[typeof(T)] = name;
    }

    /// <summary>The name a message is stored under; throws for a type nobody registered.</summary>
    public static string NameOf(Type type) =>
        ByType.TryGetValue(type, out var name)
            ? name
            : throw new InvalidOperationException(
                $"{type.Name} is not an outbox message - register it in OutboxMessageTypes."
            );

    /// <summary>The message stored under <paramref name="name"/>, or null for a name this version does not know.</summary>
    public static object? Deserialize(string name, string payload) =>
        ByName.TryGetValue(name, out var type)
            ? JsonSerializer.Deserialize(payload, type, JsonSerializerOptions.Web)
            : null;
}
