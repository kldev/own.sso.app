using System.Security.Claims;
using Sso.Api.Features.Common;

namespace Sso.Api.Features.Activity;

/// <summary>Module activity (ADR 0005): the sessions and the full log of sign-ins and tokens, for the admin.</summary>
public static class ActivityEndpoints
{
    public static RouteGroupBuilder MapActivity(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/activity").WithTags("activity");
        group
            .MapGet(
                "/sessions",
                (HttpRequest http, ClaimsPrincipal user, ListSessions h, CancellationToken ct) =>
                    h.HandleAsync(user, http.Query, ct)
            )
            .WithList(ListSessions.Spec);
        group
            .MapGet(
                "/sessions/{id:guid}",
                (Guid id, ClaimsPrincipal user, ListSessions h, CancellationToken ct) =>
                    EndpointExtensions.OneAsync(h.HandleAsync(user, IdQuery.Of(id), ct))
            )
            .Produces<ActivitySessionsRow>()
            .WithProblems(404);
        group
            .MapGet(
                "/events",
                (HttpRequest http, ClaimsPrincipal user, ListEvents h, CancellationToken ct) =>
                    h.HandleAsync(user, http.Query, ct)
            )
            .WithList(ListEvents.Spec);
        group
            .MapPost(
                "/session-end",
                async (
                    ActivitySessionEndRequest r,
                    ClaimsPrincipal user,
                    EndSession h,
                    CancellationToken ct
                ) =>
                {
                    await h.HandleAsync(user, r, ct);
                    return Results.NoContent();
                }
            )
            .WithProblems(400, 403, 404);
        return api;
    }
}
