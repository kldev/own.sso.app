using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;
using Sso.Observability.Health;

namespace Sso.Observability;

/// <summary>Logs, traces and metrics of an Sso host (ADR-0025).</summary>
public static class ObservabilityExtensions
{
    /// <summary>
    /// The standard OpenTelemetry variable. Set in the cluster (sso.k8s.local, charts/sso); absent in compose,
    /// <c>dotnet run</c> and the tests - then nothing is exported and nothing is needed.
    /// </summary>
    // ReSharper disable once MemberCanBePrivate.Global - read by the tests of sso.project.dev (OtlpExportTests).
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary><c>text</c> (default, for a terminal and <c>kubectl logs</c>) or <c>json</c> (one event per line).</summary>
    private const string ConsoleFormatKey = "Observability:ConsoleFormat";

    private const string TextTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} <{SourceContext}>{NewLine}{Exception}";

    /// <summary>
    /// True when the host ships telemetry: <see cref="OtlpEndpointKey"/> is set. Tracing and metrics are registered
    /// either way - activities still exist, so problem details carry a trace id without an exporter too.
    /// </summary>
    // ReSharper disable once MemberCanBePrivate.Global - read by the tests of sso.project.dev (OtlpExportTests).
    public static bool ExportsOtlp(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration[OtlpEndpointKey]);

    /// <summary>
    /// Serilog as the logging pipeline, OpenTelemetry as the way out. Serilog owns the levels (the <c>Serilog</c>
    /// section) and the console; every event that passes it goes on to the OpenTelemetry logger provider, so logs
    /// leave through the same exporter, with the same resource and trace id, as traces and metrics.
    /// <paramref name="serviceName"/> is <c>service.name</c>, the <c>job</c> label in Prometheus: <c>sso-api</c>,
    /// <c>sso-files</c>, ...
    /// </summary>
    public static IHostApplicationBuilder AddSsoObservability(
        this IHostApplicationBuilder builder,
        string serviceName
    )
    {
        var exportsOtlp = ExportsOtlp(builder.Configuration);

        builder.AddSerilogPipeline(serviceName, exportsOtlp);
        builder.AddOpenTelemetryPipeline(serviceName, exportsOtlp);

        // The host adds its own checks with AddHealthChecks(); this publishes every report as a metric too.
        builder.Services.AddHealthChecks();
        builder.Services.AddSingleton<IHealthCheckPublisher, HealthStatusMetrics>();

        return builder;
    }

    private static void AddSerilogPipeline(
        this IHostApplicationBuilder builder,
        string serviceName,
        bool exportsOtlp
    )
    {
        // The default console, debug and event source providers would receive every event a second time through
        // writeToProviders - the only provider left is OpenTelemetry's, and only when it exports.
        builder.Logging.ClearProviders();

        if (exportsOtlp)
        {
            builder.Logging.AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
            });
        }

        var json = string.Equals(
            builder.Configuration[ConsoleFormatKey],
            "json",
            StringComparison.OrdinalIgnoreCase
        );

        builder.Services.AddSerilog(
            (services, logger) =>
            {
                logger
                    .ReadFrom.Configuration(builder.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext()
                    .Enrich.WithProperty("ServiceName", serviceName);

                if (json)
                    logger.WriteTo.Console(new RenderedCompactJsonFormatter());
                else
                    logger.WriteTo.Console(outputTemplate: TextTemplate);
            },
            writeToProviders: true
        );
    }

    private static void AddOpenTelemetryPipeline(
        this IHostApplicationBuilder builder,
        string serviceName,
        bool exportsOtlp
    )
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";

        var telemetry = builder
            .Services.AddOpenTelemetry()
            .ConfigureResource(resource =>
                resource
                    // In a container the machine name is the container's host name: it tells replicas apart and stays the same
                    // for the pod's life, where a random id would change on every restart of the process.
                    .AddService(
                        serviceName,
                        serviceVersion: version,
                        serviceInstanceId: Environment.MachineName
                    )
                    .AddAttributes([
                        new("deployment.environment.name", builder.Environment.EnvironmentName),
                    ])
            )
            .WithTracing(tracing =>
                tracing
                    .AddSource(TelemetryNames.Application)
                    // Every HttpClient: API -> file service, and the AWS SDK's calls to S3 in the file service.
                    .AddHttpClientInstrumentation()
                    .AddNpgsql()
            )
            .WithMetrics(metrics =>
                metrics
                    .AddMeter(
                        TelemetryNames.Application,
                        TelemetryNames.Npgsql,
                        TelemetryNames.Runtime
                    )
                    .AddInstrumentation<ProcessLimitsMetrics>()
                    .AddHttpClientInstrumentation()
                    // A histogram sample recorded inside a sampled span keeps its trace id: a dot on a latency chart
                    // in Grafana opens that very request in Rootprint.
                    .SetExemplarFilter(ExemplarFilterType.TraceBased)
            );

        // The exporter reads the rest of the OTEL_* variables itself (protocol, headers, the metric interval).
        if (exportsOtlp)
            telemetry.UseOtlpExporter();
    }
}
