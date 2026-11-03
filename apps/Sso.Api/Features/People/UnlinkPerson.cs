using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Sso.Api.Features.Common;
using Sso.Api.Oidc;
using Sso.Data;
using Sso.Data.Identity;

namespace Sso.Api.Features.People;

public sealed record PeoplePersonUnlinkRequest(Guid? Id, string? Provider);

/// <summary>
/// POST /api/people/person-unlink: the SSO admin removes a person's link to one provider account, keeping the e-mail:
/// the account stops working at once with its sessions, and the next sign-in with the record's e-mail links an account
/// again. Nothing to remove is a no-op.
/// </summary>
public sealed class UnlinkPerson(SsoDbContext db, Actors actors, ProviderLinks links) : IHandler
{
    public async Task HandleAsync(
        ClaimsPrincipal user,
        PeoplePersonUnlinkRequest r,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        actor.RequireAdmin(PersonValues.Forbidden, "Only an SSO admin removes a link.");
        if (!await db.People.AnyAsync(p => p.Id == r.Id, ct))
            throw Rule.NotFound(PersonValues.NotFound, "There is no such person.");
        var provider = Text.Trim(r.Provider);
        if (provider is null || !ProviderNames.All.Contains(provider))
            throw Rule.Invalid(
                "people.provider_invalid",
                "The provider is github, google or microsoft."
            );
        if (await links.UnlinkAsync(db, r.Id!.Value, provider, ct))
            await db.SaveChangesAsync(ct);
    }
}
