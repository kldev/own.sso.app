using System.Security.Claims;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Sso.Api.Features.Common;
using Sso.Api.Oidc;
using Sso.Data;
using Sso.Data.Identity;
using Sso.Data.Oidc;

namespace Sso.Api.Features.People;

public sealed record PeoplePersonSetStatusRequest(Guid? Id, string? Status, long? RowVersion);

[UsedImplicitly(ImplicitUseTargetFlags.Members)] // serialized to JSON
public sealed record PeoplePersonSetStatusResult(long RowVersion);

/// <summary>
/// POST /api/people/person-set-status: the SSO admin deactivates or reactivates a person. Deactivating ends every
/// session of the person at once (and their applications' refresh tokens); nobody deactivates themselves.
/// </summary>
public sealed class SetPersonStatus(SsoDbContext db, Actors actors, SessionTermination sessions)
    : IHandler
{
    public async Task<PeoplePersonSetStatusResult> HandleAsync(
        ClaimsPrincipal user,
        PeoplePersonSetStatusRequest r,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        actor.RequireAdmin(PersonValues.Forbidden, "Only an SSO admin changes a person's status.");
        var person =
            await db.People.FirstOrDefaultAsync(p => p.Id == r.Id, ct)
            ?? throw Rule.NotFound(PersonValues.NotFound, "There is no such person.");
        Handlers.CheckVersion(person.RowVersion, r.RowVersion, PersonValues.Stale);
        var status = Text.Trim(r.Status);
        if (status is null || !Statuses.All.Contains(status))
            throw Rule.Invalid("people.status_invalid", "The status is active or inactive.");
        if (person.Id == actor.Id && status == Statuses.Inactive)
            throw Rule.Invalid("people.self_change", "You cannot deactivate your own account.");
        if (person.Status == status)
            return new PeoplePersonSetStatusResult(Handlers.Version(person.RowVersion));

        person.Status = status;
        if (status == Statuses.Inactive)
            await sessions.EndAsync(db, person.Id, null, null, SessionEndReasons.UserInactive, ct);
        db.Entry(person).Property(p => p.RowVersion).OriginalValue = (uint)r.RowVersion!.Value;
        await Handlers.SaveAsync(db, ct, PersonValues.Stale);
        return new PeoplePersonSetStatusResult(Handlers.Version(person.RowVersion));
    }
}
