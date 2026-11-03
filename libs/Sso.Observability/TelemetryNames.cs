namespace Sso.Observability;

/// <summary>
/// The activity sources and meters a host listens to. A source nobody subscribes to costs nothing and emits nothing,
/// so a name missing here is silently absent from every dashboard (sso.k8s.local, observability/dashboards).
/// </summary>
public static class TelemetryNames
{
    /// <summary>Our own sources and meters: <c>Sso.&lt;Area&gt;</c> (health, process limits).</summary>
    public const string Application = "Sso.*";

    /// <summary>Connection pool and command metrics; the traces come from <c>AddNpgsql()</c>.</summary>
    public const string Npgsql = "Npgsql";

    /// <summary>
    /// The runtime's own meter (.NET 9+): CPU time, working set, GC, thread pool, exceptions - the <c>dotnet_*</c>
    /// series of the dashboards. Replaces the OpenTelemetry runtime instrumentation package.
    /// </summary>
    public const string Runtime = "System.Runtime";
}
