using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Sso.Api.Features.Common;
using Sso.Api.Oidc;
using Sso.Data;
using Sso.Data.Oidc;

namespace Sso.Api.Features.Activity;

/// <summary>An SSO session, active or finished, with the person, the provider, its limits and its applications.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)] // serialized to JSON
public sealed class ActivitySessionsRow
{
    public required Guid Id { get; init; }
    public required Guid UserId { get; init; }
    public required string Username { get; init; }
    public required string DisplayName { get; init; }
    public required string Provider { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }

    /// <summary>The earlier of the idle and the absolute limit.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
    public required DateTimeOffset AbsoluteExpiresAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string? EndReason { get; init; }

    /// <summary>active, ended (by the admin, a deactivation, an unlink) or expired (idle or absolute limit).</summary>
    public required string State { get; init; }

    [JsonIgnore]
    public IPAddress? IpAddress { get; init; }

    public string? Ip => IpAddress?.ToString();
    public string? UserAgent { get; init; }

    /// <summary>The applications this session signed in to (their client ids).</summary>
    public string[] ClientIds { get; set; } = [];
}

/// <summary>GET /api/activity/sessions: every SSO session - for an active SSO admin only.</summary>
public sealed class ListSessions(SsoDbContext db, Actors actors, TimeProvider time) : IHandler
{
    public static readonly ListSpec<ActivitySessionsRow> Spec = new ListSpec<ActivitySessionsRow>()
        .Add("id", r => r.Id, Filter.Eq)
        .Add("userId", r => r.UserId, Filter.Eq)
        .Add("username", r => r.Username, Filter.Eq)
        .Add("displayName", r => r.DisplayName)
        .Add("provider", r => r.Provider, Filter.Eq)
        .Add("createdAt", r => r.CreatedAt, Filter.Range)
        .Add("lastSeenAt", r => r.LastSeenAt, Filter.Range)
        .Add("expiresAt", r => r.ExpiresAt, Filter.Range)
        .Add("absoluteExpiresAt", r => r.AbsoluteExpiresAt, Filter.Range)
        .Add("endedAt", r => r.EndedAt, Filter.Range)
        .Add("endReason", r => r.EndReason, Filter.Eq)
        .Add("state", r => r.State, Filter.Eq)
        .Add("ip", r => r.IpAddress, Filter.Eq)
        .Add("userAgent", r => r.UserAgent);

    public async Task<ListPage<ActivitySessionsRow>> HandleAsync(
        ClaimsPrincipal user,
        IQueryCollection query,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        var visible = actor.IsAdmin;
        var now = time.GetUtcNow();
        var rows =
            from s in db.Sessions.AsNoTracking()
            join u in db.People on s.UserId equals u.Id
            where visible
            let expires = s.IdleExpiresAt < s.AbsoluteExpiresAt
                ? s.IdleExpiresAt
                : s.AbsoluteExpiresAt
            select new ActivitySessionsRow
            {
                Id = s.Id,
                UserId = s.UserId,
                Username = u.Username,
                DisplayName = u.DisplayName,
                Provider = s.Provider,
                CreatedAt = s.CreatedAt,
                LastSeenAt = s.LastSeenAt,
                ExpiresAt = expires,
                AbsoluteExpiresAt = s.AbsoluteExpiresAt,
                EndedAt = s.EndedAt,
                EndReason = s.EndReason,
                State =
                    s.EndedAt != null ? "ended"
                    : now >= expires ? "expired"
                    : "active",
                IpAddress = s.Ip,
                UserAgent = s.UserAgent,
            };
        return await Spec.PageAsync(rows, query, ct, AddClientsAsync);
    }

    private async Task AddClientsAsync(List<ActivitySessionsRow> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var used = await (
            from a in db.AuthorizationCodes
            join c in db.Clients on a.ClientId equals c.Id
            where ids.Contains(a.SessionId)
            select new { a.SessionId, c.ClientId }
        )
            .Distinct()
            .ToListAsync(ct);
        foreach (var row in rows)
            row.ClientIds =
            [
                .. used.Where(c => c.SessionId == row.Id)
                    .Select(c => c.ClientId)
                    .Order(StringComparer.Ordinal),
            ];
    }
}

