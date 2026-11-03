# 0014. Marketing mail from the host, one address at a time

Status: accepted (2026-11-03)

## Context

The admins write to a list of people - e.g. "your team now signs in with Great SSO". One e-mail with many addresses
shows every address to everybody (To, Cc) or looks like bulk mail (Bcc).

## Decision

- Module **marketing**: `templates` (a product code and a language, editable; the invitation `great-sso-en` is a data
  changeset), `mailings` (keep the subject and body they went out with) and `recipients` (one row per address:
  pending -> sending -> sent, or failed). The panel's **Marketing - Mail** and the MCP tools (`mail_send`, ...) call the
  module's handlers.
- **One e-mail per address**, only that address in To.
- **The host sends** (`MailingSender`, a background service over `RecipientQueue`) - no runner or broker needed: it
  reads due rows, **claims** each with a conditional update (pending -> sending; the second of two replicas finds it no
  longer pending), sends over SMTP and marks it sent or failed.
- A failed attempt waits `Marketing:RetryAfter` and is tried again up to `Marketing:MaxAttempts`; a 5xx refusal of the
  address fails at once; `Marketing:DelayBetweenEmails` paces the sender. `skipContacted` leaves out an address that
  already got an e-mail about the same product. An empty `Smtp:Host` switches sending off.
- Every template ends with why the address got the e-mail and how to opt out.

## Consequences

- A row left in `sending` by a crash stays there for a person to look at - sending it again could e-mail twice.
- Whom the team may write to is the team's decision (consent rules differ by country).

## Revisit when

Mailings grow past a few hundred addresses (a bulk-mail provider), or opt-outs must be enforced by the system.
