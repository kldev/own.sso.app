using System.Security.Claims;
using Sso.Api.Features.Common;

namespace Sso.Api.Features.Platform;

/// <summary>Module platform: the signed-in person and an admin's MCP tokens.</summary>
public static class PlatformEndpoints
{
    public static RouteGroupBuilder MapPlatform(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/platform").WithTags("platform");
        group
            .MapGet(
                "/me",
                (HttpRequest http, ClaimsPrincipal user, GetMe h, CancellationToken ct) =>
                    h.HandleAsync(user, http.Query, ct)
            )
            .WithList(GetMe.Spec);
        group
            .MapGet(
                "/me/{id:guid}",
                (Guid id, ClaimsPrincipal user, GetMe h, CancellationToken ct) =>
                    EndpointExtensions.OneAsync(h.HandleAsync(user, IdQuery.Of(id), ct))
            )
            .Produces<PlatformMeRow>()
            .WithProblems(404);
        group
            .MapGet(
                "/mcp-tokens",
                (HttpRequest http, ClaimsPrincipal user, ListMcpTokens h, CancellationToken ct) =>
                    h.HandleAsync(user, http.Query, ct)
            )
            .WithList(ListMcpTokens.Spec);
        group
            .MapGet(
                "/mcp-tokens/{id:guid}",
                (Guid id, ClaimsPrincipal user, ListMcpTokens h, CancellationToken ct) =>
                    EndpointExtensions.OneAsync(h.HandleAsync(user, IdQuery.Of(id), ct))
            )
            .Produces<PlatformMcpTokensRow>()
            .WithProblems(404);
        group
            .MapPost(
                "/mcp-token-issue",
                async (
                    PlatformMcpTokenIssueRequest r,
                    ClaimsPrincipal user,
                    IssueMcpToken h,
                    CancellationToken ct
                ) =>
                {
                    await h.HandleAsync(user, r, ct);
                    return Results.NoContent();
                }
            )
            .WithProblems(400, 403, 409);
        group
            .MapPost(
                "/mcp-token-revoke",
                async (
                    PlatformMcpTokenRevokeRequest r,
                    ClaimsPrincipal user,
                    RevokeMcpToken h,
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
