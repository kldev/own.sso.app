using System.Security.Claims;
using System.Text.RegularExpressions;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Sso.Api.Features.Common;
using Sso.Data;
using Sso.Data.Marketing;

namespace Sso.Api.Features.Marketing;

/// <summary>The rules of a template's and a mailing's text (ADR 0008, 0014).</summary>
internal static partial class MarketingRules
{
    public const string Forbidden = "marketing.forbidden";

    public static string Subject(string? subject)
    {
        var value = Text.Trim(subject);
        return string.IsNullOrEmpty(value) || value.Length > 200
            ? throw Rule.Invalid(
                "marketing.subject_invalid",
                "Enter the subject (up to 200 characters)."
            )
            : value;
    }

    public static string Body(string? body)
    {
        var value = body?.Trim(' ', '\t', '\r', '\n');
        return string.IsNullOrEmpty(value) || value.Length > 20000
            ? throw Rule.Invalid(
                "marketing.body_invalid",
                "Enter the text of the e-mail (up to 20000 characters)."
            )
            : value;
    }

    /// <summary>One address, no display name, a domain with a dot: what MailKit and an SMTP server accept.</summary>
    public static bool IsRecipient(string email) =>
        email.Length <= 254 && RecipientPattern().IsMatch(email);

    [GeneratedRegex(
        @"^[a-z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$"
    )]
    private static partial Regex RecipientPattern();
}

public sealed record MarketingTemplateUpdateRequest(
    Guid? Id,
    string? Subject,
    string? Body,
    long? RowVersion
);

[UsedImplicitly(ImplicitUseTargetFlags.Members)] // serialized to JSON
public sealed record MarketingTemplateUpdateResult(long RowVersion);

/// <summary>
/// POST /api/marketing/template-update: the SSO admin changes a template's subject and body. Mailings already sent
/// keep their own copy. 409 when the template changed since it was read.
/// </summary>
public sealed class UpdateTemplate(SsoDbContext db, Actors actors, TimeProvider time) : IHandler
{
    private const string Stale =
        "Someone changed this template in the meantime - reload and try again.";

    public async Task<MarketingTemplateUpdateResult> HandleAsync(
        ClaimsPrincipal user,
        MarketingTemplateUpdateRequest r,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        actor.RequireAdmin(MarketingRules.Forbidden, "Only an SSO admin changes e-mail templates.");
        var template =
            await db.Templates.FirstOrDefaultAsync(t => t.Id == r.Id, ct)
            ?? throw Rule.NotFound("marketing.template_not_found", "There is no such template.");
        Handlers.CheckVersion(template.RowVersion, r.RowVersion, Stale);
        template.Subject = MarketingRules.Subject(r.Subject);
        template.Body = MarketingRules.Body(r.Body);
        template.UpdatedAt = time.GetUtcNow();
        db.Entry(template).Property(t => t.RowVersion).OriginalValue = (uint)r.RowVersion!.Value;
        await Handlers.SaveAsync(db, ct, Stale);
        return new MarketingTemplateUpdateResult(Handlers.Version(template.RowVersion));
    }
}

public sealed record MarketingMailingCreateRequest(
    Guid? Id,
    Guid? TemplateId,
    string? Subject,
    string? Body,
    string[]? Emails,
    bool? SkipContacted
);

public sealed record MarketingMailingCreateResult(int Queued, int Skipped);

