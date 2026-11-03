using System.ComponentModel.DataAnnotations;
using JetBrains.Annotations;

namespace Sso.Api.Seed;

/// <summary>
/// The seed on start (ADR 0015), section "Seed": on a database without any admin the host enters the first admin and,
/// unless Seed__Demo=false, the demo data - start the project and it is there. Seed__Enabled=false turns it off
/// (the tests seed their own data).
/// </summary>
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.Members)]
public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public bool Enabled { get; init; } = true;

    /// <summary>The demo data (example.com people, applications, sessions, a mailing) with the first admin.</summary>
    public bool Demo { get; init; } = true;
}

/// <summary>
/// The first admin, section "BootstrapAdmin" - neutral defaults, so nothing has to be configured to start. Sign in as
/// this person with a provider account of the given e-mail (or the development sign-in stub). In a real deployment set
/// BootstrapAdmin__Email, BootstrapAdmin__GithubEmail and the others before the first start.
/// </summary>
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.Members)]
public sealed class BootstrapAdminOptions
{
    public const string SectionName = "BootstrapAdmin";
    public const string DefaultEmail = "admin@example.com";

    [Required]
    public string Username { get; init; } = "admin";

    [Required, EmailAddress]
    public string Email { get; init; } = DefaultEmail;

    [Required]
    public string DisplayName { get; init; } = "Admin";

    [EmailAddress]
    public string? GithubEmail { get; init; } = DefaultEmail;

    [EmailAddress]
    public string? GoogleEmail { get; init; } = DefaultEmail;

    [EmailAddress]
    public string? MicrosoftEmail { get; init; }
}
