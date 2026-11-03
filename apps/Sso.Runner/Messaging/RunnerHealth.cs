using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Sso.Runner.Messaging;

/// <summary>Readiness of the runner: its database login works. RabbitMQ is Wolverine's - it reconnects by itself.</summary>
public static class RunnerHealth
{
    public const string Ready = "ready";

    public static IHealthChecksBuilder AddRunnerChecks(this IHealthChecksBuilder checks) =>
        checks.AddCheck<PostgresCheck>("postgres", tags: [Ready]);

    private sealed class PostgresCheck(NpgsqlDataSource db) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default
        )
        {
            try
            {
                await using var command = db.CreateCommand("SELECT 1");
                await command.ExecuteScalarAsync(cancellationToken);
                return HealthCheckResult.Healthy();
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                return HealthCheckResult.Unhealthy("PostgreSQL does not answer.", ex);
            }
        }
    }
}
