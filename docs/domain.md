# Domain

People sign in to their team's applications through Great SSO; an SSO admin enters each person (username, work e-mail,
the e-mails of their GitHub, Google and Microsoft accounts). The main aggregate is the **person** with at most one
linked account per provider; applications are registered OpenID Connect clients.

## Modules

One model, one database; a module is a folder of slices in `apps/Sso.Api/Features` over the schemas it works with
(ADR 0005). The core (identity and the protocol's state) is shared by sign-in, the OIDC provider and the modules.

```mermaid
flowchart LR
  core["core: identity + oidc<br/>people, links, applications, sessions, codes, tokens, events"]
  platform["platform<br/>me, MCP tokens"]
  people["people<br/>enter, edit, unlink, deactivate"]
  clients["clients<br/>applications' registry"]
  activity["activity<br/>log, sessions, end a session"]
  marketing["marketing<br/>templates, mailings, one e-mail per address"]
  core --> platform --> people --> clients --> activity --> marketing
```

| Module | Tables it changes | Page | MCP tools |
|---|---|---|---|
| core | `identity.external_logins`, `oidc.sessions`, `oidc.authorization_codes`, `oidc.refresh_tokens`, `oidc.events` (sign-in and protocol) | Sign in | - |
| platform | `identity.mcp_tokens` | the shell, Claude Code | - |
| people | `identity.users`, unlinking in `identity.external_logins`, ending sessions | People | `people_list`, `person_*` |
| clients | `oidc.clients` | Applications | `clients_list`, `client_*` |
| activity | ends sessions; reads `oidc.sessions`, `oidc.events` | Activity | `events_list`, `sessions_list`, `session_end` |
| marketing | `marketing.templates`, `marketing.mailings`, `marketing.recipients` | Marketing - Mail | `mail_*`, `mailing*` |

## Tables

```mermaid
erDiagram
  users ||--o{ external_logins : "links (one per provider)"
  users ||--o{ mcp_tokens : "issues"
  users ||--o{ sessions : "signs in"
  clients ||--o{ authorization_codes : "gets"
  sessions ||--o{ authorization_codes : "issues"
  clients ||--o{ refresh_tokens : "gets"
  sessions ||--o{ refresh_tokens : "lives within"
  templates ||--o{ mailings : "is sent as"
  mailings ||--o{ recipients : "goes to"

  users {
    uuid id PK
    varchar username UK
    varchar email UK
    varchar display_name
    varchar role "admin | member"
    varchar status "active | inactive"
    varchar github_email UK
    varchar google_email UK
    varchar microsoft_email UK
    xid xmin "row version"
  }
  external_logins {
    uuid user_id PK, FK
    varchar provider PK "github | google | microsoft"
    varchar subject UK "with provider"
    varchar tenant_id
    varchar email
  }
  mcp_tokens {
    uuid id PK
    uuid user_id FK
    varchar name
    bytea token_hash UK "SHA-256"
    timestamptz expires_at
    timestamptz revoked_at
  }
  clients {
    uuid id PK
    varchar client_id UK
    varchar name
    varchar secret_hash "base64url SHA-256"
    text_array redirect_uris
    text_array providers
    boolean require_pkce
    varchar status
    xid xmin "row version"
  }
  sessions {
    uuid id PK
    bytea token_hash UK
    uuid user_id FK
    varchar provider
    timestamptz idle_expires_at
    timestamptz absolute_expires_at
    timestamptz ended_at
    varchar end_reason
  }
  authorization_codes {
    bytea code_hash PK
    uuid client_id FK
    uuid session_id FK
    text redirect_uri
    varchar code_challenge
    timestamptz expires_at
    timestamptz redeemed_at
  }
  refresh_tokens {
    bytea token_hash PK
    uuid family_id
    uuid client_id FK
    uuid session_id FK
    timestamptz used_at
    timestamptz revoked_at
  }
  events {
    bigint id PK
    timestamptz occurred_at
    varchar event_type
    varchar outcome "success | refused"
    varchar reason
    uuid user_id
    varchar client_id
    jsonb detail
  }
  templates {
    uuid id PK
    varchar code UK
    varchar product
    varchar language
    varchar subject
    text body
    xid xmin "row version"
  }
  mailings {
    uuid id PK
    uuid template_id FK
    varchar subject
    text body
    uuid created_by
  }
  recipients {
    uuid id PK
    uuid mailing_id FK
    varchar email
    varchar status
    int attempts
    timestamptz next_attempt_at
  }
  outbox_messages {
    uuid id PK
    varchar message_type
    jsonb payload
    timestamptz processed_at
    int attempts
  }
  change_log {
    bigint id PK
    varchar table_name
    varchar operation
    text actor_id
    jsonb old_row
    jsonb new_row
  }
```

Schemas: `identity` (users, external_logins, mcp_tokens), `oidc` (clients, sessions, authorization_codes,
refresh_tokens, events), `marketing` (templates, mailings, recipients), `messaging` (outbox_messages), `audit`
(change_log); `wolverine` holds the runner's message storage.

## A person

```mermaid
stateDiagram-v2
  [*] --> Entered: the admin enters the person (provider e-mails)
  Entered --> Linked: first sign-in with a verified e-mail
  Linked --> Linked: later sign-ins by the provider's id
  Linked --> Entered: the admin changes the e-mail or unlinks
  Entered --> Inactive: deactivated (sessions end)
  Linked --> Inactive: deactivated (sessions end)
  Inactive --> Entered: activated
```

## A session

```mermaid
stateDiagram-v2
  [*] --> Active: sign-in through a provider
  Active --> Active: used - idle end moves 14 days ahead (at most every 15 minutes)
  Active --> Expired: 14 days without use, or 30 days after the sign-in
  Active --> Ended: an admin ends it, the person is deactivated, or the provider account is unlinked
  Expired --> [*]
  Ended --> [*]
```

Refresh tokens live within their session: ending it revokes them; a used token coming back revokes its family.

## A mailing (ADR 0014)

```mermaid
stateDiagram-v2
  [*] --> pending: mailing-create (one row per address)
  pending --> sending: the host's sender claims it
  sending --> sent: SMTP accepted
  sending --> pending: failed, attempts left (after RetryAfter)
  sending --> failed: last attempt, or the address refused
  failed --> pending: mailing-retry
```

## Integration events

None yet: the modules change the core in one transaction, and the log (`oidc.events`) is written in it. The outbox and
the runner are ready for the first one (ADR 0010).
