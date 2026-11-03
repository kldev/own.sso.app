using Microsoft.Extensions.Configuration;
using Sso.DbMigrator;

// dotnet Sso.DbMigrator.dll prepare | finish
//   prepare  before Liquibase: the migrator's role and the database
//   finish   after Liquibase: the application roles' logins
// Configuration (environment variables; secrets only there):
//   ConnectionStrings__postgres   prepare: the server's admin (a superuser locally, the admin of a managed server -
//                                 CREATEROLE, CREATEDB); finish: the admin or the migrator itself - required, no
//                                 default on purpose
//   Database__Name                sso
//   Database__Migrator            sso_migrator
//   Database__MigratorPassword    the migrator's password
//   Logins__<role>                password of each application role: Logins__sso_api, Logins__sso_runner
// Exit code 0 when done, 1 on a failure, 2 on wrong usage.
var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddCommandLine(args.Skip(1).ToArray())
    .Build();

var command = args.FirstOrDefault();
var admin = configuration.GetConnectionString("postgres");
var migratorPassword = configuration["Database:MigratorPassword"];
if (command is not ("prepare" or "finish") || string.IsNullOrWhiteSpace(admin))
{
    Console.Error.WriteLine(
        "Usage: Sso.DbMigrator prepare|finish  (ConnectionStrings__postgres = the server admin's connection)"
    );
    return 2;
}
if (command == "prepare" && string.IsNullOrWhiteSpace(migratorPassword))
{
    Console.Error.WriteLine("Database__MigratorPassword is required for prepare.");
    return 2;
}

var settings = new BootstrapSettings(
    configuration["Database:Name"] ?? "sso",
    configuration["Database:Migrator"] ?? "sso_migrator",
    migratorPassword ?? "",
    configuration
        .GetSection("Logins")
        .GetChildren()
        .Where(login => !string.IsNullOrEmpty(login.Value))
        .ToDictionary(login => login.Key, login => login.Value!)
);

try
{
    if (command == "prepare")
    {
        await DatabaseBootstrap.PrepareAsync(admin, settings, CancellationToken.None);
        Console.WriteLine($"Prepared: role {settings.Migrator}, database {settings.Database}.");
    }
    else
    {
        await DatabaseBootstrap.FinishAsync(admin, settings, CancellationToken.None);
        Console.WriteLine($"Finished: LOGIN for {string.Join(", ", settings.Logins.Keys)}.");
    }
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"{command} failed: {exception.Message}");
    return 1;
}
