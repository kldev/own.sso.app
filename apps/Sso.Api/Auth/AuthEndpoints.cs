namespace Sso.Api.Auth;

/// <summary>
/// /auth/session-expired: a page found its internal access token expired (401 from the API). The SSO session
/// lives on in the database, so the page is simply loaded again - with a new token, or with the sign-in page when the
/// session has ended. There is no sign-out (ADR 0011) and no password.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/auth/session-expired",
                (string? returnUrl) => Results.LocalRedirect(LocalUrl(returnUrl))
            )
            .AllowAnonymous();
        app.MapExternalSignIn();
        return app;
    }

    /// <summary>Only a path of this site: an absolute URL in returnUrl would be an open redirect.</summary>
    public static string LocalUrl(string? url) =>
        url is { Length: > 1 }
        && url.StartsWith('/')
        && !url.StartsWith("//")
        && !url.StartsWith("/\\")
            ? url
            : "/admin";
}
