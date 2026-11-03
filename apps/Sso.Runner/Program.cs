using JasperFx;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Sso.Data;
using Sso.Observability;
using Sso.Runner.Messaging;
using Sso.Runner.Outbox;

var builder = WebApplication.CreateBuilder(args);

// Logs, traces and metrics; shipped over OTLP only when OTEL_EXPORTER_OTLP_ENDPOINT is set.
builder.AddSsoObservability("sso-runner");

var connectionString = RunnerDatabase.Normalize(
    builder.Configuration.GetConnectionString(RunnerDatabase.ConnectionStringName)
        ?? throw new InvalidOperationException(
            $"ConnectionStrings:{RunnerDatabase.ConnectionStringName} is not set (sso_runner; deploy/deploy.sh writes it)."
        )
);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IActorAccessor, NoActor>();
builder.Services.AddSsoData(connectionString, "sso-runner");

builder.Services.AddOutboxRelay();
builder.AddRunnerMessaging(connectionString);

builder.Services.AddHealthChecks().AddRunnerChecks();

var app = builder.Build();

// Liveness: the process answers. Readiness: PostgreSQL and RabbitMQ are reachable.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions { Predicate = check => check.Tags.Contains(RunnerHealth.Ready) }
);

// Runs the host, or a Wolverine command instead: `dotnet run -- db-dump wolverine.sql` writes the DDL of the message
// storage that the Liquibase changeset carries, `describe` lists the handlers.
return await app.RunJasperFxCommands(args);

// The entry point class, public for WebApplicationFactory<Program> in the tests.
[UsedImplicitly]
public partial class Program;
