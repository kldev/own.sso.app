using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sso.Api.Auth.Providers;
using Sso.Api.Oidc;
using Sso.Data;
using Sso.Data.Identity;
using Sso.Data.Oidc;

namespace Sso.Api.Auth;

/// <summary>
/// A person signs in through a provider (ADR 0011). Found by the provider's id; otherwise linked by the verified
/// e-mail the admin entered for that provider (the first sign-in). Outcome: ok, no_entry, inactive, unverified or
/// other_account. Nothing is ever created for an unknown person (no registration) - only the refusal is logged, with
/// the e-mail's domain.
/// </summary>
public sealed class ExternalSignIn(
    IDbContextFactory<SsoDbContext> factory,
    EventLog events,
    TimeProvider time
)
{
    public async Task<(string Outcome, Guid UserId)> SignInAsync(
        ExternalAccount account,
        string? clientId,
        RequestInfo request,
        CancellationToken ct
    )
    {
        if (!ProviderNames.All.Contains(account.Provider) || string.IsNullOrEmpty(account.Subject))
            throw new ArgumentException("Unknown provider or an empty subject.");
        try
        {
            return await TrySignInAsync(account, clientId, request, ct);
        }
        catch (DbUpdateException e)
            when (e.InnerException
                    is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
            )
        {
            // Two first sign-ins of one account at once: the other linked it - this one signs in by the link.
            return await TrySignInAsync(account, clientId, request, ct);
        }
    }

    private async Task<(string Outcome, Guid UserId)> TrySignInAsync(
        ExternalAccount account,
        string? clientId,
        RequestInfo request,
        CancellationToken ct
    )
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var provider = account.Provider;
        var email = account.Email?.Trim().ToLowerInvariant() ?? "";
        var domain = email.Contains('@') ? email[(email.IndexOf('@') + 1)..] : "";

        var person = await (
            from l in db.ExternalLogins
            join u in db.People on l.UserId equals u.Id
            where l.Provider == provider && l.Subject == account.Subject
            select u
        )
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);
        var linked = person is not null;

        (string, Guid) Refuse(string reason, Guid? userId, bool withDomain)
        {
            events.Add(
                db,
                "sign_in",
                Outcomes.Refused,
                reason,
                userId,
                clientId,
                provider,
                request: request,
                detail: withDomain
                    ? new Dictionary<string, string> { ["email_domain"] = domain }
                    : null
            );
            return (reason, Guid.Empty);
        }

        if (!linked)
        {
            if (!account.EmailVerified || email.Length == 0)
            {
                var refusal = Refuse("unverified", null, true);
                await db.SaveChangesAsync(ct);
                return refusal;
            }
            var byEmail = provider switch
            {
                ProviderNames.GitHub => db.People.Where(u => u.GithubEmail == email),
                ProviderNames.Google => db.People.Where(u => u.GoogleEmail == email),
                _ => db.People.Where(u => u.MicrosoftEmail == email),
            };
            person = await byEmail.AsNoTracking().FirstOrDefaultAsync(ct);
            if (person is null)
            {
                var refusal = Refuse("no_entry", null, true);
                await db.SaveChangesAsync(ct);
                return refusal;
            }
            var personId = person.Id;
            if (
                await db.ExternalLogins.AnyAsync(
                    l => l.UserId == personId && l.Provider == provider,
                    ct
                )
            )
            {
                // One account per provider: another account of the same provider is not linked over the existing one.
                var refusal = Refuse("other_account", person.Id, false);
                await db.SaveChangesAsync(ct);
                return refusal;
            }
        }

        if (person!.Status != Statuses.Active)
        {
            var refusal = Refuse("inactive", person.Id, false);
            await db.SaveChangesAsync(ct);
            return refusal;
        }

        if (!linked)
        {
            db.ExternalLogins.Add(
                new ExternalLogin
                {
                    UserId = person.Id,
                    Provider = provider,
                    Subject = account.Subject,
                    TenantId = account.TenantId,
                    Email = email,
                    LinkedAt = time.GetUtcNow(),
                }
            );
            events.Add(
                db,
                "account_linked",
                Outcomes.Success,
                userId: person.Id,
                clientId: clientId,
                provider: provider,
                request: request
            );
        }
        events.Add(
            db,
            "sign_in",
            Outcomes.Success,
            userId: person.Id,
            clientId: clientId,
            provider: provider,
            request: request
        );
        await db.SaveChangesAsync(ct);
        return ("ok", person.Id);
    }
}
