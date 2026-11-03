using System.ComponentModel.DataAnnotations;
using JetBrains.Annotations;

namespace Sso.Api.Auth;

/// <summary>
/// The SSO session (ADR 0011): every use moves its end by IdleTimeout, never past AbsoluteLifetime. The defaults are
/// the production values (14 days idle, 30 days at most, as the owner panel of the modular monolith); an environment
/// changes them with Sessions__IdleTimeout and Sessions__AbsoluteLifetime. Section "Sessions".
/// </summary>
// Set by the configuration binder (BindConfiguration).
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.Members)]
public sealed class SsoSessionOptions : IValidatableObject
{
    public const string SectionName = "Sessions";

    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromDays(14);

    public TimeSpan AbsoluteLifetime { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// __Host- binds the cookie to this origin over https (no Domain, Path=/, Secure). Development on plain http uses
    /// another name and SecureCookie=false.
    /// </summary>
    [Required]
    public string CookieName { get; init; } = "__Host-sso.session";

    public bool SecureCookie { get; init; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (IdleTimeout <= TimeSpan.Zero)
            yield return new ValidationResult(
                $"{nameof(IdleTimeout)} must be positive, got {IdleTimeout}.",
                [nameof(IdleTimeout)]
            );
        if (IdleTimeout > AbsoluteLifetime)
            yield return new ValidationResult(
                $"{nameof(IdleTimeout)} ({IdleTimeout}) cannot be longer than {nameof(AbsoluteLifetime)} ({AbsoluteLifetime}).",
                [nameof(IdleTimeout), nameof(AbsoluteLifetime)]
            );
        if (CookieName.StartsWith("__Host-", StringComparison.Ordinal) && !SecureCookie)
            yield return new ValidationResult(
                "A __Host- cookie must be Secure - set SecureCookie=true or another CookieName.",
                [nameof(CookieName), nameof(SecureCookie)]
            );
    }
}
