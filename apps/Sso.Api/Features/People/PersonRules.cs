using Sso.Api.Features.Common;
using Sso.Data.Identity;

namespace Sso.Api.Features.People;

/// <summary>
/// The rules of a person's record, shared by create and update (ADR 0008): normalised values first (trimmed, lower
/// case where it matters), then each rule with its code and sentence, in the order the panel shows them.
/// </summary>
internal sealed record PersonValues(
    string? Username,
    string? Email,
    string? DisplayName,
    string? Role,
    string? GithubEmail,
    string? GoogleEmail,
    string? MicrosoftEmail
)
{
    public const string Forbidden = "people.forbidden";
    public const string NotFound = "people.not_found";
    public const string Stale =
        "Someone changed this person in the meantime - reload and try again.";

    public static PersonValues Normalise(
        string? username,
        string? email,
        string? displayName,
        string? role,
        string? githubEmail,
        string? googleEmail,
        string? microsoftEmail
    ) =>
        new(
            Text.Lower(username),
            Text.Lower(email),
            Text.Trim(displayName),
            Text.Trim(role),
            Text.LowerOrNull(githubEmail),
            Text.LowerOrNull(googleEmail),
            Text.LowerOrNull(microsoftEmail)
        );

    public void Validate()
    {
        if (!Text.IsName(Username))
            throw Rule.Invalid(
                "people.username_invalid",
                "Username: 2-64 characters - lower-case letters, digits, dot, dash or underscore."
            );
        if (!Text.IsEmail(Email))
            throw Rule.Invalid("people.email_invalid", "Enter a valid work e-mail address.");
        if (string.IsNullOrEmpty(DisplayName) || DisplayName.Length > 200)
            throw Rule.Invalid("people.name_invalid", "Enter the name (up to 200 characters).");
        if (Role is null || !Roles.All.Contains(Role))
            throw Rule.Invalid("people.role_invalid", "The role is admin or member.");
        if (
            (GithubEmail is not null && !Text.IsEmail(GithubEmail))
            || (GoogleEmail is not null && !Text.IsEmail(GoogleEmail))
            || (MicrosoftEmail is not null && !Text.IsEmail(MicrosoftEmail))
        )
            throw Rule.Invalid(
                "people.provider_email_invalid",
                "A provider e-mail is not a valid address."
            );
        if (GithubEmail is null && GoogleEmail is null && MicrosoftEmail is null)
            throw Rule.Invalid(
                "people.provider_email_missing",
                "Enter at least one provider e-mail - Great SSO has no other way to sign in."
            );
    }

    public static readonly RuleException UsernameTaken = Rule.Conflict(
        "people.username_taken",
        "This username is taken."
    );

    public static readonly RuleException EmailTaken = Rule.Conflict(
        "people.email_taken",
        "Another person has this e-mail."
    );

    public static readonly RuleException ProviderEmailTaken = Rule.Conflict(
        "people.provider_email_taken",
        "Another person signs in with this provider e-mail."
    );

    /// <summary>A unique index a parallel request won -> the same refusal as the check before it.</summary>
    public static readonly Dictionary<string, RuleException> UniqueIndexes = new()
    {
        ["ix_users_username"] = UsernameTaken,
        ["ix_users_email"] = EmailTaken,
        ["ix_users_github_email"] = ProviderEmailTaken,
        ["ix_users_google_email"] = ProviderEmailTaken,
        ["ix_users_microsoft_email"] = ProviderEmailTaken,
    };

    public bool SameAs(Person p) =>
        p.Username == Username
        && p.Email == Email
        && p.DisplayName == DisplayName
        && p.Role == Role
        && p.GithubEmail == GithubEmail
        && p.GoogleEmail == GoogleEmail
        && p.MicrosoftEmail == MicrosoftEmail;

    public void ApplyTo(Person p)
    {
        p.Username = Username!;
        p.Email = Email!;
        p.DisplayName = DisplayName!;
        p.Role = Role!;
        p.GithubEmail = GithubEmail;
        p.GoogleEmail = GoogleEmail;
        p.MicrosoftEmail = MicrosoftEmail;
    }
}
