# OpenID Foundation conformance suite - Basic OP

The provider is our own code (ADR 0012), so it runs the OpenID Foundation's suite, plan
**"OpenID Connect Core: Basic Certification Profile Authorization server test"** (`oidcc-basic-certification-test-plan`),
locally in Docker. Every test must be `PASSED`, or `SKIPPED`/`WARNING` for a feature outside our narrow profile with
the reason written in ADR 0012; a `FAILED` is a fix with a regression test.

The suite signs a person in through Great SSO, so Great SSO needs a configured provider (GitHub or Google in `.env`)
and a person entered for the account used - or, locally, the development sign-in stub (ADR 0018).

## Run

```bash
free -h                                   # the suite takes 1-2 GB (Java + MongoDB): nothing else heavy meanwhile
./tools/conformance/register-clients.sh   # two clients (the plan needs two), PKCE off for them only; prints the secrets
./tools/conformance/run.sh                # clones the suite (once, outside the repo), starts it, prints the address
# https://localhost.emobix.co.uk:8443 -> Create a new test plan -> the plan above, "client_secret_basic", "static client"
#   paste config.json (filled in from config.sample.json with the printed secrets) -> run every module, sign in when asked
./tools/conformance/run.sh down           # stop and remove the suite's containers
```

Export the results (HTML/JSON) into the report; never commit the filled `config.json` (secrets) - it is git-ignored.

## Notes

- The suite reaches Great SSO from a container: `register-clients.sh` registers the suite's callback
  `https://localhost.emobix.co.uk:8443/test/a/great-sso/callback`; Great SSO's issuer must be an address the suite's
  container and the browser both reach (set `Oidc__Issuer` accordingly, e.g. a host name both resolve).
- The suite's clients have `require_pkce = false` (the Basic OP plan sends no PKCE); every other client requires it.
