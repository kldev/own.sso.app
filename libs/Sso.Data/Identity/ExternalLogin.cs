namespace Sso.Data.Identity;

/// <summary>
/// A person's link to one provider account (identity.external_logins): at most one per provider, made by the first
/// sign-in with the e-mail the admin entered; afterwards the provider's stable id signs in.
/// </summary>
public sealed class ExternalLogin : IAudited
{
    public Guid UserId { get; init; }

    public string Provider { get; init; } = "";

    /// <summary>The provider's stable id: GitHub's numeric id, Google's sub, Microsoft's "tid:oid".</summary>
    public string Subject { get; init; } = "";

    /// <summary>Microsoft only: the organisation (tid) the account belongs to.</summary>
    public string? TenantId { get; init; }

    /// <summary>The verified e-mail the first sign-in came with (for the admin; never used to sign in again).</summary>
    public string Email { get; init; } = "";

    public DateTimeOffset LinkedAt { get; init; }
}
