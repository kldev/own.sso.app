# 0005. Modules as boundaries in code: vertical slices over one DbContext

Status: accepted (2026-11-03)

## Context

The panel has five areas - platform (the signed-in person, MCP tokens), people, clients (applications), activity
(sessions and the log) and marketing. They share people and sessions: deactivating a person must end their sessions in
the same transaction.

## Decision

- **One `SsoDbContext`**, one migration history; the schemas (`identity`, `oidc`, `marketing`, `messaging`, `audit`)
  keep the tables of each area together.
- **A module is a folder of vertical slices** in `apps/Sso.Api/Features/<Module>/`: each operation is a handler
  (`IHandler`, a scoped service) with its request, response row and rules in one place, and `<Module>Endpoints` maps it
  under `/api/<module>/...` with the same routes for every module (`GET /<list>`, `GET /<list>/{id}`,
  `POST /<operation>`). MCP tools call the same handlers (ADR 0013).
- Modules in dependency order: platform, people, clients, activity, marketing; a module uses the shared services of the
  core (`EventLog`, `SessionTermination`, `ProviderLinks`, `Actors`) and never the handlers of a module after it.
- Lists share one query string (`ListSpec`): `page`, `pageSize` (50, at most 200), `sort` (a declared column), `desc`,
  equality filters and `From`/`To` ranges, declared per column - EF translates them into one query; the OpenAPI
  document describes them, so the generated client has them too.

## Consequences

- A cross-module rule (deactivation ends sessions) is one transaction without messaging.
- A separate DbContext per module, if a module ever leaves the host, starts from its schema and its folder.

## Revisit when

A module needs its own deployment or its own database.
