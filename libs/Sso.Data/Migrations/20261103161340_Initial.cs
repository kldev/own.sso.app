using System;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Sso.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "oidc");

            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.EnsureSchema(
                name: "marketing");

            migrationBuilder.EnsureSchema(
                name: "messaging");

            migrationBuilder.CreateTable(
                name: "change_log",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    transaction_id = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "txid_current()"),
                    table_schema = table.Column<string>(type: "text", nullable: false),
                    table_name = table.Column<string>(type: "text", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: true),
                    db_role = table.Column<string>(type: "text", nullable: false, defaultValueSql: "session_user"),
                    old_row = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    new_row = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "clients",
                schema: "oidc",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    client_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(43)", maxLength: 43, nullable: false),
                    redirect_uris = table.Column<string[]>(type: "text[]", nullable: false),
                    providers = table.Column<string[]>(type: "text[]", nullable: false),
                    require_pkce = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "active"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    secret_set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clients", x => x.id);
                    table.CheckConstraint("ck_clients_client_id", "client_id ~ '^[a-z0-9][a-z0-9._-]{1,63}$'");
                    table.CheckConstraint("ck_clients_name", "btrim(name) <> ''");
                    table.CheckConstraint("ck_clients_providers", "cardinality(providers) >= 1 AND providers <@ ARRAY['github', 'google', 'microsoft']::text[]");
                    table.CheckConstraint("ck_clients_redirect_uris", "cardinality(redirect_uris) BETWEEN 1 AND 10");
                    table.CheckConstraint("ck_clients_secret_hash", "secret_hash ~ '^[A-Za-z0-9_-]{43}$'");
                    table.CheckConstraint("ck_clients_status", "status IN ('active', 'inactive')");
                });

            migrationBuilder.CreateTable(
                name: "events",
                schema: "oidc",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    event_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    client_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    user_agent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    detail = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_events", x => x.id);
                    table.CheckConstraint("ck_events_outcome", "outcome IN ('success', 'refused')");
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "messaging",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    message_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                    table.CheckConstraint("ck_outbox_messages_type", "message_type ~ '^[a-z][a-z0-9-]*$'");
                });

            migrationBuilder.CreateTable(
                name: "templates",
                schema: "marketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    product = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    language = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_templates", x => x.id);
                    table.CheckConstraint("ck_templates_body", "btrim(body) <> '' AND length(body) <= 20000");
                    table.CheckConstraint("ck_templates_code", "code ~ '^[a-z][a-z0-9-]{1,39}$'");
                    table.CheckConstraint("ck_templates_language", "language IN ('pl', 'en', 'de')");
                    table.CheckConstraint("ck_templates_product", "product ~ '^[a-z][a-z0-9-]{1,19}$'");
                    table.CheckConstraint("ck_templates_subject", "btrim(subject) <> ''");
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "member"),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "active"),
                    github_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    google_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    microsoft_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_display_name", "btrim(display_name) <> ''");
                    table.CheckConstraint("ck_users_email", "(email = lower(btrim(email)) AND email LIKE '%_@_%')");
                    table.CheckConstraint("ck_users_github_email", "github_email IS NULL OR (github_email = lower(btrim(github_email)) AND github_email LIKE '%_@_%')");
                    table.CheckConstraint("ck_users_google_email", "google_email IS NULL OR (google_email = lower(btrim(google_email)) AND google_email LIKE '%_@_%')");
                    table.CheckConstraint("ck_users_microsoft_email", "microsoft_email IS NULL OR (microsoft_email = lower(btrim(microsoft_email)) AND microsoft_email LIKE '%_@_%')");
                    table.CheckConstraint("ck_users_role", "role IN ('admin', 'member')");
                    table.CheckConstraint("ck_users_status", "status IN ('active', 'inactive')");
                    table.CheckConstraint("ck_users_username", "username ~ '^[a-z0-9][a-z0-9._-]{1,63}$'");
                });

            migrationBuilder.CreateTable(
                name: "mailings",
                schema: "marketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mailings", x => x.id);
                    table.ForeignKey(
                        name: "fk_mailings_templates_template_id",
                        column: x => x.template_id,
                        principalSchema: "marketing",
                        principalTable: "templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "external_logins",
                schema: "identity",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_external_logins", x => new { x.user_id, x.provider });
                    table.CheckConstraint("ck_external_logins_provider", "provider IN ('github', 'google', 'microsoft')");
                    table.ForeignKey(
                        name: "fk_external_logins_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mcp_tokens",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_tokens", x => x.id);
                    table.CheckConstraint("ck_mcp_tokens_hash", "octet_length(token_hash) = 32");
                    table.CheckConstraint("ck_mcp_tokens_name", "btrim(name) <> ''");
                    table.ForeignKey(
                        name: "fk_mcp_tokens_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                schema: "oidc",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    idle_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    absolute_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    end_reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    user_agent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sessions", x => x.id);
                    table.CheckConstraint("ck_sessions_end_reason", "end_reason IN ('idle', 'absolute', 'admin', 'user_inactive', 'unlinked')");
                    table.ForeignKey(
                        name: "fk_sessions_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recipients",
                schema: "marketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    mailing_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "pending"),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recipients", x => x.id);
                    table.CheckConstraint("ck_recipients_email", "email = lower(email)");
                    table.CheckConstraint("ck_recipients_status", "status IN ('pending', 'sending', 'sent', 'failed')");
                    table.ForeignKey(
                        name: "fk_recipients_mailings_mailing_id",
                        column: x => x.mailing_id,
                        principalSchema: "marketing",
                        principalTable: "mailings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "authorization_codes",
                schema: "oidc",
                columns: table => new
                {
                    code_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    redirect_uri = table.Column<string>(type: "text", nullable: false),
                    code_challenge = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    nonce = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    redeemed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_authorization_codes", x => x.code_hash);
                    table.ForeignKey(
                        name: "fk_authorization_codes_clients_client_id",
                        column: x => x.client_id,
                        principalSchema: "oidc",
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_authorization_codes_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "oidc",
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                schema: "oidc",
                columns: table => new
                {
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    code_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.token_hash);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_clients_client_id",
                        column: x => x.client_id,
                        principalSchema: "oidc",
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "oidc",
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_authorization_codes_client_id",
                schema: "oidc",
                table: "authorization_codes",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_authorization_codes_session_id",
                schema: "oidc",
                table: "authorization_codes",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_clients_client_id",
                schema: "oidc",
                table: "clients",
                column: "client_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_events_client_id_occurred_at",
                schema: "oidc",
                table: "events",
                columns: new[] { "client_id", "occurred_at" },
                descending: new[] { false, true },
                filter: "client_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_events_occurred_at",
                schema: "oidc",
                table: "events",
                column: "occurred_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_events_session_id",
                schema: "oidc",
                table: "events",
                column: "session_id",
                filter: "session_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_events_user_id_occurred_at",
                schema: "oidc",
                table: "events",
                columns: new[] { "user_id", "occurred_at" },
                descending: new[] { false, true },
                filter: "user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_external_logins_provider_subject",
                schema: "identity",
                table: "external_logins",
                columns: new[] { "provider", "subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mailings_created_at_id",
                schema: "marketing",
                table: "mailings",
                columns: new[] { "created_at", "id" },
                descending: new[] { true, false });

            migrationBuilder.CreateIndex(
                name: "ix_mailings_template_id",
                schema: "marketing",
                table: "mailings",
                column: "template_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_tokens_token_hash",
                schema: "identity",
                table: "mcp_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mcp_tokens_user_id_created_at",
                schema: "identity",
                table: "mcp_tokens",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                schema: "messaging",
                table: "outbox_messages",
                columns: new[] { "occurred_at", "id" },
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_processed",
                schema: "messaging",
                table: "outbox_messages",
                column: "processed_at",
                filter: "processed_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_recipients_email",
                schema: "marketing",
                table: "recipients",
                column: "email");

            migrationBuilder.CreateIndex(
                name: "ix_recipients_mailing_id_email",
                schema: "marketing",
                table: "recipients",
                columns: new[] { "mailing_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_recipients_pending",
                schema: "marketing",
                table: "recipients",
                columns: new[] { "next_attempt_at", "created_at", "id" },
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_client_id",
                schema: "oidc",
                table: "refresh_tokens",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_code_hash",
                schema: "oidc",
                table: "refresh_tokens",
                column: "code_hash",
                filter: "code_hash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_family_id",
                schema: "oidc",
                table: "refresh_tokens",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_session_id",
                schema: "oidc",
                table: "refresh_tokens",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_token_hash",
                schema: "oidc",
                table: "sessions",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sessions_user_id_created_at",
                schema: "oidc",
                table: "sessions",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_templates_code",
                schema: "marketing",
                table: "templates",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_templates_product_language",
                schema: "marketing",
                table: "templates",
                columns: new[] { "product", "language" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                schema: "identity",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_github_email",
                schema: "identity",
                table: "users",
                column: "github_email",
                unique: true,
                filter: "github_email IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_users_google_email",
                schema: "identity",
                table: "users",
                column: "google_email",
                unique: true,
                filter: "google_email IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_users_microsoft_email",
                schema: "identity",
                table: "users",
                column: "microsoft_email",
                unique: true,
                filter: "microsoft_email IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_users_username",
                schema: "identity",
                table: "users",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "authorization_codes",
                schema: "oidc");

            migrationBuilder.DropTable(
                name: "change_log",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "events",
                schema: "oidc");

            migrationBuilder.DropTable(
                name: "external_logins",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "mcp_tokens",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "recipients",
                schema: "marketing");

            migrationBuilder.DropTable(
                name: "refresh_tokens",
                schema: "oidc");

            migrationBuilder.DropTable(
                name: "mailings",
                schema: "marketing");

            migrationBuilder.DropTable(
                name: "clients",
                schema: "oidc");

            migrationBuilder.DropTable(
                name: "sessions",
                schema: "oidc");

            migrationBuilder.DropTable(
                name: "templates",
                schema: "marketing");

            migrationBuilder.DropTable(
                name: "users",
                schema: "identity");
        }
    }
}
