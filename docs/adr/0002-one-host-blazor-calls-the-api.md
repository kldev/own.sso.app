# 0002. One host: the REST API, the Blazor panel, OIDC and MCP - and the panel calls the API

Status: accepted (2026-11-03)

## Context

The panel, the API for it, the OpenID Connect provider for applications and the MCP endpoint for Claude Code all work
on the same people and applications. Separate services would mean separate deployments, separate authentication and
network calls between them for no gain at this size.

## Decision

- **One ASP.NET Core host** (`apps/Sso.Api`): `/api/...` (the panel's REST API, minimal APIs, OpenAPI at
  `/openapi/v1.json`, Scalar at `/scalar/`), the Blazor panel (interactive server) under `/admin`, the sign-in pages
  and provider callbacks (`/sign-in`, `/auth/...`), the OpenID Connect provider (`/connect/...`, `/.well-known/...`)
  and MCP (`/mcp`).
- **The panel calls `/api`** like any other client - with the client Kiota generates from the API's OpenAPI
  document (`apps/Sso.ApiClient`, generated from the committed `openapi.json`, never edited by hand) and the session's
  short access token as a bearer. No page reads the database or calls a handler directly, so the API is the only way to
  the data and every rule applies to the panel as well.
- `openapi.json` is written at build time from the code (`apps/Sso.ApiClient/generate.sh`); a test compares it with
  the live document, so the client never drifts from the API.
- Detail pages compose the module's list endpoints (`GET /api/<module>/<list>/{id}` returns the list's own row).

## Consequences

- The Blazor circuit makes HTTP calls to its own host - a little overhead for one rule set and one audit trail.
- A change of an endpoint is a change of `openapi.json` and the generated client in the same review.

## Revisit when

The panel needs offline work or a public client (WebAssembly), or the API gets clients that need versioning.
