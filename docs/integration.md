# Adding "Sign in with Great SSO" to an application

Great SSO tells an application **who** the person is; the application decides whether it has an account for them and
what they may do (ADR 0011, 0012). An application may keep its own sign-in next to the button.
`samples/Sso.SampleClient` is a complete example in one file.

## 1. Register the application

Panel → **Applications → Register an application** (or the MCP tool `client_register`):

| Field | Example (local) | Example (server) |
|---|---|---|
| Client id | `team-handbook` | `team-handbook` |
| Redirect URI | `http://localhost:5300/signin-oidc` | `https://handbook.example.com/signin-oidc` |
| Providers | GitHub, Google (Microsoft for work accounts) | the same |

Redirect URIs match **exactly** (https, or http only on localhost). Copy the secret shown once into the application's
environment (`GreatSso__ClientSecret`); Great SSO keeps only its hash.

## 2. The handler (ASP.NET Core)

```csharp
builder.Services
    .AddAuthentication(o =>
    {
        o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie()
    .AddOpenIdConnect(o =>
    {
        o.Authority = builder.Configuration["GreatSso:Authority"];       // https://sso.example.com - the issuer, exactly
        o.ClientId = builder.Configuration["GreatSso:ClientId"];         // team-handbook
        o.ClientSecret = builder.Configuration["GreatSso:ClientSecret"]; // from the environment only
        o.ResponseType = "code";                                         // PKCE is on by default and required
        o.Scope.Add("profile");
        o.Scope.Add("email");
        o.MapInboundClaims = false;                                      // keep sub, preferred_username, email
        o.GetClaimsFromUserInfoEndpoint = true;
    });
```

The default callback path is `/signin-oidc`. Add `offline_access` only when the application calls Great SSO again later
(a refresh token that rotates on every use and lives as long as the SSO session). Any other OpenID Connect library works
the same way: discovery at `<issuer>/.well-known/openid-configuration`, authorization code with PKCE S256,
`client_secret_basic` or `client_secret_post`.

## 3. What the application receives

| Claim | Scope | Value |
|---|---|---|
| `sub` | openid | the person's id in Great SSO - **the** key to store on the application's account |
| `preferred_username`, `name` | profile | username and display name |
| `email`, `email_verified` | email | the work e-mail the admin entered (verified by them) |

Never a role or a group: what the person may do comes from the application's own account.

## 4. Matching the account

1. An account with the stored `sub` - always after the first time; a username or e-mail changed in Great SSO changes
   nothing.
2. The first time: the account whose username **or** e-mail equals Great SSO's (lower case), if exactly one and active
   - store `sub` on it.
3. Nothing found or inactive: refused with a sentence saying what to do ("ask your admin to add you"). The application
   never creates an account from Great SSO unless it wants open sign-up.

## 5. What stays the application's

Its session and sign-out (signing out of the application ends only its session; the next "Sign in with Great SSO" goes
through while the SSO session lives - 14 days without use, 30 days at most), its roles and everything a person may do.

## 6. Locally, with containers

The issuer is one address for the browser and for the application's back channel. `http://localhost:6100` works when
the application runs with `dotnet run`; an application in a container cannot reach the host's `localhost` - give Great
SSO a name both resolve and set `Oidc__Issuer` to it. A server has one public https name and no such issue.

## 7. Checklist

- [ ] client registered, secret in the application's environment, redirect URI exact, providers fit its people
- [ ] `sub` stored at the first match, unique; inactive accounts refused
- [ ] tests: first match by username or e-mail, then by `sub` after a rename in Great SSO; no account → refusal
