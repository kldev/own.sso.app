using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Sso.Api.Auth;

namespace Sso.Tests;

/// <summary>
/// The REST API as a whole (ADR 0002, 0008): its OpenAPI document (only the panel's modules, the committed client's
/// source up to date), sign-in required, the actor read from the database on every call.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact, Trait("verify", "web")]
    public async Task Openapi_document_describes_the_admin_modules_and_nothing_of_the_protocol()
    {
        var doc = JsonNode.Parse(await api.CreateClient().GetStringAsync("/openapi/v1.json", Ct))!;
        Assert.Equal("Great SSO API", doc["info"]!["title"]!.GetValue<string>());
        var paths = doc["paths"]!.AsObject();

        Assert.True(paths.ContainsKey("/api/platform/me"));
        Assert.True(paths.ContainsKey("/api/people/people"));
        Assert.True(paths.ContainsKey("/api/people/person-create"));
        Assert.True(paths.ContainsKey("/api/clients/clients"));
        Assert.True(paths.ContainsKey("/api/activity/events"));
        Assert.True(paths.ContainsKey("/api/activity/sessions"));
        Assert.True(paths.ContainsKey("/api/marketing/templates"));
        Assert.True(paths.ContainsKey("/api/marketing/mailing-create"));
        // The sender's queue is no endpoint.
        Assert.DoesNotContain(
            paths,
            p => p.Key.Contains("outgoing") || p.Key.Contains("recipient-")
        );
        // The protocol (/connect, /auth) is not part of the panel's API.
        Assert.DoesNotContain(
            paths,
            p =>
                p.Key.Contains("sign-in")
                || p.Key.Contains("session-touch")
                || p.Key.Contains("code-")
        );
        Assert.DoesNotContain(
            paths,
            p =>
                p.Key.StartsWith("/connect")
                || p.Key.StartsWith("/auth")
                || p.Key.StartsWith("/mcp")
        );
    }

    [Fact, Trait("verify", "web")]
    public async Task The_committed_client_source_is_the_live_document()
    {
        // apps/Sso.ApiClient/openapi.json is what the Blazor client is generated from (generate.sh): an endpoint changed
        // without regenerating it fails here.
        var live = JsonNode.Parse(await api.CreateClient().GetStringAsync("/openapi/v1.json", Ct))!;
        var committed = JsonNode.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(RepositoryRoot(), "apps", "Sso.ApiClient", "openapi.json"),
                Ct
            )
        )!;
        Assert.True(
            JsonNode.DeepEquals(live["paths"], committed["paths"])
                && JsonNode.DeepEquals(live["components"], committed["components"]),
            "apps/Sso.ApiClient/openapi.json is out of date - run apps/Sso.ApiClient/generate.sh"
        );
    }

    private static string RepositoryRoot()
    {
        for (
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            dir is not null;
            dir = dir.Parent
        )
            if (File.Exists(Path.Combine(dir.FullName, "Sso.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Sso.slnx not found above the test output.");
    }

    [Fact, Trait("verify", "platform")]
    public async Task Api_endpoints_need_a_session_and_an_unknown_cookie_is_none()
    {
        var browser = api.Browser();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await browser.GetAsync("/api/platform/me", Ct)).StatusCode
        );

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/platform/me");
        request.Headers.Add("Cookie", $"{ApiFixture.SessionCookie}={Secrets.NewToken()}");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await browser.SendAsync(request, Ct)).StatusCode
        );
    }

    [Fact, Trait("verify", "platform")]
    public async Task The_circuit_bearer_token_reaches_the_api_as_the_same_actor()
    {
        // What the Blazor circuit does: the generated client sends the session's access token as a bearer.
        var token = api
            .Services.GetRequiredService<TokenIssuer>()
            .Issue(
                new SessionUser(
                    Guid.CreateVersion7(),
                    ApiFixture.AdminId,
                    ApiFixture.Admin,
                    "test.admin@test.example",
                    "Test Admin",
                    "admin",
                    "github",
                    DateTimeOffset.UtcNow
                )
            )
            .Token;
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var me = Assert.Single(await client.ListAsync("/api/platform/me"));
        Assert.Equal(ApiFixture.Admin, me.Str("username"));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token + "x"
        );
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/platform/me", Ct)).StatusCode
        );
    }

    [Fact, Trait("verify", "platform")]
    public async Task Me_is_the_signed_in_person()
    {
        var admin = await api.AsAsync(ApiFixture.Admin);
        var row = Assert.Single(await admin.ListAsync("/api/platform/me"));
        Assert.Equal(ApiFixture.Admin, row.Str("username"));
        Assert.Equal("test.admin@test.example", row.Str("email"));
        Assert.Equal("admin", row.Str("role"));
    }

    [Fact, Trait("verify", "people")]
    public async Task A_member_signs_in_but_sees_nothing_of_the_panel()
    {
        var member = await api.AsAsync(await api.AddPersonAsync());
        Assert.Single(await member.ListAsync("/api/platform/me"));
        Assert.Empty(await member.ListAsync("/api/people/people"));
        Assert.Empty(await member.ListAsync("/api/clients/clients"));
        Assert.Empty(await member.ListAsync("/api/activity/events"));
        await member.AssertProblemAsync(
            "/api/people/person-create",
            new
            {
                id = Guid.CreateVersion7(),
                username = "x.member",
                email = "x@test.example",
                displayName = "X",
                role = "member",
                githubEmail = "x@github.example",
            },
            HttpStatusCode.Forbidden,
            "people.forbidden"
        );
        var page = await member.GetAsync("/admin", Ct);
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/access-denied", page.Headers.Location!.OriginalString);
    }

    [Fact, Trait("verify", "platform")]
    public async Task An_inactive_actors_bearer_is_refused_and_a_missing_role_is_forbidden()
    {
        var username = await api.AddPersonAsync(role: "admin");
        var admin = await api.AsAsync(username);
        Assert.Single(await admin.ListAsync("/api/platform/me"));

        // The bearer token outlives a deactivation by up to its lifetime; the API reads the person on every call.
        var id = await api.ScalarAsync<Guid>(
            $"SELECT id FROM identity.users WHERE username = '{username}'"
        );
        var token = api
            .Services.GetRequiredService<TokenIssuer>()
            .Issue(
                new SessionUser(
                    Guid.CreateVersion7(),
                    id,
                    username,
                    $"{username}@test.example",
                    "Someone",
                    "admin",
                    "github",
                    DateTimeOffset.UtcNow
                )
            )
            .Token;
        await api.ExecuteAsync($"UPDATE identity.users SET status = 'inactive' WHERE id = '{id}'");
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var refused = await client.GetAsync("/api/people/people", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(
            "actor.inactive",
            JsonNode.Parse(await refused.Content.ReadAsStringAsync(Ct))!["code"]!.GetValue<string>()
        );
    }
}
