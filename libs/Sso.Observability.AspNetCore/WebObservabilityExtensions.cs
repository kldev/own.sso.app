using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

namespace Sso.Observability.AspNetCore;

/// <summary>The HTTP side of <see cref="ObservabilityExtensions.AddSsoObservability"/> (ADR-0025).</summary>
public static class WebObservabilityExtensions
{
    /// <summary>ASP.NET Core Identity (.NET 10): sign-ins, password checks, sign-outs - the login panels.</summary>
    private const string IdentityMeter = "Microsoft.AspNetCore.Identity";

    /// <summary>Requests the rate limiter held or rejected.</summary>
    private const string RateLimitingMeter = "Microsoft.AspNetCore.RateLimiting";

    /// <summary>
    /// Paths nobody wants a span or an information line for: probes every few seconds, the API docs and their assets.
    /// </summary>
    private static readonly string[] QuietPrefixes =
    [
        "/health",
        "/docs",
        "/openapi",
        "/scalar",
        "/favicon",
    ];

    /// <summary><see cref="ObservabilityExtensions.AddSsoObservability"/> plus the HTTP server side.</summary>
    public static IHostApplicationBuilder AddSsoWebObservability(
        this IHostApplicationBuilder builder,
        string serviceName
    )
    {
        builder.AddSsoObservability(serviceName);

        builder
            .Services.AddOpenTelemetry()
            .WithTracing(tracing =>
                tracing.AddAspNetCoreInstrumentation(options =>
                {
                    options.Filter = context => !IsQuiet(context.Request.Path);
                    // The exception as a span event: the trace shows what failed, not only that it was a 500.
                    options.RecordException = true;
                })
            )
            .WithMetrics(metrics =>
                metrics.AddAspNetCoreInstrumentation().AddMeter(IdentityMeter, RateLimitingMeter)
            );

        return builder;
    }

    /// <summary>
    /// One line per request instead of the several <c>Microsoft.AspNetCore</c> writes, with the route template next
    /// to the concrete path. Belongs before the endpoints, so it times the whole pipeline.
    /// </summary>
    public static IApplicationBuilder UseSsoRequestLogging(this IApplicationBuilder app) =>
        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate =
                "HTTP {RequestMethod:l} {RequestPath:l} responded {StatusCode} in {Elapsed:0.0} ms";
            options.GetLevel = (context, _, exception) =>
                exception is not null || context.Response.StatusCode >= 500 ? LogEventLevel.Error
                : IsQuiet(context.Request.Path) ? LogEventLevel.Verbose
                : LogEventLevel.Information;
            options.EnrichDiagnosticContext = (diagnostics, context) =>
            {
                if (context.GetEndpoint() is RouteEndpoint endpoint)
                    diagnostics.Set("Route", endpoint.RoutePattern.RawText);
            };
        });

    private static bool IsQuiet(PathString path) =>
        QuietPrefixes.Any(prefix => path.StartsWithSegments(prefix));
}
