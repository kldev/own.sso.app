namespace Sso.Data.Oidc;

/// <summary>
/// A rotating refresh token (oidc.refresh_tokens). A family = one sign-in of one application: every refresh replaces
/// the token, and a used or revoked token coming back revokes the whole family (RFC 9700 4.14.2).
/// </summary>
public sealed class RefreshToken
{
    public byte[] TokenHash { get; init; } = [];

    public Guid FamilyId { get; init; }

    /// <summary>The application's row id.</summary>
    public Guid ClientId { get; init; }

    public Guid SessionId { get; init; }

    public Guid UserId { get; init; }

    public string[] Scopes { get; init; } = [];

    /// <summary>The code the family started from - a second use of that code revokes the family.</summary>
    public byte[]? CodeHash { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}
