using JetBrains.Annotations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Sso.Api;
using Sso.Api.Auth;
using Sso.Api.Auth.Providers;
using Sso.Api.Components;
using Sso.Api.Features;
using Sso.Api.Features.Common;
using Sso.Api.Health;
using Sso.Api.Marketing;
using Sso.Api.Mcp;
using Sso.Api.Messaging;
using Sso.Api.Oidc;
using Sso.Api.Seed;
using Sso.Api.Web;
using Sso.Data;
using Sso.Observability.AspNetCore;

// The aspnet image has no curl: the Docker HEALTHCHECK runs `dotnet Sso.Api.dll healthcheck`.
if (args is ["healthcheck"])
    return await ContainerHealthCheck.RunAsync();

var builder = WebApplication.CreateBuilder(args);

// Serilog (levels in the "Serilog" section) and OpenTelemetry; exported only with OTEL_EXPORTER_OTLP_ENDPOINT.
builder.AddSsoWebObservability("sso-api");

builder.Services.AddProblemDetails();
builder.AddReverseProxy();

var connectionString =
    builder.Configuration.GetConnectionString("sso")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:sso is not set (user-secrets for dotnet run - deploy/deploy.sh writes them; environment in the container)."
    );

// The EF Core model (libs/Sso.Data): a pooled factory and a scoped context, the audit interceptor (ADR 0003, 0009).
builder.Services.AddSsoData(connectionString, "sso");

builder.Services.AddOptions<OperatorOptions>().BindConfiguration(OperatorOptions.SectionName);
builder.AddSsoAuth();
builder
    .Services.AddOptions<OidcOptions>()
    .BindConfiguration(OidcOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<SigningKeys>();

// The external sign-in cookie and the refusal page's value are protected with these keys: on a volume in the container
// (deploy.sh). The SSO session itself is a random value checked against the database, not a protected cookie.
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("sso");
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// The REST API (ADR 0002, 0008): handlers of the modules, their refusals as problems, JSON that refuses unknown
// fields, and the OpenAPI document the generated client and Scalar read.
builder.Services.AddHandlers();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.Configure<JsonOptions>(o => ApiJson.Configure(o.SerializerOptions));
builder.Services.AddOpenApi(o =>
{
    o.ShouldInclude = description =>
        description.RelativePath?.StartsWith("api/", StringComparison.Ordinal) == true;
    o.CreateSchemaReferenceId = ApiDocument.SchemaName;
    o.AddDocumentTransformer(
        (document, _, _) =>
        {
            document.Info = new OpenApiInfo
            {
                Title = "Great SSO API",
                Version = "1",
                Description =
                    "The panel's REST API: people, applications, activity, marketing and MCP tokens. "
                    + "Every endpoint needs a signed-in Great SSO admin (the session or its bearer token).",
            };
            return Task.CompletedTask;
        }
    );
});

var health = builder
    .Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: [HealthTags.Ready]);

// The broker only where one is configured (ADR 0010): Great SSO publishes no integration event yet, so it can run
// without the runner and RabbitMQ; locally (deploy/deploy.sh) both are there.
if (builder.Configuration["RabbitMq:Host"] is { Length: > 0 })
{
    builder.AddRabbitMq();
    health.AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: [HealthTags.Ready]);
}

// The first admin and the demo data on an empty database (ADR 0015).
builder.AddSeed();

// Marketing e-mails (ADR 0014): the background sender, one e-mail per address over SMTP; off without Smtp:Host.
builder.AddMarketingMail();

// The front: Blazor, interactive server, in this same host (ADR 0002).
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.AddSsoApiClient();

var app = builder.Build();

// First: the request logging, the session cookie and the rate limiter see the browser's scheme and address.
app.UseReverseProxy();

// Before the exception handler: an unhandled exception is logged with its request, not as a plain 500.
app.UseSsoRequestLogging();
app.UseExceptionHandler();

// Pages only: an API status keeps its JSON (or empty) body instead of the Blazor "not found" page.
app.UseWhen(
    http => !AuthSetup.IsApi(http.Request.Path),
    pages =>
        pages.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true)
);
app.UseCrossSiteWriteGuard();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapApi();

// The client generator (apps/Sso.ApiClient/generate.sh) reads it; it describes the API, never data.
app.MapOpenApi().AllowAnonymous();

app.MapAuth();

// The development sign-in stub, when it stands in for the providers (Development only, ADR 0018).
if (app.Services.GetRequiredService<ProviderCatalog>().IsDevelopmentStub)
    app.MapDevSignIn();

// The OpenID Connect provider for the applications (ADR 0012).
app.MapOidc();

// Claude Code configures Great SSO through MCP with an SSO admin's personal token (ADR 0013).
app.MapSsoMcp();

// The admin's panel lives under /admin; the root has nothing of its own yet.
app.MapGet("/", () => Results.Redirect("/admin")).AllowAnonymous();

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks(
        "/health/ready",
        new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(HealthTags.Ready),
            ResponseWriter = HealthResponse.WriteAsync,
        }
    )
    .AllowAnonymous();

// The API reference over the OpenAPI document, behind the sign-in. Nothing leaves the machine: no fonts from
// Scalar's CDN, no telemetry, no AI agent. In Great SSO's palette.
app.MapScalarApiReference(o =>
    o.WithTitle("Great SSO API")
        .AddDocument("v1", "Great SSO API", "/openapi/v1.json")
        .DisableDefaultFonts()
        .DisableTelemetry()
        .DisableAgent()
        .DisableMcp()
        .ForceDarkMode()
        .HideDarkModeToggle()
        .WithCustomCss(ScalarPalette.Css)
);

// Assets carry no data and the sign-in page needs them.
app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// An unknown /api/x must stay a 404, not become the Blazor "not found" page with 200.
foreach (var prefix in AuthSetup.ApiPrefixes)
    app.MapFallback($"{prefix}/{{**path}}", () => Results.NotFound()).AllowAnonymous();

await app.RunAsync();
return 0;

// The entry point class, public for WebApplicationFactory<Program> in the tests.
[UsedImplicitly]
public partial class Program;
