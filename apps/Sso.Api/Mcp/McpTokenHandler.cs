using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Sso.Api.Auth;

namespace Sso.Api.Mcp;

/// <summary>
/// /mcp's only way in (ADR 0013): <c>Authorization: Bearer mcp_…</c>, an SSO admin's personal token checked against
/// McpTokens.CheckAsync on every request - a revoked or expired token, or a person no longer an active admin,
/// stops at once. The panel's routes never accept it and /mcp never accepts a session or the panel's token.
/// </summary>
public sealed class McpTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    McpTokens tokens
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "McpToken";
    public const string Prefix = "mcp_";
    public const string TokenIdClaim = "sso:mcp_token";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer " + Prefix, StringComparison.Ordinal))
            return AuthenticateResult.NoResult();
        var token = header["Bearer ".Length..].Trim();
        var caller = await tokens.CheckAsync(Secrets.Hash(token), Context.RequestAborted);
        if (caller is null)
            return AuthenticateResult.Fail(
                "The MCP token is unknown, revoked or expired, or its person is not an active SSO admin."
            );
        var identity = new ClaimsIdentity(
            [
                new Claim(TokenIdClaim, caller.TokenId.ToString()),
                new Claim(SessionClaims.UserId, caller.UserId.ToString()),
                new Claim(SessionClaims.Username, caller.Username),
                new Claim(SessionClaims.Name, caller.DisplayName),
                new Claim(SessionClaims.Role, caller.Role),
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
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"great-sso-mcp\"";
        return Task.CompletedTask;
    }
}
