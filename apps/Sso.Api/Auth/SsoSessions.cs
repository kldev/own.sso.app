using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sso.Api.Oidc;
using Sso.Data;
using Sso.Data.Oidc;

namespace Sso.Api.Auth;

/// <summary>A person with an active SSO session.</summary>
public sealed record SessionUser(
    Guid SessionId,
    Guid UserId,
    string Username,
    string Email,
    string DisplayName,
    string Role,
    string Provider,
    DateTimeOffset AuthTime
);

/// <summary>Who sent a request - logged with every event (oidc.events), never anything secret.</summary>
public sealed record RequestInfo(string? Ip, string? UserAgent)
{
    public static RequestInfo From(HttpContext http) =>
        new(
            http.Connection.RemoteIpAddress?.ToString(),
            http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null
        );
}

/// <summary>
/// The server-side SSO sessions (oidc.sessions, ADR 0011): the browser's cookie holds a random value, the database its
/// hash. A session ends after 14 days without use or 30 days after the sign-in, whichever comes first; using it moves
/// the idle limit (at most every 15 minutes, never past the absolute one).
/// </summary>
public sealed class SsoSessions(
    IDbContextFactory<SsoDbContext> factory,
    EventLog events,
    TimeProvider time,
    IOptions<SsoSessionOptions> options
)
{
    /// <summary>How often a session in use is written: a page with many requests writes once.</summary>
    private static readonly TimeSpan ExtendEvery = TimeSpan.FromMinutes(15);

    /// <summary>Starts a session for a person who just signed in; returns the cookie's value.</summary>
    public async Task<(string Token, DateTimeOffset CookieExpires)> StartAsync(
        Guid userId,
        string provider,
        string? clientId,
        RequestInfo request,
        CancellationToken ct
    )
    {
        var token = Secrets.NewToken();
        var o = options.Value;
        var now = time.GetUtcNow();
        await using var db = await factory.CreateDbContextAsync(ct);
        var session = new SsoSession
        {
            Id = Guid.CreateVersion7(),
            TokenHash = Secrets.Hash(token),
            UserId = userId,
            Provider = provider,
            CreatedAt = now,
            LastSeenAt = now,
            IdleExpiresAt =
                now + (o.IdleTimeout < o.AbsoluteLifetime ? o.IdleTimeout : o.AbsoluteLifetime),
            AbsoluteExpiresAt = now + o.AbsoluteLifetime,
            Ip = EventIp(request),
            UserAgent = Cut(request.UserAgent),
        };
        db.Sessions.Add(session);
        events.Add(
            db,
            "session_started",
            Outcomes.Success,
            userId: userId,
            clientId: clientId,
            provider: provider,
            sessionId: session.Id,
            request: request
        );
        await db.SaveChangesAsync(ct);
        return (token, now.Add(o.AbsoluteLifetime));
    }

    /// <summary>The person behind the cookie's value while the session and the person are active; extends it.</summary>
    public async Task<SessionUser?> TouchAsync(
        string token,
        string? clientId,
        RequestInfo request,
        CancellationToken ct
    )
    {
        var hash = Secrets.Hash(token);
        await using var db = await factory.CreateDbContextAsync(ct);
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.TokenHash == hash, ct);
        if (session is null)
            return null;
        var now = time.GetUtcNow();
        if (!SessionState.IsActive(db, events, session, now))
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        var person = await db
            .People.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == session.UserId, ct);
        if (!SessionState.IsActive(person))
            return null;
        if (Extend(db, session, clientId, request, now))
            await db.SaveChangesAsync(ct);
        return new SessionUser(
            session.Id,
            person!.Id,
            person.Username,
            person.Email,
            person.DisplayName,
            person.Role,
            session.Provider,
            session.CreatedAt
        );
    }

    /// <summary>
    /// Moves the idle limit of a session in use (never past the absolute one), at most every 15 minutes; each move
    /// is logged as session_extended. True when something changed.
    /// </summary>
    public bool Extend(
        SsoDbContext db,
        SsoSession session,
        string? clientId,
        RequestInfo? request,
        DateTimeOffset now
    )
    {
        if (session.LastSeenAt > now - ExtendEvery)
            return false;
        var idle = now + options.Value.IdleTimeout;
        session.LastSeenAt = now;
        session.IdleExpiresAt = idle < session.AbsoluteExpiresAt ? idle : session.AbsoluteExpiresAt;
        events.Add(
            db,
            "session_extended",
            Outcomes.Success,
            userId: session.UserId,
            clientId: clientId,
            provider: session.Provider,
            sessionId: session.Id,
            request: request
        );
        return true;
    }

    private static System.Net.IPAddress? EventIp(RequestInfo request) =>
        request.Ip is { } ip && System.Net.IPAddress.TryParse(ip, out var address) ? address : null;

    private static string? Cut(string? value) =>
        value is null || value.Length <= 500 ? value : value[..500];
}
