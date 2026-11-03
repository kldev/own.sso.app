using System.Security.Claims;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Sso.Api.Features.Common;
using Sso.Data;
using Sso.Data.Identity;

namespace Sso.Api.Features.People;

public sealed record PeoplePersonCreateRequest(
    Guid? Id,
    string? Username,
    string? Email,
    string? DisplayName,
    string? Role,
    string? GithubEmail,
    string? GoogleEmail,
    string? MicrosoftEmail
);

[UsedImplicitly(ImplicitUseTargetFlags.Members)] // serialized to JSON
public sealed record PeoplePersonCreateResult(long RowVersion);

/// <summary>
/// POST /api/people/person-create: the SSO admin enters a person - the only way anyone gets into Great SSO (no
/// registration, ADR 0011): username, work e-mail, name, role and the e-mails of the GitHub, Google and Microsoft
/// accounts the person signs in with. The id comes from the page (ADR 0006): the same id with the same values again is
/// a no-op (a retried request), with other values a conflict.
/// </summary>
public sealed class CreatePerson(SsoDbContext db, Actors actors, TimeProvider time) : IHandler
{
    public async Task<PeoplePersonCreateResult> HandleAsync(
        ClaimsPrincipal user,
        PeoplePersonCreateRequest r,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        actor.RequireAdmin(PersonValues.Forbidden, "Only an SSO admin enters people.");
        if (r.Id is not { } id || id == Guid.Empty)
            throw Rule.Invalid("people.id_missing", "The person's id is missing.");
        var v = PersonValues.Normalise(
            r.Username,
            r.Email,
            r.DisplayName,
            Text.Trim(r.Role) is { Length: > 0 } role ? role : Roles.Member,
            r.GithubEmail,
            r.GoogleEmail,
            r.MicrosoftEmail
        );
        v.Validate();

        var existing = await db.People.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (existing is not null)
            return v.SameAs(existing)
                ? new PeoplePersonCreateResult(Handlers.Version(existing.RowVersion))
                : throw Rule.Conflict(
                    "people.id_taken",
                    "This id already belongs to another person."
                );
        if (await db.People.AnyAsync(p => p.Username == v.Username, ct))
            throw PersonValues.UsernameTaken;
        if (await db.People.AnyAsync(p => p.Email == v.Email, ct))
            throw PersonValues.EmailTaken;
        if (
            await db.People.AnyAsync(
                p =>
                    (v.GithubEmail != null && p.GithubEmail == v.GithubEmail)
                    || (v.GoogleEmail != null && p.GoogleEmail == v.GoogleEmail)
                    || (v.MicrosoftEmail != null && p.MicrosoftEmail == v.MicrosoftEmail),
                ct
            )
        )
            throw PersonValues.ProviderEmailTaken;

        var person = new Person
        {
            Id = id,
            CreatedAt = time.GetUtcNow(),
            Status = Statuses.Active,
        };
        v.ApplyTo(person);
        db.People.Add(person);
        var unique = new Dictionary<string, RuleException>(PersonValues.UniqueIndexes)
        {
            ["pk_users"] = Rule.Conflict(
                "people.id_taken",
                "This id already belongs to another person."
            ),
        };
        await Handlers.SaveAsync(db, ct, unique: unique);
        return new PeoplePersonCreateResult(Handlers.Version(person.RowVersion));
    }
}
