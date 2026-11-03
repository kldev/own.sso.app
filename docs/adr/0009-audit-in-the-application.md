# 0009. Audit in the application: the change log and the event log

Status: accepted (2026-11-03)

## Context

An admin has to see who changed a person or an application and when, and every sign-in, session and token - without
any secret ever reaching a log, and without triggers in the database (ADR 0003).

## Decision

- **`audit.change_log`** - `AuditInterceptor` (an EF `SaveChangesInterceptor`) adds an entry for every insert, update
  and delete of an `IAudited` entity (people, provider links, applications, templates, mailings) to the same
  `SaveChanges`, so it commits and rolls back with the change: table, operation, the actor (the person behind the
  request), the database role, `txid_current()`, and the old and new row as JSON by column name - without
  `secret_hash`, `token_hash` and `xmin`. Audited tables are changed through tracked entities only; bulk statements
  (`ExecuteUpdate`) are used for the protocol's own state (sessions, tokens, recipients, the outbox).
- **`oidc.events`** - `EventLog` adds an event in the unit of work of the change it describes: sign-ins and refusals
  with the reason, links, session start, extension, expiry and end, codes, tokens, refreshes and reuse, MCP tool calls
  and tokens. Never a code, token, secret or hash; a refused e-mail only as its domain. A refusal before anything is
  looked up (a provider's answer, a malformed protocol request) is saved on its own (`ProtocolLog`).
- Both are append-only for the host: `INSERT` and `SELECT`, no `UPDATE`, `DELETE` or `TRUNCATE` (ADR 0004).

## Consequences

- The panel's Activity reads `oidc.events`; `audit.change_log` is for an investigation in SQL.
- No retention yet.

## Revisit when

The log grows enough to need partitions or a retention, or the change log needs a page.
