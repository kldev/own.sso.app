--liquibase formatted sql

--changeset sso:20261103-02-ef-initial splitStatements:false
--comment: ef-migration 20261103161340_Initial
--comment: Exported by db/scripts/export-ef-migration.sh from libs/Sso.Data/Migrations - do not edit by hand.
DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'oidc') THEN
        CREATE SCHEMA oidc;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'audit') THEN
        CREATE SCHEMA audit;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'identity') THEN
        CREATE SCHEMA identity;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'marketing') THEN
        CREATE SCHEMA marketing;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'messaging') THEN
        CREATE SCHEMA messaging;
    END IF;
END $EF$;

CREATE TABLE audit.change_log (
    id bigint GENERATED ALWAYS AS IDENTITY,
    occurred_at timestamp with time zone NOT NULL DEFAULT (now()),
    transaction_id bigint NOT NULL DEFAULT (txid_current()),
    table_schema text NOT NULL,
    table_name text NOT NULL,
    operation text NOT NULL,
    actor_id text,
    db_role text NOT NULL DEFAULT (session_user),
    old_row jsonb,
    new_row jsonb,
    CONSTRAINT pk_change_log PRIMARY KEY (id)
);

CREATE TABLE oidc.clients (
    id uuid NOT NULL DEFAULT (uuidv7()),
    client_id character varying(64) NOT NULL,
    name character varying(200) NOT NULL,
    secret_hash character varying(43) NOT NULL,
    redirect_uris text[] NOT NULL,
    providers text[] NOT NULL,
    require_pkce boolean NOT NULL,
    status character varying(20) NOT NULL DEFAULT 'active',
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    secret_set_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_clients PRIMARY KEY (id),
    CONSTRAINT ck_clients_client_id CHECK (client_id ~ '^[a-z0-9][a-z0-9._-]{1,63}$'),
    CONSTRAINT ck_clients_name CHECK (btrim(name) <> ''),
    CONSTRAINT ck_clients_providers CHECK (cardinality(providers) >= 1 AND providers <@ ARRAY['github', 'google', 'microsoft']::text[]),
    CONSTRAINT ck_clients_redirect_uris CHECK (cardinality(redirect_uris) BETWEEN 1 AND 10),
    CONSTRAINT ck_clients_secret_hash CHECK (secret_hash ~ '^[A-Za-z0-9_-]{43}$'),
    CONSTRAINT ck_clients_status CHECK (status IN ('active', 'inactive'))
);

CREATE TABLE oidc.events (
    id bigint GENERATED ALWAYS AS IDENTITY,
    occurred_at timestamp with time zone NOT NULL DEFAULT (now()),
    event_type character varying(40) NOT NULL,
    outcome character varying(20) NOT NULL,
    reason character varying(60),
    user_id uuid,
    client_id character varying(64),
    provider character varying(20),
    session_id uuid,
    actor_id uuid,
    ip inet,
    user_agent character varying(500),
    detail jsonb,
    CONSTRAINT pk_events PRIMARY KEY (id),
    CONSTRAINT ck_events_outcome CHECK (outcome IN ('success', 'refused'))
);

CREATE TABLE messaging.outbox_messages (
    id uuid NOT NULL DEFAULT (uuidv7()),
    message_type character varying(100) NOT NULL,
    payload jsonb NOT NULL,
    occurred_at timestamp with time zone NOT NULL DEFAULT (now()),
    processed_at timestamp with time zone,
    attempts integer NOT NULL,
    last_error character varying(2000),
    CONSTRAINT pk_outbox_messages PRIMARY KEY (id),
    CONSTRAINT ck_outbox_messages_type CHECK (message_type ~ '^[a-z][a-z0-9-]*$')
);

CREATE TABLE marketing.templates (
    id uuid NOT NULL DEFAULT (uuidv7()),
    code character varying(40) NOT NULL,
    product character varying(20) NOT NULL,
    language character varying(2) NOT NULL,
    name character varying(100) NOT NULL,
    subject character varying(200) NOT NULL,
    body text NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_templates PRIMARY KEY (id),
    CONSTRAINT ck_templates_body CHECK (btrim(body) <> '' AND length(body) <= 20000),
    CONSTRAINT ck_templates_code CHECK (code ~ '^[a-z][a-z0-9-]{1,39}$'),
    CONSTRAINT ck_templates_language CHECK (language IN ('pl', 'en', 'de')),
    CONSTRAINT ck_templates_product CHECK (product ~ '^[a-z][a-z0-9-]{1,19}$'),
    CONSTRAINT ck_templates_subject CHECK (btrim(subject) <> '')
);

