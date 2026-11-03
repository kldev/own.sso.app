using Microsoft.EntityFrameworkCore;
using Sso.Data.Audit;
using Sso.Data.Identity;
using Sso.Data.Marketing;
using Sso.Data.Messaging;
using Sso.Data.Oidc;

namespace Sso.Data;

/// <summary>
/// The one model of Great SSO (ADR 0003, 0005). The schemas are the boundaries of the data: identity (people, their
/// provider links, MCP tokens), oidc (applications, sessions, codes, tokens, the event log), marketing, messaging (the
/// outbox) and audit. Wolverine's own tables (schema wolverine) are not part of the model - they come from
/// Wolverine's dump (db/changelog). Names are snake_case; the schema is applied by Liquibase from the exported
/// migrations, never by <c>Database.Migrate()</c>.
/// </summary>
public sealed class SsoDbContext(DbContextOptions<SsoDbContext> options) : DbContext(options)
{
    /// <summary>The PostgreSQL regular expression of a username and a client id.</summary>
    private const string NamePattern = "^[a-z0-9][a-z0-9._-]{1,63}$";

    /// <summary>Columns never written to the audit log (hashes that grant access).</summary>
    public static readonly HashSet<string> NotAudited = ["secret_hash", "token_hash", "xmin"];

    public DbSet<Person> People => Set<Person>();
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();
    public DbSet<McpToken> McpTokens => Set<McpToken>();
    public DbSet<RegisteredClient> Clients => Set<RegisteredClient>();
    public DbSet<SsoSession> Sessions => Set<SsoSession>();
    public DbSet<AuthorizationCode> AuthorizationCodes => Set<AuthorizationCode>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<OidcEvent> Events => Set<OidcEvent>();
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<Mailing> Mailings => Set<Mailing>();
    public DbSet<Recipient> Recipients => Set<Recipient>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ChangeLogEntry> ChangeLog => Set<ChangeLogEntry>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        ConfigureIdentity(model);
        ConfigureOidc(model);
        ConfigureMarketing(model);
        ConfigureMessaging(model);
        ConfigureAudit(model);
    }

    private static string InList(string column, IEnumerable<string> values) =>
        $"{column} IN ({string.Join(", ", values.Select(v => $"'{v}'"))})";

    private static string LowerEmail(string column, bool nullable) =>
        (nullable ? $"{column} IS NULL OR " : "")
        + $"({column} = lower(btrim({column})) AND {column} LIKE '%_@_%')";

    private static void ConfigureIdentity(ModelBuilder model)
    {
        model.Entity<Person>(e =>
        {
            e.ToTable(
                "users",
                "identity",
                t =>
                {
                    t.HasCheckConstraint("ck_users_username", $"username ~ '{NamePattern}'");
                    t.HasCheckConstraint("ck_users_email", LowerEmail("email", false));
                    t.HasCheckConstraint("ck_users_github_email", LowerEmail("github_email", true));
                    t.HasCheckConstraint("ck_users_google_email", LowerEmail("google_email", true));
                    t.HasCheckConstraint(
                        "ck_users_microsoft_email",
                        LowerEmail("microsoft_email", true)
                    );
                    t.HasCheckConstraint("ck_users_display_name", "btrim(display_name) <> ''");
                    t.HasCheckConstraint("ck_users_role", InList("role", Roles.All));
                    t.HasCheckConstraint("ck_users_status", InList("status", Statuses.All));
                }
            );
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasDefaultValueSql("uuidv7()");
            e.Property(p => p.Username).HasMaxLength(64);
            e.Property(p => p.Email).HasMaxLength(256);
            e.Property(p => p.DisplayName).HasMaxLength(200);
            e.Property(p => p.Role).HasMaxLength(20).HasDefaultValue(Roles.Member);
            e.Property(p => p.Status).HasMaxLength(20).HasDefaultValue(Statuses.Active);
            e.Property(p => p.GithubEmail).HasMaxLength(256);
            e.Property(p => p.GoogleEmail).HasMaxLength(256);
            e.Property(p => p.MicrosoftEmail).HasMaxLength(256);
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
            e.Property(p => p.RowVersion).IsRowVersion();
            e.HasIndex(p => p.Username).IsUnique();
            e.HasIndex(p => p.Email).IsUnique();
            // One person per provider e-mail; the same address may serve two providers of one person.
            e.HasIndex(p => p.GithubEmail).IsUnique().HasFilter("github_email IS NOT NULL");
            e.HasIndex(p => p.GoogleEmail).IsUnique().HasFilter("google_email IS NOT NULL");
            e.HasIndex(p => p.MicrosoftEmail).IsUnique().HasFilter("microsoft_email IS NOT NULL");
        });

        model.Entity<ExternalLogin>(e =>
        {
            e.ToTable(
                "external_logins",
                "identity",
                t =>
                    t.HasCheckConstraint(
                        "ck_external_logins_provider",
                        InList("provider", ProviderNames.All)
                    )
            );
            e.HasKey(l => new { l.UserId, l.Provider });
            e.Property(l => l.Provider).HasMaxLength(20);
            e.Property(l => l.Subject).HasMaxLength(200);
            e.Property(l => l.TenantId).HasMaxLength(64);
            e.Property(l => l.Email).HasMaxLength(256);
            e.Property(l => l.LinkedAt).HasDefaultValueSql("now()");
            e.HasOne<Person>()
                .WithMany()
                .HasForeignKey(l => l.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(l => new { l.Provider, l.Subject }).IsUnique();
        });

        model.Entity<McpToken>(e =>
        {
            e.ToTable(
                "mcp_tokens",
                "identity",
                t =>
                {
                    t.HasCheckConstraint("ck_mcp_tokens_name", "btrim(name) <> ''");
                    t.HasCheckConstraint("ck_mcp_tokens_hash", "octet_length(token_hash) = 32");
                }
            );
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasDefaultValueSql("uuidv7()");
            e.Property(t => t.Name).HasMaxLength(100);
            e.Property(t => t.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne<Person>()
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => new { t.UserId, t.CreatedAt }).IsDescending(false, true);
        });
    }

    private static void ConfigureOidc(ModelBuilder model)
    {
        model.Entity<RegisteredClient>(e =>
        {
            e.ToTable(
                "clients",
                "oidc",
                t =>
                {
                    t.HasCheckConstraint("ck_clients_client_id", $"client_id ~ '{NamePattern}'");
                    t.HasCheckConstraint("ck_clients_name", "btrim(name) <> ''");
                    t.HasCheckConstraint(
                        "ck_clients_secret_hash",
                        "secret_hash ~ '^[A-Za-z0-9_-]{43}$'"
                    );
                    t.HasCheckConstraint(
                        "ck_clients_redirect_uris",
                        "cardinality(redirect_uris) BETWEEN 1 AND 10"
                    );
                    t.HasCheckConstraint(
                        "ck_clients_providers",
                        "cardinality(providers) >= 1 AND providers <@ ARRAY["
                            + string.Join(", ", ProviderNames.All.Select(p => $"'{p}'"))
                            + "]::text[]"
                    );
                    t.HasCheckConstraint("ck_clients_status", InList("status", Statuses.All));
                }
            );
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasDefaultValueSql("uuidv7()");
            e.Property(c => c.ClientId).HasMaxLength(64);
            e.Property(c => c.Name).HasMaxLength(200);
            e.Property(c => c.SecretHash).HasMaxLength(43);
            e.Property(c => c.Status).HasMaxLength(20).HasDefaultValue(Statuses.Active);
            e.Property(c => c.CreatedAt).HasDefaultValueSql("now()");
            e.Property(c => c.SecretSetAt).HasDefaultValueSql("now()");
            e.Property(c => c.RowVersion).IsRowVersion();
            e.HasIndex(c => c.ClientId).IsUnique();
        });

        model.Entity<SsoSession>(e =>
        {
            e.ToTable(
                "sessions",
                "oidc",
                t =>
                    t.HasCheckConstraint(
                        "ck_sessions_end_reason",
                        InList("end_reason", SessionEndReasons.All)
                    )
            );
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasDefaultValueSql("uuidv7()");
            e.Property(s => s.Provider).HasMaxLength(20);
            e.Property(s => s.EndReason).HasMaxLength(20);
            e.Property(s => s.UserAgent).HasMaxLength(500);
            e.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
            e.Property(s => s.LastSeenAt).HasDefaultValueSql("now()");
            e.HasOne<Person>()
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(s => s.TokenHash).IsUnique();
            e.HasIndex(s => new { s.UserId, s.CreatedAt }).IsDescending(false, true);
        });

        model.Entity<AuthorizationCode>(e =>
        {
            e.ToTable("authorization_codes", "oidc");
            e.HasKey(c => c.CodeHash);
            e.Property(c => c.CodeChallenge).HasMaxLength(128);
            e.Property(c => c.Nonce).HasMaxLength(500);
            e.Property(c => c.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne<RegisteredClient>()
                .WithMany()
                .HasForeignKey(c => c.ClientId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SsoSession>()
                .WithMany()
                .HasForeignKey(c => c.SessionId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(c => c.SessionId);
        });

        model.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens", "oidc");
            e.HasKey(r => r.TokenHash);
            e.Property(r => r.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne<RegisteredClient>()
                .WithMany()
                .HasForeignKey(r => r.ClientId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SsoSession>()
                .WithMany()
                .HasForeignKey(r => r.SessionId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(r => r.FamilyId);
            e.HasIndex(r => r.SessionId);
            e.HasIndex(r => r.CodeHash).HasFilter("code_hash IS NOT NULL");
        });

        model.Entity<OidcEvent>(e =>
        {
            e.ToTable(
                "events",
                "oidc",
                t =>
                    t.HasCheckConstraint(
                        "ck_events_outcome",
                        InList("outcome", [Outcomes.Success, Outcomes.Refused])
                    )
            );
            e.HasKey(v => v.Id);
            e.Property(v => v.Id).UseIdentityAlwaysColumn();
            e.Property(v => v.OccurredAt).HasDefaultValueSql("now()");
            e.Property(v => v.EventType).HasMaxLength(40);
            e.Property(v => v.Outcome).HasMaxLength(20);
            e.Property(v => v.Reason).HasMaxLength(60);
            e.Property(v => v.ClientId).HasMaxLength(64);
            e.Property(v => v.Provider).HasMaxLength(20);
            e.Property(v => v.UserAgent).HasMaxLength(500);
            e.HasIndex(v => v.OccurredAt).IsDescending();
            e.HasIndex(v => new { v.UserId, v.OccurredAt })
                .IsDescending(false, true)
                .HasFilter("user_id IS NOT NULL");
            e.HasIndex(v => new { v.ClientId, v.OccurredAt })
                .IsDescending(false, true)
                .HasFilter("client_id IS NOT NULL");
            e.HasIndex(v => v.SessionId).HasFilter("session_id IS NOT NULL");
        });
    }

    private static void ConfigureMarketing(ModelBuilder model)
    {
        model.Entity<Template>(e =>
        {
            e.ToTable(
                "templates",
                "marketing",
                t =>
                {
                    t.HasCheckConstraint("ck_templates_code", "code ~ '^[a-z][a-z0-9-]{1,39}$'");
                    t.HasCheckConstraint(
                        "ck_templates_product",
                        "product ~ '^[a-z][a-z0-9-]{1,19}$'"
                    );
                    t.HasCheckConstraint(
                        "ck_templates_language",
                        InList("language", Languages.All)
                    );
                    t.HasCheckConstraint("ck_templates_subject", "btrim(subject) <> ''");
                    t.HasCheckConstraint(
                        "ck_templates_body",
                        "btrim(body) <> '' AND length(body) <= 20000"
                    );
                }
            );
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasDefaultValueSql("uuidv7()");
            e.Property(t => t.Code).HasMaxLength(40);
            e.Property(t => t.Product).HasMaxLength(20);
            e.Property(t => t.Language).HasMaxLength(2);
            e.Property(t => t.Name).HasMaxLength(100);
            e.Property(t => t.Subject).HasMaxLength(200);
            e.Property(t => t.CreatedAt).HasDefaultValueSql("now()");
            e.Property(t => t.UpdatedAt).HasDefaultValueSql("now()");
            e.Property(t => t.RowVersion).IsRowVersion();
            e.HasIndex(t => t.Code).IsUnique();
            e.HasIndex(t => new { t.Product, t.Language }).IsUnique();
        });

        model.Entity<Mailing>(e =>
        {
            e.ToTable("mailings", "marketing");
            e.HasKey(m => m.Id);
            e.Property(m => m.Subject).HasMaxLength(200);
            e.Property(m => m.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne<Template>()
                .WithMany()
                .HasForeignKey(m => m.TemplateId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(m => new { m.CreatedAt, m.Id }).IsDescending(true, false);
            e.HasIndex(m => m.TemplateId);
        });

        model.Entity<Recipient>(e =>
        {
            e.ToTable(
                "recipients",
                "marketing",
                t =>
                {
                    t.HasCheckConstraint(
                        "ck_recipients_status",
                        InList("status", RecipientStatuses.All)
                    );
                    t.HasCheckConstraint("ck_recipients_email", "email = lower(email)");
                }
            );
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasDefaultValueSql("uuidv7()");
            e.Property(r => r.Email).HasMaxLength(254);
            e.Property(r => r.Status).HasMaxLength(10).HasDefaultValue(RecipientStatuses.Pending);
            e.Property(r => r.Attempts).HasDefaultValue(0);
            e.Property(r => r.LastError).HasMaxLength(1000);
            e.Property(r => r.NextAttemptAt).HasDefaultValueSql("now()");
            e.Property(r => r.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne<Mailing>()
                .WithMany()
                .HasForeignKey(r => r.MailingId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(r => new { r.MailingId, r.Email }).IsUnique();
            // The sender's queue: pending rows that are due, in order.
            e.HasIndex(r => new
                {
                    r.NextAttemptAt,
                    r.CreatedAt,
                    r.Id,
                })
                .HasFilter("status = 'pending'")
                .HasDatabaseName("ix_recipients_pending");
            // "Already got this product's e-mail": the address's earlier e-mails.
            e.HasIndex(r => r.Email);
        });
    }

    private static void ConfigureMessaging(ModelBuilder model)
    {
        model.Entity<OutboxMessage>(e =>
        {
            e.ToTable(
                "outbox_messages",
                "messaging",
                t =>
                    t.HasCheckConstraint(
                        "ck_outbox_messages_type",
                        "message_type ~ '^[a-z][a-z0-9-]*$'"
                    )
            );
            e.HasKey(m => m.Id);
            e.Property(m => m.Id).HasDefaultValueSql("uuidv7()");
            e.Property(m => m.MessageType).HasMaxLength(100);
            e.Property(m => m.Payload).HasColumnType("jsonb");
            e.Property(m => m.OccurredAt).HasDefaultValueSql("now()");
            e.Property(m => m.Attempts).HasDefaultValue(0);
            e.Property(m => m.LastError).HasMaxLength(2000);
            // The relay's queue: pending in order; and the retention's sweep of processed ones.
            e.HasIndex(m => new { m.OccurredAt, m.Id })
                .HasFilter("processed_at IS NULL")
                .HasDatabaseName("ix_outbox_messages_pending");
            e.HasIndex(m => m.ProcessedAt)
                .HasFilter("processed_at IS NOT NULL")
                .HasDatabaseName("ix_outbox_messages_processed");
        });
    }

    private static void ConfigureAudit(ModelBuilder model)
    {
        model.Entity<ChangeLogEntry>(e =>
        {
            e.ToTable("change_log", "audit");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).UseIdentityAlwaysColumn();
            e.Property(c => c.OccurredAt).HasDefaultValueSql("now()");
            e.Property(c => c.TransactionId).HasDefaultValueSql("txid_current()");
            e.Property(c => c.DbRole).IsRequired().HasDefaultValueSql("session_user");
        });
    }
}
