# 0004. Three database roles: the migrator owns, the host and the runner read and write rows

Status: accepted (2026-11-03)

## Context

The PostgreSQL server may be shared with other applications, and a managed server gives no superuser. An application
that can create or drop tables can be talked into doing it.

## Decision

- **`sso_migrator`** - LOGIN, CREATEROLE, never a superuser; owns the database and every object; runs Liquibase.
  `apps/Sso.DbMigrator prepare` makes it and the database as the server's admin, `finish` gives the application roles
  LOGIN and a password after Liquibase (the same code in `deploy/deploy.sh` and in the tests).
- **`sso_api`** (the host) - `SELECT, INSERT, UPDATE` on the tables of `identity`, `oidc` and `marketing` it works with,
  `DELETE` only on `identity.external_logins` (unlinking); on `oidc.events` and `audit.change_log` only `SELECT, INSERT`
  (append-only, ADR 0009); on `messaging.outbox_messages` only `SELECT, INSERT`. Tables a later migration adds get
  `SELECT, INSERT, UPDATE` by default privileges; `DELETE` is always granted by hand. JIT off for the role.
- **`sso_runner`** (the runner) - `SELECT, UPDATE, DELETE` on `messaging.outbox_messages` and Wolverine's tables;
  nothing else.
- The grants are a changeset (`20261103-03-core-grant-application-roles.sql`); `CONNECT` on the database only for the
  three roles.

## Consequences

- Neither the host nor the runner can create, alter, drop or truncate anything (`RoleTests`).
- A new table needs its grant reviewed; a forgotten one is a 500 in the tests, not a silent hole.

## Revisit when

Reporting or another process needs read access (a fourth, read-only role).
