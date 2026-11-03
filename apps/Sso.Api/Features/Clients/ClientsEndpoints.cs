using System.Security.Claims;
using Sso.Api.Features.Common;

namespace Sso.Api.Features.Clients;

/// <summary>Module clients (ADR 0005): the registry of applications that sign people in through Great SSO.</summary>
public static class ClientsEndpoints
{
    public static RouteGroupBuilder MapClients(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/clients").WithTags("clients");
        group
            .MapGet(
                "/clients",
                (HttpRequest http, ClaimsPrincipal user, ListClients h, CancellationToken ct) =>
                    h.HandleAsync(user, http.Query, ct)
            )
            .WithList(ListClients.Spec);
        group
            .MapGet(
                "/clients/{id:guid}",
                (Guid id, ClaimsPrincipal user, ListClients h, CancellationToken ct) =>
                    EndpointExtensions.OneAsync(h.HandleAsync(user, IdQuery.Of(id), ct))
            )
            .Produces<ClientsClientsRow>()
            .WithProblems(404);
        group
            .MapPost(
                "/client-create",
                (
                    ClientsClientCreateRequest r,
                    ClaimsPrincipal user,
                    CreateClient h,
                    CancellationToken ct
                ) => h.HandleAsync(user, r, ct)
            )
            .WithProblems(400, 403, 409);
        group
            .MapPost(
                "/client-update",
                (
                    ClientsClientUpdateRequest r,
                    ClaimsPrincipal user,
                    UpdateClient h,
                    CancellationToken ct
                ) => h.HandleAsync(user, r, ct)
            )
            .WithProblems(400, 403, 404, 409);
        group
            .MapPost(
                "/client-set-secret",
                (
                    ClientsClientSetSecretRequest r,
                    ClaimsPrincipal user,
                    SetClientSecret h,
                    CancellationToken ct
                ) => h.HandleAsync(user, r, ct)
            )
            .WithProblems(400, 403, 404, 409);
        group
            .MapPost(
                "/client-set-status",
                (
                    ClientsClientSetStatusRequest r,
                    ClaimsPrincipal user,
                    SetClientStatus h,
                    CancellationToken ct
                ) => h.HandleAsync(user, r, ct)
            )
            .WithProblems(400, 403, 404, 409);
        return api;
    }
}
