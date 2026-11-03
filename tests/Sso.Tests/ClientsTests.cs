using System.Net;
using System.Text.Json.Nodes;
using Sso.Api.Auth;

namespace Sso.Tests;

/// <summary>The applications' registry: exact redirect URIs, providers, a secret known only by its hash.</summary>
[Trait("verify", "clients")]
[Collection(ApiCollection.Name)]
public sealed class ClientsTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Client(
        Guid? id = null,
        string? clientId = null,
        string[]? uris = null,
        string[]? providers = null,
        string? secretHash = null,
        string name = "Handbook"
    ) =>
        new
        {
            id = id ?? Guid.CreateVersion7(),
            clientId = clientId ?? $"app-{Guid.NewGuid():N}"[..16],
            name,
            redirectUris = uris
                ?? ["https://handbook.example/signin-oidc", "http://localhost:5900/signin-oidc"],
            providers = providers ?? ["github", "google"],
            secretHash = secretHash ?? Secrets.HashText(Secrets.NewToken()),
        };

    [Fact]
    public async Task The_admin_registers_an_application_and_the_list_never_shows_the_secret()
    {
        var admin = await api.AsAsync(ApiFixture.Admin);
        var body = Client(clientId: "Handbook-Main");
        var created = await admin.CallAsync("/api/clients/client-create", body);
        Assert.Equal(
            created.RowVersion(),
            (await admin.CallAsync("/api/clients/client-create", body)).RowVersion()
        );

        var response = await admin.GetAsync("/api/clients/clients?clientId=handbook-main", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(
            "secret",
            text.Replace("secretSetAt", ""),
            StringComparison.OrdinalIgnoreCase
        );
        var row = Assert.Single(JsonNode.Parse(text)!["items"]!.AsArray());
        Assert.Equal("[\"github\",\"google\"]", row!["providers"]!.ToJsonString());
        Assert.True(row["requirePkce"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Every_rule_has_its_code()
    {
        var admin = await api.AsAsync(ApiFixture.Admin);
        var bad = HttpStatusCode.BadRequest;
        await admin.AssertProblemAsync(
            "/api/clients/client-create",
            Client(clientId: "x"),
            bad,
            "clients.client_id_invalid"
        );
        await admin.AssertProblemAsync(
            "/api/clients/client-create",
            Client(name: ""),
            bad,
            "clients.name_invalid"
        );
        foreach (
            var uri in new[]
            {
                "http://handbook.example/cb",
                "https://handbook.example/cb#x",
                "/relative",
                "javascript:alert(1)",
            }
        )
            await admin.AssertProblemAsync(
                "/api/clients/client-create",
                Client(uris: [uri]),
                bad,
                "clients.redirect_uris_invalid"
            );
        await admin.AssertProblemAsync(
            "/api/clients/client-create",
            Client(uris: []),
            bad,
            "clients.redirect_uris_invalid"
        );
        await admin.AssertProblemAsync(
            "/api/clients/client-create",
            Client(providers: ["facebook"]),
            bad,
            "clients.providers_invalid"
        );
        await admin.AssertProblemAsync(
            "/api/clients/client-create",
            Client(secretHash: "plain-secret"),
            bad,
            "clients.secret_hash_invalid"
        );

        var body = Client();
        await admin.CallAsync("/api/clients/client-create", body);
        var clientId = (string)body.GetType().GetProperty("clientId")!.GetValue(body)!;
        await admin.AssertProblemAsync(
            "/api/clients/client-create",
            Client(clientId: clientId),
            HttpStatusCode.Conflict,
            "clients.client_id_taken"
        );
        await admin.AssertProblemAsync(
            "/api/clients/client-create",
            Client(id: (Guid)body.GetType().GetProperty("id")!.GetValue(body)!),
            HttpStatusCode.Conflict,
            "clients.id_taken"
        );

        var member = await api.AsAsync(await api.AddPersonAsync());
        await member.AssertProblemAsync(
            "/api/clients/client-create",
            Client(),
            HttpStatusCode.Forbidden,
            "clients.forbidden"
        );
    }

    [Fact]
    public async Task A_new_secret_replaces_the_old_one_and_switching_off_stops_the_application()
    {
        var admin = await api.AsAsync(ApiFixture.Admin);
        var oldSecret = Secrets.NewToken();
        var body = Client(secretHash: Secrets.HashText(oldSecret));
        var id = (Guid)body.GetType().GetProperty("id")!.GetValue(body)!;
        var clientId = (string)body.GetType().GetProperty("clientId")!.GetValue(body)!;
        var version = (await admin.CallAsync("/api/clients/client-create", body)).RowVersion();

        var newSecret = Secrets.NewToken();
        version = (
            await admin.CallAsync(
                "/api/clients/client-set-secret",
                new
                {
                    id,
                    secretHash = Secrets.HashText(newSecret),
                    rowVersion = version,
                }
            )
        ).RowVersion();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Token(clientId, oldSecret)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Token(clientId, newSecret)).StatusCode); // authenticated, then no code

        await admin.AssertProblemAsync(
            "/api/clients/client-update",
            new
            {
                id,
                name = "X",
                redirectUris = new[] { "https://a.example/cb" },
                providers = new[] { "github" },
                rowVersion = version - 1,
            },
            HttpStatusCode.Conflict,
            "concurrency"
        );
        await admin.CallAsync(
            "/api/clients/client-set-status",
            new
            {
                id,
                status = "inactive",
                rowVersion = version,
            }
        );
        Assert.Equal(HttpStatusCode.Unauthorized, (await Token(clientId, newSecret)).StatusCode);
    }

    private Task<HttpResponseMessage> Token(string clientId, string secret) =>
        api.Browser()
            .PostAsync(
                "/connect/token",
                new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        ["grant_type"] = "authorization_code",
                        ["code"] = "nope",
                        ["redirect_uri"] = "https://handbook.example/signin-oidc",
                        ["client_id"] = clientId,
                        ["client_secret"] = secret,
                    }
                ),
                Ct
            );
}
