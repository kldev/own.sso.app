using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

// A Great SSO client as docs/integration.md describes it: a cookie for the application's own session, the framework's
// OpenID Connect handler for the sign-in (authorization code + PKCE, form_post), the identity from the ID token and
// userinfo. The client secret comes from the environment only: GreatSso__ClientSecret.
var builder = WebApplication.CreateBuilder(args);
var sso = builder.Configuration.GetSection("GreatSso");

builder
    .Services.AddAuthentication(o =>
    {
        o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(o => o.Cookie.Name = "sample.session")
    .AddOpenIdConnect(o =>
    {
        o.Authority = sso["Authority"];
        o.ClientId = sso["ClientId"];
        o.ClientSecret =
            sso["ClientSecret"]
            ?? throw new InvalidOperationException(
                "GreatSso__ClientSecret is not set - the secret Great SSO showed once for sample-client."
            );
        o.ResponseType = "code";
        o.UsePkce = true;
        o.Scope.Add("profile");
        o.Scope.Add("email");
        o.GetClaimsFromUserInfoEndpoint = true;
        o.MapInboundClaims = false;
        o.TokenValidationParameters.NameClaimType = "name";
        // Locally the issuer is http://localhost:6100; anywhere else it is https.
        o.RequireHttpsMetadata = !o.Authority!.StartsWith(
            "http://localhost",
            StringComparison.Ordinal
        );
    });
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet(
    "/",
    (ClaimsPrincipal user) =>
    {
        string Claim(string type) => WebUtility.HtmlEncode(user.FindFirstValue(type) ?? "-");
        var body =
            user.Identity?.IsAuthenticated == true
                ? $"""
                    <h1>Signed in through Great SSO</h1>
                    <p>The application received the person's identity - never a password, never a role.</p>
                    <dl data-testid="claims">
                      <dt>sub</dt><dd>{Claim("sub")}</dd>
                      <dt>preferred_username</dt><dd>{Claim("preferred_username")}</dd>
                      <dt>name</dt><dd>{Claim("name")}</dd>
                      <dt>email</dt><dd>{Claim("email")}</dd>
                    </dl>
                    """
                : """
                    <h1>Sample application</h1>
                    <p>This application has no accounts of its own: its people sign in with Great SSO.</p>
                    <a class="button" href="/signin">Sign in with Great SSO</a>
                    """;
        return Results.Content(
            $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Sample application</title>
            <style>
              body { font-family: system-ui, sans-serif; max-width: 36rem; margin: 4rem auto; padding: 0 1rem; color: #18181b; }
              dl { display: grid; grid-template-columns: max-content 1fr; gap: .5rem 1.5rem; padding: 1rem;
                   border: 1px solid #e4e4e7; border-radius: .75rem; }
              dt { font-family: ui-monospace, monospace; color: #52525b; } dd { margin: 0; font-weight: 600; }
              .button { display: inline-block; padding: .6rem 1rem; border-radius: .5rem; background: #0f766e;
                        color: #fff; text-decoration: none; font-weight: 600; }
            </style></head><body>{{body}}</body></html>
            """,
            "text/html; charset=utf-8"
        );
    }
);
app.MapGet("/signin", () => Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }));

app.Run();
