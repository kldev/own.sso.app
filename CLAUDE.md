# CLAUDE.md

Great SSO - one sign-in for a team's applications through GitHub, Google or a work Microsoft account, for people an SSO
admin has entered beforehand. One .NET 10 host (REST API, Blazor panel calling it, OpenID Connect provider, MCP), EF
Core on PostgreSQL 18 with the schema applied by Liquibase, a runner on RabbitMQ through Wolverine.
What and why: `PRODUCT.md`; the domain: `docs/domain.md`; how it is built: `docs/design.md`, `docs/adr/`.
**Everything in English**: UI texts, docs, ADRs, comments, messages.

## Commands

```bash
./deploy/deploy.sh                    # everything on Docker Swarm: infrastructure stack, database, Liquibase, roles,
                                      # host (:6100) and runner; the first start seeds admin@example.com + demo data
./deploy/deploy.sh --db-only          # database + Liquibase only (after a change in db/, with dotnet run)
./deploy/remove.sh                    # frees port 6100 before dotnet run (the database stays); --purge drops it all
dotnet run --project apps/Sso.Api      # host on :6100, Development: sign in through the development stub
dotnet run --project apps/Sso.Runner   # runner

# a change of the model (libs/Sso.Data): migration -> changeset -> Liquibase (ADR 0003)
dotnet tool run dotnet-ef migrations add <Name> --project libs/Sso.Data --startup-project libs/Sso.Data --output-dir Migrations
db/scripts/export-ef-migration.sh <Name> <slug>   # writes db/changelog/<yyyy>/1-ddl/<date>-<nn>-ef-<slug>.sql + CHANGELOG
apps/Sso.ApiClient/generate.sh         # after an API change: openapi.json from the code, then the Kiota client

./verify <schema|platform|web|sign-in|sessions|people|clients|activity|marketing|oidc|mcp|outbox|seed|all>
db/scripts/check-changesets.sh        # changeset rules (pre-commit, ./verify schema)
dotnet csharpier check .              # formatting (pre-commit)
./scripts/inspect-code.sh             # jb inspectcode, 0 findings (by hand before a push)
cd e2e && npm install && npm run screenshots   # Playwright walk -> docs/screenshots (needs the host with the demo seed)
```

Tests: xUnit v3 on Microsoft.Testing.Platform, Testcontainers (PostgreSQL 18 + Liquibase from `db/`). On a 16 GB laptop:
build once with `-m:2`, then `dotnet build-server shutdown`, run only the touched group (`./verify <group>` or
`-- --filter-class Sso.Tests.<Class>`), `DOTNET_gcServer=0`; never the whole suite at once.

## Architecture

- **The EF Core model is the schema** (ADR 0003): `libs/Sso.Data` (`SsoDbContext`); migrations are exported to dated
  Liquibase changesets, no `__EFMigrationsHistory`; no functions, views, triggers or policies in the database. Roles,
  grants and Wolverine's storage are hand-written changesets. Three roles: `sso_migrator` owns, `sso_api` and
  `sso_runner` only read and write rows (ADR 0004).
- **Modules = vertical slices** (ADR 0005): `apps/Sso.Api/Features/<Module>/` - each operation an `IHandler` (scoped)
  with its request, row and rules, mapped by `<Module>Endpoints` under `/api/<module>/...` (lists share `ListSpec`).
  Rules throw `RuleException` with a descriptive code (`people.username_invalid`) -> problem details (ADR 0008).
  Order: platform, people, clients, activity, marketing.
- **One host** (ADR 0002): Blazor pages call `/api` with the Kiota client (`apps/Sso.ApiClient/Generated/`, never edit
  by hand; source `openapi.json`, written by `generate.sh`) and the session's token. Ids for new records come from the
  page (`Guid.CreateVersion7()`, ADR 0006); changes send `rowVersion` (xmin, ADR 0007).
- **Single sign-on** (ADR 0011, 0012): people sign in only through GitHub, Google or a work Microsoft account, and only
  if an admin entered them (no registration, no password, no sign-out). Server-side sessions (14 days idle, 30 at most).
  Great SSO is the OpenID Connect provider of the applications - our own narrow profile in `apps/Sso.Api/Oidc`
  (`OidcStore`, row locks for codes and refresh tokens). Roles `admin` (panel) and `member`; tokens never carry a role.
- **Audit** (ADR 0009): `AuditInterceptor` -> `audit.change_log` for `IAudited` entities; `EventLog` -> `oidc.events`;
  both in the transaction of the change, never a secret. Change audited tables through tracked entities only.
- **MCP** (ADR 0013): `/mcp` with an admin's personal token; every panel operation is a tool in
  `apps/Sso.Api/Mcp/` calling the same handler - a new panel feature gets its tool.
- **Outbox** (ADR 0010): `Outbox.Enqueue` in the caller's unit of work; the runner relays with `OutboxBatch`
  (`FOR UPDATE SKIP LOCKED`) into Wolverine's durable outbox. A new message type is registered in `OutboxMessageTypes`.
- **Seed** (ADR 0015): on a database without an admin the host seeds `BootstrapAdmin` and, unless `Seed__Demo=false`,
  the demo data. **Development sign-in stub** (ADR 0018): `DevSignIn__Enabled`, Development only.
- **Look** (ADR 0016): teal on zinc tokens on Tailwind v4 (`apps/Sso.Api/Styles/app.css` -> `wwwroot/css/app.css`,
  committed; `npm install` once in `apps/Sso.Api`). Icons: add to `tools/generate-icons.py`, run it.

## Conventions

- `TreatWarningsAsErrors`, central package versions (`Directory.Packages.props`), libraries by `ProjectReference`.
- Husky: pre-commit = CSharpier + changeset rules + build; run the inspection before a push.
- Changesets are never edited once applied; a fix is a new migration and a new export.
- Per module: tests for success, every rule's code, 409 for a stale version, 404; e2e screenshots in
  `docs/screenshots/`; then `PRODUCT.md`'s state.
- Secrets only in `.env` (git-ignored), user-secrets or the environment - never in the repository.
