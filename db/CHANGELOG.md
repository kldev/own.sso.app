# Database changelog

One entry per file; the path is relative to `changelog/`. The schema's source is the EF Core model
(`libs/Sso.Data`): `db/scripts/export-ef-migration.sh` writes the `*-ef-*` changesets from the migrations (ADR 0003).
Hand-written changesets are only what the model does not describe: roles, grants and Wolverine's storage. Rules:
`db/scripts/check-changesets.sh` (pre-commit and `./verify schema`).

## 2026-11-03

- `2026/1-ddl/20261103-01-core-create-roles.sql`
- `2026/1-ddl/20261103-02-ef-initial.sql` - EF migration 20261103161340_Initial
- `2026/1-ddl/20261103-03-core-grant-application-roles.sql`
- `2026/1-ddl/20261103-04-core-create-wolverine-storage.sql`
- `2026/2-data/20261103-01-marketing-seed-invitation-template.sql`
- `2026/1-ddl/20261103-05-ef-attempts-default-zero.sql` - EF migration 20261103164416_AttemptsDefaultZero
