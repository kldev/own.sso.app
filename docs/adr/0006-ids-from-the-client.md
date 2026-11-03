# 0006. Ids from the client (UUIDv7) and creates that can be retried

Status: accepted (2026-11-03)

## Context

A double click, a retried request after a timeout or an agent that calls a tool twice must not create two people,
applications or mailings.

## Decision

- The page (or the MCP tool) makes the id of a new record with `Guid.CreateVersion7()` and sends it with the create.
- A create with an id that exists and **the same values** is a no-op that answers like the first call (its row
  version, or what a mailing queued); with other values it is a conflict (`people.id_taken`, `clients.id_taken`,
  `platform.id_taken`, `marketing.id_taken`).
- Rows the server makes (sessions, events, outbox messages, recipients) get UUIDv7 or identity ids in C#; the columns
  keep `uuidv7()` defaults (PostgreSQL 18) for hand-written SQL.

## Consequences

- No "create then read back the id" round trip; UUIDv7 keeps inserts in index order.

## Revisit when

An id has to be meaningful outside Great SSO.
