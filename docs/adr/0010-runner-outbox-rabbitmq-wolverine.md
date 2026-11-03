# 0010. The runner: a transactional outbox on EF Core, RabbitMQ through Wolverine

Status: accepted (2026-11-03)

## Context

Work that should happen after a change - informing another module, a slow call - must not be lost when the broker is
away, and must not happen when the change is rolled back. The host should run without a broker when nothing needs one.

## Decision

- **The outbox is a table of the model** (`messaging.outbox_messages`): `Outbox.Enqueue(db, message, now)` adds the
  message to the caller's unit of work, so it is saved and rolled back with the change. A message has a registered name
  (`OutboxMessageTypes`) and a JSON payload of ids - never secrets.
- **The runner** (`apps/Sso.Runner`, its own process and role) relays it: in one transaction it claims a batch
  (`SELECT ... FOR UPDATE SKIP LOCKED` - a second runner takes other rows), hands each message to Wolverine's durable
  outbox **in the same transaction** (`DatabaseEnvelopeTransaction` on EF's connection), marks the rows processed and
  commits; Wolverine then sends to RabbitMQ and retries from its own storage while the broker is away. A type this
  version does not know counts a failed attempt. Processed rows are deleted after `Outbox:Retention`.
- The batch logic is `OutboxBatch` in `libs/Sso.Data` (claim, process, delete), so it is tested without a broker.
- Wolverine stores its envelopes in PostgreSQL (schema `wolverine`, tables from its own dump as a changeset - it creates
  nothing at start) and listens to the runner's queue on RabbitMQ (`UseDurableInbox`, sequential, retries then dead
  letters).
- Not Wolverine's EF Core outbox in the host: it would make the host a Wolverine node with a broker even where no message
  is ever sent, and tie the domain's transaction to Wolverine's envelope tables. The host has RabbitMQ only when
  `RabbitMq:Host` is set (readiness checks it then).
- Great SSO publishes no integration event yet; the tests register their own message type.

## Consequences

- A message leaves the outbox once; a handler must still be idempotent (Wolverine may deliver twice after a crash).

## Revisit when

The first module needs to react to another one - its event gets a name in `OutboxMessageTypes` and a handler in the
runner.
