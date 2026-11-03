using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Sso.Api.Auth.Providers;

/// <summary>An external provider SSO signs in with.</summary>
public sealed record Provider(string Key, string DisplayName);

/// <summary>The providers that are configured in this deployment, in the order the sign-in page shows them.</summary>
public sealed class ProviderCatalog(IReadOnlyList<Provider> configured)
{
    public static readonly IReadOnlyList<Provider> All =
    [
        new("github", "GitHub"),
        new("google", "Google"),
        new("microsoft", "Microsoft"),
    ];

    public IReadOnlyList<Provider> Configured { get; } = configured;

    /// <summary>The providers are the development sign-in stub (DevSignIn), not the real ones.</summary>
    public bool IsDevelopmentStub { get; init; }

    public Provider? Find(string? key) =>
        Configured.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal));

    public static string DisplayName(string? key) =>
        All.FirstOrDefault(p => p.Key == key)?.DisplayName ?? key ?? "";
}

/// <summary>
/// Registers the handlers of the configured providers: GitHub with the framework's OAuth handler (GitHub has no
/// OpenID Connect for people), Google and Microsoft with its OpenID Connect handler - no third-party packages
/// (ADR 0011). Each signs in to the short external cookie; /auth/external/{provider}/done turns it into an SSO session.
/// </summary>
public static class ProviderSetup
{
    public const string ExternalScheme = "SsoExternal";

    public static ProviderCatalog AddSsoProviders(
        this AuthenticationBuilder auth,
        IConfiguration configuration,
        IHostEnvironment environment
    )
    {
        var options =
            configuration.GetSection(ProviderOptions.SectionName).Get<ProviderOptions>()
            ?? new ProviderOptions();
        var configured = new List<Provider>();

        auth.AddCookie(
            ExternalScheme,
            o =>
            {
                o.Cookie.Name = "sso.external";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromMinutes(10);
                o.SlidingExpiration = false;
            }
        );

        // The development stub replaces every provider (ADR 0018); never outside Development.
        if (auth.AddDevSignIn(configuration, environment, configured))
        {
            var stubbed = new ProviderCatalog(configured) { IsDevelopmentStub = true };
            auth.Services.AddSingleton(stubbed);
            auth.Services.AddSingleton(options);
            return stubbed;
        }

        if (options.GitHub.IsConfigured)
        {
            configured.Add(ProviderCatalog.All[0]);
            auth.AddOAuth("github", o => ConfigureGitHub(o, options.GitHub));
        }
        if (options.Google.IsConfigured)
        {
            configured.Add(ProviderCatalog.All[1]);
            auth.AddOpenIdConnect("google", o => ConfigureGoogle(o, options.Google));
        }
        if (options.Microsoft.IsConfigured)
        {
            configured.Add(ProviderCatalog.All[2]);
            auth.AddOpenIdConnect("microsoft", o => ConfigureMicrosoft(o, options.Microsoft));
        }

        var catalog = new ProviderCatalog(configured);
        auth.Services.AddSingleton(catalog);
        auth.Services.AddSingleton(options);
        return catalog;
    }

    private static void ConfigureGitHub(OAuthOptions o, ProviderCredentials credentials)
    {
        o.SignInScheme = ExternalScheme;
        o.ClientId = credentials.ClientId!;
        o.ClientSecret = credentials.ClientSecret!;
        o.CallbackPath = "/signin/github";
        o.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
        o.TokenEndpoint = "https://github.com/login/oauth/access_token";
        o.UserInformationEndpoint = "https://api.github.com/user";
        o.UsePkce = true;
        o.Scope.Add("read:user");
        o.Scope.Add("user:email");
        o.SaveTokens = false;
        o.Events.OnCreatingTicket = async context =>
        {
            using var user = await GitHubGetAsync(context, context.Options.UserInformationEndpoint);
            using var emails = await GitHubGetAsync(context, "https://api.github.com/user/emails");
            var account = GitHubAccount.From(user.RootElement, emails.RootElement);
            context.Principal = new ClaimsPrincipal(account.ToIdentity(ExternalScheme));
        };
        o.Events.OnRemoteFailure = RemoteFailure;
    }

