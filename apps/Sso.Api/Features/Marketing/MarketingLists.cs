using System.Security.Claims;
using System.Text.Json.Serialization;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Sso.Api.Features.Common;
using Sso.Data;
using Sso.Data.Marketing;

namespace Sso.Api.Features.Marketing;

/// <summary>An e-mail template per product and language.</summary>
public sealed class MarketingTemplatesRow
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required string Product { get; init; }
    public required string Language { get; init; }
    public required string Name { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    [JsonIgnore]
    public uint Xmin { get; init; }

    public long RowVersion => Xmin;
}

/// <summary>GET /api/marketing/templates - for an active SSO admin only.</summary>
public sealed class ListTemplates(SsoDbContext db, Actors actors) : IHandler
{
    public static readonly ListSpec<MarketingTemplatesRow> Spec =
        new ListSpec<MarketingTemplatesRow>()
            .Add("id", r => r.Id, Filter.Eq)
            .Add("code", r => r.Code, Filter.Eq)
            .Add("product", r => r.Product, Filter.Eq)
            .Add("language", r => r.Language, Filter.Eq)
            .Add("name", r => r.Name, Filter.Eq)
            .Add("subject", r => r.Subject)
            .Add("createdAt", r => r.CreatedAt, Filter.Range)
            .Add("updatedAt", r => r.UpdatedAt, Filter.Range)
            .Add("rowVersion", r => r.Xmin, Filter.Eq);

    public async Task<ListPage<MarketingTemplatesRow>> HandleAsync(
        ClaimsPrincipal user,
        IQueryCollection query,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        var visible = actor.IsAdmin;
        var rows = db
            .Templates.AsNoTracking()
            .Where(_ => visible)
            .Select(t => new MarketingTemplatesRow
            {
                Id = t.Id,
                Code = t.Code,
                Product = t.Product,
                Language = t.Language,
                Name = t.Name,
                Subject = t.Subject,
                Body = t.Body,
                CreatedAt = t.CreatedAt,
                UpdatedAt = t.UpdatedAt,
                Xmin = t.RowVersion,
            });
        return await Spec.PageAsync(rows, query, ct);
    }
}

