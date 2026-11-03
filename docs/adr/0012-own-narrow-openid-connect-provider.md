# 0012. Our own narrow OpenID Connect provider

Status: accepted (2026-11-03)

## Context

The team's applications should sign people in with the standard handler of their framework ("Sign in with Great SSO").
A full identity server (OpenIddict, Duende) brings far more protocol than needed and its own storage model.

## Decision

The profile is narrow enough to own (`apps/Sso.Api/Oidc`), with the framework's JWT primitives only:
- `/.well-known/openid-configuration`, `/.well-known/jwks.json`, `/connect/authorize` (GET/POST, `response_mode` query
  or form_post), `/connect/token`, `/connect/userinfo`. Nothing else: no implicit or hybrid flow, no password or
  client_credentials grant, no request objects, no dynamic registration, no end_session.
- Authorization code flow; **PKCE S256 required** (off only for a conformance-suite client, set by hand); exact
  `redirect_uri` match (RFC 9700), an unknown client or URI never gets a redirect; `state`, `nonce`, `prompt=none|login`,
  `max_age` honoured; RFC 9207 `iss` in every answer.
- **Codes**: 256 random bits, only the hash stored, 60 seconds, single use - taken under `SELECT ... FOR UPDATE`, any
  attempt with the right client uses the code up, a second use revokes the tokens issued from it (RFC 6749 4.1.2).
- Client authentication `client_secret_basic` or `client_secret_post`; the secret is made on the admin's page, shown
  once, stored as base64url(SHA-256) and compared in fixed time.
- ID and access tokens: RS256 JWTs, 60 minutes, `at+jwt` for access; `sub` = the person's id, `preferred_username`,
  `name` (scope profile), `email` = the work e-mail (scope email, `email_verified` true). **Never a role**: what a person
  may do in an application is the application's own business.
- **Refresh tokens** (scope `offline_access`) rotate on every use under a row lock; a used or revoked one coming back
  revokes its whole family (RFC 9700 4.14.2); they live as long as the SSO session and extend it.
- **Keys**: RSA PEM files in `Oidc:KeysPath` (a volume, never the repository); the first start makes one; the newest
  signs, all are in the JWKS.
- Every step and refusal is an event (ADR 0009); `OidcStore` keeps the protocol's state in EF Core.

## Consequences

- Owning the protocol means owning its tests: `OidcTests` sign in a real application built on ASP.NET Core's handler
  and break one rule at a time, including two parallel redeems and refreshes; the OpenID Foundation's Basic OP
  conformance suite runs from `tools/conformance/`.

## Revisit when

An application needs a public client without a secret (a mobile app), roles in tokens, or single sign-out.
