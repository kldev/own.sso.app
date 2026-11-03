using System.Security.Claims;
using System.Text.RegularExpressions;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Sso.Api.Features.Common;
using Sso.Api.Oidc;
using Sso.Data;
using Sso.Data.Identity;
using Sso.Data.Oidc;

namespace Sso.Api.Features.Platform;

/// <summary>One of the signed-in admin's MCP tokens - never the hash.</summary>
public sealed class PlatformMcpTokensRow
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>active, expired or revoked.</summary>
    public required string State { get; init; }
}

/// <summary>
/// GET /api/platform/mcp-tokens: the signed-in admin's own MCP tokens for Claude Code (ADR 0013). Nobody sees another
/// person's tokens; a member sees none.
/// </summary>
public sealed class ListMcpTokens(SsoDbContext db, Actors actors, TimeProvider time) : IHandler
{
    public static readonly ListSpec<PlatformMcpTokensRow> Spec =
        new ListSpec<PlatformMcpTokensRow>()
            .Add("id", r => r.Id, Filter.Eq)
            .Add("name", r => r.Name, Filter.Eq)
            .Add("createdAt", r => r.CreatedAt, Filter.Range)
            .Add("expiresAt", r => r.ExpiresAt, Filter.Range)
            .Add("revokedAt", r => r.RevokedAt, Filter.Range)
            .Add("lastUsedAt", r => r.LastUsedAt, Filter.Range)
            .Add("state", r => r.State, Filter.Eq);

    public async Task<ListPage<PlatformMcpTokensRow>> HandleAsync(
        ClaimsPrincipal user,
        IQueryCollection query,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        var now = time.GetUtcNow();
        var visible = actor.IsAdmin;
        var rows = db
            .McpTokens.AsNoTracking()
            .Where(t => visible && t.UserId == actor.Id)
            .Select(t => new PlatformMcpTokensRow
            {
                Id = t.Id,
                Name = t.Name,
                CreatedAt = t.CreatedAt,
                ExpiresAt = t.ExpiresAt,
                RevokedAt = t.RevokedAt,
                LastUsedAt = t.LastUsedAt,
                State =
                    t.RevokedAt != null ? "revoked"
                    : t.ExpiresAt != null && t.ExpiresAt <= now ? "expired"
                    : "active",
            });
        return await Spec.PageAsync(rows, query, ct);
    }
}

[UsedImplicitly(ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature)] // deserialized from JSON
public sealed record PlatformMcpTokenIssueRequest(
    Guid? Id,
    string? Name,
    string? TokenHash,
    int? ValidDays
);

/// <summary>
/// POST /api/platform/mcp-token-issue: an SSO admin issues an MCP token for Claude Code. The page makes the token,
/// shows it once and sends its SHA-256 as hex; 1-365 days of validity. The same id with the same name is a no-op.
/// </summary>
public sealed partial class IssueMcpToken(
    SsoDbContext db,
    Actors actors,
    EventLog events,
    TimeProvider time
) : IHandler
{
    public async Task HandleAsync(
        ClaimsPrincipal user,
        PlatformMcpTokenIssueRequest r,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        actor.RequireAdmin("platform.forbidden", "Only an SSO admin issues MCP tokens.");
        if (r.Id is not { } id || id == Guid.Empty)
            throw Rule.Invalid("platform.id_missing", "The token's id is missing.");
        var name = Text.Trim(r.Name);
        if (string.IsNullOrEmpty(name) || name.Length > 100)
            throw Rule.Invalid(
                "platform.token_name_invalid",
                "Name the token (up to 100 characters), e.g. \"Laptop - Claude Code\"."
            );
        if (r.TokenHash is null || !HexSha256().IsMatch(r.TokenHash))
            throw Rule.Invalid(
                "platform.token_hash_invalid",
                "The token's hash is not SHA-256 in hex."
            );
        if (r.ValidDays is not (>= 1 and <= 365))
            throw Rule.Invalid(
                "platform.token_validity_invalid",
                "A token is valid for 1 to 365 days."
            );

        var existing = await db.McpTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (existing is not null)
        {
            if (existing.Name == name && existing.UserId == actor.Id)
                return;
            throw Rule.Conflict("platform.id_taken", "This id already belongs to another token.");
        }
        var now = time.GetUtcNow();
        db.McpTokens.Add(
            new McpToken
            {
                Id = id,
                UserId = actor.Id,
                Name = name,
                TokenHash = Convert.FromHexString(r.TokenHash),
                CreatedAt = now,
                ExpiresAt = now.AddDays(r.ValidDays.Value),
            }
        );
        events.Add(
            db,
            "mcp_token_issued",
            Outcomes.Success,
            userId: actor.Id,
            detail: new Dictionary<string, object>
            {
                ["token"] = name,
                ["valid_days"] = r.ValidDays,
            },
            request: null
        );
        await db.SaveChangesAsync(ct);
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HexSha256();
}

[UsedImplicitly(ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature)] // deserialized from JSON
public sealed record PlatformMcpTokenRevokeRequest(Guid? Id);

/// <summary>POST /api/platform/mcp-token-revoke: an SSO admin revokes one of their own tokens; it stops at once.</summary>
public sealed class RevokeMcpToken(
    SsoDbContext db,
    Actors actors,
    EventLog events,
    TimeProvider time
) : IHandler
{
    public async Task HandleAsync(
        ClaimsPrincipal user,
        PlatformMcpTokenRevokeRequest r,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        actor.RequireAdmin("platform.forbidden", "Only an SSO admin revokes MCP tokens.");
        var token =
            await db.McpTokens.FirstOrDefaultAsync(t => t.Id == r.Id && t.UserId == actor.Id, ct)
            ?? throw Rule.NotFound("platform.token_not_found", "There is no such token of yours.");
        if (token.RevokedAt is not null)
            return;
        token.RevokedAt = time.GetUtcNow();
        events.Add(
            db,
            "mcp_token_revoked",
            Outcomes.Success,
            userId: actor.Id,
            detail: new Dictionary<string, object> { ["token"] = token.Name }
        );
        await db.SaveChangesAsync(ct);
    }
}
