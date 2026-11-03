using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Sso.Api.Auth;
using Sso.Data;
using Sso.Data.Identity;

namespace Sso.Api.Features.Common;

/// <summary>Who calls an operation: an active person of Great SSO and their role.</summary>
public sealed record Actor(Guid Id, string Role)
{
    public bool IsAdmin => Role == Roles.Admin;

    /// <summary>403 with the operation's own sentence unless the actor is an admin.</summary>
    public void RequireAdmin(string code, string message)
    {
        if (!IsAdmin)
            throw Rule.Forbidden(code, message);
    }
}

/// <summary>
/// The actor of a request, read on every call from the database - a role or a status changed in the panel counts at
/// once, whatever token the caller holds (ADR 0011). No person, or not active: 401.
/// </summary>
public sealed class Actors(SsoDbContext db)
{
    public async Task<Actor> RequireAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirstValue(SessionClaims.UserId), out var id))
            throw Rule.Unauthorized("actor.missing", "Sign in again.");
        var role = await db
            .People.Where(p => p.Id == id && p.Status == Statuses.Active)
            .Select(p => p.Role)
            .FirstOrDefaultAsync(ct);
        return role is null
            ? throw Rule.Unauthorized(
                "actor.inactive",
                "Your account is not active any more - sign in again."
            )
            : new Actor(id, role);
    }
}

/// <summary>The actor for the audit log and the event log: "sub" of the request's user (session, bearer, MCP).</summary>
internal sealed class HttpActorAccessor(IHttpContextAccessor http) : IActorAccessor
{
    public Guid? ActorId =>
        Guid.TryParse(http.HttpContext?.User.FindFirstValue(SessionClaims.UserId), out var id)
            ? id
            : null;
}
