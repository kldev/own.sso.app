# 0011. Single sign-on through external providers - no passwords, no registration, server-side sessions

Status: accepted (2026-11-03)

## Context

A team's people already have accounts at GitHub, Google or a work Microsoft tenant. Great SSO should let them sign in
to the team's applications once, with those accounts, while an admin decides who exists - and keep no password of its
own to leak.

## Decision

**Who signs in**
- Only people an SSO admin has entered: username, work e-mail, name, role (`admin`, `member`) and the e-mails of their
  GitHub, Google and Microsoft accounts. **No registration**: an unknown person is refused, logged with the e-mail's
  domain only, and nothing is created. The first admin is seeded on start (ADR 0015).
- **No password, no sign-out** in Great SSO. A session ends with its limits or when an admin ends it.

**Linking accounts** (`identity.external_logins`, `ExternalSignIn`)
- The e-mail serves only the **first** sign-in: a verified e-mail equal to the one the admin entered for that provider
  links the account; from then on the provider's own stable id signs in (GitHub's numeric `id`, Google's `sub`,
  Microsoft's `tid:oid`). Two first sign-ins at once link once (a unique index, the second signs in by the link).
- **One account per provider and person**; another account of the same provider is refused (`other_account`).
  Changing it is the admin's job: a changed provider e-mail in the record unlinks the old account at once (its
  sessions and refresh tokens end); the next sign-in with the new e-mail links the new one.
- Verified means: Google `email_verified`; GitHub the **primary** e-mail with `verified` from `GET /user/emails`;
  Microsoft only for organisations on `Providers:Microsoft:AllowedTenantIds` (never personal accounts), with `email` or
  the UPN.

**Providers** - the framework's handlers only: GitHub with `AddOAuth`, Google and Microsoft with
`AddOpenIdConnect`, PKCE everywhere. A provider exists only when its id and secret are configured; an application lists
the providers it accepts and the sign-in page shows the intersection. In Development a stub can stand in for all three
(ADR 0018).

**Sessions** (`oidc.sessions`, `SsoSessions`) - server side: the cookie (`__Host-sso.session`, HttpOnly, Secure,
SameSite=Lax) holds 256 random bits, the database their SHA-256. Every use (the panel, an application's authorize, a
refresh) moves the end by `Sessions:IdleTimeout` (14 days), never past `Sessions:AbsoluteLifetime` (30 days), with at
most one write per 15 minutes. Deactivating a person, unlinking, or an admin's "End" ends sessions at once
(`SessionTermination`, with their refresh tokens). The panel's API calls carry a 60-minute access token of the session;
every call reads the person again, so a deactivation counts at once (401, ADR 0008).

## Consequences

- No credential store to protect; the providers' security (2FA) is the team's.
- Losing access to the provider account means asking an admin.

## Revisit when

Someone must sign in without any of the three providers, or applications need single sign-out.
