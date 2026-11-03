using System.Net.Http.Headers;
using System.Security.Claims;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Sso.Api.Auth;
using Sso.Api.Auth.Providers;
using Sso.Api.Marketing;
using Sso.DbMigrator;
using Testcontainers.PostgreSql;

namespace Sso.Tests;

/// <summary>
/// The API against a throw-away PostgreSQL 18 with the schema of db/ (the changesets exported from the EF model, the
/// roles, the grants and the invitation template), deployed by the Liquibase image of db/docker/liquibase as
/// sso_migrator - CREATEROLE, owner of the database, not a superuser: the same setup as deploy/deploy.sh. The API
/// connects as sso_api, which may only read and write rows. Nobody has a password: a test signs a person in either
/// through the real /auth/external/{provider}/done with the external cookie a provider's handler would write
/// (ExternalSignInAsync), or straight with an SSO session written to the database (AsAsync). The providers are
/// "configured" with fake ids. The seed on start is off here; SeedTests run it on copies of the empty schema.
/// </summary>
// Created by xUnit for ICollectionFixture<ApiFixture>.
[UsedImplicitly]
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>The tests' own SSO admin (never the owner's seeded record).</summary>
    public const string Admin = "test.admin";
    public static readonly Guid AdminId = Guid.Parse("019a3f00-0000-7000-8000-0000000000aa");

    /// <summary>The Microsoft organisation the tests allow.</summary>
    public const string AllowedTenant = "11111111-2222-4333-8444-555555555555";

    public const string SessionCookie = "sso.session";

    private const string ApiPassword = "api-test-only";
    private const string RunnerPassword = "runner-test-only";
    private const string MigratorPassword = "migrator-test-only";

    /// <summary>A copy of the empty schema (after Liquibase, before any row of the tests) - for SeedTests.</summary>
    public const string EmptyTemplate = "sso_empty";

    /// <summary>What the marketing sender sent in this run (instead of an SMTP server).</summary>
    public RecordingMailSender Mail { get; } = new();

    private readonly INetwork _network = new NetworkBuilder().Build();
    private PostgreSqlContainer? _postgres;

    /// <summary>The superuser on the database sso - setup only (logins), never the API.</summary>
    private string AdminConnection =>
        new NpgsqlConnectionStringBuilder(
            _postgres?.GetConnectionString() ?? throw new InvalidOperationException("Not started.")
        )
        {
            Database = "sso",
        }.ConnectionString;

    private string AppConnection => ConnectionString("sso", "sso_api");

    /// <summary>A connection string of an application role to a database of this server.</summary>
    public string ConnectionString(string database, string role) =>
        new NpgsqlConnectionStringBuilder(AdminConnection)
        {
            Database = database,
            Username = role,
            Password = role == "sso_runner" ? RunnerPassword : ApiPassword,
            ApplicationName = "sso-tests",
        }.ConnectionString;

    /// <summary>The superuser's connection to <paramref name="database"/> - setup and checks only.</summary>
    public string SuperuserConnection(string database) =>
        new NpgsqlConnectionStringBuilder(AdminConnection) { Database = database }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        var db = DbDirectory();
        await _network.CreateAsync();
        _postgres = new PostgreSqlBuilder("postgres:18.6-alpine")
            .WithNetwork(_network)
            .WithNetworkAliases("db")
            .WithDatabase("postgres")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await _postgres.StartAsync();
        // The superuser's part around Liquibase - the same code as the deployment (apps/Sso.DbMigrator).
        var bootstrap = new BootstrapSettings(
            "sso",
            "sso_migrator",
            MigratorPassword,
            new Dictionary<string, string>
            {
                ["sso_api"] = ApiPassword,
                ["sso_runner"] = RunnerPassword,
            }
        );
        await DatabaseBootstrap.PrepareAsync(
            _postgres.GetConnectionString(),
            bootstrap,
            CancellationToken.None
        );

        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(Path.Combine(db, "docker", "liquibase"))
            .WithName("sso/liquibase:5.0.4")
            .WithCleanUp(false)
            .Build();
        await image.CreateAsync();
        await LiquibaseUpdateAsync(image, db);

        await DatabaseBootstrap.FinishAsync(
            _postgres.GetConnectionString(),
            bootstrap,
            CancellationToken.None
        );
        // Nobody is connected to sso yet (pooled connections of the bootstrap closed): a template of the empty schema
        // for the seed's tests.
        NpgsqlConnection.ClearAllPools();
        await SqlAsync(
            _postgres.GetConnectionString(),
            $"CREATE DATABASE {EmptyTemplate} TEMPLATE sso"
        );
        await SqlAsync(
            AdminConnection,
            $"""
            INSERT INTO identity.users (id, username, email, display_name, role, github_email)
            VALUES ('{AdminId}', '{Admin}', 'test.admin@test.example', 'Test Admin', 'admin', 'test.admin@test.example')
            """,
            // Templates of three test products besides the seeded invitation (great-sso-en).
            """
            INSERT INTO marketing.templates (id, code, product, language, name, subject, body) VALUES
            (uuidv7(), 'test-a-en', 'test-a', 'en', 'Test A', 'Test A', 'Hello, https://sso.example.com'),
            (uuidv7(), 'test-b-en', 'test-b', 'en', 'Test B', 'Test B', 'Hello from B.'),
            (uuidv7(), 'test-c-en', 'test-c', 'en', 'Test C', 'Test C', 'Hello from C.')
            """
        );
    }

    private static async Task SqlAsync(string connectionString, params string[] statements)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var sql in statements)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task LiquibaseUpdateAsync(IFutureDockerImage image, string db)
    {
        var liquibase = new ContainerBuilder(image)
            .WithNetwork(_network)
            .WithBindMount(db, "/liquibase/workspace")
            .WithWorkingDirectory("/liquibase/workspace")
            .WithCommand(
                "--url=jdbc:postgresql://db:5432/sso",
                "--username=sso_migrator",
                $"--password={MigratorPassword}",
                "--changelog-file=changelog.yaml",
                "update"
            )
            .Build();
        try
        {
            // A one-shot container: it runs update and exits; the exit code tells whether every changeset applied.
            await liquibase.StartAsync();
            var exitCode = await liquibase.GetExitCodeAsync().WaitAsync(TimeSpan.FromMinutes(3));
            if (exitCode != 0)
            {
                var (stdout, stderr) = await liquibase.GetLogsAsync();
                throw new InvalidOperationException(
                    $"Liquibase update failed ({exitCode}):{Environment.NewLine}{stdout}{stderr}"
                );
            }
        }
        finally
        {
            await liquibase.DisposeAsync();
        }
    }

    private static string DbDirectory()
    {
        for (
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            dir is not null;
            dir = dir.Parent
        )
            if (File.Exists(Path.Combine(dir.FullName, "db", "changelog.yaml")))
                return Path.Combine(dir.FullName, "db");
        throw new InvalidOperationException("db/changelog.yaml not found above the test output.");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:sso", AppConnection);
        // The admin and the demo data come from the tests themselves (SeedTests run the seed on their own copies).
        builder.UseSetting("Seed:Enabled", "false");
        // The real providers' handlers (with fake ids), not the development stub of appsettings.Development.json.
        builder.UseSetting("DevSignIn:Enabled", "false");
        builder.UseSetting("Jwt:SigningKey", "api-tests-only-signing-key-0123456789abcdef");
        // Every test signs in again from the same address; the limit itself is not under test.
        builder.UseSetting("Jwt:SignInAttemptsPerMinute", "1000");
        // Configured providers with fake credentials: their buttons and paths exist, nothing calls them.
        builder.UseSetting("Providers:GitHub:ClientId", "test-github");
        builder.UseSetting("Providers:GitHub:ClientSecret", "test-github-secret");
        builder.UseSetting("Providers:Google:ClientId", "test-google");
        builder.UseSetting("Providers:Google:ClientSecret", "test-google-secret");
        builder.UseSetting("Providers:Microsoft:ClientId", "test-microsoft");
        builder.UseSetting("Providers:Microsoft:ClientSecret", "test-microsoft-secret");
        builder.UseSetting("Providers:Microsoft:AllowedTenantIds:0", AllowedTenant);
        builder.UseSetting("Sessions:CookieName", SessionCookie);
        builder.UseSetting("Sessions:SecureCookie", "false");
        builder.UseSetting("Oidc:Issuer", "http://localhost");
        builder.UseSetting("Oidc:KeysPath", KeysPath);
        // Readiness is not under test here; the broker only needs a valid configuration.
        builder.UseSetting("RabbitMq:Host", "localhost");
        builder.UseSetting("RabbitMq:UserName", "test");
        builder.UseSetting("RabbitMq:Password", "test");
        // The marketing sender runs, quickly, and sends into the recorder.
        builder.UseSetting("Smtp:Host", "smtp.test");
        builder.UseSetting("Marketing:PollInterval", "00:00:00.1");
        builder.UseSetting("Marketing:DelayBetweenEmails", "00:00:00");
        builder.UseSetting("Marketing:RetryAfter", "00:00:00");
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IMarketingMailSender>(Mail)
        );
    }

    /// <summary>The OIDC signing keys of this run (a fresh directory: the host makes its first key at start).</summary>
    private static readonly string KeysPath = Path.Combine(
        Path.GetTempPath(),
        $"sso-tests-keys-{Guid.NewGuid():N}"
    );

    /// <summary>
    /// A browser-like client of <paramref name="username"/> with a new SSO session written to the database (the
    /// sign-in itself is under test in SignInTests); no automatic redirects, JSON accepted.
    /// </summary>
    public async Task<HttpClient> AsAsync(string username, string provider = "github")
    {
        var (client, _) = await SessionAsync(username, provider);
        return client;
    }

    /// <summary>As <see cref="AsAsync"/>, with the session's id for tests that look at it.</summary>
    public async Task<(HttpClient Client, Guid SessionId)> SessionAsync(
        string username,
        string provider = "github"
    )
    {
        var token = Secrets.NewToken();
        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            WITH s AS (
                INSERT INTO oidc.sessions (id, token_hash, user_id, provider, idle_expires_at, absolute_expires_at, ip, user_agent)
                SELECT uuidv7(), $1, u.id, $2, now() + interval '14 days', now() + interval '30 days', '127.0.0.1', 'tests'
                FROM identity.users AS u WHERE u.username = $3
                RETURNING id, user_id, provider
            ), e AS (
                INSERT INTO oidc.events (event_type, outcome, user_id, provider, session_id, ip, user_agent)
                SELECT 'session_started', 'success', user_id, provider, id, '127.0.0.1', 'tests' FROM s
            )
            SELECT id FROM s
            """,
            connection
        );
        command.Parameters.AddWithValue(Secrets.Hash(token));
        command.Parameters.AddWithValue(provider);
        command.Parameters.AddWithValue(username);
        var sessionId = (Guid)(
            await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException($"No person {username}.")
        );
        var client = CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            }
        );
        client.DefaultRequestHeaders.Add("Cookie", $"{SessionCookie}={token}");
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json")
        );
        return (client, sessionId);
    }

    /// <summary>A new person entered straight in the database - for tests that need one.</summary>
    public async Task<string> AddPersonAsync(
        string role = "member",
        string? github = null,
        string? google = null,
        string? microsoft = null,
        string status = "active"
    )
    {
        var username = $"p.{Guid.NewGuid():N}"[..14];
        static string Value(string? v) => v is null ? "NULL" : $"'{v}'";
        await SqlAsync(
            AdminConnection,
            $"""
            INSERT INTO identity.users (username, email, display_name, role, status, github_email, google_email, microsoft_email)
            VALUES ('{username}', '{username}@test.example', 'Person {username}', '{role}', '{status}',
                    {Value(github ?? $"{username}@github.example")}, {Value(google)}, {Value(
                microsoft
            )})
            """
        );
        return username;
    }

    /// <summary>
    /// What a provider's handler leaves behind: the external cookie with the account (as ProviderSetup writes it), then
    /// GET /auth/external/{provider}/done - the real decision and session start. Returns the response of done.
    /// </summary>
    public async Task<HttpResponseMessage> ExternalSignInAsync(
        HttpClient browser,
        ExternalAccount account,
        string returnUrl = "/admin"
    )
    {
        var cookie = Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(ProviderSetup.ExternalScheme);
        var properties = new AuthenticationProperties
        {
            Items = { [ExternalSignInEndpoints.ReturnUrlItem] = returnUrl },
        };
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(account.ToIdentity(ProviderSetup.ExternalScheme)),
            properties,
            ProviderSetup.ExternalScheme
        );
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/auth/external/{account.Provider}/done"
        );
        request.Headers.Add(
            "Cookie",
            $"{cookie.Cookie.Name}={cookie.TicketDataFormat.Protect(ticket)}"
        );
        return await browser.SendAsync(request);
    }

    /// <summary>Runs SQL as the superuser - test setup only (e.g. making an account inactive).</summary>
    public Task ExecuteAsync(string sql) => SqlAsync(AdminConnection, sql);

    /// <summary>One value read as the superuser - for checking what a table stores, not what the API shows.</summary>
    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>A client without a session that does not follow redirects.</summary>
    public HttpClient Browser() =>
        CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            }
        );

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_postgres is not null)
            await _postgres.DisposeAsync();
        await _network.DisposeAsync();
        if (Directory.Exists(KeysPath))
            Directory.Delete(KeysPath, recursive: true);
    }
}

/// <summary>One database and one host for every test class: a second PostgreSQL container would only cost memory.</summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
