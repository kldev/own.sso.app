namespace Sso.Data.Oidc;

/// <summary>
/// An application that signs people in through Great SSO (oidc.clients), registered by an SSO admin. The secret is
/// shown once and stored only as base64url(SHA-256(secret)).
/// </summary>
public sealed class RegisteredClient : IAudited
{
    public Guid Id { get; init; }

    /// <summary>The OAuth client id: lower case letters, digits, dot, dash, underscore; 2-64 characters.</summary>
    public string ClientId { get; init; } = "";

    public string Name { get; set; } = "";

    /// <summary>base64url(SHA-256(secret)), 43 characters; never audited, never shown.</summary>
    public string SecretHash { get; set; } = "";

    /// <summary>Exact matches only (RFC 9700): no wildcards, no prefix matching.</summary>
    public string[] RedirectUris { get; set; } = [];

    /// <summary>The providers this application lets people sign in with.</summary>
    public string[] Providers { get; set; } = [];

    /// <summary>PKCE S256 is required by default; off only for a test client such as the conformance suite.</summary>
    public bool RequirePkce { get; init; } = true;

    public string Status { get; set; } = Identity.Statuses.Active;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset SecretSetAt { get; set; }

    public uint RowVersion { get; init; }
}
