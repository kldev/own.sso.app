using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Sso.Api.Auth.Providers;
using Sso.Api.Oidc;

namespace Sso.Api.Auth;

/// <summary>
/// Signing in through a provider: GET /auth/external/{provider} sends the browser to the provider (a 404 for a provider
/// that is not configured, a refusal when the application does not allow it), the provider's handler comes back to
/// the external cookie, and GET /auth/external/{provider}/done decides: ExternalSignIn finds or links the
/// person entered by the admin, and an SSO session starts. Nobody unknown gets anything - there is no registration.
/// </summary>
public static class ExternalSignInEndpoints
{
    public const string ReturnUrlItem = "sso.returnUrl";

    public static IEndpointRouteBuilder MapExternalSignIn(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/external/{provider}", StartAsync)
            .AllowAnonymous()
            .RequireRateLimiting(AuthSetup.SignInRateLimit);
        app.MapGet("/auth/external/{provider}/done", DoneAsync).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> StartAsync(
        string provider,
        string? returnUrl,
        HttpContext http,
        ProviderCatalog providers,
        ClientStore clients,
        SignInRefusals refusals,
        CancellationToken ct
    )
    {
        if (providers.Find(provider) is null)
            return Results.NotFound();
        var target = AuthEndpoints.LocalUrl(returnUrl);
        var client = await clients.FromReturnUrlAsync(target, ct);
        if (client is not null && !client.Providers.Contains(provider))
        {
            await refusals.RecordAsync(
                "provider_not_allowed",
                client.ClientId,
                provider,
                RequestInfo.From(http),
                ct
            );
            return Results.Redirect(
                refusals.PageUrl(new Refusal("provider_not_allowed", provider, null), target)
            );
        }
        var properties = new AuthenticationProperties
        {
            RedirectUri = $"/auth/external/{provider}/done",
            Items = { [ReturnUrlItem] = target },
        };
        return Results.Challenge(properties, [provider]);
    }

    private static async Task<IResult> DoneAsync(
        string provider,
        HttpContext http,
        ProviderCatalog providers,
        ExternalSignIn signIn,
        SsoSessions sessions,
        SignInRefusals refusals,
        IOptions<SsoSessionOptions> sessionOptions,
        CancellationToken ct
    )
    {
        if (providers.Find(provider) is null)
            return Results.NotFound();
        var result = await http.AuthenticateAsync(ProviderSetup.ExternalScheme);
        await http.SignOutAsync(ProviderSetup.ExternalScheme);
        if (!result.Succeeded)
            return Results.Redirect("/sign-in?error=provider");

        var returnUrl = AuthEndpoints.LocalUrl(
            result.Properties?.Items.TryGetValue(ReturnUrlItem, out var url) == true ? url : null
        );
        var account = ExternalAccount.FromPrincipal(provider, result.Principal!);
        if (account is null)
            return Results.Redirect(
                $"/sign-in?error=provider&returnUrl={Uri.EscapeDataString(returnUrl)}"
            );
        var clientId = ClientStore.ClientIdOf(returnUrl);
        var request = RequestInfo.From(http);

        if (account.Refusal is { } early)
        {
            await refusals.RecordAsync(early, clientId, provider, request, ct);
            return Results.Redirect(
                refusals.PageUrl(new Refusal(early, provider, account.Email), returnUrl)
            );
        }

        var (outcome, userId) = await signIn.SignInAsync(account, clientId, request, ct);
        if (outcome != "ok")
            return Results.Redirect(
                refusals.PageUrl(new Refusal(outcome, provider, account.Email), returnUrl)
            );

        var (token, expires) = await sessions.StartAsync(userId, provider, clientId, request, ct);
        SsoSessionHandler.WriteCookie(http, sessionOptions.Value, token, expires);
        return Results.LocalRedirect(returnUrl);
    }
}
