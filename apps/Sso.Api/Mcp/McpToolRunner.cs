using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using ModelContextProtocol;
using Sso.Api.Auth;
using Sso.Api.Features.Common;
using Sso.Api.Oidc;
using Sso.Data;
using Sso.Data.Oidc;

namespace Sso.Api.Mcp;

/// <summary>
/// What a tool does (ADR 0013): the same handlers as the panel's API, as the token's person - so every rule, role
/// check and log entry is the same. A refusal reaches the agent as a tool error with the rule's own sentence and code.
/// Each call is logged in oidc.events (mcp_tool), never with its arguments.
/// </summary>
public sealed class McpToolRunner(
    IHttpContextAccessor http,
    IServiceProvider services,
    IDbContextFactory<SsoDbContext> factory,
    EventLog events
)
{
    public ClaimsPrincipal User =>
        http.HttpContext?.User ?? throw new McpException("The call carries no person.");

    public T Handler<T>()
        where T : IHandler => services.GetRequiredService<T>();

    public async Task<T> RunAsync<T>(string tool, Func<Task<T>> work, CancellationToken ct)
    {
        var request = RequestInfo.From(http.HttpContext!);
        var token = User.FindFirstValue(McpTokenHandler.TokenIdClaim);
        try
        {
            var result = await work();
            await LogAsync(tool, Outcomes.Success, null, token, request, ct);
            return result;
        }
        catch (RuleException e)
        {
            await LogAsync(tool, Outcomes.Refused, e.Code, token, request, ct);
            throw new McpException($"{e.Message} ({e.Code})");
        }
        catch (McpException e)
        {
            await LogAsync(tool, Outcomes.Refused, e.Message, token, request, ct);
            throw;
        }
    }

    /// <summary>A list's query string from the tool's arguments (null and empty arguments are left out).</summary>
    public static IQueryCollection Query(params (string Key, object? Value)[] values) =>
        new QueryCollection(
            values
                .Where(v => v.Value is not null && v.Value.ToString() is { Length: > 0 })
                .ToDictionary(
                    v => v.Key,
                    v => new StringValues(
                        v.Value is bool b ? (b ? "true" : "false") : v.Value!.ToString()
                    )
                )
        );

    private async Task LogAsync(
        string tool,
        string outcome,
        string? reason,
        string? token,
        RequestInfo request,
        CancellationToken ct
    )
    {
        // Its own unit of work: a refused tool's half-made changes are never saved with the log entry.
        await using var db = await factory.CreateDbContextAsync(ct);
        var tokenName = Guid.TryParse(token, out var tokenId)
            ? await db
                .McpTokens.Where(t => t.Id == tokenId)
                .Select(t => t.Name)
                .FirstOrDefaultAsync(ct)
            : null;
        Guid? userId = Guid.TryParse(User.FindFirstValue(SessionClaims.UserId), out var id)
            ? id
            : null;
        events.Add(
            db,
            "mcp_tool",
            outcome,
            reason,
            userId,
            request: request,
            detail: new Dictionary<string, string?> { ["tool"] = tool, ["token"] = tokenName }
        );
        await db.SaveChangesAsync(ct);
    }
}
