using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Sso.Api.Health;

public static class HealthTags
{
    /// <summary>Readiness: dependencies the API needs to serve requests. /health (liveness) checks nothing.</summary>
    public const string Ready = "ready";
}

internal sealed class PostgresHealthCheck(NpgsqlDataSource db) : IHealthCheck
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
            return HealthCheckResult.Unhealthy("PostgreSQL is not reachable.", ex);
        }
    }
}

/// <summary>Readiness as JSON: status per check with its reason - what an admin reads first when a pod is not ready.</summary>
internal static class HealthResponse
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task WriteAsync(HttpContext http, HealthReport report)
    {
        http.Response.ContentType = "application/json";
        return http.Response.WriteAsync(
            JsonSerializer.Serialize(
                new
                {
                    status = report.Status.ToString(),
                    checks = report.Entries.ToDictionary(
                        e => e.Key,
                        e => new { status = e.Value.Status.ToString(), e.Value.Description }
                    ),
                },
                Json
            )
        );
    }
}
