# Runbook: sign-in with Google (Gmail and Google Workspace)

Switches on the "Google" button of `/sign-in` (ADR 0011). Great SSO talks OpenID Connect to
`https://accounts.google.com` with PKCE and the scopes `openid email profile` (`ConfigureGoogle` in
`apps/Sso.Api/Auth/Providers/ProviderSetup.cs`). Any Google account works - `@gmail.com` or Google Workspace - as long
as an SSO admin entered its e-mail. The provider is on only when **both** its client id and secret are set.

| Environment | Authorized redirect URI | Where the secrets go |
|---|---|---|
| `dotnet run` / `./deploy/deploy.sh` | `http://localhost:6100/signin/google` | `.env` and user-secrets |
| A server | `https://sso.example.com/signin/google` | the server's environment (`Providers__Google__*`) |

Keep **two clients** (local and server) so a leaked local secret never opens the server.

## 1. Google Cloud: project and consent screen

In the [Google Cloud console](https://console.cloud.google.com/) (**Google Auth Platform**; older consoles call it
**APIs & Services → OAuth consent screen**):

1. **New project**, e.g. `great-sso`. No API has to be enabled - sign-in needs none.
2. **Google Auth Platform → Get started**: **App name** `Great SSO`, a **User support email** you own, **Audience**
   **External** (Gmail accounts are outside any Workspace; **Internal** only when every person is in your own
   Workspace), and the developer contact e-mail.
3. **Branding**: application home page `https://sso.example.com`, authorized domain `example.com` (yours). A logo
   triggers Google's brand verification - leave it out unless you want that.
4. **Data access**: `openid`, `.../auth/userinfo.email`, `.../auth/userinfo.profile` - all non-sensitive.
5. **Audience → Publishing status**: **Testing** lets only the listed test users in (fine locally); **In production**
   for the server, otherwise everyone else gets "Access blocked".

## 2. Google Cloud: the OAuth client

1. **Google Auth Platform → Clients → Create client**, type **Web application**, name `Great SSO` (or
   `Great SSO (local)`).
2. **Authorized JavaScript origins**: empty. **Authorized redirect URIs**: the URI from the table, exactly.
3. **Create**; copy the **Client ID** (`…apps.googleusercontent.com`) and the **Client secret** (`GOCSPX-…`) at once.

Redirect URI changes can take a few minutes at Google.

## 3. Secrets

Locally, `.env` (copy from `.env.sample`):

```bash
Providers__Google__ClientId=123...apps.googleusercontent.com
Providers__Google__ClientSecret=GOCSPX-...
```

`./deploy/deploy.sh` passes them to the container. For `dotnet run` use the user-secrets of `Sso.Api` (and
`DevSignIn:Enabled=false`), as in the GitHub runbook. While the app is in **Testing**, add your account as a test user.
On a server: the same values as environment variables of the host.

## 4. Great SSO: let people in

1. **Panel → Applications**: each application that should accept Google has **Google** on.
2. **Panel → People**: the person's **Google e-mail** is the address of their Google account. Google must report it as
   verified - always true for Gmail and Workspace. The first sign-in links the account; after that Google's `sub` signs
   in.

## 5. Check

1. `/sign-in` shows the Google button; sign in with an entered person; **Panel → Activity** has the sign-in.

| Symptom | Cause |
|---|---|
| No Google button | id or secret empty in the running process, the stub is on, or the application does not accept Google |
| "Error 400: redirect_uri_mismatch" | the redirect URI differs (http/https, port, host) or was just added |
| "Access blocked: … has not completed the Google verification process" | Testing and the account is not a test user - add it or publish the app |
| "Error 403: org_internal" | Audience **Internal** and the account is outside that Workspace |
| Refused, `no_entry` | the person is not in People, or the Google e-mail differs |
| Refused, `other_account` | another Google account is linked - the admin changes the Google e-mail |

## Rotation and removal

Add a new secret to the client, put it in the environment, restart, check a sign-in, then delete the old secret.
To switch Google off, remove both values and restart.
