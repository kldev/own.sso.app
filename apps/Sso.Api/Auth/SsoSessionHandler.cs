using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Sso.Api.Auth;

/// <summary>
/// The browser's way in: the SSO session cookie, checked against the database on each request (oidc.session_touch,
/// which also extends the session). The principal carries a short internal access token for the panel's contract calls
/// (ADR 0002). Pages without a session go to /sign-in, API paths answer 401/403.
/// </summary>
public sealed class SsoSessionHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SsoSessions sessions,
    TokenIssuer tokens,
    IOptions<SsoSessionOptions> sessionOptions
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "SsoSession";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Cookies[sessionOptions.Value.CookieName];
        if (string.IsNullOrEmpty(token))
            return AuthenticateResult.NoResult();
        var user = await sessions.TouchAsync(
            token,
            null,
            RequestInfo.From(Context),
            Context.RequestAborted
        );
        if (user is null)
            return AuthenticateResult.Fail("The SSO session ended or does not exist.");
        Context.Items[typeof(SessionUser)] = user;
        var (accessToken, _) = tokens.Issue(user);
        var identity = new ClaimsIdentity(
            [
                new Claim(SessionClaims.UserId, user.UserId.ToString()),
                new Claim(SessionClaims.Name, user.DisplayName),
                new Claim(SessionClaims.Username, user.Username),
                new Claim(SessionClaims.Email, user.Email),
                new Claim(SessionClaims.Role, user.Role),
                new Claim(SessionClaims.SessionId, user.SessionId.ToString()),
                new Claim(SessionClaims.AccessToken, accessToken),
            ],
            SchemeName,
            SessionClaims.Name,
            SessionClaims.Role
        );
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)
        );
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (AuthSetup.IsApi(Request.Path))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
        var returnUrl =
            properties.RedirectUri ?? $"{Request.PathBase}{Request.Path}{Request.QueryString}";
        Response.Redirect($"/sign-in?returnUrl={Uri.EscapeDataString(returnUrl)}");
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        if (AuthSetup.IsApi(Request.Path))
            Response.StatusCode = StatusCodes.Status403Forbidden;
        else
            Response.Redirect("/access-denied");
        return Task.CompletedTask;
    }

    /// <summary>Writes the session cookie after a sign-in: HttpOnly, SameSite=Lax, Secure outside local http.</summary>
    public static void WriteCookie(
        HttpContext http,
        SsoSessionOptions options,
        string token,
        DateTimeOffset expires
    ) =>
        http.Response.Cookies.Append(
            options.CookieName,
            token,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = options.SecureCookie,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Expires = expires,
                IsEssential = true,
            }
        );
}
