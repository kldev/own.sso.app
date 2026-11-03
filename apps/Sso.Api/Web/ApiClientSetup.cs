using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Sso.Api.Auth;
using Sso.ApiClient.Generated;

namespace Sso.Api.Web;

/// <summary>
/// The Blazor pages use the client generated from the API's OpenAPI document (Kiota), like any other client would, and
/// call this same host over HTTP: the API stays the only way to the data, also for our own front (ADR 0002).
/// </summary>
public static class ApiClientSetup
{
    private const string HttpClientName = "sso-self";

    public static WebApplicationBuilder AddSsoApiClient(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<SelfAddress>();
        builder.Services.AddHttpClient(HttpClientName);
        builder.Services.AddScoped<SessionTokenProvider>();
        // One client per circuit: the token comes from the signed-in user of that circuit.
        builder.Services.AddScoped(sp =>
        {
            var auth = new BaseBearerTokenAuthenticationProvider(
                sp.GetRequiredService<SessionTokenProvider>()
            );
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            var adapter = new HttpClientRequestAdapter(auth, httpClient: http)
            {
                BaseUrl = sp.GetRequiredService<SelfAddress>().BaseUrl,
            };
            return new SsoApi(adapter);
        });
        builder.Services.AddScoped<CurrentUser>();
        return builder;
    }
}

/// <summary>
/// Where this host listens, for its own client: the first address of the server (launchSettings for dotnet run,
/// ASPNETCORE_HTTP_PORTS in the container), with a wildcard host turned into localhost. Read on first use - after start.
/// </summary>
public sealed class SelfAddress(IServer server)
{
    public string BaseUrl => field ??= Resolve();

    private string Resolve()
    {
        var address =
            server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("The server has no address yet.");
        var uri = new UriBuilder(
            address.Replace("://+", "://localhost").Replace("://*", "://localhost")
        );
        if (uri.Host is "0.0.0.0" or "[::]" or "::")
            uri.Host = "localhost";
        return uri.Uri.GetLeftPart(UriPartial.Authority);
    }
}

/// <summary>The access token of the circuit's user, kept in the session cookie (claim sso:access_token).</summary>
public sealed class SessionTokenProvider(AuthenticationStateProvider authState)
    : IAccessTokenProvider
{
    public AllowedHostsValidator AllowedHostsValidator { get; } = new(["localhost", "127.0.0.1"]);

    public async Task<string> GetAuthorizationTokenAsync(
        Uri uri,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default
    )
    {
        var state = await authState.GetAuthenticationStateAsync();
        return state.User.FindFirstValue(SessionClaims.AccessToken) ?? "";
    }
}
