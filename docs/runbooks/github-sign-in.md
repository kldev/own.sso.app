# Runbook: sign-in with GitHub

Switches on the "GitHub" button of `/sign-in` (ADR 0011). Great SSO uses a GitHub **OAuth App** (not a GitHub App)
with PKCE and the scopes `read:user user:email` (`ConfigureGitHub` in `apps/Sso.Api/Auth/Providers/ProviderSetup.cs`).
The provider is on only when **both** its client id and secret are set - otherwise there is no button and
`/signin/github` is a 404. (Locally you can skip this: the development sign-in stub stands in for it, ADR 0018.)

| Environment | Callback URL to register | Where the secrets go |
|---|---|---|
| `dotnet run` / `./deploy/deploy.sh` | `http://localhost:6100/signin/github` | `.env` and user-secrets |
| A server | `https://sso.example.com/signin/github` | the server's environment (`Providers__GitHub__*`) |

A GitHub OAuth App has **exactly one** callback URL - make two apps: one for local work, one for the server.

## 1. GitHub: create the OAuth App

1. As an owner of the organization that should own the app: **Organization → Settings → Developer settings → OAuth
   Apps → New OAuth App** (a personal app works too, but then it belongs to one person).
2. Fill in:
   - **Application name**: `Great SSO` (or `Great SSO (local)`) - people see it on GitHub's consent page.
   - **Homepage URL**: `https://sso.example.com` (local: `http://localhost:6100`).
   - **Authorization callback URL**: from the table above, exactly (scheme, host, port, no trailing slash).
   - **Enable Device Flow**: leave **off**.
3. **Register application**. Copy the **Client ID** (`Ov23li…`).
4. **Generate a new client secret** and copy it at once - GitHub shows it only once.

No webhook, no permissions - the scopes come from Great SSO's request.

## 2. Secrets

Locally, `.env` at the repository's root (never committed; copy from `.env.sample`):

```bash
Providers__GitHub__ClientId=Ov23li...
Providers__GitHub__ClientSecret=...
```

`./deploy/deploy.sh` reads `.env` and passes the values to the container (with a provider configured, the container
runs without the development stub). `dotnet run` does **not** read `.env` - use the user-secrets of `Sso.Api` and
switch the stub off:

```bash
dotnet user-secrets set "Providers:GitHub:ClientId" "Ov23li..." --project apps/Sso.Api
dotnet user-secrets set "Providers:GitHub:ClientSecret" "..." --project apps/Sso.Api
dotnet user-secrets set "DevSignIn:Enabled" "false" --project apps/Sso.Api
```

On a server, the same two values as environment variables of the host - never in the repository.

## 3. Great SSO: let people in

The button alone lets nobody in - there is no registration.

1. **Panel → Applications**: each application that should accept GitHub has **GitHub** on. The sign-in page shows only
   the providers that are configured **and** accepted by the application asking.
2. **Panel → People**: the person's **GitHub e-mail** is their GitHub **primary, verified** e-mail (GitHub → Settings →
   Emails). The public profile e-mail does not count. The first sign-in links the account; after that GitHub's numeric
   id signs in, and an e-mail changed on GitHub changes nothing.

## 4. Check

1. `/sign-in` shows the GitHub button.
2. Sign in with an entered person: GitHub asks for consent once, then you land in the panel or the application.
3. **Panel → Activity** has the sign-in (provider GitHub); a refusal has its reason there.

| Symptom | Cause |
|---|---|
| No GitHub button | id or secret empty in the running process (`.env` is not read by `dotnet run`), the stub is on, or the application does not accept GitHub |
| GitHub: "The redirect_uri is not associated with this application" | the callback URL differs (http/https, port, host) - behind a reverse proxy the host must see `https` (`ForwardedHeaders__KnownNetworks__0`) |
| Back on `/sign-in?error=provider` | cancelled on GitHub, wrong client secret, or the secret was revoked |
| Refused, `no_entry` | the person is not in People, or the GitHub e-mail is not their primary verified one |
| Refused, `unverified` | GitHub did not report the e-mail as verified |
| Refused, `provider_not_allowed` | the application asking does not accept GitHub |
| Refused, `other_account` | the person has another GitHub account linked - the admin changes the GitHub e-mail (it unlinks the old one) |

## Rotation and removal

- **Rotate the secret**: in the OAuth App **Generate a new client secret** (the old one keeps working until you delete
  it), put it in the environment, restart, check a sign-in, then **delete the old secret** on GitHub.
- **Switch GitHub off**: remove both values from the environment and restart.
