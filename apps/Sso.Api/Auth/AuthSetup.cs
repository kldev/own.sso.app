using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Sso.Api.Auth.Providers;
using Sso.Api.Features.Common;
using Sso.Api.Mcp;
using Sso.Api.Oidc;
using Sso.Data;

namespace Sso.Api.Auth;

public static class AuthPolicies
{
    /// <summary>An SSO admin (identity.users.role = admin): the panel.</summary>
    public const string Admin = "admin";
}

/// <summary>Claims of the session (cookie) and of the internal access token.</summary>
public static class SessionClaims
{
    public const string UserId = "sub";
    public const string Name = "name";
    public const string Username = "preferred_username";
    public const string Email = "email";
    public const string Role = "role";
    public const string SessionId = "sid";
    public const string AccessToken = "sso:access_token";
}

/// <summary>
/// One host, two ways in (ADR 0002): the browser has the SSO session cookie (SsoSessionHandler); the Blazor circuit calls the
/// API with the generated client, server side, with the session's access token as a bearer. A policy scheme picks
/// one per request, so every endpoint simply requires an authenticated user and reads the actor from "sub".
/// </summary>
public static class AuthSetup
{
    private const string SelectorScheme = "SessionOrBearer";
    public const string SignInRateLimit = "sign-in";

    /// <summary>Paths that answer with a status and a JSON body, never with a page.</summary>
    public static readonly string[] ApiPrefixes =
    [
        "/api",
        "/openapi",
        "/internal",
        "/auth",
        "/connect",
        "/.well-known",
        "/mcp",
    ];

    public static bool IsApi(PathString path) => ApiPrefixes.Any(p => path.StartsWithSegments(p));

    public static WebApplicationBuilder AddSsoAuth(this WebApplicationBuilder builder)
    {
        builder
            .Services.AddOptions<JwtOptions>()
            .BindConfiguration(JwtOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder
            .Services.AddOptions<SsoSessionOptions>()
            .BindConfiguration(SsoSessionOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var auth = builder
            .Services.AddAuthentication(SelectorScheme)
            .AddPolicyScheme(
                SelectorScheme,
                "SSO session or bearer",
                o =>
                    o.ForwardDefaultSelector = http =>
                        // /mcp only with an MCP token, and an MCP token nowhere else (ADR 0013).
                        http.Request.Path.StartsWithSegments(McpSetup.Path)
                            ? McpTokenHandler.SchemeName
                        : http.Request.Headers.Authorization.ToString().StartsWith("Bearer ")
                            ? JwtBearerDefaults.AuthenticationScheme
                        : SsoSessionHandler.SchemeName
            )
            .AddScheme<AuthenticationSchemeOptions, SsoSessionHandler>(
                SsoSessionHandler.SchemeName,
                null
            )
            .AddJwtBearer();
        auth.AddSsoProviders(builder.Configuration, builder.Environment);
        auth.AddSsoMcp();

        builder
            .Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>(
                (bearer, jwt) =>
                {
                    // Claims keep their JWT names (sub), so the actor is read from "sub" in one place.
                    bearer.MapInboundClaims = false;
                    bearer.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidIssuer = jwt.Value.Issuer,
                        ValidAudience = jwt.Value.Audience,
                        IssuerSigningKey = SigningKey(jwt.Value),
                        NameClaimType = SessionClaims.Name,
                        RoleClaimType = SessionClaims.Role,
                        ClockSkew = TimeSpan.FromSeconds(30),
                    };
                }
            );

        builder
            .Services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.Admin, policy => policy.RequireRole("admin"))
            .AddSsoMcpPolicy()
            // Everything needs a signed-in user unless it says otherwise (sign-in page, assets, /health, /connect).
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        // Sign-in starts per minute per client address; the page comes back with a message.
        var attempts =
            builder
                .Configuration.GetSection(JwtOptions.SectionName)
                .Get<JwtOptions>()
                ?.SignInAttemptsPerMinute
            ?? 20;
        builder.Services.AddRateLimiter(o =>
        {
            o.OnRejected = (ctx, _) =>
            {
                if (
                    IsApi(ctx.HttpContext.Request.Path)
                    && !ctx.HttpContext.Request.Path.StartsWithSegments("/auth")
                )
                    ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                else
                    ctx.HttpContext.Response.Redirect("/sign-in?error=throttled");
                return ValueTask.CompletedTask;
            };
            o.AddPolicy(
                SignInRateLimit,
                http =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = attempts,
                            Window = TimeSpan.FromMinutes(1),
                        }
                    )
            );
        });

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IActorAccessor, HttpActorAccessor>();
        builder.Services.AddSingleton<TokenIssuer>();
        builder.Services.AddSingleton<EventLog>();
        builder.Services.AddSingleton<ProtocolLog>();
        builder.Services.AddSingleton<SessionTermination>();
        builder.Services.AddSingleton<ProviderLinks>();
        builder.Services.AddSingleton<SsoSessions>();
        builder.Services.AddSingleton<ExternalSignIn>();
        builder.Services.AddSingleton<SignInRefusals>();
        builder.Services.AddSingleton<ClientStore>();
        builder.Services.AddSingleton<OidcStore>();
        builder.Services.AddScoped<Actors>();
        return builder;
    }

    public static SymmetricSecurityKey SigningKey(JwtOptions options) =>
        new(Encoding.UTF8.GetBytes(options.SigningKey));

    /// <summary>
    /// Writes from another site are refused: the session cookie would ride along with a form
    /// or a fetch from another origin to an API operation. Browsers send Sec-Fetch-Site and Origin; the Blazor
    /// circuit's own calls (server side) and curl send neither and pass.
    /// </summary>
    public static IApplicationBuilder UseCrossSiteWriteGuard(this IApplicationBuilder app) =>
        app.Use(
            async (context, next) =>
            {
                var request = context.Request;
                // The OpenID Connect endpoints are called from the applications' sites by design (a form post to
                // authorize, the back channel to token); they do not act on the SSO cookie's authority alone.
                if (
                    !HttpMethods.IsGet(request.Method)
                    && !HttpMethods.IsHead(request.Method)
                    && !request.Path.StartsWithSegments("/connect")
                )
                {
                    var site = request.Headers["Sec-Fetch-Site"].ToString();
                    var origin = request.Headers.Origin.ToString();
                    var foreignOrigin =
                        origin.Length > 0
                        && (
                            !Uri.TryCreate(origin, UriKind.Absolute, out var url)
                            || !string.Equals(
                                url.Host,
                                request.Host.Host,
                                StringComparison.OrdinalIgnoreCase
                            )
                        );
                    if (site is "cross-site" or "same-site" || foreignOrigin)
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }
                }
                await next(context);
            }
        );
}