CREATE TABLE identity.users (
    id uuid NOT NULL DEFAULT (uuidv7()),
    username character varying(64) NOT NULL,
    email character varying(256) NOT NULL,
    display_name character varying(200) NOT NULL,
    role character varying(20) NOT NULL DEFAULT 'member',
    status character varying(20) NOT NULL DEFAULT 'active',
    github_email character varying(256),
    google_email character varying(256),
    microsoft_email character varying(256),
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_users PRIMARY KEY (id),
    CONSTRAINT ck_users_display_name CHECK (btrim(display_name) <> ''),
    CONSTRAINT ck_users_email CHECK ((email = lower(btrim(email)) AND email LIKE '%_@_%')),
    CONSTRAINT ck_users_github_email CHECK (github_email IS NULL OR (github_email = lower(btrim(github_email)) AND github_email LIKE '%_@_%')),
    CONSTRAINT ck_users_google_email CHECK (google_email IS NULL OR (google_email = lower(btrim(google_email)) AND google_email LIKE '%_@_%')),
    CONSTRAINT ck_users_microsoft_email CHECK (microsoft_email IS NULL OR (microsoft_email = lower(btrim(microsoft_email)) AND microsoft_email LIKE '%_@_%')),
    CONSTRAINT ck_users_role CHECK (role IN ('admin', 'member')),
    CONSTRAINT ck_users_status CHECK (status IN ('active', 'inactive')),
    CONSTRAINT ck_users_username CHECK (username ~ '^[a-z0-9][a-z0-9._-]{1,63}$')
);

CREATE TABLE marketing.mailings (
    id uuid NOT NULL,
    template_id uuid NOT NULL,
    subject character varying(200) NOT NULL,
    body text NOT NULL,
    created_by uuid NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_mailings PRIMARY KEY (id),
    CONSTRAINT fk_mailings_templates_template_id FOREIGN KEY (template_id) REFERENCES marketing.templates (id) ON DELETE RESTRICT
);

CREATE TABLE identity.external_logins (
    user_id uuid NOT NULL,
    provider character varying(20) NOT NULL,
    subject character varying(200) NOT NULL,
    tenant_id character varying(64),
    email character varying(256) NOT NULL,
    linked_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_external_logins PRIMARY KEY (user_id, provider),
    CONSTRAINT ck_external_logins_provider CHECK (provider IN ('github', 'google', 'microsoft')),
    CONSTRAINT fk_external_logins_users_user_id FOREIGN KEY (user_id) REFERENCES identity.users (id) ON DELETE RESTRICT
);

CREATE TABLE identity.mcp_tokens (
    id uuid NOT NULL DEFAULT (uuidv7()),
    user_id uuid NOT NULL,
    name character varying(100) NOT NULL,
    token_hash bytea NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    expires_at timestamp with time zone,
    revoked_at timestamp with time zone,
    last_used_at timestamp with time zone,
    CONSTRAINT pk_mcp_tokens PRIMARY KEY (id),
    CONSTRAINT ck_mcp_tokens_hash CHECK (octet_length(token_hash) = 32),
    CONSTRAINT ck_mcp_tokens_name CHECK (btrim(name) <> ''),
    CONSTRAINT fk_mcp_tokens_users_user_id FOREIGN KEY (user_id) REFERENCES identity.users (id) ON DELETE RESTRICT
);

CREATE TABLE oidc.sessions (
    id uuid NOT NULL DEFAULT (uuidv7()),
    token_hash bytea NOT NULL,
    user_id uuid NOT NULL,
    provider character varying(20) NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    last_seen_at timestamp with time zone NOT NULL DEFAULT (now()),
    idle_expires_at timestamp with time zone NOT NULL,
    absolute_expires_at timestamp with time zone NOT NULL,
    ended_at timestamp with time zone,
    end_reason character varying(20),
    ip inet,
    user_agent character varying(500),
    CONSTRAINT pk_sessions PRIMARY KEY (id),
    CONSTRAINT ck_sessions_end_reason CHECK (end_reason IN ('idle', 'absolute', 'admin', 'user_inactive', 'unlinked')),
    CONSTRAINT fk_sessions_users_user_id FOREIGN KEY (user_id) REFERENCES identity.users (id) ON DELETE RESTRICT
);

CREATE TABLE marketing.recipients (
    id uuid NOT NULL DEFAULT (uuidv7()),
    mailing_id uuid NOT NULL,
    email character varying(254) NOT NULL,
    status character varying(10) NOT NULL DEFAULT 'pending',
    attempts integer NOT NULL,
    last_error character varying(1000),
    sent_at timestamp with time zone,
    next_attempt_at timestamp with time zone NOT NULL DEFAULT (now()),
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_recipients PRIMARY KEY (id),
    CONSTRAINT ck_recipients_email CHECK (email = lower(email)),
    CONSTRAINT ck_recipients_status CHECK (status IN ('pending', 'sending', 'sent', 'failed')),
    CONSTRAINT fk_recipients_mailings_mailing_id FOREIGN KEY (mailing_id) REFERENCES marketing.mailings (id) ON DELETE RESTRICT
);

CREATE TABLE oidc.authorization_codes (
    code_hash bytea NOT NULL,
    client_id uuid NOT NULL,
    session_id uuid NOT NULL,
    user_id uuid NOT NULL,
    redirect_uri text NOT NULL,
    code_challenge character varying(128),
    nonce character varying(500),
    scopes text[] NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    expires_at timestamp with time zone NOT NULL,
    redeemed_at timestamp with time zone,
    CONSTRAINT pk_authorization_codes PRIMARY KEY (code_hash),
    CONSTRAINT fk_authorization_codes_clients_client_id FOREIGN KEY (client_id) REFERENCES oidc.clients (id) ON DELETE RESTRICT,
    CONSTRAINT fk_authorization_codes_sessions_session_id FOREIGN KEY (session_id) REFERENCES oidc.sessions (id) ON DELETE RESTRICT
);

