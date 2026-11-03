# 0007. Optimistic concurrency with xmin

Status: accepted (2026-11-03)

## Context

Two admins can edit the same person or application; the second save must not silently overwrite the first.

## Decision

- People, applications and templates map PostgreSQL's system column `xmin` as a row version
  (`uint RowVersion` with `IsRowVersion()`, Npgsql maps it to `xmin`/`xid` - no extra column). Lists return it as
  `rowVersion` (a number); every change sends it back.
- The handler compares the version it loaded with the one sent **before** any other rule (a stale form is a 409
  first), and EF checks it again in the `UPDATE ... WHERE xmin = @version` - a change between the two is a
  `DbUpdateConcurrencyException`, answered the same way: 409, code `concurrency`, the module's sentence ("Someone
  changed this person in the meantime - reload and try again.").
- Where two requests race for a single value - an authorization code, a refresh token - the handler takes the row
  under `SELECT ... FOR UPDATE` in its transaction instead (ADR 0012).

## Consequences

- No lock is held while a person thinks; the page reloads the row on a 409.
- `xmin` changes with every update of the row, also one the user did not see (e.g. a status change).

## Revisit when

A record needs merging of independent field changes instead of a reload.