/// <summary>One entry of the log of sign-ins, links, sessions, tokens and MCP calls.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)] // serialized to JSON
public sealed class ActivityEventsRow
{
    public required long Id { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required string EventType { get; init; }
    public required string Outcome { get; init; }
    public string? Reason { get; init; }
    public Guid? UserId { get; init; }
    public string? Username { get; init; }
    public string? DisplayName { get; init; }
    public string? ClientId { get; init; }
    public string? Provider { get; init; }
    public Guid? SessionId { get; init; }
    public Guid? ActorId { get; init; }

    [JsonIgnore]
    public IPAddress? IpAddress { get; init; }

    public string? Ip => IpAddress?.ToString();
    public string? UserAgent { get; init; }

    [JsonIgnore]
    public JsonDocument? DetailDocument { get; init; }

    /// <summary>What else the event says (a grant type, a token's name, an e-mail's domain) - never a secret.</summary>
    public JsonElement? Detail => DetailDocument?.RootElement;
}

/// <summary>GET /api/activity/events: the full log, filtered by person, application, type, outcome and date.</summary>
public sealed class ListEvents(SsoDbContext db, Actors actors) : IHandler
{
    public static readonly ListSpec<ActivityEventsRow> Spec = new ListSpec<ActivityEventsRow>()
        .Add("id", r => r.Id, Filter.Eq | Filter.Range)
        .Add("occurredAt", r => r.OccurredAt, Filter.Range)
        .Add("eventType", r => r.EventType, Filter.Eq)
        .Add("outcome", r => r.Outcome, Filter.Eq)
        .Add("reason", r => r.Reason, Filter.Eq)
        .Add("userId", r => r.UserId, Filter.Eq)
        .Add("username", r => r.Username, Filter.Eq)
        .Add("displayName", r => r.DisplayName)
        .Add("clientId", r => r.ClientId, Filter.Eq)
        .Add("provider", r => r.Provider, Filter.Eq)
        .Add("sessionId", r => r.SessionId, Filter.Eq)
        .Add("actorId", r => r.ActorId, Filter.Eq)
        .Add("ip", r => r.IpAddress, Filter.Eq)
        .Add("userAgent", r => r.UserAgent);

    public async Task<ListPage<ActivityEventsRow>> HandleAsync(
        ClaimsPrincipal user,
        IQueryCollection query,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        var visible = actor.IsAdmin;
        var rows =
            from e in db.Events.AsNoTracking()
            join u in db.People on e.UserId equals u.Id into people
            from u in people.DefaultIfEmpty()
            where visible
            select new ActivityEventsRow
            {
                Id = e.Id,
                OccurredAt = e.OccurredAt,
                EventType = e.EventType,
                Outcome = e.Outcome,
                Reason = e.Reason,
                UserId = e.UserId,
                Username = u == null ? null : u.Username,
                DisplayName = u == null ? null : u.DisplayName,
                ClientId = e.ClientId,
                Provider = e.Provider,
                SessionId = e.SessionId,
                ActorId = e.ActorId,
                IpAddress = e.Ip,
                UserAgent = e.UserAgent,
                DetailDocument = e.Detail,
            };
        return await Spec.PageAsync(rows, query, ct);
    }
}

public sealed record ActivitySessionEndRequest(Guid? Id);

/// <summary>
/// POST /api/activity/session-end: the SSO admin ends a session at once - the browser has to sign in again, and the
/// applications' refresh tokens of that session stop working. An already finished session is a no-op.
/// </summary>
public sealed class EndSession(SsoDbContext db, Actors actors, SessionTermination sessions)
    : IHandler
{
    public async Task HandleAsync(
        ClaimsPrincipal user,
        ActivitySessionEndRequest r,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        actor.RequireAdmin("activity.forbidden", "Only an SSO admin ends sessions.");
        if (r.Id is not { } id || !await db.Sessions.AnyAsync(s => s.Id == id, ct))
            throw Rule.NotFound("activity.session_not_found", "There is no such session.");
        if (await sessions.EndAsync(db, null, id, null, SessionEndReasons.Admin, ct) > 0)
            await db.SaveChangesAsync(ct);
    }
}
