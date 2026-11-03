using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sso.Api.Auth;

namespace Sso.Tests;

/// <summary>
/// /mcp for Claude Code (ADR 0013): an SSO admin's own token, every panel operation as a tool, the same rules as the
/// API, every call logged.
/// </summary>
[Trait("verify", "mcp")]
[Collection(ApiCollection.Name)]
public sealed class McpTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_admins_token_lists_every_panel_operation_as_a_tool()
    {
        var token = await IssueAsync(await api.AsAsync(ApiFixture.Admin));
        var names = (await ResultAsync(await CallAsync(token, "tools/list")))
            .GetProperty("tools")
            .EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        foreach (
            var tool in new[]
            {
                "people_list",
                "person_add",
                "person_update",
                "person_set_status",
                "person_unlink",
                "clients_list",
                "client_register",
                "client_update",
                "client_new_secret",
                "client_set_status",
                "events_list",
                "sessions_list",
                "session_end",
                "mail_templates_list",
                "mail_template_update",
                "mail_send",
                "mailings_list",
                "mailing_addresses",
                "mailing_retry",
            }
        )
            Assert.Contains(tool, names);
        Assert.DoesNotContain(names, n => n!.Contains("token"));
    }

    [Fact]
    public async Task The_agent_sends_a_template_to_a_list_and_each_address_gets_its_own_email()
    {
        var token = await IssueAsync(await api.AsAsync(ApiFixture.Admin));
        var addresses = new[]
        {
            $"mcp.{Guid.NewGuid():N}"[..14] + "@garage.example",
            $"mcp.{Guid.NewGuid():N}"[..14] + "@garage.example",
        };
        var sent = await ToolAsync(
            token,
            "mail_send",
            new { template = "test-a-en", emails = addresses }
        );
        Assert.Equal(2, sent.GetProperty("queued").GetInt32());
        var mailingId = sent.GetProperty("mailingId").GetString();

        for (var i = 0; i < 100 && addresses.Any(a => api.Mail.To(a).Count == 0); i++)
            await Task.Delay(100, Ct);
        foreach (var address in addresses)
            Assert.Contains("https://sso.example.com", Assert.Single(api.Mail.To(address)).Body);

        var rows = await ToolAsync(token, "mailing_addresses", new { mailingId });
        Assert.Equal(2, rows.GetArrayLength());
    }

    [Fact]
    public async Task The_agent_enters_a_person_and_registers_an_application_and_every_call_is_logged()
    {
        var token = await IssueAsync(await api.AsAsync(ApiFixture.Admin));
        var username = $"mcp.{Guid.NewGuid():N}"[..14];
        var added = await ToolAsync(
            token,
            "person_add",
            new
            {
                username,
                email = $"{username}@test.example",
                displayName = "Agent Made",
                githubEmail = $"{username}@github.example",
            }
        );
        Assert.Equal(username, added.GetProperty("username").GetString());

        var updated = await ToolAsync(
            token,
            "person_update",
            new
            {
                username,
                displayName = "Agent Renamed",
                googleEmail = $"{username}@gmail.example",
            }
        );
        Assert.Equal("Agent Renamed", updated.GetProperty("displayName").GetString());
        Assert.Equal($"{username}@github.example", updated.GetProperty("githubEmail").GetString());

        var clientId = $"mcp-{Guid.NewGuid():N}"[..14];
        var registered = await ToolAsync(
            token,
            "client_register",
            new
            {
                clientId,
                name = "Agent app",
                redirectUris = new[] { "https://agent.example/signin-oidc" },
                providers = new[] { "github" },
            }
        );
        var secret = registered.GetProperty("clientSecret").GetString()!;
        Assert.Equal(
            Secrets.HashText(secret),
            await api.ScalarAsync<string>(
                $"SELECT secret_hash::text FROM oidc.clients WHERE client_id = '{clientId}'"
            )
        );

        Assert.True(
            await api.ScalarAsync<long>(
                $"SELECT count(*) FROM oidc.events WHERE event_type = 'mcp_tool' AND user_id = '{ApiFixture.AdminId}' AND detail->>'tool' IN ('person_add', 'person_update', 'client_register')"
            ) >= 3
        );
        // The change itself is the admin's (the actor), as from the panel.
        Assert.Equal(
            ApiFixture.AdminId.ToString(),
            await api.ScalarAsync<string>(
                $"SELECT actor_id FROM audit.change_log WHERE table_name = 'users' AND new_row->>'username' = '{username}' ORDER BY id LIMIT 1"
            )
        );
        // The arguments are not logged: no secret, no e-mail in mcp_tool events.
        Assert.Equal(
            0L,
            await api.ScalarAsync<long>(
                $"SELECT count(*) FROM oidc.events WHERE event_type = 'mcp_tool' AND detail::text LIKE '%{username}%'"
            )
        );
    }

    [Fact]
    public async Task A_rule_reaches_the_agent_as_a_tool_error_with_its_sentence_and_code()
    {
        var token = await IssueAsync(await api.AsAsync(ApiFixture.Admin));
        var response = await CallAsync(
            token,
            "tools/call",
            new
            {
                name = "person_add",
                arguments = new
                {
                    username = "Bad Name",
                    email = "x@test.example",
                    displayName = "X",
                    githubEmail = "x@github.example",
                },
            }
        );
        var result = await ResultAsync(response);
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains(
            "people.username_invalid",
            result.GetProperty("content")[0].GetProperty("text").GetString()
        );

        var self = await CallAsync(
            token,
            "tools/call",
            new
            {
                name = "person_set_status",
                arguments = new { username = ApiFixture.Admin, status = "inactive" },
            }
        );
        Assert.Contains(
            "people.self_change",
            (await ResultAsync(self)).GetProperty("content")[0].GetProperty("text").GetString()
        );
    }

    [Fact]
    public async Task Without_a_valid_token_of_an_active_admin_mcp_is_closed()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(null, "tools/list")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await CallAsync("mcp_" + Secrets.NewToken(), "tools/list")).StatusCode
        );

        // The panel's session is no way into /mcp, and the MCP token is no way into the panel.
        var admin = await api.AsAsync(ApiFixture.Admin);
        var token = await IssueAsync(admin);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (
                await admin.PostAsJsonAsync(
                    "/mcp",
                    new
                    {
                        jsonrpc = "2.0",
                        id = 1,
                        method = "tools/list",
                    },
                    Ct
                )
            ).StatusCode
        );
        var panel = api.Browser();
        panel.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await panel.GetAsync("/api/platform/me", Ct)).StatusCode
        );

        // Revoked: closed at once.
        var tokenId = await api.ScalarAsync<Guid>(
            $"SELECT id FROM identity.mcp_tokens WHERE token_hash = '\\x{Hex(token)}'"
        );
        await admin.CallAsync("/api/platform/mcp-token-revoke", new { id = tokenId });
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await CallAsync(token, "tools/list")).StatusCode
        );
    }

    [Fact]
    public async Task A_demoted_admins_token_stops_and_a_member_cannot_get_one()
    {
        var username = await api.AddPersonAsync(role: "admin");
        var token = await IssueAsync(await api.AsAsync(username));
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(token, "tools/list")).StatusCode);
        await api.ExecuteAsync(
            $"UPDATE identity.users SET role = 'member' WHERE username = '{username}'"
        );
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await CallAsync(token, "tools/list")).StatusCode
        );

        var member = await api.AsAsync(await api.AddPersonAsync());
        await member.AssertProblemAsync(
            "/api/platform/mcp-token-issue",
            new
            {
                id = Guid.CreateVersion7(),
                name = "x",
                tokenHash = Hex("mcp_x"),
                validDays = 30,
            },
            HttpStatusCode.Forbidden,
            "platform.forbidden"
        );
    }

    // ---- helpers ----

    private static string Hex(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static async Task<string> IssueAsync(HttpClient admin)
    {
        var token = "mcp_" + Secrets.NewToken();
        await admin.CallAsync(
            "/api/platform/mcp-token-issue",
            new
            {
                id = Guid.CreateVersion7(),
                name = "Tests - Claude Code",
                tokenHash = Hex(token),
                validDays = 30,
            }
        );
        return token;
    }

    private async Task<HttpResponseMessage> CallAsync(
        string? token,
        string method,
        object? @params = null
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Content = JsonContent.Create(
            new
            {
                jsonrpc = "2.0",
                id = 1,
                method,
                @params = @params ?? new { },
            }
        );
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await api.Browser().SendAsync(request, Ct);
    }

    /// <summary>The JSON-RPC result, whether the server answered with JSON or with one SSE event.</summary>
    private static async Task<JsonElement> ResultAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {body}");
        var json = body.TrimStart().StartsWith('{')
            ? body
            : body.Split('\n').Last(l => l.StartsWith("data:"))["data:".Length..];
        return JsonDocument.Parse(json).RootElement.GetProperty("result").Clone();
    }

    private async Task<JsonElement> ToolAsync(string token, string name, object arguments)
    {
        var result = await ResultAsync(
            await CallAsync(token, "tools/call", new { name, arguments })
        );
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean(), text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