    private static async Task<JsonDocument> GitHubGetAsync(
        OAuthCreatingTicketContext context,
        string url
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            context.AccessToken
        );
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json")
        );
        // GitHub refuses requests without a User-Agent.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("great-sso", "1.0"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await context.Backchannel.SendAsync(
            request,
            context.HttpContext.RequestAborted
        );
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted),
            cancellationToken: context.HttpContext.RequestAborted
        );
    }

    private static void ConfigureOidc(
        OpenIdConnectOptions o,
        ProviderCredentials credentials,
        string callback
    )
    {
        o.SignInScheme = ExternalScheme;
        o.ClientId = credentials.ClientId;
        o.ClientSecret = credentials.ClientSecret;
        o.CallbackPath = callback;
        o.ResponseType = "code";
        // The answer comes back as a GET (top-level redirect), not the handler's default form_post: a POST from
        // the provider's site is refused by the cross-site write guard (403), and a SameSite=Lax correlation cookie
        // would not ride along with it anyway.
        o.ResponseMode = OpenIdConnectResponseMode.Query;
        o.UsePkce = true;
        o.Scope.Clear();
        o.Scope.Add("openid");
        o.Scope.Add("email");
        o.Scope.Add("profile");
        o.MapInboundClaims = false;
        o.SaveTokens = false;
        o.GetClaimsFromUserInfoEndpoint = false;
        // No sign-out through the providers (ADR 0011: no sign-out in Great SSO).
        o.SignedOutCallbackPath = callback + "-signed-out";
        o.Events.OnRemoteFailure = RemoteFailure;
    }

    private static void ConfigureGoogle(OpenIdConnectOptions o, ProviderCredentials credentials)
    {
        ConfigureOidc(o, credentials, "/signin/google");
        o.Authority = "https://accounts.google.com";
        o.Events.OnTokenValidated = context =>
        {
            context.Principal = new ClaimsPrincipal(
                GoogleAccount.From(context.Principal!).ToIdentity(ExternalScheme)
            );
            return Task.CompletedTask;
        };
    }

    private static void ConfigureMicrosoft(OpenIdConnectOptions o, MicrosoftCredentials credentials)
    {
        ConfigureOidc(o, credentials, "/signin/microsoft");
        // Work and school accounts only; the organisations are checked on the token (below and in MicrosoftAccount).
        o.Authority = "https://login.microsoftonline.com/organizations/v2.0";
        var allowed = credentials
            .AllowedTenantIds.Where(t => !string.IsNullOrWhiteSpace(t))
            .ToArray();
        o.TokenValidationParameters.IssuerValidator = (issuer, token, _) =>
        {
            var tenantId =
                token is JsonWebToken jwt && jwt.TryGetPayloadValue<string>("tid", out var tid)
                    ? tid
                    : null;
            return MicrosoftAccount.IsValidIssuer(issuer, tenantId, allowed)
                ? issuer
                : throw new SecurityTokenInvalidIssuerException(
                    "The Microsoft organisation of this account is not allowed."
                );
        };
        o.Events.OnTokenValidated = context =>
        {
            context.Principal = new ClaimsPrincipal(
                MicrosoftAccount.From(context.Principal!, allowed).ToIdentity(ExternalScheme)
            );
            return Task.CompletedTask;
        };
    }

    /// <summary>The person cancelled at the provider, or the provider's answer was invalid: back to the sign-in page.</summary>
    private static async Task RemoteFailure(RemoteFailureContext context)
    {
        var returnUrl =
            context.Properties?.Items.TryGetValue(
                ExternalSignInEndpoints.ReturnUrlItem,
                out var url
            ) == true
                ? url
                : null;
        if (context.Failure is SecurityTokenInvalidIssuerException)
        {
            // A Microsoft account of an organisation not on the list: refused before anything is looked up.
            var refusals = context.HttpContext.RequestServices.GetRequiredService<SignInRefusals>();
            await refusals.RecordAsync(
                "tenant_not_allowed",
                Oidc.ClientStore.ClientIdOf(returnUrl),
                context.Scheme.Name,
                RequestInfo.From(context.HttpContext),
                context.HttpContext.RequestAborted
            );
            context.Response.Redirect(
                refusals.PageUrl(
                    new Refusal("tenant_not_allowed", context.Scheme.Name, null),
                    returnUrl
                )
            );
        }
        else
        {
            var back = returnUrl is null ? "" : $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
            context.Response.Redirect($"/sign-in?error=provider{back}");
        }
        context.HandleResponse();
    }
}
