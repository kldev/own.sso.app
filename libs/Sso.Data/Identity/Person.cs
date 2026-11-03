namespace Sso.Data.Identity;

/// <summary>
/// A person an SSO admin has entered (identity.users) - the only way anyone gets into Great SSO: no registration and
/// no password (ADR 0011). The person signs in through an external provider whose e-mail the admin wrote here.
/// </summary>
public sealed class Person : IAudited
{
    public Guid Id { get; init; }

    /// <summary>Lower case letters, digits, dot, dash, underscore; 2-64 characters.</summary>
    public string Username { get; set; } = "";

    /// <summary>The work e-mail - what applications receive and match their accounts by. Lower case.</summary>
    public string Email { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary><see cref="Roles"/>: admin runs the panel, member only signs in to applications.</summary>
    public string Role { get; set; } = Roles.Member;

    /// <summary><see cref="Statuses"/>.</summary>
    public string Status { get; set; } = Statuses.Active;

    /// <summary>
    /// The e-mails of the person's provider accounts: the first sign-in with a verified e-mail equal to one of these
    /// links the account (<see cref="ExternalLogin"/>); afterwards the provider's own id signs in. Null = none.
    /// </summary>
    public string? GithubEmail { get; set; }

    public string? GoogleEmail { get; set; }

    public string? MicrosoftEmail { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>xmin - the row version the API hands out and checks (ADR 0007).</summary>
    public uint RowVersion { get; init; }
}

/// <summary>Roles of a person: a new role is a constant here and a value of the CHECK in the model.</summary>
public static class Roles
{
    public const string Admin = "admin";
    public const string Member = "member";

    public static readonly string[] All = [Admin, Member];
}

/// <summary>Status of a person or an application.</summary>
public static class Statuses
{
    public const string Active = "active";
    public const string Inactive = "inactive";

    public static readonly string[] All = [Active, Inactive];
}

/// <summary>The external providers a person signs in with.</summary>
public static class ProviderNames
{
    public const string GitHub = "github";
    public const string Google = "google";
    public const string Microsoft = "microsoft";

    public static readonly string[] All = [GitHub, Google, Microsoft];
}
