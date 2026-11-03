using System.Diagnostics.Metrics;

namespace Sso.Observability;

/// <summary>
/// The denominator of "memory %" on the dashboards: the working set alone says nothing until it is set against what
/// the process may use. Built by the meter provider, so it lives as long as it does.
/// </summary>
public sealed class ProcessLimitsMetrics
{
    private const string MeterName = "Sso.Process";

    /// <summary>cgroup v2; <c>max</c> means no limit, and so does a missing file outside a container.</summary>
    private const string CgroupMemoryLimit = "/sys/fs/cgroup/memory.max";

    public ProcessLimitsMetrics(IMeterFactory meters)
    {
        var meter = meters.Create(MeterName);
        meter.CreateObservableGauge(
            "sso.process.memory.limit",
            MemoryLimitBytes,
            unit: "By",
            description: "Memory the process may use: the container limit, or what the GC sees."
        );
    }

    private static long MemoryLimitBytes() =>
        File.Exists(CgroupMemoryLimit)
        && long.TryParse(File.ReadAllText(CgroupMemoryLimit).Trim(), out var limit)
            ? limit
            : GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
}
