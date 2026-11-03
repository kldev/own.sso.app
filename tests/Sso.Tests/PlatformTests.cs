using System.Net;
using System.Security.Cryptography;
using System.Text;
using Sso.Api.Auth;

namespace Sso.Tests;

/// <summary>An admin's MCP tokens in the panel: every rule, own tokens only, never the hash, revoking.</summary>
[Trait("verify", "platform")]
[Collection(ApiCollection.Name)]
public sealed class PlatformTests(ApiFixture api)
{
    private static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static object Token(
        Guid? id = null,
        string name = "Laptop - Claude Code",
        string? hash = null,
        int validDays = 30
    ) =>
        new
        {
            id = id ?? Guid.CreateVersion7(),
            name,
            tokenHash = hash ?? Hash("mcp_" + Secrets.NewToken()),
            validDays,
        };

    [Fact]
    public async Task Every_rule_of_a_token_has_its_code()
    {
        var admin = await api.AsAsync(ApiFixture.Admin);
        const string path = "/api/platform/mcp-token-issue";
        await admin.AssertProblemAsync(
            path,
            new
            {
                name = "x",
                tokenHash = Hash("x"),
                validDays = 1,
            },
            HttpStatusCode.BadRequest,
            "platform.id_missing"
        );
        await admin.AssertProblemAsync(
            path,
            Token(name: "  "),
            HttpStatusCode.BadRequest,
            "platform.token_name_invalid"
        );
        await admin.AssertProblemAsync(
            path,
            Token(hash: "abc"),
            HttpStatusCode.BadRequest,
            "platform.token_hash_invalid"
        );
        await admin.AssertProblemAsync(
            path,
            Token(validDays: 366),
            HttpStatusCode.BadRequest,
            "platform.token_validity_invalid"
        );

        var id = Guid.CreateVersion7();
        await admin.CallAsync(path, Token(id, "Same"));
        await admin.CallAsync(path, Token(id, "Same"));
        await admin.AssertProblemAsync(
            path,
            Token(id, "Other"),
            HttpStatusCode.Conflict,
            "platform.id_taken"
        );

        var member = await api.AsAsync(await api.AddPersonAsync());
        await member.AssertProblemAsync(
            path,
            Token(),
            HttpStatusCode.Forbidden,
            "platform.forbidden"
        );
        Assert.Empty(await member.ListAsync("/api/platform/mcp-tokens"));
    }

    [Fact]
    public async Task An_admin_sees_only_own_tokens_never_the_hash_and_revokes_them()
    {
        var mine = await api.AsAsync(ApiFixture.Admin);
        var other = await api.AsAsync(await api.AddPersonAsync(role: "admin"));
        var id = Guid.CreateVersion7();
        await mine.CallAsync("/api/platform/mcp-token-issue", Token(id, "Mine"));

        var row = Assert.Single(await mine.ListAsync($"/api/platform/mcp-tokens?id={id}"));
        Assert.Equal("active", row.Str("state"));
        Assert.Null(row!["tokenHash"]);
        Assert.Empty(await other.ListAsync($"/api/platform/mcp-tokens?id={id}"));

        await other.AssertProblemAsync(
            "/api/platform/mcp-token-revoke",
            new { id },
            HttpStatusCode.NotFound,
            "platform.token_not_found"
        );
        await mine.CallAsync("/api/platform/mcp-token-revoke", new { id });
        await mine.CallAsync("/api/platform/mcp-token-revoke", new { id });
        Assert.Equal(
            "revoked",
            Assert.Single(await mine.ListAsync($"/api/platform/mcp-tokens?id={id}")).Str("state")
        );
        Assert.Equal(
            1L,
            await api.ScalarAsync<long>(
                "SELECT count(*) FROM oidc.events WHERE event_type = 'mcp_token_revoked' AND detail->>'token' = 'Mine'"
            )
        );
    }
}
