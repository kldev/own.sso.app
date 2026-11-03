using System.Security.Claims;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Sso.Api.Features.Common;
using Sso.Api.Oidc;
using Sso.Data;
using Sso.Data.Identity;

namespace Sso.Api.Features.People;

public sealed record PeoplePersonUpdateRequest(
    Guid? Id,
    string? Username,
    string? Email,
    string? DisplayName,
    string? Role,
    string? GithubEmail,
    string? GoogleEmail,
    string? MicrosoftEmail,
    long? RowVersion
);

[UsedImplicitly(ImplicitUseTargetFlags.Members)] // serialized to JSON
public sealed record PeoplePersonUpdateResult(long RowVersion);

/// <summary>
/// POST /api/people/person-update: the SSO admin edits a person. Changing a provider e-mail of a linked account
/// removes the link in the same step: the old account stops working at once with its sessions, and the next sign-in
/// with the new e-mail links the new account - this is how a person's account is changed, by the admin only. An admin
/// cannot take away their own admin role. 409 when the row changed since it was read (ADR 0007).
/// </summary>
public sealed class UpdatePerson(SsoDbContext db, Actors actors, ProviderLinks links) : IHandler
{
    public async Task<PeoplePersonUpdateResult> HandleAsync(
        ClaimsPrincipal user,
        PeoplePersonUpdateRequest r,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        actor.RequireAdmin(PersonValues.Forbidden, "Only an SSO admin edits people.");
        var person =
            await db.People.FirstOrDefaultAsync(p => p.Id == r.Id, ct)
            ?? throw Rule.NotFound(PersonValues.NotFound, "There is no such person.");
        Handlers.CheckVersion(person.RowVersion, r.RowVersion, PersonValues.Stale);
        var v = PersonValues.Normalise(
            r.Username,
            r.Email,
            r.DisplayName,
            r.Role,
            r.GithubEmail,
            r.GoogleEmail,
            r.MicrosoftEmail
        );
        v.Validate();
        if (person.Id == actor.Id && person.Role == Roles.Admin && v.Role != Roles.Admin)
            throw Rule.Invalid("people.self_change", "You cannot take away your own admin role.");
        var id = person.Id;
        if (await db.People.AnyAsync(p => p.Username == v.Username && p.Id != id, ct))
            throw PersonValues.UsernameTaken;
        if (await db.People.AnyAsync(p => p.Email == v.Email && p.Id != id, ct))
            throw PersonValues.EmailTaken;
        if (
            await db.People.AnyAsync(
                p =>
                    p.Id != id
                    && (
                        (v.GithubEmail != null && p.GithubEmail == v.GithubEmail)
                        || (v.GoogleEmail != null && p.GoogleEmail == v.GoogleEmail)
                        || (v.MicrosoftEmail != null && p.MicrosoftEmail == v.MicrosoftEmail)
                    ),
                ct
            )
        )
            throw PersonValues.ProviderEmailTaken;

        if (person.GithubEmail != v.GithubEmail)
            await links.UnlinkAsync(db, id, ProviderNames.GitHub, ct);
        if (person.GoogleEmail != v.GoogleEmail)
            await links.UnlinkAsync(db, id, ProviderNames.Google, ct);
        if (person.MicrosoftEmail != v.MicrosoftEmail)
            await links.UnlinkAsync(db, id, ProviderNames.Microsoft, ct);

        v.ApplyTo(person);
        db.Entry(person).Property(p => p.RowVersion).OriginalValue = (uint)r.RowVersion!.Value;
        await Handlers.SaveAsync(db, ct, PersonValues.Stale, PersonValues.UniqueIndexes);
        return new PeoplePersonUpdateResult(Handlers.Version(person.RowVersion));
    }
}
