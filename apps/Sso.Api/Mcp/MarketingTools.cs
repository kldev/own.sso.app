using System.ComponentModel;
using JetBrains.Annotations;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Sso.Api.Features.Marketing;

namespace Sso.Api.Mcp;

/// <summary>
/// The panel's Marketing - Mail as MCP tools (ADR 0013, 0014): templates, a mailing to a list of addresses (one e-mail
/// per address), its progress and a retry of the failed ones. Thin calls of the same handlers as the API.
/// </summary>
[McpServerToolType]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)] // Found and called by the MCP server.
public sealed class MarketingTools(McpToolRunner run)
{
    [McpServerTool(
        Name = "mail_templates_list",
        Title = "List the marketing e-mail templates",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false
    )]
    [Description(
        "The e-mail templates per product and language (e.g. great-sso-en, the invitation to Great SSO), with subject and body."
    )]
    public Task<IReadOnlyList<MarketingTemplatesRow>> TemplatesList(
        CancellationToken ct = default
    ) =>
        run.RunAsync(
            "mail_templates_list",
            async () =>
                (
                    await run.Handler<ListTemplates>()
                        .HandleAsync(
                            run.User,
                            McpToolRunner.Query(("sort", "code"), ("pageSize", 200)),
                            ct
                        )
                ).Items,
            ct
        );

    [McpServerTool(
        Name = "mail_template_update",
        Title = "Change a marketing e-mail template",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false
    )]
    [Description(
        "Changes a template's subject and body (plain text). Mailings already sent keep their own copy."
    )]
    public Task<MarketingTemplatesRow> TemplateUpdate(
        [Description("The template's code, e.g. great-sso-en.")] string code,
        string subject,
        string body,
        CancellationToken ct = default
    ) =>
        run.RunAsync(
            "mail_template_update",
            async () =>
            {
                var row = await TemplateAsync(code, ct);
                await run.Handler<UpdateTemplate>()
                    .HandleAsync(
                        run.User,
                        new MarketingTemplateUpdateRequest(row.Id, subject, body, row.RowVersion),
                        ct
                    );
                return await TemplateAsync(code, ct);
            },
            ct
        );

    [McpServerTool(
        Name = "mail_send",
        Title = "Send a marketing e-mail to a list of addresses",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true
    )]
    [Description(
        "Sends a template's e-mail to 1-500 addresses - every address gets its own e-mail with only itself in To; the "
            + "host sends them one by one in the background (mailings_list shows the progress). E-mails leave the system "
            + "and cannot be taken back: confirm the list and the template with the person first. Without subject/body "
            + "the template's text is used. skipContacted (default true) leaves out addresses that already got an e-mail "
            + "about the same product."
    )]
    public Task<object> MailSend(
        [Description("The template's code, e.g. great-sso-en.")] string template,
        [Description("The addresses, one per item.")] string[] emails,
        [Description("This mailing's subject instead of the template's.")] string? subject = null,
        [Description("This mailing's body instead of the template's.")] string? body = null,
        bool skipContacted = true,
        CancellationToken ct = default
    ) =>
        run.RunAsync<object>(
            "mail_send",
            async () =>
            {
                var row = await TemplateAsync(template, ct);
                var id = Guid.CreateVersion7();
                var result = await run.Handler<CreateMailing>()
                    .HandleAsync(
                        run.User,
                        new MarketingMailingCreateRequest(
                            id,
                            row.Id,
                            string.IsNullOrWhiteSpace(subject) ? row.Subject : subject,
                            string.IsNullOrWhiteSpace(body) ? row.Body : body,
                            emails,
                            skipContacted
                        ),
                        ct
                    );
                return new
                {
                    mailingId = id,
                    queued = result.Queued,
                    skipped = result.Skipped,
                };
            },
            ct
        );

    [McpServerTool(
        Name = "mailings_list",
        Title = "List the marketing mailings",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false
    )]
    [Description(
        "The mailings, newest first, with how many addresses are pending, sending, sent and failed."
    )]
    public Task<IReadOnlyList<MarketingMailingsRow>> MailingsList(
        [Description("How many, 1-200 (20 by default).")] int limit = 20,
        CancellationToken ct = default
    ) =>
        run.RunAsync(
            "mailings_list",
            async () =>
                (
                    await run.Handler<ListMailings>()
                        .HandleAsync(
                            run.User,
                            McpToolRunner.Query(
                                ("sort", "createdAt"),
                                ("desc", true),
                                ("pageSize", Math.Clamp(limit, 1, 200))
                            ),
                            ct
                        )
                ).Items,
            ct
        );

    [McpServerTool(
        Name = "mailing_addresses",
        Title = "Read a mailing's addresses",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false
    )]
    [Description(
        "Every address of a mailing with its status, attempts, last error and when it was sent."
    )]
    public Task<IReadOnlyList<MarketingRecipientsRow>> MailingAddresses(
        Guid mailingId,
        CancellationToken ct = default
    ) =>
        run.RunAsync(
            "mailing_addresses",
            async () =>
            {
                var rows = new List<MarketingRecipientsRow>();
                for (var page = 1; ; page++)
                {
                    var result = await run.Handler<ListRecipients>()
                        .HandleAsync(
                            run.User,
                            McpToolRunner.Query(
                                ("mailingId", mailingId),
                                ("sort", "email"),
                                ("page", page),
                                ("pageSize", 200)
                            ),
                            ct
                        );
                    rows.AddRange(result.Items);
                    if (!result.HasMore)
                        return (IReadOnlyList<MarketingRecipientsRow>)rows;
                }
            },
            ct
        );

    [McpServerTool(
        Name = "mailing_retry",
        Title = "Send a mailing's failed addresses again",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true
    )]
    [Description("The failed addresses of a mailing go back to the queue; the number queued.")]
    public Task<object> MailingRetry(Guid mailingId, CancellationToken ct = default) =>
        run.RunAsync<object>(
            "mailing_retry",
            async () =>
            {
                var result = await run.Handler<RetryMailing>()
                    .HandleAsync(run.User, new MarketingMailingRetryRequest(mailingId), ct);
                return new { mailingId, queued = result.Queued };
            },
            ct
        );

    private async Task<MarketingTemplatesRow> TemplateAsync(string code, CancellationToken ct) =>
        (
            await run.Handler<ListTemplates>()
                .HandleAsync(
                    run.User,
                    McpToolRunner.Query(("code", code.Trim().ToLowerInvariant())),
                    ct
                )
        ).Items.SingleOrDefault()
        ?? throw new McpException(
            $"There is no template '{code}' (mail_templates_list shows them)."
        );
}
