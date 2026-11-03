using Microsoft.EntityFrameworkCore;
using Sso.Api.Auth;
using Sso.Data;
using Sso.Data.Identity;
using Sso.Data.Oidc;

namespace Sso.Api.Oidc;

/// <summary>The person behind a code or a refresh token, for the tokens.</summary>
public sealed record TokenSubject(
    Guid UserId,
    string Username,
    string Email,
    string DisplayName,
    string[] Scopes,
    Guid SessionId,
    DateTimeOffset AuthTime,
    string? Nonce,
    bool RefreshIssued
);

/// <summary>
/// The protocol's state (ADR 0012): one-time authorization codes, rotating refresh tokens, userinfo. A code and a
/// refresh token are taken under a row lock (SELECT ... FOR UPDATE) in one transaction, so two parallel requests
/// with the same value never both succeed. Every outcome is logged in oidc.events in the same transaction.
/// </summary>
public sealed class OidcStore(
    IDbContextFactory<SsoDbContext> factory,
    EventLog events,
    SsoSessions sessions,
    TimeProvider time
)
{
    public async Task IssueCodeAsync(
        byte[] codeHash,
        string clientId,
        Guid sessionId,
        string redirectUri,
        string? codeChallenge,
        string? nonce,
        string[] scopes,
        TimeSpan lifetime,
        RequestInfo request,
        CancellationToken ct
    )
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId, ct);
        var client = await db
            .Clients.AsNoTracking()
            .SingleAsync(c => c.ClientId == clientId && c.Status == Statuses.Active, ct);
        var now = time.GetUtcNow();
        db.AuthorizationCodes.Add(
            new AuthorizationCode
            {
                CodeHash = codeHash,
                ClientId = client.Id,
                SessionId = sessionId,
                UserId = session.UserId,
                RedirectUri = redirectUri,
                CodeChallenge = codeChallenge,
                Nonce = nonce,
                Scopes = scopes,
                CreatedAt = now,
                ExpiresAt = now + lifetime,
            }
        );
        events.Add(
            db,
            "code_issued",
            Outcomes.Success,
            userId: session.UserId,
            clientId: clientId,
            provider: session.Provider,
            sessionId: sessionId,
            request: request
        );
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The authorization_code grant: the code is single-use (a second use revokes the tokens issued from it, RFC 6749
    /// 4.1.2), short-lived, bound to the client, the redirect URI and the PKCE challenge (S256 of the verifier). Any
    /// attempt with the right client uses the code up. With offline_access a refresh token starts a new family.
    /// </summary>
    /// <returns>The subject, or null for invalid_grant (the reason is in the event log).</returns>
    public async Task<TokenSubject?> RedeemCodeAsync(
        byte[] codeHash,
        string clientId,
        string? redirectUri,
        string? verifierChallenge,
        byte[] refreshHash,
        RequestInfo request,
        CancellationToken ct
    )
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = time.GetUtcNow();
        var code = (
            await db
                .AuthorizationCodes.FromSql(
                    $"SELECT * FROM oidc.authorization_codes WHERE code_hash = {codeHash} FOR UPDATE"
                )
                .ToListAsync(ct)
        ).FirstOrDefault();
        string? reason;
        RegisteredClient? client = null;
        SsoSession? session = null;
        Person? person = null;
        if (code is null)
            reason = "unknown_code";
        else
        {
            client = await db.Clients.AsNoTracking().SingleAsync(c => c.Id == code.ClientId, ct);
            if (client.ClientId != clientId)
                reason = "client_mismatch";
            else if (code.RedeemedAt is not null)
            {
                reason = "code_reused";
                var families = await db
                    .RefreshTokens.Where(r => r.CodeHash == codeHash)
                    .Select(r => r.FamilyId)
                    .Distinct()
                    .ToListAsync(ct);
                var tokens = await db
                    .RefreshTokens.Where(r => families.Contains(r.FamilyId) && r.RevokedAt == null)
                    .ToListAsync(ct);
                foreach (var token in tokens)
                    token.RevokedAt = now;
            }
            else
            {
                code.RedeemedAt = now;
                session = await db.Sessions.SingleAsync(s => s.Id == code.SessionId, ct);
                person = await db.People.AsNoTracking().SingleAsync(p => p.Id == code.UserId, ct);
                reason =
                    now >= code.ExpiresAt ? "code_expired"
                    : code.RedirectUri != (redirectUri ?? "") ? "redirect_uri_mismatch"
                    : code.CodeChallenge is not null && code.CodeChallenge != verifierChallenge
                        ? "pkce_failed"
                    : code.CodeChallenge is null && verifierChallenge is not null ? "pkce_failed"
                    : client.Status != Statuses.Active ? "client_inactive"
                    : person.Status != Statuses.Active ? "user_inactive"
                    : !SessionState.IsActive(db, events, session, now) ? "session_ended"
                    : null;
            }
        }

        if (reason is not null)
        {
            events.Add(
                db,
                "token",
                Outcomes.Refused,
                reason,
                code?.UserId,
                clientId,
                sessionId: code?.SessionId,
                request: request,
                detail: new Dictionary<string, object> { ["grant_type"] = "authorization_code" }
            );
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return null;
        }

        var refresh = code!.Scopes.Contains("offline_access");
        if (refresh)
            db.RefreshTokens.Add(
                new RefreshToken
                {
                    TokenHash = refreshHash,
                    FamilyId = Guid.CreateVersion7(),
                    ClientId = client!.Id,
                    SessionId = session!.Id,
                    UserId = person!.Id,
                    Scopes = code.Scopes,
                    CodeHash = codeHash,
                    CreatedAt = now,
                }
            );
        events.Add(
            db,
            "tokens_issued",
            Outcomes.Success,
            userId: person!.Id,
            clientId: clientId,
            provider: session!.Provider,
            sessionId: session.Id,
            request: request,
            detail: new Dictionary<string, object>
            {
                ["grant_type"] = "authorization_code",
                ["refresh_token"] = refresh,
            }
        );
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new TokenSubject(
            person.Id,
            person.Username,
            person.Email,
            person.DisplayName,
            code.Scopes,
            session.Id,
            session.CreatedAt,
            code.Nonce,
            refresh
        );
    }

    /// <summary>
    /// The refresh_token grant: the token is replaced by a new one of the same family, and using it extends the SSO
    /// session (it never outlives it). A used or revoked token coming back revokes the family; an inactive client,
    /// person or session revokes it too.
    /// </summary>
    /// <returns>The subject, or null for invalid_grant (the reason is in the event log).</returns>
    public async Task<TokenSubject?> RotateRefreshAsync(
        byte[] tokenHash,
        byte[] newHash,
        string clientId,
        RequestInfo request,
        CancellationToken ct
    )
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = time.GetUtcNow();
        var token = (
            await db
                .RefreshTokens.FromSql(
                    $"SELECT * FROM oidc.refresh_tokens WHERE token_hash = {tokenHash} FOR UPDATE"
                )
                .ToListAsync(ct)
        ).FirstOrDefault();
        string? reason = null;
        SsoSession? session = null;
        Person? person = null;
        if (token is null)
            reason = "unknown_token";
        else
        {
            var client = await db
                .Clients.AsNoTracking()
                .SingleAsync(c => c.Id == token.ClientId, ct);
            session = await db.Sessions.SingleAsync(s => s.Id == token.SessionId, ct);
            person = await db.People.AsNoTracking().SingleAsync(p => p.Id == token.UserId, ct);
            if (client.ClientId != clientId)
                reason = "client_mismatch";
            else if (token.UsedAt is not null || token.RevokedAt is not null)
            {
                reason = "refresh_reused";
                await RevokeFamilyAsync(db, token.FamilyId, now, ct);
            }
            else if (client.Status != Statuses.Active)
                reason = "client_inactive";
            else if (person.Status != Statuses.Active)
                reason = "user_inactive";
            else if (!SessionState.IsActive(db, events, session, now))
                reason = "session_ended";
            if (reason is "client_inactive" or "user_inactive" or "session_ended")
                await RevokeFamilyAsync(db, token.FamilyId, now, ct);
        }

        if (reason is not null)
        {
            events.Add(
                db,
                "token",
                Outcomes.Refused,
                reason,
                token?.UserId,
                clientId,
                sessionId: token?.SessionId,
                request: request,
                detail: new Dictionary<string, object> { ["grant_type"] = "refresh_token" }
            );
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return null;
        }

        token!.UsedAt = now;
        db.RefreshTokens.Add(
            new RefreshToken
            {
                TokenHash = newHash,
                FamilyId = token.FamilyId,
                ClientId = token.ClientId,
                SessionId = token.SessionId,
                UserId = token.UserId,
                Scopes = token.Scopes,
                CreatedAt = now,
            }
        );
        sessions.Extend(db, session!, clientId, request, now);
        events.Add(
            db,
            "token_refreshed",
            Outcomes.Success,
            userId: person!.Id,
            clientId: clientId,
            provider: session!.Provider,
            sessionId: session.Id,
            request: request
        );
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new TokenSubject(
            person.Id,
            person.Username,
            person.Email,
            person.DisplayName,
            token.Scopes,
            session.Id,
            session.CreatedAt,
            null,
            true
        );
    }

    /// <summary>/connect/userinfo: the person behind a valid access token, while the person and the session are active.</summary>
    public async Task<Person?> UserInfoAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var session = await db.Sessions.FirstOrDefaultAsync(
            s => s.Id == sessionId && s.UserId == userId,
            ct
        );
        if (session is null)
            return null;
        if (!SessionState.IsActive(db, events, session, time.GetUtcNow()))
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        return await db
            .People.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == userId && p.Status == Statuses.Active, ct);
    }

    private static async Task RevokeFamilyAsync(
        SsoDbContext db,
        Guid familyId,
        DateTimeOffset now,
        CancellationToken ct
    )
    {
        var family = await db
            .RefreshTokens.Where(r => r.FamilyId == familyId && r.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var member in family)
            member.RevokedAt = now;
    }
}