/// <summary>
/// POST /api/marketing/mailing-create: the SSO admin sends a template's e-mail to a list of addresses (ADR 0014) -
/// every address gets its own e-mail, nobody sees another recipient. The subject and body may be changed for this
/// mailing only; the mailing keeps them. Addresses are trimmed, lower-cased and de-duplicated; with skipContacted an
/// address that already got an e-mail of the same product is left out. The same id with the same template is a no-op
/// that answers what the first call queued.
/// </summary>
public sealed class CreateMailing(SsoDbContext db, Actors actors, TimeProvider time) : IHandler
{
    public async Task<MarketingMailingCreateResult> HandleAsync(
        ClaimsPrincipal user,
        MarketingMailingCreateRequest r,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        actor.RequireAdmin(MarketingRules.Forbidden, "Only an SSO admin sends marketing e-mails.");
        if (r.Id is not { } id || id == Guid.Empty)
            throw Rule.Invalid("marketing.id_missing", "The mailing's id is missing.");
        var existing = await db.Mailings.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
        if (existing is not null)
            return existing.TemplateId != r.TemplateId
                ? throw Rule.Conflict(
                    "marketing.id_taken",
                    "This id already belongs to another mailing."
                )
                : new MarketingMailingCreateResult(
                    await db.Recipients.CountAsync(x => x.MailingId == id, ct),
                    0
                );
        var template =
            await db.Templates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == r.TemplateId, ct)
            ?? throw Rule.NotFound("marketing.template_not_found", "There is no such template.");
        var emails = (r.Emails ?? [])
            .Select(e => e.Trim().ToLowerInvariant())
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (emails.Count is < 1 or > 500)
            throw Rule.Invalid("marketing.emails_count", "Enter 1-500 e-mail addresses.");
        var invalid = emails.Where(e => !MarketingRules.IsRecipient(e)).ToList();
        if (invalid.Count > 0)
            throw Rule.Invalid(
                "marketing.email_invalid",
                $"Not an e-mail address: {string.Join(", ", invalid)}."
            );
        var subject = MarketingRules.Subject(r.Subject);
        var body = MarketingRules.Body(r.Body);

        var send = emails;
        if (r.SkipContacted == true)
        {
            var product = template.Product;
            var contacted = await (
                from x in db.Recipients
                join m in db.Mailings on x.MailingId equals m.Id
                join t in db.Templates on m.TemplateId equals t.Id
                where
                    emails.Contains(x.Email)
                    && t.Product == product
                    && x.Status != RecipientStatuses.Failed
                select x.Email
            )
                .Distinct()
                .ToListAsync(ct);
            send = [.. emails.Except(contacted, StringComparer.Ordinal)];
        }
        if (send.Count == 0)
            throw Rule.Invalid(
                "marketing.nothing_to_send",
                "Every address already got this product's e-mail - nothing to send."
            );

        var now = time.GetUtcNow();
        db.Mailings.Add(
            new Mailing
            {
                Id = id,
                TemplateId = template.Id,
                Subject = subject,
                Body = body,
                CreatedBy = actor.Id,
                CreatedAt = now,
            }
        );
        foreach (var email in send)
            db.Recipients.Add(
                new Recipient
                {
                    Id = Guid.CreateVersion7(),
                    MailingId = id,
                    Email = email,
                    Status = RecipientStatuses.Pending,
                    NextAttemptAt = now,
                    CreatedAt = now,
                }
            );
        await db.SaveChangesAsync(ct);
        return new MarketingMailingCreateResult(send.Count, emails.Count - send.Count);
    }
}

public sealed record MarketingMailingRetryRequest(Guid? Id);

public sealed record MarketingMailingRetryResult(int Queued);

/// <summary>
/// POST /api/marketing/mailing-retry: the SSO admin sends the failed addresses of a mailing again (after the SMTP
/// settings were fixed, say): failed rows go back to pending with their attempts reset. Nothing failed is a no-op.
/// </summary>
public sealed class RetryMailing(SsoDbContext db, Actors actors, TimeProvider time) : IHandler
{
    public async Task<MarketingMailingRetryResult> HandleAsync(
        ClaimsPrincipal user,
        MarketingMailingRetryRequest r,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        actor.RequireAdmin(MarketingRules.Forbidden, "Only an SSO admin sends marketing e-mails.");
        if (r.Id is not { } id || !await db.Mailings.AnyAsync(m => m.Id == id, ct))
            throw Rule.NotFound("marketing.mailing_not_found", "There is no such mailing.");
        var now = time.GetUtcNow();
        var queued = await db
            .Recipients.Where(x => x.MailingId == id && x.Status == RecipientStatuses.Failed)
            .ExecuteUpdateAsync(
                set =>
                    set.SetProperty(x => x.Status, RecipientStatuses.Pending)
                        .SetProperty(x => x.Attempts, 0)
                        .SetProperty(x => x.LastError, (string?)null)
                        .SetProperty(x => x.NextAttemptAt, now),
                ct
            );
        return new MarketingMailingRetryResult(queued);
    }
}
