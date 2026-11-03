using System.Net;

namespace Sso.Tests;

/// <summary>The one host serves the Blazor front next to the API (ADR 0002): what is public and what is not.</summary>
[Trait("verify", "web")]
[Collection(ApiCollection.Name)]
public sealed class WebHostTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_sign_in_page_and_its_assets_are_public_the_rest_redirects_to_it()
    {
        var browser = api.Browser();

        var signIn = await browser.GetAsync("/sign-in", Ct);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/css/app.css", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/img/logo.svg", Ct)).StatusCode);
        // The framework script: public, and present at all (RequiresAspNetWebAssets in Sso.Api.csproj).
        Assert.Equal(
            HttpStatusCode.OK,
            (await browser.GetAsync("/_framework/blazor.web.js", Ct)).StatusCode
        );

        var root = await browser.GetAsync("/", Ct);
        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
        Assert.Equal("/admin", root.Headers.Location!.ToString());

        var people = await browser.GetAsync("/admin/people", Ct);
        Assert.Equal(HttpStatusCode.Redirect, people.StatusCode);
        Assert.Equal("/sign-in?returnUrl=%2Fadmin%2Fpeople", people.Headers.Location!.ToString());

        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/scalar/", Ct)).StatusCode);
    }

    [Fact]
    public async Task There_is_no_password_no_demo_no_registration_and_no_sign_out()
    {
        var browser = api.Browser();
        var page = await browser.GetStringAsync("/sign-in", Ct);
        // Whose service it is, on the page itself (a visitor and a phishing filter both see it).
        Assert.Contains("data-testid=\"operator\"", page);
        Assert.Contains("sign-in service of", page);
        Assert.Contains("never asks for a password", page);
        Assert.DoesNotContain("type=\"password\"", page);
        Assert.DoesNotContain("name=\"username\"", page);
        Assert.DoesNotContain("sign-up", page, StringComparison.OrdinalIgnoreCase);

        foreach (var path in new[] { "/auth/login", "/auth/demo-login", "/auth/logout" })
        {
            var response = await browser.PostAsync(path, null, Ct);
            Assert.True(
                response.StatusCode == HttpStatusCode.NotFound,
                $"{path}: {(int)response.StatusCode} {response.Headers.Location}"
            );
        }
        // No such page at all: a signed-in admin gets 404 (an anonymous browser is sent to the sign-in page first).
        var admin = await api.AsAsync(ApiFixture.Admin);
        foreach (var path in new[] { "/sign-up", "/register" })
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(path, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_signed_in_admin_gets_the_panel_and_scalar()
    {
        var admin = await api.AsAsync(ApiFixture.Admin);
        foreach (
            var path in new[]
            {
                "/admin",
                "/admin/people",
                "/admin/applications",
                "/admin/activity",
                "/admin/marketing/mail",
                "/scalar/",
            }
        )
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(path, Ct)).StatusCode);

        // A member is signed in, but the panel is not theirs.
        var member = await api.AsAsync(await api.AddPersonAsync());
        foreach (
            var path in new[]
            {
                "/admin",
                "/admin/people",
                "/admin/applications",
                "/admin/activity",
                "/admin/marketing/mail",
            }
        )
        {
            var page = await member.GetAsync(path, Ct);
            Assert.Equal("/access-denied", page.Headers.Location?.OriginalString);
        }
        var denied = WebUtility.HtmlDecode(await member.GetStringAsync("/access-denied", Ct));
        Assert.Contains("You are signed in to Great SSO", denied);
    }

    [Fact]
    public async Task Writes_from_another_site_are_refused()
    {
        var admin = await api.AsAsync(ApiFixture.Admin);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/people/person-unlink");
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.SendAsync(request, Ct)).StatusCode);
    }

    [Fact]
    public async Task Liveness_is_public_and_an_unknown_api_path_stays_404()
    {
        Assert.Equal(HttpStatusCode.OK, (await api.Browser().GetAsync("/health", Ct)).StatusCode);
        var admin = await api.AsAsync(ApiFixture.Admin);
        var unknown = await admin.GetAsync("/api/nothing/here", Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.DoesNotContain("<html", await unknown.Content.ReadAsStringAsync(Ct));
    }
}
