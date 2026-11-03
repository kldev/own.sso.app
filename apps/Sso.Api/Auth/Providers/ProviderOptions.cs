using JetBrains.Annotations;

namespace Sso.Api.Auth.Providers;

/// <summary>
/// The external providers, section "Providers". A provider is on only when its client id and secret are set (and, for
/// Microsoft, at least one allowed tenant) - otherwise its button is not shown and its paths are a 404. Secrets come
/// from the environment only (Providers__GitHub__ClientSecret, ...), never from the repository.
/// </summary>
// Set by the configuration binder (BindConfiguration).
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.Members)]
public sealed class ProviderOptions
{
    public const string SectionName = "Providers";

    public ProviderCredentials GitHub { get; init; } = new();

    public ProviderCredentials Google { get; init; } = new();

    public MicrosoftCredentials Microsoft { get; init; } = new();
}

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.Members)]
public class ProviderCredentials
{
    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public virtual bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>
/// Work accounts only (Entra ID / Microsoft 365), and only of the listed organisations (tid): an empty list switches
/// Microsoft off - it never means "every organisation".
/// </summary>
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.Members)]
public sealed class MicrosoftCredentials : ProviderCredentials
{
    public string[] AllowedTenantIds { get; init; } = [];

    public override bool IsConfigured =>
        base.IsConfigured && AllowedTenantIds.Any(t => !string.IsNullOrWhiteSpace(t));
}
