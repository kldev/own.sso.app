namespace Sso.Data.Oidc;

/// <summary>A one-time, short-lived authorization code (oidc.authorization_codes); only its hash is stored.</summary>
public sealed class AuthorizationCode
{
    public byte[] CodeHash { get; init; } = [];

    /// <summary>The application's row id (<see cref="RegisteredClient.Id"/>), not its OAuth client id.</summary>
    public Guid ClientId { get; init; }

    public Guid SessionId { get; init; }

    public Guid UserId { get; init; }

    public string RedirectUri { get; init; } = "";

    /// <summary>The PKCE S256 challenge; null for a client without PKCE.</summary>
    public string? CodeChallenge { get; init; }

    public string? Nonce { get; init; }

    public string[] Scopes { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? RedeemedAt { get; set; }
}
