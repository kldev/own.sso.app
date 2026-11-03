using System.ComponentModel.DataAnnotations;
using JetBrains.Annotations;

namespace Sso.Api.Oidc;

/// <summary>
/// The OpenID Connect provider (ADR 0012), section "Oidc". The issuer is the address the applications are configured
/// with - it is in every token and in the discovery document, so it never comes from the request.
/// </summary>
// Set by the configuration binder (BindConfiguration).
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.Members)]
public sealed class OidcOptions
{
    public const string SectionName = "Oidc";

    /// <summary>e.g. https://sso.example.com - no trailing slash.</summary>
    [Required, Url]
    public required string Issuer { get; init; }

    /// <summary>
    /// The directory of the signing keys (RSA, PEM, one file per key; the newest signs, all are published in the JWKS so
    /// tokens signed before a rotation stay valid). Empty at the first start: a key is made. A secret - a volume, never
    /// the repository.
    /// </summary>
    [Required]
    public required string KeysPath { get; init; }

    /// <summary>An authorization code is redeemed within this time or never (RFC 6749 4.1.2: at most 10 minutes).</summary>
    [Range(typeof(TimeSpan), "00:00:10", "00:10:00")]
    public TimeSpan CodeLifetime { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>ID and access tokens are short; the refresh token (rotating, bound to the SSO session) keeps people in.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "1.00:00:00")]
    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromMinutes(60);
}
