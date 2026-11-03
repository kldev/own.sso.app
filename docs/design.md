# Design

## Repository

```text
apps/Sso.Api/          the host: REST API (Features/), Blazor panel (Components/), sign-in (Auth/), OIDC (Oidc/),
                       MCP (Mcp/), marketing sender (Marketing/), seed (Seed/), Scalar, health
apps/Sso.ApiClient/    the panel's client of /api: openapi.json (from the code) + Kiota's Generated/ (never edited)
apps/Sso.Runner/       the runner: outbox relay, Wolverine on RabbitMQ, handlers of integration events
apps/Sso.DbMigrator/   prepare | finish around Liquibase (the migrator role, the database, the logins)
libs/Sso.Data/         the EF Core model (the schema's source), migrations, audit interceptor, outbox
libs/                  message contracts, observability
db/                    Liquibase: changelog/<yyyy>/{1-ddl,2-data}, scripts (export of EF migrations, rules)
samples/               Sso.SampleClient - an application signing in through Great SSO
tests/Sso.Tests/       tests on a real PostgreSQL 18 with the schema from db/ (groups: ./verify <group>)
e2e/                   Playwright walk that writes docs/screenshots
deploy/                Docker Swarm: infrastructure stack, deploy.sh, remove.sh
docs/                  domain, design, integration, runbooks, ADRs, screenshots
```

## A request of the panel

1. The browser holds the SSO session cookie (a random value; the database keeps its hash). Every request checks it
   (`SsoSessionHandler` → `SsoSessions.TouchAsync`, which extends the session) and gets a short internal access token
   (60 minutes) for the API.
2. A Blazor page calls `/api/<module>/...` with the generated Kiota client and that token as a bearer - to the same
   host (ADR 0002).
3. The endpoint calls its handler (`Features/<Module>/...`, a scoped service): `Actors.RequireAsync` reads the caller
   from the database (401 when not active), `Actor.RequireAdmin` checks the role (403), the handler checks its rules in
   order and saves through EF Core - the audit interceptor writes `audit.change_log` and the handler's events go to
   `oidc.events` in the same transaction.
4. A refusal is a `RuleException` → a problem response with `code` and `detail` (ADR 0008); a stale row version is a
   409 `concurrency` (ADR 0007).

## Lists

Every `GET /api/<module>/<list>` takes the same query string (`ListSpec`): `page`, `pageSize` (50, max 200), `sort`
(one of the declared columns), `desc`, an equality filter `?<column>=` and ranges `?<column>From=&<column>To=` where the
column allows them. The answer is `{ items, hasMore, page, pageSize }`; `GET .../<list>/{id}` returns one row of the
same shape or 404. A member gets empty lists.

## Error codes

| Module | Validation (400) | Role (403) | Not found (404) | Conflict (409) |
|---|---|---|---|---|
| any | `request.invalid_json`, `list.invalid_parameter`, `database.*` | - | - | `concurrency`, `database.unique` |
| platform | `platform.id_missing`, `platform.token_name_invalid`, `platform.token_hash_invalid`, `platform.token_validity_invalid` | `platform.forbidden` | `platform.token_not_found` | `platform.id_taken` |
| people | `people.id_missing`, `people.username_invalid`, `people.email_invalid`, `people.name_invalid`, `people.role_invalid`, `people.provider_email_invalid`, `people.provider_email_missing`, `people.self_change`, `people.status_invalid`, `people.provider_invalid` | `people.forbidden` | `people.not_found` | `people.id_taken`, `people.username_taken`, `people.email_taken`, `people.provider_email_taken` |
| clients | `clients.id_missing`, `clients.client_id_invalid`, `clients.name_invalid`, `clients.redirect_uris_invalid`, `clients.providers_invalid`, `clients.secret_hash_invalid`, `clients.status_invalid` | `clients.forbidden` | `clients.not_found` | `clients.id_taken`, `clients.client_id_taken` |
| activity | - | `activity.forbidden` | `activity.session_not_found` | - |
| marketing | `marketing.id_missing`, `marketing.emails_count`, `marketing.email_invalid`, `marketing.subject_invalid`, `marketing.body_invalid`, `marketing.nothing_to_send` | `marketing.forbidden` | `marketing.template_not_found`, `marketing.mailing_not_found` | `marketing.id_taken` |

401 `actor.missing` / `actor.inactive`: no active person behind the call. The protocol endpoints answer with OAuth
errors (`invalid_request`, `invalid_grant`, `invalid_client`, ...), never with these codes.

## Sign-in and the OpenID Connect provider (ADR 0011, 0012)

```text
browser ─ /sign-in ─ /auth/external/{provider} ─► provider ─► /signin/{provider} (framework handler, external cookie)
        ─ /auth/external/{provider}/done ─ ExternalSignIn (find or link; refuse) ─ SsoSessions.StartAsync ─ cookie
application ─ /connect/authorize (session + client + PKCE) ─ OidcStore.IssueCodeAsync ─► redirect_uri?code&state&iss
            ─ /connect/token (client secret, verifier) ─ OidcStore.RedeemCodeAsync | RotateRefreshAsync ─► RS256 tokens
            ─ /connect/userinfo (access token) ─ OidcStore.UserInfoAsync
```

A code and a refresh token are taken under `SELECT ... FOR UPDATE` in one transaction, so two parallel requests with
the same value never both succeed.

## Database

| Role | What it does (ADR 0004) |
|---|---|
| `sso_migrator` | Liquibase; owns the database and every object; CREATEROLE, never a superuser |
| `sso_api` | the host: rows of `identity`, `oidc`, `marketing`; append-only `oidc.events`, `audit.change_log`; inserts outbox messages |
| `sso_runner` | the runner: claims, marks and clears the outbox; Wolverine's tables |

The schema's source is the EF model; a change is `dotnet ef migrations add <Name>` → `db/scripts/export-ef-migration.sh
<Name> <slug>` → Liquibase (ADR 0003). There are no functions, views, triggers or policies.

## Background work

- **The marketing sender** in the host (`MailingSender`): due recipients, one claimed at a time, one e-mail per address
  (ADR 0014).
- **The seed** on start (`SeedOnStart`): the first admin and the demo data on a database without an admin (ADR 0015).
- **The runner**: the outbox relay to RabbitMQ through Wolverine and the handlers of integration events (ADR 0010).

## Deployment (ADR 0017)

`./deploy/deploy.sh` on a machine with Docker: the infrastructure stack (PostgreSQL 18, RabbitMQ, MailPit), prepare,
Liquibase, finish, the host on port 6100 and the runner. Images for a registry come from tags `v-<api|db|migrator>-<n>`
(GitHub Actions, `vars.IMAGE_REGISTRY`).
