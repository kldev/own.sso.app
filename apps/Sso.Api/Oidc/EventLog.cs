using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sso.Api.Auth;
using Sso.Data;
using Sso.Data.Oidc;

namespace Sso.Api.Oidc;

/// <summary>
/// The full log of sign-ins, sessions, tokens and MCP calls (oidc.events, ADR 0009). An event is added to the unit of
/// work of the change it describes and saved with it; a refusal before anything is looked up is saved on its own.
/// Never a token, a code or a secret - only ids, reasons, the address and the user agent.
/// </summary>
public sealed class EventLog(IActorAccessor actor, TimeProvider time)
{
    /// <summary>The kinds of refusal the host may record before anything is looked up.</summary>
    private static readonly HashSet<string> RefusalTypes =
    [
        "sign_in",
        "authorize",
        "token",
        "userinfo",
    ];

    public OidcEvent Add(
        SsoDbContext db,
        string eventType,
        string outcome,
        string? reason = null,
        Guid? userId = null,
        string? clientId = null,
        string? provider = null,
        Guid? sessionId = null,
        RequestInfo? request = null,
        object? detail = null
    )
    {
        var row = new OidcEvent
        {
            OccurredAt = time.GetUtcNow(),
            EventType = eventType,
            Outcome = outcome,
            Reason = Cut(reason, 60),
            UserId = userId,
            ClientId = Cut(clientId, 64),
            Provider = Cut(provider, 20),
            SessionId = sessionId,
            ActorId = actor.ActorId,
            Ip = request?.Ip is { } ip && IPAddress.TryParse(ip, out var address) ? address : null,
            UserAgent = Cut(request?.UserAgent, 500),
            Detail = detail is null ? null : JsonSerializer.SerializeToDocument(detail),
        };
        db.Events.Add(row);
        return row;
    }

    /// <summary>A refusal of the host (a provider's answer, a malformed protocol request), saved at once.</summary>
    public async Task RecordRefusalAsync(
        SsoDbContext db,
        string eventType,
        string reason,
        string? clientId,
        string? provider,
        RequestInfo request,
        CancellationToken ct
    )
    {
        if (!RefusalTypes.Contains(eventType))
            throw new ArgumentException($"Not an event the host may record: {eventType}.");
        Add(db, eventType, Outcomes.Refused, reason, null, clientId, provider, null, request);
        await db.SaveChangesAsync(ct);
    }

    private static string? Cut(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];
}

/// <summary>
/// Refusals of the host before anything is looked up - a provider's answer, a malformed protocol request - each
/// saved on its own (oidc.events).
/// </summary>
public sealed class ProtocolLog(IDbContextFactory<SsoDbContext> factory, EventLog events)
{
    public async Task RecordAsync(
        string eventType,
        string reason,
        string? clientId,
        string? provider,
        RequestInfo request,
        CancellationToken ct
    )
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await events.RecordRefusalAsync(db, eventType, reason, clientId, provider, request, ct);
    }
}
