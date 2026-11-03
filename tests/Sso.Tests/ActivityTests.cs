using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Sso.Api.Auth;

namespace Sso.Tests;

/// <summary>The full audit: sessions with their applications, the log, ending a session; nothing secret in the log.</summary>
[Trait("verify", "activity")]
[Collection(ApiCollection.Name)]
public sealed class ActivityTests(ApiFixture api)
{
    private const string RedirectUri = "https://audit.example/signin-oidc";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_sign_in_to_an_application_is_logged_step_by_step_without_any_secret_value()
    {
        var clientId = $"audit-{Guid.NewGuid():N}"[..18];
        var secret = Secrets.NewToken();
        await api.ExecuteAsync(
            $"INSERT INTO oidc.clients (id, client_id, name, secret_hash, redirect_uris, providers, require_pkce) VALUES (uuidv7(), '{clientId}', 'Audit', '{Secrets.HashText(secret)}', ARRAY['{RedirectUri}'], ARRAY['github'], true)"
        );
        var username = await api.AddPersonAsync();
        var (browser, sessionId) = await api.SessionAsync(username);

        var verifier = Secrets.NewToken() + "abcdef";
        var authorize = await browser.GetAsync(
            $"/connect/authorize?client_id={clientId}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&response_type=code&scope=openid%20offline_access&state=s&code_challenge={Secrets.S256(verifier)}&code_challenge_method=S256",
            Ct
        );
        var code = Microsoft
            .AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)[
                "code"
            ]
            .ToString();
        var tokens = (
            await (
                await api.Browser()
                    .PostAsync(
                        "/connect/token",
                        new FormUrlEncodedContent(
                            new Dictionary<string, string>
                            {
                                ["grant_type"] = "authorization_code",
                                ["code"] = code,
                                ["redirect_uri"] = RedirectUri,
                                ["code_verifier"] = verifier,
                                ["client_id"] = clientId,
                                ["client_secret"] = secret,
                            }
                        ),
                        Ct
                    )
            ).Content.ReadFromJsonAsync<JsonObject>(Ct)
        )!;

        var admin = await api.AsAsync(ApiFixture.Admin);
        var events = await admin.ListAsync($"/api/activity/events?clientId={clientId}&sort=id");
        Assert.Equal(["code_issued", "tokens_issued"], events.Select(e => e.Str("eventType")));
        var session = Assert.Single(
            await admin.ListAsync($"/api/activity/sessions?id={sessionId}")
        );
        Assert.Equal($"[\"{clientId}\"]", session!["clientIds"]!.ToJsonString());
        Assert.Equal("active", session.Str("state"));

        // No value that grants access is in the log - not the code, the tokens, the verifier or the secret.
        foreach (
            var value in new[]
            {
                code,
                verifier,
                secret,
                tokens.Str("access_token"),
                tokens.Str("refresh_token"),
                tokens.Str("id_token"),
            }
        )
            Assert.Equal(
                0L,
                await api.ScalarAsync<long>(
                    $"SELECT count(*) FROM oidc.events WHERE concat_ws(' ', reason, detail::text, user_agent, client_id) LIKE '%{value}%'"
                )
            );
        Assert.Equal(
            0L,
            await api.ScalarAsync<long>(
                $"SELECT count(*) FROM audit.change_log WHERE concat_ws(' ', old_row::text, new_row::text) LIKE '%{Secrets.HashText(secret)}%'"
            )
        );
    }

    [Fact]
    public async Task The_admin_ends_a_session_and_it_stops_at_once()
    {
        var (browser, sessionId) = await api.SessionAsync(await api.AddPersonAsync(role: "admin"));
        Assert.Equal(
            HttpStatusCode.OK,
            (await browser.GetAsync("/api/platform/me", Ct)).StatusCode
        );

        var admin = await api.AsAsync(ApiFixture.Admin);
        await admin.CallAsync("/api/activity/session-end", new { id = sessionId });
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await browser.GetAsync("/api/platform/me", Ct)).StatusCode
        );
        var row = Assert.Single(await admin.ListAsync($"/api/activity/sessions?id={sessionId}"));
        Assert.Equal("ended", row.Str("state"));
        Assert.Equal("admin", row.Str("endReason"));
        var ended = Assert.Single(
            await admin.ListAsync(
                $"/api/activity/events?sessionId={sessionId}&eventType=session_ended"
            )
        );
        Assert.Equal(ApiFixture.AdminId.ToString(), ended.Str("actorId"));
        await admin.AssertProblemAsync(
            "/api/activity/session-end",
            new { id = Guid.CreateVersion7() },
            HttpStatusCode.NotFound,
            "activity.session_not_found"
        );
    }

    [Fact]
    public async Task The_host_appends_to_the_log_and_reads_it_but_never_changes_it()
    {
        Assert.True(
            await api.ScalarAsync<bool>(
                "SELECT has_table_privilege('sso_api', 'oidc.events', 'INSERT') AND has_table_privilege('sso_api', 'oidc.events', 'SELECT')"
            )
        );
        foreach (var table in new[] { "oidc.events", "audit.change_log" })
            Assert.False(
                await api.ScalarAsync<bool>(
                    $"SELECT has_table_privilege('sso_api', '{table}', 'UPDATE') OR has_table_privilege('sso_api', '{table}', 'DELETE') OR has_table_privilege('sso_api', '{table}', 'TRUNCATE')"
                )
            );
        Assert.False(
            await api.ScalarAsync<bool>(
                "SELECT has_table_privilege('sso_runner', 'oidc.events', 'SELECT')"
            )
        );
    }
}
