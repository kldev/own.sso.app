using JetBrains.Annotations;

namespace Sso.Api.Web;

/// <summary>
/// Who runs this SSO - shown on every public page (sign-in, refusals), so a visitor (and an automatic phishing filter)
/// sees whose service it is and that it never asks for a password. Section "Operator".
/// </summary>
// Set by the configuration binder (BindConfiguration).
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.Members)]
public sealed class OperatorOptions
{
    public const string SectionName = "Operator";

    public string Name { get; init; } = "Example Team";

    public string Website { get; init; } = "https://example.com";

    public string? Contact { get; init; }
}
