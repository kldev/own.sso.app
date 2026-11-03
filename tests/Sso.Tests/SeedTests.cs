using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Sso.Api.Auth;
using Sso.Api.Auth.Providers;
using Sso.Api.Oidc;
using Sso.Api.Seed;
using Sso.Data;

namespace Sso.Tests;

/// <summary>
/// The seed on start (ADR 0015), each case on its own copy of the empty schema (ApiFixture.EmptyTemplate) as sso_api:
/// an empty database gets the first admin and the demo data, a seeded one is never touched again, replicas starting at
/// once seed once, and the demo can be switched off.
/// </summary>
[Trait("verify", "seed")]
[Collection(ApiCollection.Name)]
public sealed class SeedTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Factory(string connectionString) : IDbContextFactory<SsoDbContext>
    {
        private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(connectionString);

        public SsoDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<SsoDbContext>();
            SsoData.Configure(options, _dataSource);
            return new SsoDbContext(options.Options);
        }
    }

    /// <summary>A new database from the empty template, and the connection of sso_api to it.</summary>
    private async Task<string> EmptyDatabaseAsync()
    {
        var name = $"seed_{Guid.NewGuid():N}"[..20];
        await using var connection = new NpgsqlConnection(api.SuperuserConnection("postgres"));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            $"CREATE DATABASE {name} TEMPLATE {ApiFixture.EmptyTemplate}",
            connection
        );
        await command.ExecuteNonQueryAsync(Ct);
        return name;
    }

    private static Seeder Seeder(string connectionString, bool demo = true) =>
        new(
            new Factory(connectionString),
            Options.Create(new SeedOptions { Demo = demo }),
            Options.Create(new BootstrapAdminOptions()),
            TimeProvider.System,
            NullLogger<Seeder>.Instance
        );

    private async Task<long> CountAsync(string database, string table)
    {
        await using var connection = new NpgsqlConnection(api.SuperuserConnection(database));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task An_empty_database_gets_the_first_admin_and_the_demo_data_and_the_admin_signs_in()
    {
        var database = await EmptyDatabaseAsync();
        var connection = api.ConnectionString(database, "sso_api");
        Assert.True(await Seeder(connection).SeedAsync(Ct));

        Assert.Equal(
            1L,
            await CountAsync(
                database,
                "identity.users WHERE username = 'admin' AND role = 'admin' AND email = 'admin@example.com' AND github_email = 'admin@example.com'"
            )
        );
        Assert.Equal(6L, await CountAsync(database, "identity.users"));
        Assert.Equal(1L, await CountAsync(database, "identity.users WHERE status = 'inactive'"));
        Assert.Equal(3L, await CountAsync(database, "oidc.clients"));
        Assert.Equal(
            1L,
            await CountAsync(
                database,
                $"oidc.clients WHERE client_id = '{Api.Seed.Seeder.SampleClientId}'"
            )
        );
        Assert.True(await CountAsync(database, "oidc.sessions") >= 4);
        Assert.True(await CountAsync(database, "oidc.events") > 10);
        Assert.Equal(1L, await CountAsync(database, "identity.mcp_tokens"));
        Assert.Equal(1L, await CountAsync(database, "marketing.mailings"));
        Assert.Equal(
            3L,
            await CountAsync(database, "(SELECT DISTINCT status FROM marketing.recipients) AS s")
        );
        Assert.Equal(
            0L,
            await CountAsync(database, "identity.users WHERE email NOT LIKE '%example.com'")
        );

        // The admin signs in like anybody else - here through the provider's answer the sign-in stub gives.
        var events = new EventLog(new NoActor(), TimeProvider.System);
        var signIn = new ExternalSignIn(new Factory(connection), events, TimeProvider.System);
        var (outcome, _) = await signIn.SignInAsync(
            new ExternalAccount("github", "dev-admin", null, "admin@example.com", true),
            null,
            new RequestInfo("127.0.0.1", "tests"),
            Ct
        );
        Assert.Equal("ok", outcome);
    }

    [Fact]
    public async Task A_seeded_database_is_never_seeded_again_even_after_the_admin_changed_it()
    {
        var database = await EmptyDatabaseAsync();
        var connection = api.ConnectionString(database, "sso_api");
        Assert.True(await Seeder(connection).SeedAsync(Ct));
        await using (var admin = new NpgsqlConnection(api.SuperuserConnection(database)))
        {
            await admin.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                "UPDATE identity.users SET display_name = 'Renamed', email = 'boss@example.com' WHERE username = 'admin'; DELETE FROM marketing.recipients; ",
                admin
            );
            await command.ExecuteNonQueryAsync(Ct);
        }

        Assert.False(await Seeder(connection).SeedAsync(Ct));
        Assert.Equal(6L, await CountAsync(database, "identity.users"));
        Assert.Equal(
            1L,
            await CountAsync(database, "identity.users WHERE display_name = 'Renamed'")
        );
        Assert.Equal(0L, await CountAsync(database, "marketing.recipients"));
    }

    [Fact]
    public async Task Two_replicas_starting_at_once_seed_once()
    {
        var database = await EmptyDatabaseAsync();
        var connection = api.ConnectionString(database, "sso_api");
        var results = await Task.WhenAll(
            Seeder(connection).SeedAsync(Ct),
            Seeder(connection).SeedAsync(Ct)
        );
        Assert.Single(results, seeded => seeded);
        Assert.Equal(1L, await CountAsync(database, "identity.users WHERE username = 'admin'"));
        Assert.Equal(6L, await CountAsync(database, "identity.users"));
    }

    [Fact]
    public async Task Without_the_demo_only_the_first_admin_is_seeded()
    {
        var database = await EmptyDatabaseAsync();
        Assert.True(
            await Seeder(api.ConnectionString(database, "sso_api"), demo: false).SeedAsync(Ct)
        );
        Assert.Equal(1L, await CountAsync(database, "identity.users"));
        Assert.Equal(0L, await CountAsync(database, "oidc.clients"));
        Assert.Equal(0L, await CountAsync(database, "marketing.mailings"));
        // The invitation template is the changeset's, not the seed's.
        Assert.Equal(1L, await CountAsync(database, "marketing.templates"));
    }
}
