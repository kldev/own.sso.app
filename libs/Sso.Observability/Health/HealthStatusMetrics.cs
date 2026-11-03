using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Sso.Observability.Health;

/// <summary>
/// Publishes the latest health report as a gauge, one series per check - "the API has lost PostgreSQL" gets a history
/// on a dashboard, and a host without an HTTP port (the planned worker) reports it the same way.
/// </summary>
public sealed class HealthStatusMetrics : IHealthCheckPublisher
{
    private const string MeterName = "Sso.Health";

    private readonly ConcurrentDictionary<string, HealthStatus> _latest = new();

    public HealthStatusMetrics(IMeterFactory meters)
    {
        var meter = meters.Create(MeterName);
        meter.CreateObservableGauge(
            "sso.health.status",
            Observe,
            unit: "{status}",
            description: "1 when the check is healthy, 0.5 when degraded, 0 when unhealthy."
        );
    }

    public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        foreach (var (name, entry) in report.Entries)
            _latest[name] = entry.Status;

        return Task.CompletedTask;
    }

    private IEnumerable<Measurement<double>> Observe() =>
        _latest.Select(pair => new Measurement<double>(
            pair.Value switch
            {
                HealthStatus.Healthy => 1,
                HealthStatus.Degraded => 0.5,
                _ => 0,
            },
            new KeyValuePair<string, object?>("check", pair.Key)
        ));
}
