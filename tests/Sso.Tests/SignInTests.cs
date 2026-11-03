using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sso.Api.Auth.Providers;

namespace Sso.Tests;

/// <summary>
/// Signing in through a provider, after the provider's handler (the external cookie): the real decision of
/// identity.external_sign_in and the session start. No registration: an unknown person changes nothing in the database.
/// </summary>
[Trait("verify", "sign-in")]
[Collection(ApiCollection.Name)]
public sealed class SignInTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewSubject() => Random.Shared.NextInt64(1, long.MaxValue).ToString();

    [Fact]
    public async Task The_page_shows_the_configured_providers_and_links_to_them()
    {
        var page = await api.Browser().GetStringAsync("/sign-in?returnUrl=%2Fadmin%2Fpeople", Ct);
        foreach (var provider in new[] { "github", "google", "microsoft" })
            Assert.Contains($"href=\"auth/external/{provider}?returnUrl=%2Fadmin%2Fpeople\"", page);
        Assert.Contains("Ask your SSO admin to add one", page);
    }

    [Fact]
    public async Task First_sign_in_links_by_the_verified_email_later_ones_by_the_providers_id()
    {
        var username = await api.AddPersonAsync(role: "admin", github: "first.link@github.example");
        var subject = NewSubject();
        var browser = api.Browser();

        var first = await api.ExternalSignInAsync(
            browser,
            new ExternalAccount("github", subject, null, "First.Link@GitHub.example", true),
            "/admin/people"
        );
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal("/admin/people", first.Headers.Location!.OriginalString);
        var cookie = Assert.Single(
            first.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith(ApiFixture.SessionCookie + "=")
        );
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            subject,
            await api.ScalarAsync<string>(
                $"SELECT l.subject::text FROM identity.external_logins AS l JOIN identity.users AS u ON u.id = l.user_id WHERE u.username = '{username}' AND l.provider = 'github'"
            )
        );

        // The e-mail at GitHub changed since: the id still signs in.
        var later = await api.ExternalSignInAsync(
            browser,
            new ExternalAccount("github", subject, null, "renamed@github.example", true)
        );
        Assert.Equal(HttpStatusCode.Redirect, later.StatusCode);
        Assert.Equal("/admin", later.Headers.Location!.OriginalString);
        Assert.Equal(
            2L,
            await api.ScalarAsync<long>(
                $"SELECT count(*) FROM oidc.events AS e JOIN identity.users AS u ON u.id = e.user_id WHERE u.username = '{username}' AND e.event_type = 'session_started'"
            )
        );
    }

    [Fact]
    public async Task Two_parallel_first_sign_ins_of_one_account_link_it_once()
    {
        var username = await api.AddPersonAsync(
            github: $"twice.{Guid.NewGuid():N}"[..16] + "@github.example"
        );
        var email = await api.ScalarAsync<string>(
            $"SELECT github_email::text FROM identity.users WHERE username = '{username}'"
        );
        var subject = NewSubject();
        var answers = await Task.WhenAll(
            api.ExternalSignInAsync(
                api.Browser(),
                new ExternalAccount("github", subject, null, email, true)
            ),
            api.ExternalSignInAsync(
                api.Browser(),
                new ExternalAccount("github", subject, null, email, true)
            )
        );
        // Both sign in (the second by the link the first made); the account is linked once.
        Assert.All(answers, a => Assert.Equal("/admin", a.Headers.Location!.OriginalString));
        Assert.Equal(
            1L,
            await api.ScalarAsync<long>(
                $"SELECT count(*) FROM identity.external_logins WHERE provider = 'github' AND subject = '{subject}'"
            )
        );
    }

    [Fact]
    public async Task An_unknown_person_is_refused_and_nothing_is_created()
    {
        var people = await api.ScalarAsync<long>("SELECT count(*) FROM identity.users");
        var links = await api.ScalarAsync<long>("SELECT count(*) FROM identity.external_logins");
        var sessions = await api.ScalarAsync<long>("SELECT count(*) FROM oidc.sessions");

        var response = await api.ExternalSignInAsync(
            api.Browser(),
            new ExternalAccount("google", NewSubject(), null, "stranger@gmail.example", true)
        );

        await AssertRefusedAsync(
            response,
            "no_entry",
            "There is no Great SSO entry for this account yet",
            "stranger@gmail.example"
        );
        Assert.DoesNotContain(
            response.Headers.TryGetValues("Set-Cookie", out var set) ? set : [],
            c => c.StartsWith(ApiFixture.SessionCookie)
        );
        Assert.Equal(people, await api.ScalarAsync<long>("SELECT count(*) FROM identity.users"));
        Assert.Equal(
            links,
            await api.ScalarAsync<long>("SELECT count(*) FROM identity.external_logins")
        );
        Assert.Equal(sessions, await api.ScalarAsync<long>("SELECT count(*) FROM oidc.sessions"));
        // Logged with the domain only - the stranger's address is not kept.
        Assert.Equal(
            "gmail.example",
            await api.ScalarAsync<string>(
                "SELECT detail->>'email_domain' FROM oidc.events WHERE event_type = 'sign_in' AND reason = 'no_entry' ORDER BY id DESC LIMIT 1"
            )
        );
    }

    [Fact]
    public async Task An_unverified_email_and_an_inactive_person_are_refused()
    {
        await api.AddPersonAsync(google: "not.verified@gmail.example");
        await AssertRefusedAsync(
            await api.ExternalSignInAsync(
                api.Browser(),
                new ExternalAccount(
                    "google",
                    NewSubject(),
                    null,
                    "not.verified@gmail.example",
                    false
                )
            ),
            "unverified",
            "This e-mail address is not verified",
            "not.verified@gmail.example"
        );

        await api.AddPersonAsync(github: "gone@github.example", status: "inactive");
        await AssertRefusedAsync(
            await api.ExternalSignInAsync(
                api.Browser(),
                new ExternalAccount("github", NewSubject(), null, "gone@github.example", true)
            ),
            "inactive",
            "Your Great SSO access is switched off",
            "gone@github.example"
        );
    }

    [Fact]
    public async Task One_account_per_provider_another_github_account_is_refused()
    {
        await api.AddPersonAsync(github: "one.account@github.example");
        var first = await api.ExternalSignInAsync(
            api.Browser(),
            new ExternalAccount("github", NewSubject(), null, "one.account@github.example", true)
        );
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal("/admin", first.Headers.Location!.OriginalString);

        // Another GitHub account with the same e-mail: not linked over the first one.
        await AssertRefusedAsync(
            await api.ExternalSignInAsync(
                api.Browser(),
                new ExternalAccount(
                    "github",
                    NewSubject(),
                    null,
                    "one.account@github.example",
                    true
                )
            ),
            "other_account",
            "This is not the account linked to you",
            "one.account@github.example"
        );
    }

    [Fact]
    public async Task Microsoft_personal_accounts_and_other_organisations_are_refused_work_accounts_link_by_tid_and_oid()
    {
        await api.AddPersonAsync(microsoft: "worker@org.example");
        var oid = Guid.NewGuid().ToString();

        await AssertRefusedAsync(
            await api.ExternalSignInAsync(
                api.Browser(),
                new ExternalAccount(
                    "microsoft",
                    $"{MicrosoftAccount.ConsumersTenantId}:{oid}",
                    MicrosoftAccount.ConsumersTenantId,
                    "worker@org.example",
                    false,
                    "personal_account"
                )
            ),
            "personal_account",
            "Use your work Microsoft account",
            null
        );
        await AssertRefusedAsync(
            await api.ExternalSignInAsync(
                api.Browser(),
                new ExternalAccount(
                    "microsoft",
                    $"99999999-0000-4000-8000-000000000000:{oid}",
                    "99999999-0000-4000-8000-000000000000",
                    "worker@org.example",
                    false,
                    "tenant_not_allowed"
                )
            ),
            "tenant_not_allowed",
            "Your organisation is not on the list",
            null
        );

        var ok = await api.ExternalSignInAsync(
            api.Browser(),
            new ExternalAccount(
                "microsoft",
                $"{ApiFixture.AllowedTenant}:{oid}",
                ApiFixture.AllowedTenant,
                "worker@org.example",
                true
            )
        );
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal("/admin", ok.Headers.Location!.OriginalString);
        Assert.Equal(
            ApiFixture.AllowedTenant,
            await api.ScalarAsync<string>(
                $"SELECT tenant_id::text FROM identity.external_logins WHERE subject = '{ApiFixture.AllowedTenant}:{oid}'"
            )
        );
    }

    [Fact]
    public async Task A_provider_that_is_not_configured_or_unknown_is_404()
    {
        var browser = api.Browser();
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await browser.GetAsync("/auth/external/facebook", Ct)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await browser.GetAsync("/auth/external/facebook/done", Ct)).StatusCode
        );
    }

    [Fact]
    public async Task Starting_a_sign_in_goes_to_the_provider_and_keeps_only_a_local_return_url()
    {
        var response = await api.Browser()
            .GetAsync("/auth/external/github?returnUrl=https%3A%2F%2Fevil.example", Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("https://github.com/login/oauth/authorize?", location);
        Assert.Contains("code_challenge_method=S256", location);
        Assert.Contains("user%3Aemail", location);
    }

    [Theory]
    [InlineData("google")]
    [InlineData("microsoft")]
    public void OpenID_Connect_providers_answer_with_a_GET_that_the_cross_site_guard_lets_through(
        string scheme
    )
    {
        var options = api
            .Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(scheme);
        Assert.Equal("query", options.ResponseMode);
    }

    [Fact]
    public async Task A_form_post_from_another_site_to_a_provider_callback_is_refused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/signin/google");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        request.Headers.Add("Origin", "https://accounts.google.com");
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await api.Browser().SendAsync(request, Ct)).StatusCode
        );
    }

    private async Task AssertRefusedAsync(
        HttpResponseMessage response,
        string reason,
        string title,
        string? account
    )
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/sign-in/refused?r=", location);
        // The account's e-mail never travels in clear in the URL.
        if (account is not null)
            Assert.DoesNotContain(Uri.EscapeDataString(account), location);
        var page = await api.Browser().GetStringAsync(location, Ct);
        Assert.Contains($"data-reason=\"{reason}\"", page);
        Assert.Contains(WebUtility.HtmlEncode(title), page);
        if (account is not null)
            Assert.Contains(account, page);
    }
}
