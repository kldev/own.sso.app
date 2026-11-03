using Microsoft.EntityFrameworkCore;
using Sso.Data;
using Sso.Data.Identity;

namespace Sso.Api.Mcp;

/// <summary>The admin behind a valid MCP token.</summary>
public sealed record McpCaller(
    Guid TokenId,
    Guid UserId,
    string Username,
    string DisplayName,
    string Role
);

/// <summary>
/// /mcp's authentication (ADR 0013): the admin behind a token that is neither revoked nor expired, while the person is
/// an active admin - nothing otherwise. last_used_at is written at most once a minute.
/// </summary>
public sealed class McpTokens(IDbContextFactory<SsoDbContext> factory, TimeProvider time)
{
    public async Task<McpCaller?> CheckAsync(byte[] tokenHash, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var db = await factory.CreateDbContextAsync(ct);
        var caller = await (
            from t in db.McpTokens
            join u in db.People on t.UserId equals u.Id
            where
                t.TokenHash == tokenHash
                && t.RevokedAt == null
                && (t.ExpiresAt == null || t.ExpiresAt > now)
                && u.Status == Statuses.Active
                && u.Role == Roles.Admin
            select new McpCaller(t.Id, u.Id, u.Username, u.DisplayName, u.Role)
        ).FirstOrDefaultAsync(ct);
        if (caller is null)
            return null;
        var minuteAgo = now.AddMinutes(-1);
        await db
            .McpTokens.Where(t =>
                t.Id == caller.TokenId && (t.LastUsedAt == null || t.LastUsedAt < minuteAgo)
            )
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.LastUsedAt, now), ct);
        return caller;
    }
}
