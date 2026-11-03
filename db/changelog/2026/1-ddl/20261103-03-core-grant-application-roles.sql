--liquibase formatted sql

--changeset sso:20261103-03-core-grant-application-roles splitStatements:false
--comment: What the host (sso_api) and the runner (sso_runner) may do with the rows (ADR 0004): DML on the tables they
--comment: need and nothing else - no CREATE, ALTER or DROP anywhere (the migrator owns every object). The event log
--comment: and the audit log are append-only for the host (no UPDATE, no DELETE, ADR 0009); the outbox is written by
--comment: the host and claimed, marked and cleared by the runner (ADR 0010). Tables a later migration adds get the
--comment: host's SELECT, INSERT, UPDATE by default (DELETE always explicitly).
GRANT USAGE ON SCHEMA identity, oidc, audit, marketing, messaging TO sso_api;

GRANT SELECT, INSERT, UPDATE ON identity.users, identity.mcp_tokens TO sso_api;
GRANT SELECT, INSERT, DELETE ON identity.external_logins TO sso_api;

GRANT SELECT, INSERT, UPDATE ON oidc.clients, oidc.sessions, oidc.authorization_codes, oidc.refresh_tokens TO sso_api;
GRANT SELECT, INSERT ON oidc.events TO sso_api;

GRANT SELECT, INSERT, UPDATE ON marketing.templates, marketing.recipients TO sso_api;
GRANT SELECT, INSERT ON marketing.mailings TO sso_api;

-- SELECT only for INSERT ... RETURNING of the generated id; the log itself is never read by the host.
GRANT SELECT, INSERT ON audit.change_log TO sso_api;
GRANT SELECT, INSERT ON messaging.outbox_messages TO sso_api;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA oidc, audit TO sso_api;

ALTER DEFAULT PRIVILEGES IN SCHEMA identity, oidc, marketing GRANT SELECT, INSERT, UPDATE ON TABLES TO sso_api;
ALTER DEFAULT PRIVILEGES IN SCHEMA identity, oidc, audit, marketing, messaging
    GRANT USAGE, SELECT ON SEQUENCES TO sso_api;

GRANT USAGE ON SCHEMA messaging TO sso_runner;
GRANT SELECT, UPDATE, DELETE ON messaging.outbox_messages TO sso_runner;
--rollback REVOKE ALL ON ALL TABLES IN SCHEMA identity, oidc, audit, marketing, messaging FROM sso_api, sso_runner;
--rollback REVOKE ALL ON ALL SEQUENCES IN SCHEMA oidc, audit FROM sso_api;
--rollback ALTER DEFAULT PRIVILEGES IN SCHEMA identity, oidc, marketing REVOKE SELECT, INSERT, UPDATE ON TABLES FROM sso_api;
--rollback ALTER DEFAULT PRIVILEGES IN SCHEMA identity, oidc, audit, marketing, messaging REVOKE USAGE, SELECT ON SEQUENCES FROM sso_api;
--rollback REVOKE USAGE ON SCHEMA identity, oidc, audit, marketing, messaging FROM sso_api, sso_runner;
