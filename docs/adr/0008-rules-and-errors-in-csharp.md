# 0008. Rules and errors in C#: stable codes and sentences for people

Status: accepted (2026-11-03)

## Context

A refused request has to say what is wrong - to the panel's user, to an agent through MCP, to another client - and a
client needs something stable to react to, never a database error code.

## Decision

- Every rule lives in the handler of its operation (or in the module's `...Rules`), with the same order and sentences
  in the panel, the API and MCP. A refusal is a `RuleException(status, code, sentence)`; `Rule.Invalid/Forbidden/
  NotFound/Conflict/Unauthorized` make them.
- **Codes are descriptive and stable**: `<module>.<rule>`, e.g. `people.username_invalid`, `people.self_change`,
  `clients.redirect_uris_invalid`, `marketing.nothing_to_send`, `concurrency`, `actor.inactive`.
- **Statuses**: 400 a rule of the request, 401 no active person behind the call (also a deactivated person's still
  valid bearer token), 403 the person's role may not do it, 404 no such record, 409 a conflict (id, unique value, row
  version). A list asked by a member is 200 with no rows - the panel shows nothing of what a member may not see.
- **Problem details** (RFC 9457) with `code` and `detail` (the sentence), `errors` for malformed fields: a field of the
  wrong type or an unknown field (request bodies refuse unknown fields, numbers are numbers) is named
  (`request.invalid_json`), a wrong list parameter too (`list.invalid_parameter`). A rule of the schema the code does not
  check itself (a check, a foreign key, a unique index won by a parallel request) is described by its constraint's name
  (`database.*`), never with values; anything else is a 500 without details.
- The OpenAPI document names the error shape `ProblemDetails`; the generated client throws it and the panel shows its
  sentence (`ApiErrors`).

## Consequences

- Tests assert status and code; the sentences are the user interface.

## Revisit when

The UI is translated - the codes stay, the sentences move to resources.
