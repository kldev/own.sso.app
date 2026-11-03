using System.ComponentModel.DataAnnotations;
using JetBrains.Annotations;

namespace Sso.Api.Auth;

/// <summary>The panel's internal access tokens: the Blazor circuit sends one to the contract endpoints. Section "Jwt".</summary>
// Set by the configuration binder (BindConfiguration).
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.Members)]
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; init; } = "sso-api";

    [Required]
    public string Audience { get; init; } = "sso";

    /// <summary>HMAC-SHA256 key, at least 32 characters. A secret: environment variable Jwt__SigningKey only.</summary>
    [Required, MinLength(32)]
    public required string SigningKey { get; init; }

    /// <summary>
    /// How long the panel's internal access token lives. The SSO session itself lives in the database (Sessions:*);
    /// a page whose token expired reloads and gets a new one from the session.
    /// </summary>
    [Range(typeof(TimeSpan), "00:05:00", "1.00:00:00")]
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Sign-in starts per minute per client address (Jwt__SignInAttemptsPerMinute).</summary>
    [Range(1, 10_000)]
    public int SignInAttemptsPerMinute { get; init; } = 20;
}
