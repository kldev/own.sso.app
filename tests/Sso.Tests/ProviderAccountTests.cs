using System.Security.Claims;
using System.Text.Json;
using Sso.Api.Auth;
using Sso.Api.Auth.Providers;

namespace Sso.Tests;

/// <summary>What each provider's answer becomes (no network): the stable id, the e-mail and whether it counts as verified.</summary>
[Trait("verify", "sign-in")]
public sealed class ProviderAccountTests
{
    private const string Tenant = "11111111-2222-4333-8444-555555555555";

    [Fact]
    public void GitHub_takes_the_numeric_id_and_only_the_primary_email_verified_by_github()
    {
        using var user = JsonDocument.Parse(
            """{"id": 583231, "login": "octocat", "email": "public@example.com"}"""
        );
        using var emails = JsonDocument.Parse(
            """
            [{"email": "other@example.com", "primary": false, "verified": true},
             {"email": "main@example.com", "primary": true, "verified": true}]
            """
        );
        var account = GitHubAccount.From(user.RootElement, emails.RootElement);
        Assert.Equal(
            new ExternalAccount("github", "583231", null, "main@example.com", true),
            account
        );

        using var unverified = JsonDocument.Parse(
            """[{"email": "main@example.com", "primary": true, "verified": false}]"""
        );
        Assert.False(GitHubAccount.From(user.RootElement, unverified.RootElement).EmailVerified);
        using var none = JsonDocument.Parse("[]");
        Assert.Null(GitHubAccount.From(user.RootElement, none.RootElement).Email);
    }

    [Fact]
    public void Google_needs_email_verified()
    {
        var verified = GoogleAccount.From(
            Principal(("sub", "1077"), ("email", "a@gmail.com"), ("email_verified", "true"))
        );
        Assert.Equal(new ExternalAccount("google", "1077", null, "a@gmail.com", true), verified);
        Assert.False(
            GoogleAccount.From(Principal(("sub", "1077"), ("email", "a@gmail.com"))).EmailVerified
        );
    }

    [Fact]
    public void Microsoft_accepts_only_work_accounts_of_the_allowed_organisations()
    {
        var oid = "aaaaaaaa-0000-4000-8000-000000000001";
        var work = MicrosoftAccount.From(
            Principal(("tid", Tenant), ("oid", oid), ("email", "w@org.example")),
            [Tenant]
        );
        Assert.Equal(
            new ExternalAccount("microsoft", $"{Tenant}:{oid}", Tenant, "w@org.example", true),
            work
        );

        // No email claim: the sign-in name (UPN) of the allowed organisation.
        var upn = MicrosoftAccount.From(
            Principal(("tid", Tenant), ("oid", oid), ("preferred_username", "w@org.example")),
            [Tenant]
        );
        Assert.Equal("w@org.example", upn.Email);

        var personal = MicrosoftAccount.From(
            Principal(
                ("tid", MicrosoftAccount.ConsumersTenantId),
                ("oid", oid),
                ("email", "me@outlook.com")
            ),
            [Tenant, MicrosoftAccount.ConsumersTenantId]
        );
        Assert.Equal("personal_account", personal.Refusal);
        Assert.False(personal.EmailVerified);

        var other = MicrosoftAccount.From(
            Principal(
                ("tid", "99999999-0000-4000-8000-000000000000"),
                ("oid", oid),
                ("email", "w@other.example")
            ),
            [Tenant]
        );
        Assert.Equal("tenant_not_allowed", other.Refusal);

        Assert.True(
            MicrosoftAccount.IsValidIssuer(
                $"https://login.microsoftonline.com/{Tenant}/v2.0",
                Tenant,
                [Tenant]
            )
        );
        Assert.False(
            MicrosoftAccount.IsValidIssuer(
                "https://login.microsoftonline.com/99999999-0000-4000-8000-000000000000/v2.0",
                "99999999-0000-4000-8000-000000000000",
                [Tenant]
            )
        );
        Assert.False(
            MicrosoftAccount.IsValidIssuer($"https://evil.example/{Tenant}/v2.0", Tenant, [Tenant])
        );
        Assert.False(
            MicrosoftAccount.IsValidIssuer(
                $"https://login.microsoftonline.com/{Tenant}/v2.0",
                Tenant,
                []
            )
        );
    }

    [Fact]
    public void An_empty_tenant_list_switches_microsoft_off()
    {
        Assert.False(new MicrosoftCredentials { ClientId = "x", ClientSecret = "y" }.IsConfigured);
        Assert.True(
            new MicrosoftCredentials
            {
                ClientId = "x",
                ClientSecret = "y",
                AllowedTenantIds = [Tenant],
            }.IsConfigured
        );
        Assert.False(new ProviderCredentials { ClientId = "x" }.IsConfigured);
    }

    [Fact]
    public void Session_limits_are_validated()
    {
        static IEnumerable<string> Errors(SsoSessionOptions o) =>
            o.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(o))
                .Select(r => r.ErrorMessage!);
        Assert.Empty(Errors(new SsoSessionOptions()));
        Assert.Contains(
            Errors(new SsoSessionOptions { IdleTimeout = TimeSpan.FromDays(31) }),
            e => e.Contains("cannot be longer")
        );
        Assert.Contains(
            Errors(new SsoSessionOptions { IdleTimeout = TimeSpan.Zero }),
            e => e.Contains("positive")
        );
        Assert.Contains(
            Errors(new SsoSessionOptions { SecureCookie = false }),
            e => e.Contains("__Host-")
        );
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));
}
