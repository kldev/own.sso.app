# 0015. Seed on start: the first admin and the demo data

Status: accepted (2026-11-03)

## Context

Nobody can sign in to an empty Great SSO - only an admin enters people. Starting the project should give a working
system with something to look at, without editing SQL and without personal data in the repository.

## Decision

- **`Seeder`** runs when the host starts (`SeedOnStart`, a hosted service, before the server listens), after Liquibase
  has applied the schema. In one transaction under a PostgreSQL advisory lock it checks whether **any admin** exists;
  if so, the database is seeded already and nothing happens - also after the admin changed the data in the panel.
  Replicas starting at once seed once.
- **The first admin** from `BootstrapAdmin:*` with neutral defaults - username `admin`, e-mail, GitHub and Google
  e-mail `admin@example.com` - so no configuration is needed to start; a real deployment sets
  `BootstrapAdmin__Email`, `BootstrapAdmin__GithubEmail`, ... before its first start. A warning in the log says when
  the default e-mail was used.
- **The demo data** with it, unless `Seed__Demo=false` (default `true` in every environment): a team on example.com
  (an admin and members, one inactive, linked provider accounts), three applications (among them
  `samples/Sso.SampleClient` with its redirect URI), sessions in every state with their log, an MCP token and a mailing
  of the invitation template with addresses sent, failed and pending. Its session tokens and secrets are random values
  nobody knows. It is what the end-to-end screenshots show (ADR 0018).
- `Seed__Enabled=false` switches the seed off (the tests seed their own data).

## Consequences

- `./deploy/deploy.sh`, then sign in as `admin@example.com` (locally through the development stub) - everything is
  there.
- A production database gets the demo data too unless the deployment sets `Seed__Demo=false`; the admin can deactivate
  the demo people and applications in the panel.
- Proof: `SeedTests` (an empty database, a second start, two at once, the demo off) on copies of the empty schema.

## Revisit when

A deployment needs a scripted import of real people instead.
