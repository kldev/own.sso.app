# 0003. The EF Core model is the source of the schema; Liquibase applies it

Status: accepted (2026-11-03)

## Context

The data access is EF Core (Npgsql). Its migrations describe the schema well, but `Database.Migrate()` at start makes
every replica a schema changer, needs DDL rights for the application and keeps a second history next to the
deployment's. Liquibase is the tool the deployment already runs as the database owner, before the host starts.

## Decision

- **One model** (`libs/Sso.Data`, `SsoDbContext`) with every table, check, index, default and the `xmin` row versions.
  `dotnet ef migrations add <Name>` creates a migration from a change of the model.
- **`db/scripts/export-ef-migration.sh <Name> <slug>`** turns the migration into a dated Liquibase changeset
  `db/changelog/<yyyy>/1-ddl/<yyyymmdd>-<nn>-ef-<slug>.sql`: the up script of
  `dotnet ef migrations script <previous> <Name> --no-transactions` (Liquibase runs the changeset in a transaction) and,
  as `--rollback`, the script back to the previous migration. It records the migration's id
  (`--comment: ef-migration <id>`) and adds the file to `db/CHANGELOG.md`.
- **No `__EFMigrationsHistory` in the database**: the export drops its statements; the history is Liquibase's
  `databasechangelog`. The host never migrates (`PendingModelChangesWarning` is ignored on purpose).
- Hand-written changesets only for what the model does not describe: roles, grants (ADR 0004), Wolverine's storage
  (its own DDL, ADR 0010) and data (the invitation template).
- **No logic in the database**: no function, procedure, view, trigger, policy or rule. `db/scripts/check-changesets.sh`
  (pre-commit, `./verify schema`) refuses them, an edited or missing EF changeset and EF's history table.
- Proof (`SchemaTests`, `./verify schema`): the model has no pending change (`HasPendingModelChanges()` and
  `dotnet ef migrations has-pending-model-changes`), every migration has a changeset whose SQL equals what EF generates
  now, every table and column of the model exists after Liquibase, there is no EF history table and there are no
  routines, views, triggers or policies.

## Consequences

- A change of the model is three steps: the model, `migrations add`, the export - and the tests fail when one is
  missing. A changeset applied anywhere is never edited; a fix is a new migration.
- The schema can be reviewed as SQL, and rolled back with Liquibase.

## Revisit when

A second database engine, or a schema change Liquibase cannot run in a transaction (e.g. `CREATE INDEX CONCURRENTLY` -
then a hand-written changeset with `runInTransaction:false`).
