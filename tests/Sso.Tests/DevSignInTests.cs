using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Sso.Tests;

/// <summary>
/// The development sign-in stub (ADR 0018): it stands in for the providers in Development only, signs in through the
/// same decision as a real provider, and does not exist anywhere else - its flag stops a non-Development start.
/// </summary>
[Trait("verify", "sign-in")]
[Collection(ApiCollection.Name)]
public sealed class DevSignInTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_the_flag_the_stub_does_not_exist()
    {
        // Nobody signed in: the path is no form, only the sign-in redirect every unknown page gets.
        var anonymous = await api.Browser().GetAsync("/dev/sign-in/github", Ct);
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.StartsWith("/sign-in", anonymous.Headers.Location!.OriginalString);
        // Signed in: not found; and the provider's start goes to the real provider, not the stub.
        var admin = await api.AsAsync(ApiFixture.Admin);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.GetAsync("/dev/sign-in/github", Ct)).StatusCode
        );
        var start = await api.Browser().GetAsync("/auth/external/github?returnUrl=%2Fadmin", Ct);
        Assert.StartsWith("https://github.com/", start.Headers.Location!.OriginalString);
    }

    [Fact]
    public void The_flag_outside_development_stops_the_start()
    {
        using var production = api.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("DevSignIn:Enabled", "true");
        });
        var error = Assert.ThrowsAny<Exception>(production.CreateClient);
        Assert.Contains("DevSignIn", error.ToString());
    }

    [Fact]
    public async Task The_stub_signs_in_an_entered_person_and_refuses_anybody_else()
    {
        await using var development = api.WithWebHostBuilder(b =>
            b.UseSetting("DevSignIn:Enabled", "true")
        );
        var browser = development.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
            }
        );
        var username = await api.AddPersonAsync();
        var email = $"{username}@github.example";

        var start = await browser.GetAsync("/auth/external/github?returnUrl=%2Fadmin", Ct);
        Assert.StartsWith("/dev/sign-in/github", start.Headers.Location!.OriginalString);
        Assert.Contains(
            "development sign-in",
            await browser.GetStringAsync("/dev/sign-in/github", Ct)
        );

        var post = await browser.PostAsync(
            "/dev/sign-in/github",
            new FormUrlEncodedContent(
                new Dictionary<string, string> { ["email"] = email, ["returnUrl"] = "/admin" }
            ),
            Ct
        );
        Assert.Equal("/auth/external/github/done", post.Headers.Location!.OriginalString);
        var done = await browser.GetAsync("/auth/external/github/done", Ct);
        Assert.Equal("/admin", done.Headers.Location!.OriginalString);

        var stranger = development.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
            }
        );
        await stranger.PostAsync(
            "/dev/sign-in/github",
            new FormUrlEncodedContent(
                new Dictionary<string, string> { ["email"] = "nobody@example.com" }
            ),
            Ct
        );
        var refused = await stranger.GetAsync("/auth/external/github/done", Ct);
        Assert.StartsWith("/sign-in/refused", refused.Headers.Location!.OriginalString);
    }
}
