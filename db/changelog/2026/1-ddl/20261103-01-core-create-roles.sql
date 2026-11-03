--liquibase formatted sql

--changeset sso:20261103-01-core-create-roles splitStatements:false
--comment: The application roles (ADR 0004). The migrator (sso_migrator, made by Sso.DbMigrator prepare) owns the
--comment: database and every object and runs Liquibase; sso_api (the host) and sso_runner (the runner) only read and
--comment: write rows - their grants are in 20261103-03. Roles are server-wide, hence the prefix; they are NOLOGIN here,
--comment: Sso.DbMigrator finish gives them LOGIN and a password.
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'sso_api') THEN
        CREATE ROLE sso_api NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'sso_runner') THEN
        CREATE ROLE sso_runner NOLOGIN;
    END IF;
END
$$;

-- No JIT for the API: its reads are short and indexed; the planner's estimate of a list with per-row sub-selects can
-- pass jit_above_cost and compile for longer than the query runs. Applies to new connections.
ALTER ROLE sso_api SET jit = off;

-- Nobody but the owner creates anything in public.
REVOKE ALL ON SCHEMA public FROM PUBLIC;
--rollback ALTER ROLE sso_api RESET jit;
