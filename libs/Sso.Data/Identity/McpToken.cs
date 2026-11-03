namespace Sso.Data.Identity;

/// <summary>
/// An SSO admin's personal token for Claude Code's MCP connection (identity.mcp_tokens, ADR 0013). The token is shown
/// once; the table keeps its SHA-256. It carries no role: every call checks that its person is still an active admin.
/// </summary>
public sealed class McpToken
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    public string Name { get; init; } = "";

    /// <summary>SHA-256 of the token, 32 bytes.</summary>
    public byte[] TokenHash { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; init; }
}
