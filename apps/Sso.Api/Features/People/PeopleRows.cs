using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Sso.Api.Features.Common;
using Sso.Data;
using Sso.Data.Identity;
using Sso.Data.Oidc;

namespace Sso.Api.Features.People;

/// <summary>A person of Great SSO with the provider e-mails and links - what the panel's list shows.</summary>
public sealed class PeoplePeopleRow
{
    public required Guid Id { get; init; }
    public required string Username { get; init; }
    public required string Email { get; init; }
    public required string DisplayName { get; init; }
    public required string Role { get; init; }
    public required string Status { get; init; }
    public string? GithubEmail { get; init; }
    public string? GoogleEmail { get; init; }
    public string? MicrosoftEmail { get; init; }
    public DateTimeOffset? GithubLinkedAt { get; init; }
    public DateTimeOffset? GoogleLinkedAt { get; init; }
    public DateTimeOffset? MicrosoftLinkedAt { get; init; }
    public string? MicrosoftTenantId { get; init; }
    public DateTimeOffset? LastSignInAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonIgnore]
    public uint Xmin { get; init; }

    public long RowVersion => Xmin;
}

/// <summary>GET /api/people/people and /api/people/people/{id}: every person - for an active SSO admin only.</summary>
public sealed class ListPeople(SsoDbContext db, Actors actors) : IHandler
{
    public static readonly ListSpec<PeoplePeopleRow> Spec = new ListSpec<PeoplePeopleRow>()
        .Add("id", r => r.Id, Filter.Eq)
        .Add("username", r => r.Username, Filter.Eq)
        .Add("email", r => r.Email)
        .Add("displayName", r => r.DisplayName)
        .Add("role", r => r.Role, Filter.Eq)
        .Add("status", r => r.Status, Filter.Eq)
        .Add("githubEmail", r => r.GithubEmail)
        .Add("googleEmail", r => r.GoogleEmail)
        .Add("microsoftEmail", r => r.MicrosoftEmail)
        .Add("githubLinkedAt", r => r.GithubLinkedAt, Filter.Range)
        .Add("googleLinkedAt", r => r.GoogleLinkedAt, Filter.Range)
        .Add("microsoftLinkedAt", r => r.MicrosoftLinkedAt, Filter.Range)
        .Add("microsoftTenantId", r => r.MicrosoftTenantId, Filter.Eq)
        .Add("lastSignInAt", r => r.LastSignInAt, Filter.Range)
        .Add("createdAt", r => r.CreatedAt, Filter.Range)
        .Add("rowVersion", r => r.Xmin, Filter.Eq);

    public async Task<ListPage<PeoplePeopleRow>> HandleAsync(
        ClaimsPrincipal user,
        IQueryCollection query,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        return await Spec.PageAsync(Rows(actor.IsAdmin), query, ct);
    }

    private IQueryable<PeoplePeopleRow> Rows(bool visible) =>
        db
            .People.AsNoTracking()
            .Where(_ => visible)
            .Select(u => new PeoplePeopleRow
            {
                Id = u.Id,
                Username = u.Username,
                Email = u.Email,
                DisplayName = u.DisplayName,
                Role = u.Role,
                Status = u.Status,
                GithubEmail = u.GithubEmail,
                GoogleEmail = u.GoogleEmail,
                MicrosoftEmail = u.MicrosoftEmail,
                GithubLinkedAt = db
                    .ExternalLogins.Where(l =>
                        l.UserId == u.Id && l.Provider == ProviderNames.GitHub
                    )
                    .Select(l => (DateTimeOffset?)l.LinkedAt)
                    .FirstOrDefault(),
                GoogleLinkedAt = db
                    .ExternalLogins.Where(l =>
                        l.UserId == u.Id && l.Provider == ProviderNames.Google
                    )
                    .Select(l => (DateTimeOffset?)l.LinkedAt)
                    .FirstOrDefault(),
                MicrosoftLinkedAt = db
                    .ExternalLogins.Where(l =>
                        l.UserId == u.Id && l.Provider == ProviderNames.Microsoft
                    )
                    .Select(l => (DateTimeOffset?)l.LinkedAt)
                    .FirstOrDefault(),
                MicrosoftTenantId = db
                    .ExternalLogins.Where(l =>
                        l.UserId == u.Id && l.Provider == ProviderNames.Microsoft
                    )
                    .Select(l => l.TenantId)
                    .FirstOrDefault(),
                LastSignInAt = db
                    .Events.Where(e =>
                        e.UserId == u.Id
                        && e.EventType == "sign_in"
                        && e.Outcome == Outcomes.Success
                    )
                    .Max(e => (DateTimeOffset?)e.OccurredAt),
                CreatedAt = u.CreatedAt,
                Xmin = u.RowVersion,
            });
}
