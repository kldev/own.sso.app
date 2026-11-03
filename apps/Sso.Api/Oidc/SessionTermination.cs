using Microsoft.EntityFrameworkCore;
using Sso.Data;
using Sso.Data.Identity;
using Sso.Data.Oidc;

namespace Sso.Api.Oidc;

/// <summary>
/// Ends SSO sessions at once, with their applications' refresh tokens (ADR 0011): one session, all of a person, or
/// those a person started with one provider. Each ended session is logged with the reason. The changes are added to
/// the caller's unit of work - the caller saves them with its own change.
/// </summary>
public sealed class SessionTermination(EventLog events, TimeProvider time)
{
    public async Task<int> EndAsync(
        SsoDbContext db,
        Guid? userId,
        Guid? sessionId,
        string? provider,
        string reason,
        CancellationToken ct
    )
    {
        if (userId is null && sessionId is null)
            throw new ArgumentException("End the sessions of a person or one session.");
        var now = time.GetUtcNow();
        var query = db.Sessions.Where(s => s.EndedAt == null);
        if (sessionId is { } sid)
            query = query.Where(s => s.Id == sid);
        if (userId is { } uid)
            query = query.Where(s => s.UserId == uid);
        if (provider is not null)
            query = query.Where(s => s.Provider == provider);
        var sessions = await query.ToListAsync(ct);
        if (sessions.Count == 0)
            return 0;
        var ids = sessions.Select(s => s.Id).ToList();
        var tokens = await db
            .RefreshTokens.Where(r => ids.Contains(r.SessionId) && r.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var token in tokens)
            token.RevokedAt = now;
        foreach (var session in sessions)
        {
            session.EndedAt = now;
            session.EndReason = reason;
            events.Add(
                db,
                "session_ended",
                Outcomes.Success,
                reason,
                session.UserId,
                provider: session.Provider,
                sessionId: session.Id
            );
        }
        return sessions.Count;
    }
}

/// <summary>
/// Removes a person's link to a provider account (e.g. to change the account at the person's request): the account
/// stops working at once with its sessions; the next sign-in with the record's e-mail links an account again.
/// </summary>
public sealed class ProviderLinks(SessionTermination sessions, EventLog events)
{
    /// <summary>False when there was no link. The changes go to the caller's unit of work.</summary>
    public async Task<bool> UnlinkAsync(
        SsoDbContext db,
        Guid userId,
        string provider,
        CancellationToken ct
    )
    {
        var link = await db.ExternalLogins.FirstOrDefaultAsync(
            l => l.UserId == userId && l.Provider == provider,
            ct
        );
        if (link is null)
            return false;
        db.ExternalLogins.Remove(link);
        await sessions.EndAsync(db, userId, null, provider, SessionEndReasons.Unlinked, ct);
        events.Add(
            db,
            "link_removed",
            Outcomes.Success,
            userId: userId,
            provider: provider,
            detail: new Dictionary<string, object?>
            {
                ["email"] = link.Email,
                ["linked_at"] = link.LinkedAt,
            }
        );
        return true;
    }
}

/// <summary>Whether a session is still active - and the reason it is not.</summary>
public static class SessionState
{
    /// <summary>null while active; "absolute" or "idle" when past a limit; "ended" when already ended.</summary>
    private static string? Expiry(SsoSession session, DateTimeOffset now) =>
        session.EndedAt is not null ? "ended"
        : now >= session.AbsoluteExpiresAt ? SessionEndReasons.Absolute
        : now >= session.IdleExpiresAt ? SessionEndReasons.Idle
        : null;

    /// <summary>
    /// Ends a session past its idle or absolute limit (logged once, in the caller's unit of work) and tells whether it
    /// is still active.
    /// </summary>
    public static bool IsActive(
        SsoDbContext db,
        EventLog events,
        SsoSession session,
        DateTimeOffset now
    )
    {
        var reason = Expiry(session, now);
        if (reason is null)
            return true;
        if (reason == "ended")
            return false;
        session.EndedAt = now;
        session.EndReason = reason;
        events.Add(
            db,
            "session_expired",
            Outcomes.Success,
            reason,
            session.UserId,
            provider: session.Provider,
            sessionId: session.Id
        );
        return false;
    }

    /// <summary>The roles' admin check reused by the protocol: a person who may still sign in.</summary>
    public static bool IsActive(Person? person) => person is { Status: Statuses.Active };
}
