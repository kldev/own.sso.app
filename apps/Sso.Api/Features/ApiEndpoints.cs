using Sso.Api.Features.Activity;
using Sso.Api.Features.Clients;
using Sso.Api.Features.Marketing;
using Sso.Api.Features.People;
using Sso.Api.Features.Platform;

namespace Sso.Api.Features;

/// <summary>
/// The REST API of the panel under /api (ADR 0002, 0005): the modules in their dependency order - platform, people,
/// clients, activity, marketing. Every endpoint needs a signed-in person (the session cookie or the circuit's bearer);
/// the panel calls them with the generated client, MCP's tools call the same handlers.
/// </summary>
public static class ApiEndpoints
{
    public static RouteGroupBuilder MapApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapPlatform();
        api.MapPeople();
        api.MapClients();
        api.MapActivity();
        api.MapMarketing();
        return api;
    }
}
