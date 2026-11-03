using System.Net;
using Sso.Api.Auth;
using Sso.Data.Identity;

namespace Sso.Tests;

/// <summary>
/// The audit log in the application (ADR 0009): who changed what and when, in the transaction of the change, never
/// the value of a secret.
/// </summary>
[Trait("verify", "schema")]
[Collection(ApiCollection.Name)]
public sealed class AuditTests(ApiFixture api)
{
    [Fact]
    public async Task Changes_are_logged_with_the_actor_and_never_the_secret_hash()
    {
        var admin = await api.AsAsync(ApiFixture.Admin);
        var id = Guid.CreateVersion7();
        var clientId = $"audited-{Guid.NewGuid():N}"[..20];
        var created = await admin.CallAsync(
            "/api/clients/client-create",
            new
            {
                id,
                clientId,
                name = "Audited",
                redirectUris = new[] { "https://audited.example/signin-oidc" },
                providers = new[] { "github" },
                secretHash = Secrets.HashText(Secrets.NewToken()),
            }
        );
        await admin.CallAsync(
            "/api/clients/client-set-secret",
            new
            {
                id,
                secretHash = Secrets.HashText(Secrets.NewToken()),
                rowVersion = created.RowVersion(),
            }
        );

        Assert.Equal(
            ApiFixture.AdminId.ToString(),
            await api.ScalarAsync<string>(
                $"SELECT actor_id FROM audit.change_log WHERE table_schema = 'oidc' AND table_name = 'clients' AND operation = 'INSERT' AND new_row->>'id' = '{id}'"
            )
        );
        Assert.Equal(
            2L,
            await api.ScalarAsync<long>(
                $"SELECT count(*) FROM audit.change_log WHERE table_name = 'clients' AND coalesce(new_row, old_row)->>'id' = '{id}'"
            )
        );
        Assert.Equal(
            0L,
            await api.ScalarAsync<long>(
                "SELECT count(*) FROM audit.change_log WHERE table_name = 'clients' AND (new_row ? 'secret_hash' OR old_row ? 'secret_hash')"
            )
        );
        Assert.Equal(
            "sso_api",
            await api.ScalarAsync<string>(
                $"SELECT db_role FROM audit.change_log WHERE table_name = 'clients' AND new_row->>'id' = '{id}' LIMIT 1"
            )
        );
    }

    [Fact]
    public async Task An_audit_entry_rolls_back_with_its_change()
    {
        var name = $"rb.{Guid.NewGuid():N}"[..14];
        await using (var db = TestDb.Context(api.ConnectionString("sso", "sso_api")))
        {
            await using var tx = await db.Database.BeginTransactionAsync(
                TestContext.Current.CancellationToken
            );
            db.People.Add(
                new Person
                {
                    Id = Guid.CreateVersion7(),
                    Username = name,
                    Email = $"{name}@test.example",
                    DisplayName = "Rolled back",
                    GithubEmail = $"{name}@github.example",
                }
            );
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await tx.RollbackAsync(TestContext.Current.CancellationToken);
        }
        Assert.Equal(
            0L,
            await api.ScalarAsync<long>(
                $"SELECT count(*) FROM audit.change_log WHERE new_row->>'username' = '{name}'"
            )
        );
    }

    [Fact]
    public async Task A_sign_in_writes_no_audit_entry_of_a_person()
    {
        // Sessions, codes and tokens are the protocol's state, logged in oidc.events; the audit is for records.
        var before = await api.ScalarAsync<long>(
            "SELECT count(*) FROM audit.change_log WHERE table_name = 'users'"
        );
        var browser = await api.AsAsync(ApiFixture.Admin);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await browser.GetAsync("/api/platform/me", TestContext.Current.CancellationToken)
            ).StatusCode
        );
        Assert.Equal(
            before,
            await api.ScalarAsync<long>(
                "SELECT count(*) FROM audit.change_log WHERE table_name = 'users'"
            )
        );
    }
}
