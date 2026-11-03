using System.Net;
using System.Text.Json;

namespace Sso.Data.Oidc;

/// <summary>
/// The full log of sign-ins, sessions, tokens and MCP calls (oidc.events). Append-only: the API role may insert and
/// read, never update or delete (ADR 0009). Never a token, a code or a secret - only ids, reasons and the address.
/// </summary>
public sealed class OidcEvent
{
    public long Id { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public string EventType { get; init; } = "";

    /// <summary>success or refused.</summary>
    public string Outcome { get; init; } = "";

    public string? Reason { get; init; }

    public Guid? UserId { get; init; }

    /// <summary>The OAuth client id of the application (text, not the row id).</summary>
    public string? ClientId { get; init; }

    public string? Provider { get; init; }

    public Guid? SessionId { get; init; }

    public Guid? ActorId { get; init; }

    public IPAddress? Ip { get; init; }

    public string? UserAgent { get; init; }

    public JsonDocument? Detail { get; init; }
}

public static class Outcomes
{
    public const string Success = "success";
    public const string Refused = "refused";
}