/// <summary>A mailing with who sent it and how far the sender got.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)] // serialized to JSON
public sealed class MarketingMailingsRow
{
    public required Guid Id { get; init; }
    public required Guid TemplateId { get; init; }
    public required string TemplateCode { get; init; }
    public required string TemplateName { get; init; }
    public required string Product { get; init; }
    public required string Language { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public required Guid CreatedBy { get; init; }
    public string? CreatedByName { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required long Total { get; init; }
    public required long Pending { get; init; }
    public required long Sending { get; init; }
    public required long Sent { get; init; }
    public required long Failed { get; init; }
    public DateTimeOffset? LastSentAt { get; init; }
}

/// <summary>GET /api/marketing/mailings - for an active SSO admin only.</summary>
public sealed class ListMailings(SsoDbContext db, Actors actors) : IHandler
{
    public static readonly ListSpec<MarketingMailingsRow> Spec =
        new ListSpec<MarketingMailingsRow>()
            .Add("id", r => r.Id, Filter.Eq)
            .Add("templateId", r => r.TemplateId, Filter.Eq)
            .Add("templateCode", r => r.TemplateCode, Filter.Eq)
            .Add("templateName", r => r.TemplateName, Filter.Eq)
            .Add("product", r => r.Product, Filter.Eq)
            .Add("language", r => r.Language, Filter.Eq)
            .Add("subject", r => r.Subject)
            .Add("createdBy", r => r.CreatedBy, Filter.Eq)
            .Add("createdByName", r => r.CreatedByName)
            .Add("createdAt", r => r.CreatedAt, Filter.Range)
            .Add("total", r => r.Total, Filter.Eq | Filter.Range)
            .Add("pending", r => r.Pending, Filter.Eq | Filter.Range)
            .Add("sending", r => r.Sending, Filter.Eq | Filter.Range)
            .Add("sent", r => r.Sent, Filter.Eq | Filter.Range)
            .Add("failed", r => r.Failed, Filter.Eq | Filter.Range)
            .Add("lastSentAt", r => r.LastSentAt, Filter.Range);

    public async Task<ListPage<MarketingMailingsRow>> HandleAsync(
        ClaimsPrincipal user,
        IQueryCollection query,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        var visible = actor.IsAdmin;
        var rows =
            from m in db.Mailings.AsNoTracking()
            join t in db.Templates on m.TemplateId equals t.Id
            where visible
            select new MarketingMailingsRow
            {
                Id = m.Id,
                TemplateId = m.TemplateId,
                TemplateCode = t.Code,
                TemplateName = t.Name,
                Product = t.Product,
                Language = t.Language,
                Subject = m.Subject,
                Body = m.Body,
                CreatedBy = m.CreatedBy,
                CreatedByName = db
                    .People.Where(p => p.Id == m.CreatedBy)
                    .Select(p => p.DisplayName)
                    .FirstOrDefault(),
                CreatedAt = m.CreatedAt,
                Total = db.Recipients.LongCount(r => r.MailingId == m.Id),
                Pending = db.Recipients.LongCount(r =>
                    r.MailingId == m.Id && r.Status == RecipientStatuses.Pending
                ),
                Sending = db.Recipients.LongCount(r =>
                    r.MailingId == m.Id && r.Status == RecipientStatuses.Sending
                ),
                Sent = db.Recipients.LongCount(r =>
                    r.MailingId == m.Id && r.Status == RecipientStatuses.Sent
                ),
                Failed = db.Recipients.LongCount(r =>
                    r.MailingId == m.Id && r.Status == RecipientStatuses.Failed
                ),
                LastSentAt = db.Recipients.Where(r => r.MailingId == m.Id).Max(r => r.SentAt),
            };
        return await Spec.PageAsync(rows, query, ct);
    }
}

/// <summary>One address of a mailing with its status, attempts, the last SMTP error and when it went out.</summary>
public sealed class MarketingRecipientsRow
{
    public required Guid Id { get; init; }
    public required Guid MailingId { get; init; }
    public required string Email { get; init; }
    public required string Status { get; init; }
    public required int Attempts { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? SentAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string Product { get; init; }
}

/// <summary>GET /api/marketing/recipients - for an active SSO admin only.</summary>
public sealed class ListRecipients(SsoDbContext db, Actors actors) : IHandler
{
    public static readonly ListSpec<MarketingRecipientsRow> Spec =
        new ListSpec<MarketingRecipientsRow>()
            .Add("id", r => r.Id, Filter.Eq)
            .Add("mailingId", r => r.MailingId, Filter.Eq)
            .Add("email", r => r.Email)
            .Add("status", r => r.Status, Filter.Eq)
            .Add("attempts", r => r.Attempts, Filter.Eq | Filter.Range)
            .Add("lastError", r => r.LastError)
            .Add("sentAt", r => r.SentAt, Filter.Range)
            .Add("createdAt", r => r.CreatedAt, Filter.Range)
            .Add("product", r => r.Product, Filter.Eq);

    public async Task<ListPage<MarketingRecipientsRow>> HandleAsync(
        ClaimsPrincipal user,
        IQueryCollection query,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        var visible = actor.IsAdmin;
        var rows =
            from r in db.Recipients.AsNoTracking()
            join m in db.Mailings on r.MailingId equals m.Id
            join t in db.Templates on m.TemplateId equals t.Id
            where visible
            select new MarketingRecipientsRow
            {
                Id = r.Id,
                MailingId = r.MailingId,
                Email = r.Email,
                Status = r.Status,
                Attempts = r.Attempts,
                LastError = r.LastError,
                SentAt = r.SentAt,
                CreatedAt = r.CreatedAt,
                Product = t.Product,
            };
        return await Spec.PageAsync(rows, query, ct);
    }
}
