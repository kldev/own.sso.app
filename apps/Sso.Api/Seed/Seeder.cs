using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sso.Api.Auth;
using Sso.Api.Auth.Providers;
using Sso.Data;
using Sso.Data.Identity;
using Sso.Data.Marketing;
using Sso.Data.Oidc;

namespace Sso.Api.Seed;

/// <summary>
/// The seed on start (ADR 0015). On a database without any admin it enters the first admin (BootstrapAdmin) and, with
/// Seed:Demo, the demo data - all in one transaction under an advisory lock, so replicas starting at once seed once.
/// A database that has an admin is seeded already: nothing happens, also after the admin changed the data in the panel.
/// Only example.com data; the demo's session tokens and secrets are random values nobody knows.
/// </summary>
public sealed class Seeder(
    IDbContextFactory<SsoDbContext> factory,
    IOptions<SeedOptions> seed,
    IOptions<BootstrapAdminOptions> admin,
    TimeProvider time,
    ILogger<Seeder> logger
)
{
    /// <summary>The advisory lock key of the seed ("great-sso.seed").</summary>
    private const long LockKey = 0x67_72_65_61_74_73_65_65;

    /// <summary>The invitation template of db/changelog/2026/2-data (a fixed id).</summary>
    private static readonly Guid InvitationTemplateId = Guid.Parse(
        "019a4a00-0000-7000-8000-000000000001"
    );

    /// <summary>The sample application of samples/Sso.SampleClient.</summary>
    public const string SampleClientId = "sample-client";
    private const string SampleRedirectUri = "http://localhost:5200/signin-oidc";

    /// <summary>True when this call seeded; false when the database had an admin already.</summary>
    public async Task<bool> SeedAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockKey})", ct);
        if (await db.People.AnyAsync(p => p.Role == Roles.Admin, ct))
        {
            logger.LogInformation("The database has an admin already - nothing to seed");
            return false;
        }

        var now = time.GetUtcNow();
        var first = FirstAdmin(now);
        db.People.Add(first);
        if (seed.Value.Demo)
            Demo(db, first, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation(
            "Seeded the first admin {Username} ({Email}){Demo}",
            first.Username,
            first.Email,
            seed.Value.Demo ? " and the demo data" : ""
        );
        if (first.Email == BootstrapAdminOptions.DefaultEmail)
            logger.LogWarning(
                "The first admin signs in with {Email} - set BootstrapAdmin__Email and BootstrapAdmin__GithubEmail "
                    + "(or __GoogleEmail, __MicrosoftEmail) to a real person's accounts outside a local try-out",
                first.Email
            );
        return true;
    }

    private Person FirstAdmin(DateTimeOffset now)
    {
        var o = admin.Value;
        static string? Lower(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
        return new Person
        {
            Id = Guid.CreateVersion7(),
            Username = o.Username.Trim().ToLowerInvariant(),
            Email = Lower(o.Email)!,
            DisplayName = o.DisplayName.Trim(),
            Role = Roles.Admin,
            Status = Statuses.Active,
            GithubEmail = Lower(o.GithubEmail),
            GoogleEmail = Lower(o.GoogleEmail),
            MicrosoftEmail = Lower(o.MicrosoftEmail),
            CreatedAt = now,
        };
    }

    /// <summary>
    /// A small team on example.com: an admin and members (one inactive), three applications (the sample client among
    /// them), sessions in every state with their log, an MCP token and a mailing of the invitation in mixed states.
    /// </summary>
    private static void Demo(SsoDbContext db, Person first, DateTimeOffset now)
    {
        Person Member(
            string username,
            string name,
            string role,
            string? github,
            string? google,
            string? microsoft,
            string status = Statuses.Active,
            int daysAgo = 20
        ) =>
            new()
            {
                Id = Guid.CreateVersion7(),
                Username = username,
                Email = $"{username.Replace('.', '-')}@example.com",
                DisplayName = name,
                Role = role,
                Status = status,
                GithubEmail = github,
                GoogleEmail = google,
                MicrosoftEmail = microsoft,
                CreatedAt = now.AddDays(-daysAgo),
            };

        var ada = Member(
            "ada.lovelace",
            "Ada Lovelace",
            Roles.Admin,
            "ada@example.com",
            null,
            "ada@corp.example.com",
            daysAgo: 28
        );
        var alan = Member(
            "alan.turing",
            "Alan Turing",
            Roles.Member,
            "alan@example.com",
            "alan.turing@example.com",
            null,
            daysAgo: 21
        );
        var grace = Member(
            "grace.hopper",
            "Grace Hopper",
            Roles.Member,
            null,
            "grace@example.com",
            null,
            daysAgo: 14
        );
        var linus = Member(
            "linus.t",
            "Linus T.",
            Roles.Member,
            "linus@example.com",
            null,
            null,
            daysAgo: 9
        );
        var ken = Member(
            "ken.thompson",
            "Ken Thompson",
            Roles.Member,
            "ken@example.com",
            null,
            null,
            Statuses.Inactive,
            daysAgo: 25
        );
        db.People.AddRange(ada, alan, grace, linus, ken);

        // The ids the development sign-in stub gives these e-mails: the demo's linked people sign in through it too.
        void Link(Person p, string provider, string email, int daysAgo, string? tenant = null) =>
            db.ExternalLogins.Add(
                new ExternalLogin
                {
                    UserId = p.Id,
                    Provider = provider,
                    Subject = DevSignIn.Subject(email),
                    TenantId = tenant,
                    Email = email,
                    LinkedAt = now.AddDays(-daysAgo),
                }
            );
        Link(ada, ProviderNames.GitHub, "ada@example.com", 27);
        Link(alan, ProviderNames.GitHub, "alan@example.com", 20);
        Link(alan, ProviderNames.Google, "alan.turing@example.com", 6);
        Link(grace, ProviderNames.Google, "grace@example.com", 13);
        Link(ken, ProviderNames.GitHub, "ken@example.com", 24);

        RegisteredClient App(
            string clientId,
            string name,
            string[] uris,
            string[] providers,
            int daysAgo,
            string status = Statuses.Active
        ) =>
            new()
            {
                Id = Guid.CreateVersion7(),
                ClientId = clientId,
                Name = name,
                SecretHash = Secrets.HashText(Secrets.NewToken()),
                RedirectUris = uris,
                Providers = providers,
                RequirePkce = true,
                Status = status,
                CreatedAt = now.AddDays(-daysAgo),
                SecretSetAt = now.AddDays(-daysAgo),
            };
        var handbook = App(
            "team-handbook",
            "Team Handbook",
            ["https://handbook.example.com/signin-oidc"],
            [ProviderNames.GitHub, ProviderNames.Google],
            26
        );
        var sample = App(
            SampleClientId,
            "Great SSO sample client",
            [SampleRedirectUri],
            [ProviderNames.GitHub, ProviderNames.Google, ProviderNames.Microsoft],
            18
        );
        var helpdesk = App(
            "helpdesk",
            "Helpdesk",
            ["https://help.example.com/signin-oidc"],
            [ProviderNames.Microsoft],
            12,
            Statuses.Inactive
        );
        db.Clients.AddRange(handbook, sample, helpdesk);

        var ip = IPAddress.Parse("203.0.113.10");
        const string agent =
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5) AppleWebKit/605.1.15 Safari/605.1.15";

        // Collected and added in time order at the end: the log's ids follow its times, as in a real log.
        var events = new List<OidcEvent>();
        void Event(
            string type,
            string outcome,
            int minutesAgo,
            Person? person = null,
            string? client = null,
            string? provider = null,
            Guid? session = null,
            string? reason = null,
            Dictionary<string, object>? detail = null
        ) =>
            events.Add(
                new OidcEvent
                {
                    OccurredAt = now.AddMinutes(-minutesAgo),
                    EventType = type,
                    Outcome = outcome,
                    Reason = reason,
                    UserId = person?.Id,
                    ClientId = client,
                    Provider = provider,
                    SessionId = session,
                    Ip = ip,
                    UserAgent = agent,
                    Detail = detail is null
                        ? null
                        : System.Text.Json.JsonSerializer.SerializeToDocument(detail),
                }
            );

        SsoSession Session(
            Person p,
            string provider,
            int minutesAgo,
            string? endReason = null,
            int endedMinutesAgo = 0,
            bool expired = false
        )
        {
            var created = now.AddMinutes(-minutesAgo);
            var session = new SsoSession
            {
                Id = Guid.CreateVersion7(),
                TokenHash = RandomNumberGenerator.GetBytes(32),
                UserId = p.Id,
                Provider = provider,
                CreatedAt = created,
                LastSeenAt = expired
                    ? created.AddDays(1)
                    : now.AddMinutes(-Math.Min(minutesAgo, 5)),
                IdleExpiresAt = expired ? created.AddDays(15) : now.AddDays(14),
                AbsoluteExpiresAt = created.AddDays(30),
                EndedAt = endReason is null ? null : now.AddMinutes(-endedMinutesAgo),
                EndReason = endReason,
                Ip = ip,
                UserAgent = agent,
            };
            db.Sessions.Add(session);
            Event("sign_in", Outcomes.Success, minutesAgo, p, provider: provider);
            Event(
                "session_started",
                Outcomes.Success,
                minutesAgo,
                p,
                provider: provider,
                session: session.Id
            );
            return session;
        }

        void SignedIn(SsoSession session, Person p, RegisteredClient app, int minutesAgo)
        {
            db.AuthorizationCodes.Add(
                new AuthorizationCode
                {
                    CodeHash = RandomNumberGenerator.GetBytes(32),
                    ClientId = app.Id,
                    SessionId = session.Id,
                    UserId = p.Id,
                    RedirectUri = app.RedirectUris[0],
                    Scopes = ["openid", "profile", "email"],
                    CreatedAt = now.AddMinutes(-minutesAgo),
                    ExpiresAt = now.AddMinutes(-minutesAgo + 1),
                    RedeemedAt = now.AddMinutes(-minutesAgo),
                }
            );
            Event(
                "code_issued",
                Outcomes.Success,
                minutesAgo,
                p,
                app.ClientId,
                session.Provider,
                session.Id
            );
            Event(
                "tokens_issued",
                Outcomes.Success,
                minutesAgo,
                p,
                app.ClientId,
                session.Provider,
                session.Id,
                detail: new() { ["grant_type"] = "authorization_code", ["refresh_token"] = false }
            );
        }

        var adaSession = Session(ada, ProviderNames.GitHub, 180);
        SignedIn(adaSession, ada, handbook, 170);
        SignedIn(adaSession, ada, sample, 60);
        var alanSession = Session(alan, ProviderNames.Google, 95);
        SignedIn(alanSession, alan, handbook, 90);
        var graceSession = Session(grace, ProviderNames.Google, 60 * 30);
        Event(
            "session_ended",
            Outcomes.Success,
            60 * 26,
            grace,
            provider: ProviderNames.Google,
            session: graceSession.Id,
            reason: SessionEndReasons.Admin
        );
        graceSession.EndedAt = now.AddHours(-26);
        graceSession.EndReason = SessionEndReasons.Admin;
        Session(alan, ProviderNames.GitHub, 60 * 24 * 19, expired: true);
        var kenSession = Session(ken, ProviderNames.GitHub, 60 * 24 * 3);
        kenSession.EndedAt = now.AddDays(-2);
        kenSession.EndReason = SessionEndReasons.UserInactive;
        Event(
            "account_linked",
            Outcomes.Success,
            60 * 24 * 6,
            alan,
            provider: ProviderNames.Google
        );
        Event(
            "sign_in",
            Outcomes.Refused,
            42,
            client: handbook.ClientId,
            provider: ProviderNames.GitHub,
            reason: "no_entry",
            detail: new() { ["email_domain"] = "gmail.example" }
        );
        Event(
            "sign_in",
            Outcomes.Refused,
            31,
            ken,
            provider: ProviderNames.GitHub,
            reason: "inactive"
        );

        db.Events.AddRange(events.OrderBy(e => e.OccurredAt));

        db.McpTokens.Add(
            new McpToken
            {
                Id = Guid.CreateVersion7(),
                UserId = first.Id,
                Name = "Laptop - Claude Code",
                TokenHash = RandomNumberGenerator.GetBytes(32),
                CreatedAt = now.AddDays(-3),
                ExpiresAt = now.AddDays(87),
                LastUsedAt = now.AddHours(-2),
            }
        );

        var mailing = new Mailing
        {
            Id = Guid.CreateVersion7(),
            TemplateId = InvitationTemplateId,
            Subject = "Your team now signs in with Great SSO",
            Body =
                "Hello,\n\nyour team now signs in to its applications with Great SSO.\n\nhttps://sso.example.com",
            CreatedBy = first.Id,
            CreatedAt = now.AddDays(-1),
        };
        db.Mailings.Add(mailing);
        Recipient To(
            string email,
            string status,
            int attempts,
            string? error = null,
            bool sent = false,
            bool later = false
        ) =>
            new()
            {
                Id = Guid.CreateVersion7(),
                MailingId = mailing.Id,
                Email = email,
                Status = status,
                Attempts = attempts,
                LastError = error,
                SentAt = sent ? now.AddDays(-1).AddMinutes(5) : null,
                NextAttemptAt = later ? now.AddDays(1) : now.AddDays(-1),
                CreatedAt = now.AddDays(-1),
            };
        db.Recipients.AddRange(
            To("alan.turing@example.com", RecipientStatuses.Sent, 1, sent: true),
            To("grace@example.com", RecipientStatuses.Sent, 1, sent: true),
            To("linus@example.com", RecipientStatuses.Failed, 3, "550 5.1.1 Mailbox unavailable"),
            To(
                "new.colleague@example.com",
                RecipientStatuses.Pending,
                1,
                "421 Try again later",
                later: true
            )
        );
    }
}

/// <summary>Runs the seed before the host serves (a hosted service starts before the server listens).</summary>
internal sealed class SeedOnStart(
    Seeder seeder,
    IOptions<SeedOptions> options,
    ILogger<SeedOnStart> logger
) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
            return;
        try
        {
            await seeder.SeedAsync(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The database is away or not migrated yet: the host still starts (readiness tells), the next start seeds.
            logger.LogError(e, "The seed on start failed - it runs again at the next start");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class SeedSetup
{
    public static WebApplicationBuilder AddSeed(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<SeedOptions>().BindConfiguration(SeedOptions.SectionName);
        builder
            .Services.AddOptions<BootstrapAdminOptions>()
            .BindConfiguration(BootstrapAdminOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddSingleton<Seeder>();
        builder.Services.AddHostedService<SeedOnStart>();
        return builder;
    }
}
