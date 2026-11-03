using System.Net;
using System.Text.Json.Nodes;
using Sso.Api.Auth.Providers;

namespace Sso.Tests;

/// <summary>The people module: only the SSO admin enters and changes people; changing an account is the admin's.</summary>
[Trait("verify", "people")]
[Collection(ApiCollection.Name)]
public sealed class PeopleTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Person(
        Guid? id = null,
        string? username = null,
        string? github = null,
        string? google = null,
        string? microsoft = null,
        string displayName = "Ada Admin",
        string role = "member",
        string? email = null
    )
    {
        var unique = $"{Guid.NewGuid():N}"[..10];
        return new
        {
            id = id ?? Guid.CreateVersion7(),
            username = username ?? $"u.{unique}",
            email = email ?? $"u.{unique}@test.example",
            displayName,
            role,
            githubEmail = github ?? $"u.{unique}@github.example",
            googleEmail = google,
            microsoftEmail = microsoft,
        };
    }

    [Fact]
    public async Task The_admin_enters_a_person_who_appears_in_the_list_and_a_retry_is_a_no_op()
    {
        var admin = await api.AsAsync(ApiFixture.Admin);
        var body = Person(google: "ada@gmail.example");
        var created = await admin.CallAsync("/api/people/person-create", body);
        Assert.Equal(
            created.RowVersion(),
            (await admin.CallAsync("/api/people/person-create", body)).RowVersion()
        );

        var id = (Guid)body.GetType().GetProperty("id")!.GetValue(body)!;
        var row = Assert.Single(await admin.ListAsync($"/api/people/people?id={id}"));
        Assert.Equal("ada@gmail.example", row.Str("googleEmail"));
        Assert.Equal("member", row.Str("role"));
        Assert.Null(row!["githubLinkedAt"]);
        Assert.Equal(created.RowVersion(), row["rowVersion"]!.GetValue<long>());
    }

    [Fact]
    public async Task Every_rule_has_its_code()
    {
        var admin = await api.AsAsync(ApiFixture.Admin);
        var bad = HttpStatusCode.BadRequest;
        var conflict = HttpStatusCode.Conflict;
        await admin.AssertProblemAsync(
            "/api/people/person-create",
            Person(username: "A B"),
            bad,
            "people.username_invalid"
        );
        await admin.AssertProblemAsync(
            "/api/people/person-create",
            Person(email: "nope"),
            bad,
            "people.email_invalid"
        );
        await admin.AssertProblemAsync(
            "/api/people/person-create",
            Person(displayName: " "),
            bad,
            "people.name_invalid"
        );
        await admin.AssertProblemAsync(
            "/api/people/person-create",
            Person(role: "owner"),
            bad,
            "people.role_invalid"
        );
        await admin.AssertProblemAsync(
            "/api/people/person-create",
            Person(github: "not-an-email"),
            bad,
            "people.provider_email_invalid"
        );
        await admin.AssertProblemAsync(
            "/api/people/person-create",
            new
            {
                id = Guid.CreateVersion7(),
                username = "no.provider",
                email = "no.provider@test.example",
                displayName = "N",
                role = "member",
            },
            bad,
            "people.provider_email_missing"
        );
        await admin.AssertProblemAsync(
            "/api/people/person-create",
            Person(username: ApiFixture.Admin),
            conflict,
            "people.username_taken"
        );
        await admin.AssertProblemAsync(
            "/api/people/person-create",
            Person(email: "test.admin@test.example"),
            conflict,
            "people.email_taken"
        );
        // The same GitHub e-mail for two people.
        await admin.AssertProblemAsync(
            "/api/people/person-create",
            Person(github: "test.admin@test.example"),
            conflict,
            "people.provider_email_taken"
        );

        var body = Person();
        await admin.CallAsync("/api/people/person-create", body);
        await admin.AssertProblemAsync(
            "/api/people/person-create",
            Person(id: (Guid)body.GetType().GetProperty("id")!.GetValue(body)!),
            conflict,
            "people.id_taken"
        );
    }

    [Fact]
    public async Task An_admin_changes_a_persons_github_account_the_old_one_stops_at_once_the_new_one_links()
    {
        var username = await api.AddPersonAsync(github: "old@github.example");
        var oldSubject = Random.Shared.NextInt64(1, long.MaxValue).ToString();
        Assert.Equal(
            "/admin",
            (
                await api.ExternalSignInAsync(
                    api.Browser(),
                    new ExternalAccount("github", oldSubject, null, "old@github.example", true)
                )
            )
                .Headers
                .Location!
                .OriginalString
        );
        var (session, sessionId) = await api.SessionAsync(username);

        var admin = await api.AsAsync(ApiFixture.Admin);
        var row = (await admin.ListAsync($"/api/people/people?username={username}"))[0]!;
        Assert.NotNull(row["githubLinkedAt"]);
        await admin.CallAsync(
            "/api/people/person-update",
            Update(row, github: "new@github.example")
        );

        // The old account: no link, its sessions ended.
        Assert.Equal(
            0L,
            await api.ScalarAsync<long>(
                $"SELECT count(*) FROM identity.external_logins WHERE subject = '{oldSubject}'"
            )
        );
        Assert.Equal(
            "unlinked",
            await api.ScalarAsync<string>(
                $"SELECT end_reason::text FROM oidc.sessions WHERE id = '{sessionId}'"
            )
        );
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await session.GetAsync("/api/platform/me", Ct)).StatusCode
        );
        var refused = await api.ExternalSignInAsync(
            api.Browser(),
            new ExternalAccount("github", oldSubject, null, "old@github.example", true)
        );
        Assert.StartsWith("/sign-in/refused", refused.Headers.Location!.OriginalString);

        // The new account links at its first sign-in.
        var ok = await api.ExternalSignInAsync(
            api.Browser(),
            new ExternalAccount(
                "github",
                Random.Shared.NextInt64(1, long.MaxValue).ToString(),
                null,
                "new@github.example",
                true
            )
        );
        Assert.Equal("/admin", ok.Headers.Location!.OriginalString);
        Assert.Equal(
            1L,
            await api.ScalarAsync<long>(
                "SELECT count(*) FROM oidc.events WHERE event_type = 'link_removed' AND detail->>'email' = 'old@github.example'"
            )
        );
    }

    [Fact]
    public async Task A_stale_row_version_is_409_and_nobody_demotes_or_deactivates_themselves()
    {
        var admin = await api.AsAsync(ApiFixture.Admin);
        var username = await api.AddPersonAsync();
        var row = (await admin.ListAsync($"/api/people/people?username={username}"))[0]!;
        await admin.CallAsync("/api/people/person-update", Update(row, name: "Renamed"));
        await admin.AssertProblemAsync(
            "/api/people/person-update",
            Update(row, name: "Again"),
            HttpStatusCode.Conflict,
            "concurrency"
        );

        var me = (await admin.ListAsync($"/api/people/people?username={ApiFixture.Admin}"))[0]!;
        await admin.AssertProblemAsync(
            "/api/people/person-update",
            Update(me, role: "member"),
            HttpStatusCode.BadRequest,
            "people.self_change"
        );
        await admin.AssertProblemAsync(
            "/api/people/person-set-status",
            new
            {
                id = me["id"]!.GetValue<Guid>(),
                status = "inactive",
                rowVersion = me["rowVersion"]!.GetValue<long>(),
            },
            HttpStatusCode.BadRequest,
            "people.self_change"
        );
        await admin.AssertProblemAsync(
            "/api/people/person-update",
            new
            {
                id = Guid.CreateVersion7(),
                username = "x.y",
                email = "x@y.example",
                displayName = "X",
                role = "member",
                githubEmail = "x@y.example",
                rowVersion = 1,
            },
            HttpStatusCode.NotFound,
            "people.not_found"
        );
    }

    [Fact]
    public async Task Unlink_keeps_the_email_and_the_next_sign_in_links_again()
    {
        var username = await api.AddPersonAsync(google: "relink@gmail.example");
        var subject = Random.Shared.NextInt64(1, long.MaxValue).ToString();
        await api.ExternalSignInAsync(
            api.Browser(),
            new ExternalAccount("google", subject, null, "relink@gmail.example", true)
        );
        var admin = await api.AsAsync(ApiFixture.Admin);
        var row = (await admin.ListAsync($"/api/people/people?username={username}"))[0]!;
        Assert.NotNull(row["googleLinkedAt"]);

        await admin.CallAsync(
            "/api/people/person-unlink",
            new { id = row["id"]!.GetValue<Guid>(), provider = "google" }
        );
        Assert.Null(
            (await admin.ListAsync($"/api/people/people?username={username}"))[0]!["googleLinkedAt"]
        );
        await admin.AssertProblemAsync(
            "/api/people/person-unlink",
            new { id = row["id"]!.GetValue<Guid>(), provider = "facebook" },
            HttpStatusCode.BadRequest,
            "people.provider_invalid"
        );

        var again = await api.ExternalSignInAsync(
            api.Browser(),
            new ExternalAccount(
                "google",
                Random.Shared.NextInt64(1, long.MaxValue).ToString(),
                null,
                "relink@gmail.example",
                true
            )
        );
        Assert.Equal("/admin", again.Headers.Location!.OriginalString);
    }

    private static object Update(
        JsonNode row,
        string? github = null,
        string? name = null,
        string? role = null
    ) =>
        new
        {
            id = row["id"]!.GetValue<Guid>(),
            username = row.Str("username"),
            email = row.Str("email"),
            displayName = name ?? row.Str("displayName"),
            role = role ?? row.Str("role"),
            githubEmail = github ?? row["githubEmail"]?.GetValue<string>(),
            googleEmail = row["googleEmail"]?.GetValue<string>(),
            microsoftEmail = row["microsoftEmail"]?.GetValue<string>(),
            rowVersion = row["rowVersion"]!.GetValue<long>(),
        };
}
