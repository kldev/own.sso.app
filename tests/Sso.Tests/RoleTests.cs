using Npgsql;

namespace Sso.Tests;

/// <summary>
/// The three roles (ADR 0004): the migrator owns every object; the host (sso_api) and the runner (sso_runner) read
/// and write rows of the tables they need and can create, alter or drop nothing.
/// </summary>
[Trait("verify", "schema")]
[Collection(ApiCollection.Name)]
public sealed class RoleTests(ApiFixture api)
{
    [Fact]
    public async Task The_api_role_cannot_create_drop_or_alter()
    {
        var host = api.ConnectionString("sso", "sso_api");
        foreach (
            var sql in new[]
            {
                "CREATE TABLE identity.x (i int)",
                "CREATE TABLE public.x (i int)",
                "CREATE SCHEMA x",
                "ALTER TABLE identity.users ADD COLUMN x int",
                "DROP TABLE marketing.mailings",
                "TRUNCATE oidc.events",
                "CREATE FUNCTION identity.f() RETURNS int LANGUAGE sql AS 'SELECT 1'",
            }
        )
            Assert.Equal(
                PostgresErrorCodes.InsufficientPrivilege,
                await TestDb.SqlStateAsync(host, sql)
            );
    }

    [Fact]
    public async Task The_api_role_reads_and_writes_rows_but_never_deletes_a_person()
    {
        var host = api.ConnectionString("sso", "sso_api");
        Assert.Null(await TestDb.SqlStateAsync(host, "SELECT count(*) FROM identity.users"));
        Assert.Equal(
            PostgresErrorCodes.InsufficientPrivilege,
            await TestDb.SqlStateAsync(host, "DELETE FROM identity.users WHERE false")
        );
        Assert.Equal(
            PostgresErrorCodes.InsufficientPrivilege,
            await TestDb.SqlStateAsync(host, "SELECT count(*) FROM wolverine.wolverine_nodes")
        );
    }

    [Fact]
    public async Task The_runner_reaches_only_the_outbox_and_wolverine()
    {
        var runner = api.ConnectionString("sso", "sso_runner");
        Assert.Null(
            await TestDb.SqlStateAsync(runner, "SELECT count(*) FROM messaging.outbox_messages")
        );
        Assert.Null(
            await TestDb.SqlStateAsync(runner, "SELECT count(*) FROM wolverine.wolverine_nodes")
        );
        foreach (var table in new[] { "identity.users", "oidc.events", "marketing.recipients" })
            Assert.Equal(
                PostgresErrorCodes.InsufficientPrivilege,
                await TestDb.SqlStateAsync(runner, $"SELECT count(*) FROM {table}")
            );
        Assert.Equal(
            PostgresErrorCodes.InsufficientPrivilege,
            await TestDb.SqlStateAsync(runner, "CREATE TABLE messaging.x (i int)")
        );
    }

    [Fact]
    public async Task The_migrator_owns_every_object()
    {
        Assert.Equal(
            0L,
            await api.ScalarAsync<long>(
                """
                SELECT count(*) FROM pg_class AS c JOIN pg_namespace AS n ON n.oid = c.relnamespace
                WHERE n.nspname IN ('identity', 'oidc', 'audit', 'marketing', 'messaging', 'wolverine')
                  AND pg_get_userbyid(c.relowner) <> 'sso_migrator'
                """
            )
        );
        Assert.False(
            await api.ScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'sso_owner')"
            )
        );
    }
}
