# Great SSO

Great SSO - one sign-in for a team's applications through GitHub, Google or a work Microsoft account, for people an SSO
admin has entered beforehand. Every application used to keep its own passwords; Great SSO lets a person sign in once
with an account they already have, while the admin keeps the list of who exists. No self-registration, no passwords,
roles stay in the applications. One .NET 10 host (REST API, Blazor panel, OpenID Connect provider, MCP), EF Core on
PostgreSQL 18 with the schema applied by Liquibase, a runner on RabbitMQ through Wolverine.

## What it proves

1. **One sign-in for people the admin entered - nobody else.**
   Done when: an entered person signs in with GitHub, Google or Microsoft (first time by the verified e-mail, then by
   the provider's id), an unknown person is refused and nothing is created, another account of the same provider is
   refused. `./verify sign-in`, `./verify people`
2. **Great SSO is a correct OpenID Connect provider for ASP.NET Core applications.**
   Done when: an application built on the framework's handler signs in and gets `sub`, username and work e-mail, every
   protocol rule is enforced one by one (also against parallel requests), and the OpenID Foundation suite's Basic OP
   plan passes. `./verify oidc`, `tools/conformance/`
3. **Sessions and the audit are trustworthy.**
   Done when: 14 days idle, 30 days at most, deactivation and unlinking end sessions at once; every sign-in, session
   and token is in the log, every record change in the change log, without a single secret value.
   `./verify sessions`, `./verify activity`, `./verify schema`
4. **The EF Core model is the schema, and the database holds no logic.**
   Done when: the database after Liquibase equals the model, every migration is an exported changeset, there are no
   functions, views, triggers or policies, and the application roles cannot change the schema. `./verify schema`
5. **Start it and it works.**
   Done when: `./deploy/deploy.sh` on a machine with Docker brings everything up, the seed makes the first admin and
   the demo data, and the e2e walk signs in and writes the screenshots. `./verify seed`, `e2e/screenshots.mjs`

## Non-goals

Not ever:
- passwords or self-registration in Great SSO (ADR 0011);
- logic in the database (ADR 0003);
- microservices or a database per module (ADR 0005).

Not yet:
- several teams in one deployment (ADR 0001);
- single sign-out, roles in tokens, SAML, public clients (mobile);
- a retention of the logs.

## Decisions

| # | Decision | Why | Revisit when |
|---|---|---|---|
| D1 | One operator, no tenants (ADR 0001) | one team uses a deployment | two teams in one deployment |
| D2 | One host; the panel calls /api with a generated client (ADR 0002) | one deployment, one rule set | a public or offline client |
| D3 | The EF Core model is the schema, Liquibase applies the exported migrations (ADR 0003) | typed model, reviewable SQL, no DDL rights for the app | another database engine |
| D4 | Three database roles (ADR 0004) | the app cannot change the schema | a read-only consumer |
| D5 | Modules as vertical slices over one DbContext (ADR 0005) | boundaries without services, one transaction | a module leaves the host |
| D6 | Ids from the client, creates that can be retried (ADR 0006) | double clicks, agents | - |
| D7 | Optimistic concurrency with xmin (ADR 0007) | no lost updates, no locks held | field-level merging |
| D8 | Rules in C#, stable codes and sentences (ADR 0008) | the same refusal in the panel, the API and MCP | translations |
| D9 | Audit in the application (ADR 0009) | who changed what, never a secret | retention, a page |
| D10 | Outbox on EF Core, runner on RabbitMQ through Wolverine (ADR 0010) | reliable reactions, the host without a broker | the first integration event |
| D11 | Single sign-on through external providers, server-side sessions (ADR 0011) | no passwords, the admin keeps the list | another provider |
| D12 | Our own narrow OIDC provider (ADR 0012) | the profile is small enough to own and test | public clients, single sign-out |
| D13 | MCP for Claude Code with an admin's token (ADR 0013) | configure Great SSO from Claude Code | OAuth for MCP |
| D14 | Marketing mail from the host, one address at a time (ADR 0014) | nobody sees another address, no broker needed | bulk mail |
| D15 | Seed on start: the first admin and the demo data (ADR 0015) | start it and it works | a scripted import |
| D16 | Great SSO's look: teal on zinc, WCAG AA (ADR 0016) | calm, readable in both themes | a brand per deployment |
| D17 | Deployment on Docker Swarm (ADR 0017) | one command on a machine with Docker | several nodes |
| D18 | Development sign-in stub and the e2e walk (ADR 0018) | try it without registering OAuth apps | a provider's test tenant in CI |

## State

Works today:
- sign-in page with the configured providers, a refusal page per reason; no registration;
- server-side SSO sessions (14 days idle, 30 days at most), ended by deactivation, unlinking or an admin;
- OpenID Connect provider: discovery, JWKS, authorize (query/form_post), token (code + PKCE, rotating refresh tokens),
  userinfo; tested with ASP.NET Core's own handler;
- the panel for SSO admins: People, Applications (secret shown once), Activity (log and sessions), Marketing - Mail,
  Claude Code (MCP tokens); light and dark theme, phone layout;
- REST API `/api/...` with OpenAPI and Scalar; MCP `/mcp` with every panel operation as a tool;
- the seed on start (first admin, demo data), the development sign-in stub, the sample client;
- tests on PostgreSQL 18 with Liquibase (`./verify <group>`), the Playwright walk writing `docs/screenshots/`;
- Docker Swarm deployment with its own infrastructure stack; images from tags.

Next: the conformance suite run with real providers; the first integration event through the outbox.
