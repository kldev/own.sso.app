# 0017. Deployment on Docker Swarm

Status: accepted (2026-11-03)

## Context

Great SSO is one host, an optional runner, PostgreSQL, RabbitMQ and an SMTP server. A deployment should be one command
on a machine with Docker, and the schema must be applied before the host starts.

## Decision

- **`deploy/infrastructure.yml`** - a Docker Swarm stack with PostgreSQL 18, RabbitMQ and MailPit on an attachable
  overlay network. `deploy/infra.sh` deploys it (and makes the machine a single-node swarm when needed); names and
  ports are parameters (`SSO_STACK=sso-infra`, `SSO_NETWORK=sso-net`, `SSO_PG_PORT`, ...).
- **`deploy/deploy.sh`** - starts the infrastructure when missing, then: `Sso.DbMigrator prepare` (the migrator role
  and the database, ADR 0004), **Liquibase** (`db/`, the changesets exported from the EF model, ADR 0003) as the
  migrator, `Sso.DbMigrator finish` (logins of `sso_api` and `sso_runner`), the RabbitMQ vhost and user, then the images
  and containers of the host (`great-sso:dev`, port 6100, keys on a volume) and the runner. A failed Liquibase update
  stops it before anything starts on a half-made database. Idempotent: run again, it applies new changesets and
  recreates the containers. `deploy/remove.sh` stops them (`--purge` also drops the database and the keys).
- Secrets come from `.env` (never committed) and are passed as environment variables, never as arguments; the access
  token's signing key is made once and kept in user-secrets.
- Images for a registry: GitHub Actions build `sso-api`, `sso-db` (Liquibase with the changelog) and `sso-db-migrator`
  from tags `v-<api|db|migrator>-<version>` and push them to `vars.IMAGE_REGISTRY` (default
  `ghcr.io/<repository owner>`).

## Consequences

- `git clone`, `./deploy/deploy.sh`, sign in - the seed makes the rest (ADR 0015).
- High availability is out of scope: one node, one database.

## Revisit when

More than one node, or a managed database and broker.
