using System.Security.Claims;
using Sso.Api.Features.Common;

namespace Sso.Api.Features.People;

/// <summary>Module people (ADR 0005): the people an SSO admin enters and their links to provider accounts.</summary>
public static class PeopleEndpoints
{
    public static RouteGroupBuilder MapPeople(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/people").WithTags("people");
        group
            .MapGet(
                "/people",
                (HttpRequest http, ClaimsPrincipal user, ListPeople h, CancellationToken ct) =>
                    h.HandleAsync(user, http.Query, ct)
            )
            .WithList(ListPeople.Spec);
        group
            .MapGet(
                "/people/{id:guid}",
                (Guid id, ClaimsPrincipal user, ListPeople h, CancellationToken ct) =>
                    EndpointExtensions.OneAsync(h.HandleAsync(user, IdQuery.Of(id), ct))
            )
            .Produces<PeoplePeopleRow>()
            .WithProblems(404);
        group
            .MapPost(
                "/person-create",
                (
                    PeoplePersonCreateRequest r,
                    ClaimsPrincipal user,
                    CreatePerson h,
                    CancellationToken ct
                ) => h.HandleAsync(user, r, ct)
            )
            .WithProblems(400, 403, 409);
        group
            .MapPost(
                "/person-update",
                (
                    PeoplePersonUpdateRequest r,
                    ClaimsPrincipal user,
                    UpdatePerson h,
                    CancellationToken ct
                ) => h.HandleAsync(user, r, ct)
            )
            .WithProblems(400, 403, 404, 409);
        group
            .MapPost(
                "/person-set-status",
                (
                    PeoplePersonSetStatusRequest r,
                    ClaimsPrincipal user,
                    SetPersonStatus h,
                    CancellationToken ct
                ) => h.HandleAsync(user, r, ct)
            )
            .WithProblems(400, 403, 404, 409);
        group
            .MapPost(
                "/person-unlink",
                async (
                    PeoplePersonUnlinkRequest r,
                    ClaimsPrincipal user,
                    UnlinkPerson h,
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
