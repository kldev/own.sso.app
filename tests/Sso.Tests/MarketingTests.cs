using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Sso.Api.Marketing;

namespace Sso.Tests;

/// <summary>
/// Marketing e-mails (ADR 0014): a template's e-mail to a list of addresses, each address its own e-mail; the text is
/// kept per mailing; failed addresses are tried again and then failed; an address that already got the product's
/// e-mail can be skipped.
/// </summary>
[Trait("verify", "marketing")]
[Collection(ApiCollection.Name)]
public sealed class MarketingTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Address(string label = "shop") =>
        $"{label}.{Guid.NewGuid():N}"[..20] + "@garage.example";

    private static async Task<JsonNode> TemplateAsync(HttpClient admin, string code) =>
        Assert.Single(await admin.ListAsync($"/api/marketing/templates?code={code}"))!;

    private static object Mailing(
        JsonNode template,
        string[] emails,
        Guid? id = null,
        bool skipContacted = false,
        string? subject = null,
        string? body = null
    ) =>
        new
        {
            id = id ?? Guid.CreateVersion7(),
            templateId = template.Str("id"),
            subject = subject ?? template.Str("subject"),
            body = body ?? template.Str("body"),
            emails,
            skipContacted,
        };

    /// <summary>The recipients of a mailing once the sender has nothing pending or sending left for it.</summary>
    private static async Task<JsonArray> SettledAsync(HttpClient admin, Guid mailingId)
    {
        for (var i = 0; i < 100; i++)
        {
            var rows = await admin.ListAsync($"/api/marketing/recipients?mailingId={mailingId}");
            if (rows.All(r => r.Str("status") is "sent" or "failed"))
                return rows;
            await Task.Delay(100, Ct);
        }
        Assert.Fail($"Mailing {mailingId} did not settle.");
        return [];
    }

    [Fact]
    public async Task The_invitation_to_great_sso_is_seeded_in_english()
    {
        var admin = await api.AsAsync(ApiFixture.Admin);
        var invitation = await TemplateAsync(admin, "great-sso-en");
        Assert.Equal("great-sso", invitation.Str("product"));
        Assert.Equal("en", invitation.Str("language"));
        Assert.Contains("Great SSO", invitation.Str("subject"));
        Assert.Contains("https://sso.example.com", invitation.Str("body"));
    }

    [Fact]
    public async Task Every_address_gets_its_own_email_once_and_sees_no_other_address()
    {
        var admin = await api.AsAsync(ApiFixture.Admin);
        var template = await TemplateAsync(admin, "test-a-en");
        var first = Address();
        var second = Address();
        var id = Guid.CreateVersion7();
        var body = Mailing(
            template,
            [$"  {first.ToUpperInvariant()} ", second, first, ""],
            id,
            subject: "Program do warsztatu",
            body: "Hello,\n\nhttps://handbook.example.com"
        );

        var created = await admin.CallAsync("/api/marketing/mailing-create", body);
        Assert.Equal(2, created["queued"]!.GetValue<int>());
        Assert.Equal(0, created["skipped"]!.GetValue<int>());
        // The same call again (a retry of the page) queues nothing more.
        Assert.Equal(
            2,
            (await admin.CallAsync("/api/marketing/mailing-create", body))[
                "queued"
            ]!.GetValue<int>()
        );

        var rows = await SettledAsync(admin, id);
        Assert.All(rows, r => Assert.Equal("sent", r.Str("status")));
        foreach (var address in new[] { first, second })
        {
            var email = Assert.Single(api.Mail.To(address));
            Assert.Equal("Program do warsztatu", email.Subject);
            Assert.Contains("handbook.example.com", email.Body);
        }

        var mailing = Assert.Single(await admin.ListAsync($"/api/marketing/mailings?id={id}"))!;
        Assert.Equal(2, mailing["sent"]!.GetValue<long>());
        Assert.Equal(0, mailing["pending"]!.GetValue<long>());
        Assert.Equal("Test Admin", mailing.Str("createdByName"));
        Assert.Equal("test-a", mailing.Str("product"));
    }

    [Fact]
    public async Task A_failing_address_is_tried_again_then_failed_and_a_refused_one_fails_at_once()
    {
        var admin = await api.AsAsync(ApiFixture.Admin);
        var template = await TemplateAsync(admin, "great-sso-en");
        var failing = Address("fail");
        var refused = Address("refused");
        var good = Address();
        var id = Guid.CreateVersion7();
        await admin.CallAsync(
            "/api/marketing/mailing-create",
            Mailing(template, [failing, refused, good], id)
        );

        var rows = await SettledAsync(admin, id);
        var byEmail = rows.ToDictionary(r => r.Str("email"));
        Assert.Equal("sent", byEmail[good].Str("status"));
        Assert.Equal("failed", byEmail[failing].Str("status"));
        Assert.Equal(3, byEmail[failing]!["attempts"]!.GetValue<int>());
        Assert.Contains("away", byEmail[failing].Str("lastError"));
        Assert.Equal("failed", byEmail[refused].Str("status"));
        Assert.Equal(1, byEmail[refused]!["attempts"]!.GetValue<int>());

        var retried = await admin.CallAsync("/api/marketing/mailing-retry", new { id });
        Assert.Equal(2, retried["queued"]!.GetValue<int>());
        await SettledAsync(admin, id);
        Assert.Single(api.Mail.To(good));
    }

    [Fact]
    public async Task An_address_that_already_got_the_products_email_can_be_skipped()
    {
        var admin = await api.AsAsync(ApiFixture.Admin);
        var productB = await TemplateAsync(admin, "test-b-en");
        var known = Address();
        var fresh = Address();
        var firstId = Guid.CreateVersion7();
        await admin.CallAsync("/api/marketing/mailing-create", Mailing(productB, [known], firstId));
        await SettledAsync(admin, firstId);

        var second = await admin.CallAsync(
            "/api/marketing/mailing-create",
            Mailing(productB, [known, fresh], skipContacted: true)
        );
        Assert.Equal(1, second["queued"]!.GetValue<int>());
        Assert.Equal(1, second["skipped"]!.GetValue<int>());

        await admin.AssertProblemAsync(
            "/api/marketing/mailing-create",
            Mailing(productB, [known], skipContacted: true),
            HttpStatusCode.BadRequest,
            "marketing.nothing_to_send"
        );
        // Another product is another e-mail.
        var productA = await TemplateAsync(admin, "test-a-en");
        var other = await admin.CallAsync(
            "/api/marketing/mailing-create",
            Mailing(productA, [known], skipContacted: true)
        );
        Assert.Equal(1, other["queued"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_admin_edits_a_template_and_a_sent_mailing_keeps_its_text()
    {
        var admin = await api.AsAsync(ApiFixture.Admin);
        var template = await TemplateAsync(admin, "test-c-en");
        var id = Guid.CreateVersion7();
        await admin.CallAsync("/api/marketing/mailing-create", Mailing(template, [Address()], id));

        var updated = await admin.CallAsync(
            "/api/marketing/template-update",
            new
            {
                id = template.Str("id"),
                subject = "  New subject  ",
                body = template.Str("body") + "\nPS",
                rowVersion = template["rowVersion"]!.GetValue<long>(),
            }
        );
        await admin.AssertProblemAsync(
            "/api/marketing/template-update",
            new
            {
                id = template.Str("id"),
                subject = "Stale",
                body = "Stale",
                rowVersion = template["rowVersion"]!.GetValue<long>(),
            },
            HttpStatusCode.Conflict,
            "concurrency"
        );
        Assert.Equal("New subject", (await TemplateAsync(admin, "test-c-en")).Str("subject"));
        var mailing = Assert.Single(await admin.ListAsync($"/api/marketing/mailings?id={id}"))!;
        Assert.Equal(template.Str("subject"), mailing.Str("subject"));

        // Back as it was, for the other tests.
        await admin.CallAsync(
            "/api/marketing/template-update",
            new
            {
                id = template.Str("id"),
                subject = template.Str("subject"),
                body = template.Str("body"),
                rowVersion = updated.RowVersion(),
            }
        );
    }

    [Fact]
    public async Task Every_rule_has_its_code()
    {
        var admin = await api.AsAsync(ApiFixture.Admin);
        var template = await TemplateAsync(admin, "test-a-en");
        var bad = HttpStatusCode.BadRequest;
        const string create = "/api/marketing/mailing-create";

        await admin.AssertProblemAsync(
            create,
            Mailing(template, []),
            bad,
            "marketing.emails_count"
        );
        await admin.AssertProblemAsync(
            create,
            Mailing(template, [" ", ""]),
            bad,
            "marketing.emails_count"
        );
        await admin.AssertProblemAsync(
            create,
            Mailing(template, Enumerable.Range(0, 501).Select(_ => Address()).ToArray()),
            bad,
            "marketing.emails_count"
        );
        foreach (
            var address in new[]
            {
                "no-at.example",
                "a@b",
                "Jan <jan@garage.example>",
                "a@b.example, c@d.example",
            }
        )
            await admin.AssertProblemAsync(
                create,
                Mailing(template, [address]),
                bad,
                "marketing.email_invalid"
            );
        await admin.AssertProblemAsync(
            create,
            Mailing(template, [Address()], subject: " "),
            bad,
            "marketing.subject_invalid"
        );
        await admin.AssertProblemAsync(
            create,
            Mailing(template, [Address()], body: "\n"),
            bad,
            "marketing.body_invalid"
        );
        await admin.AssertProblemAsync(
            create,
            new
            {
                id = Guid.CreateVersion7(),
                templateId = Guid.CreateVersion7(),
                subject = "x",
                body = "x",
                emails = new[] { Address() },
                skipContacted = false,
            },
            HttpStatusCode.NotFound,
            "marketing.template_not_found"
        );
        await admin.AssertProblemAsync(
            "/api/marketing/mailing-retry",
            new { id = Guid.CreateVersion7() },
            HttpStatusCode.NotFound,
            "marketing.mailing_not_found"
        );

        var id = Guid.CreateVersion7();
        await admin.CallAsync(create, Mailing(template, [Address()], id));
        var other = await TemplateAsync(admin, "test-b-en");
        await admin.AssertProblemAsync(
            create,
            Mailing(other, [Address()], id),
            HttpStatusCode.Conflict,
            "marketing.id_taken"
        );

        var member = await api.AsAsync(await api.AddPersonAsync());
        await member.AssertProblemAsync(
            create,
            Mailing(template, [Address()]),
            HttpStatusCode.Forbidden,
            "marketing.forbidden"
        );
        Assert.Empty(await member.ListAsync("/api/marketing/templates"));
        Assert.Empty(await member.ListAsync("/api/marketing/mailings"));
    }

    [Fact]
    public async Task Two_senders_never_claim_one_address_twice()
    {
        // A recipient due only tomorrow: the running sender leaves it alone, two claims race for it here.
        var admin = await api.AsAsync(ApiFixture.Admin);
        var template = await TemplateAsync(admin, "test-b-en");
        var mailingId = Guid.CreateVersion7();
        var recipientId = Guid.CreateVersion7();
        await api.ExecuteAsync(
            $"""
            INSERT INTO marketing.mailings (id, template_id, subject, body, created_by)
            VALUES ('{mailingId}', '{template.Str("id")}', 'Race', 'Race', '{ApiFixture.AdminId}');
            INSERT INTO marketing.recipients (id, mailing_id, email, next_attempt_at)
            VALUES ('{recipientId}', '{mailingId}', '{Address("race")}', now() + interval '1 day');
            """
        );
        var queue = api.Services.GetRequiredService<RecipientQueue>();
        var claims = await Task.WhenAll(
            queue.ClaimAsync(recipientId, Ct),
            queue.ClaimAsync(recipientId, Ct)
        );
        Assert.Single(claims, c => c);
        Assert.Equal(
            "sending",
            await api.ScalarAsync<string>(
                $"SELECT status::text FROM marketing.recipients WHERE id = '{recipientId}' AND attempts = 1"
            )
        );
    }
}
