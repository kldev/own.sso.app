# 0018. A development sign-in stub, and the end-to-end walk that writes the screenshots

Status: accepted (2026-11-03)

## Context

A local try-out and the end-to-end screenshots need people to sign in, but registering OAuth applications at GitHub,
Google and Microsoft for every machine is a chore - and a shortcut into production must never exist.

## Decision

- **`DevSignIn`**: with `DevSignIn__Enabled=true` the stub stands in for all three providers. The provider's button
  leads to a small form that asks for the account's e-mail; the stub then writes exactly what a provider's handler
  would - the external cookie with a stable account id and a verified e-mail - and the real `/auth/external/{provider}
  /done` decides: the matching of the person, the refusals and the session are the production code.
- **Development only**: on by default in `appsettings.Development.json`; in any other environment the flag stops the
  start with an explanation (`DevSignInTests`). `deploy/deploy.sh` runs the local container in Development with the
  stub only when `.env` names no provider.
- **`e2e/screenshots.mjs`** (Playwright): signs in through the stub as the seeded admin and walks the panel - people,
  a person with providers, applications and a secret shown once, sessions and the log, marketing, an MCP token, Scalar,
  the dark theme on a desktop and a phone, a refused sign-in on a phone, and `samples/Sso.SampleClient` signed in
  through Great SSO - writing `docs/screenshots/NN-name.png`. Every step waits for what it needs; the first step that
  fails fails the run, so it is a smoke test as well.

## Consequences

- Trying Great SSO needs Docker and nothing else; the README's screenshots are made from the same code and seed.

## Revisit when

A provider offers a test tenant worth wiring into CI.
