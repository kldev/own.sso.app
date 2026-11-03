using System.Security.Claims;
using Sso.Api.Features.Common;

namespace Sso.Api.Features.Marketing;

/// <summary>Module marketing (ADR 0014): templates, mailings and their recipients.</summary>
public static class MarketingEndpoints
{
    public static RouteGroupBuilder MapMarketing(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/marketing").WithTags("marketing");
        group
            .MapGet(
                "/templates",
                (HttpRequest http, ClaimsPrincipal user, ListTemplates h, CancellationToken ct) =>
                    h.HandleAsync(user, http.Query, ct)
            )
            .WithList(ListTemplates.Spec);
        group
            .MapGet(
                "/templates/{id:guid}",
                (Guid id, ClaimsPrincipal user, ListTemplates h, CancellationToken ct) =>
                    EndpointExtensions.OneAsync(h.HandleAsync(user, IdQuery.Of(id), ct))
            )
            .Produces<MarketingTemplatesRow>()
            .WithProblems(404);
        group
            .MapGet(
                "/mailings",
                (HttpRequest http, ClaimsPrincipal user, ListMailings h, CancellationToken ct) =>
                    h.HandleAsync(user, http.Query, ct)
            )
            .WithList(ListMailings.Spec);
        group
            .MapGet(
                "/mailings/{id:guid}",
                (Guid id, ClaimsPrincipal user, ListMailings h, CancellationToken ct) =>
                    EndpointExtensions.OneAsync(h.HandleAsync(user, IdQuery.Of(id), ct))
            )
            .Produces<MarketingMailingsRow>()
            .WithProblems(404);
        group
            .MapGet(
                "/recipients",
                (HttpRequest http, ClaimsPrincipal user, ListRecipients h, CancellationToken ct) =>
                    h.HandleAsync(user, http.Query, ct)
            )
            .WithList(ListRecipients.Spec);
        group
            .MapGet(
                "/recipients/{id:guid}",
                (Guid id, ClaimsPrincipal user, ListRecipients h, CancellationToken ct) =>
                    EndpointExtensions.OneAsync(h.HandleAsync(user, IdQuery.Of(id), ct))
            )
            .Produces<MarketingRecipientsRow>()
            .WithProblems(404);
        group
            .MapPost(
                "/template-update",
                (
                    MarketingTemplateUpdateRequest r,
                    ClaimsPrincipal user,
                    UpdateTemplate h,
                    CancellationToken ct
                ) => h.HandleAsync(user, r, ct)
            )
            .WithProblems(400, 403, 404, 409);
        group
            .MapPost(
                "/mailing-create",
                (
                    MarketingMailingCreateRequest r,
                    ClaimsPrincipal user,
                    CreateMailing h,
                    CancellationToken ct
                ) => h.HandleAsync(user, r, ct)
            )
            .WithProblems(400, 403, 404, 409);
        group
            .MapPost(
                "/mailing-retry",
                (
                    MarketingMailingRetryRequest r,
                    ClaimsPrincipal user,
                    RetryMailing h,
                    CancellationToken ct
                ) => h.HandleAsync(user, r, ct)
            )
            .WithProblems(400, 403, 404);
        return api;
    }
}