CREATE TABLE oidc.refresh_tokens (
    token_hash bytea NOT NULL,
    family_id uuid NOT NULL,
    client_id uuid NOT NULL,
    session_id uuid NOT NULL,
    user_id uuid NOT NULL,
    scopes text[] NOT NULL,
    code_hash bytea,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    used_at timestamp with time zone,
    revoked_at timestamp with time zone,
    CONSTRAINT pk_refresh_tokens PRIMARY KEY (token_hash),
    CONSTRAINT fk_refresh_tokens_clients_client_id FOREIGN KEY (client_id) REFERENCES oidc.clients (id) ON DELETE RESTRICT,
    CONSTRAINT fk_refresh_tokens_sessions_session_id FOREIGN KEY (session_id) REFERENCES oidc.sessions (id) ON DELETE RESTRICT
);

CREATE INDEX ix_authorization_codes_client_id ON oidc.authorization_codes (client_id);

CREATE INDEX ix_authorization_codes_session_id ON oidc.authorization_codes (session_id);

CREATE UNIQUE INDEX ix_clients_client_id ON oidc.clients (client_id);

CREATE INDEX ix_events_client_id_occurred_at ON oidc.events (client_id, occurred_at DESC) WHERE client_id IS NOT NULL;

CREATE INDEX ix_events_occurred_at ON oidc.events (occurred_at DESC);

CREATE INDEX ix_events_session_id ON oidc.events (session_id) WHERE session_id IS NOT NULL;

CREATE INDEX ix_events_user_id_occurred_at ON oidc.events (user_id, occurred_at DESC) WHERE user_id IS NOT NULL;

CREATE UNIQUE INDEX ix_external_logins_provider_subject ON identity.external_logins (provider, subject);

CREATE INDEX ix_mailings_created_at_id ON marketing.mailings (created_at DESC, id);

CREATE INDEX ix_mailings_template_id ON marketing.mailings (template_id);

CREATE UNIQUE INDEX ix_mcp_tokens_token_hash ON identity.mcp_tokens (token_hash);

CREATE INDEX ix_mcp_tokens_user_id_created_at ON identity.mcp_tokens (user_id, created_at DESC);

CREATE INDEX ix_outbox_messages_pending ON messaging.outbox_messages (occurred_at, id) WHERE processed_at IS NULL;

CREATE INDEX ix_outbox_messages_processed ON messaging.outbox_messages (processed_at) WHERE processed_at IS NOT NULL;

CREATE INDEX ix_recipients_email ON marketing.recipients (email);

CREATE UNIQUE INDEX ix_recipients_mailing_id_email ON marketing.recipients (mailing_id, email);

CREATE INDEX ix_recipients_pending ON marketing.recipients (next_attempt_at, created_at, id) WHERE status = 'pending';

CREATE INDEX ix_refresh_tokens_client_id ON oidc.refresh_tokens (client_id);

CREATE INDEX ix_refresh_tokens_code_hash ON oidc.refresh_tokens (code_hash) WHERE code_hash IS NOT NULL;

CREATE INDEX ix_refresh_tokens_family_id ON oidc.refresh_tokens (family_id);

CREATE INDEX ix_refresh_tokens_session_id ON oidc.refresh_tokens (session_id);

CREATE UNIQUE INDEX ix_sessions_token_hash ON oidc.sessions (token_hash);

CREATE INDEX ix_sessions_user_id_created_at ON oidc.sessions (user_id, created_at DESC);

CREATE UNIQUE INDEX ix_templates_code ON marketing.templates (code);

CREATE UNIQUE INDEX ix_templates_product_language ON marketing.templates (product, language);

CREATE UNIQUE INDEX ix_users_email ON identity.users (email);

CREATE UNIQUE INDEX ix_users_github_email ON identity.users (github_email) WHERE github_email IS NOT NULL;

CREATE UNIQUE INDEX ix_users_google_email ON identity.users (google_email) WHERE google_email IS NOT NULL;

CREATE UNIQUE INDEX ix_users_microsoft_email ON identity.users (microsoft_email) WHERE microsoft_email IS NOT NULL;

CREATE UNIQUE INDEX ix_users_username ON identity.users (username);
--rollback DROP TABLE oidc.authorization_codes;
--rollback DROP TABLE audit.change_log;
--rollback DROP TABLE oidc.events;
--rollback DROP TABLE identity.external_logins;
--rollback DROP TABLE identity.mcp_tokens;
--rollback DROP TABLE messaging.outbox_messages;
--rollback DROP TABLE marketing.recipients;
--rollback DROP TABLE oidc.refresh_tokens;
--rollback DROP TABLE marketing.mailings;
--rollback DROP TABLE oidc.clients;
--rollback DROP TABLE oidc.sessions;
--rollback DROP TABLE marketing.templates;
--rollback DROP TABLE identity.users;
