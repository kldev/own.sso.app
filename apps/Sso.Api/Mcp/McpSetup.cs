using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.AspNetCore;

namespace Sso.Api.Mcp;

/// <summary>
/// /mcp for Claude Code (ADR 0013): streamable HTTP, stateless, opened only by an SSO admin's personal MCP token
/// (McpTokenHandler). Configure Claude Code with the panel's command (Claude Code page): <c>claude mcp add
/// --transport http great-sso &lt;issuer&gt;/mcp --header "Authorization: Bearer mcp_…"</c>.
/// </summary>
public static class McpSetup
{
    public const string Path = "/mcp";
    private const string Policy = "mcp";

    private const string Instructions =
        "Great SSO: one sign-in for a team's applications through GitHub, Google or a work Microsoft account. You act "
        + "as the SSO admin who issued the token; every call is logged. People: only those entered here can sign in "
        + "(no registration); a person's first sign-in with a provider e-mail links that account, later the provider's "
        + "own id signs in; one account per provider - to change it, change the provider e-mail (person_update). "
        + "Applications: OpenID Connect clients; a client secret is returned once (client_register, "
        + "client_new_secret) - give it to the person for the application's environment, never write it into a "
        + "repository. Roles inside applications are the applications' own; SSO's roles are admin and member. "
        + "Read before you change: people_list and clients_list give the current state. Marketing: mail_send e-mails "
        + "a template to a list of addresses, one e-mail each - it leaves the system, so confirm the list first.";

    public static AuthenticationBuilder AddSsoMcp(this AuthenticationBuilder auth)
    {
        auth.AddScheme<AuthenticationSchemeOptions, McpTokenHandler>(
            McpTokenHandler.SchemeName,
            null
        );
        auth.Services.AddScoped<McpToolRunner>();
        auth.Services.AddSingleton<McpTokens>();
        auth.Services.AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "great-sso", Version = "1.0" };
                o.ServerInstructions = Instructions;
            })
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<SsoTools>()
            .WithTools<MarketingTools>();
        return auth;
    }

    public static AuthorizationBuilder AddSsoMcpPolicy(this AuthorizationBuilder authorization) =>
        authorization.AddPolicy(
            Policy,
            p =>
                p.AddAuthenticationSchemes(McpTokenHandler.SchemeName)
                    .RequireAuthenticatedUser()
                    .RequireRole("admin")
        );

    public static void MapSsoMcp(this IEndpointRouteBuilder app) =>
        app.MapMcp(Path).RequireAuthorization(Policy).DisableAntiforgery();
}
