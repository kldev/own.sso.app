using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Sso.Api.Features.Common;
using Sso.Data;

namespace Sso.Api.Features.Platform;

/// <summary>The signed-in person - the shell of the panel (sidebar, top bar, role badge).</summary>
public sealed class PlatformMeRow
{
    public required Guid Id { get; init; }
    public required string Username { get; init; }
    public required string Email { get; init; }
    public required string DisplayName { get; init; }
    public required string Role { get; init; }
}

/// <summary>GET /api/platform/me: one row, the caller (401 when the caller is not an active person).</summary>
public sealed class GetMe(SsoDbContext db, Actors actors) : IHandler
{
    public static readonly ListSpec<PlatformMeRow> Spec = new ListSpec<PlatformMeRow>()
        .Add("id", r => r.Id, Filter.Eq)
        .Add("username", r => r.Username, Filter.Eq)
        .Add("email", r => r.Email)
        .Add("displayName", r => r.DisplayName)
        .Add("role", r => r.Role, Filter.Eq);

    public async Task<ListPage<PlatformMeRow>> HandleAsync(
        ClaimsPrincipal user,
        IQueryCollection query,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        var rows = db
            .People.AsNoTracking()
            .Where(u => u.Id == actor.Id)
            .Select(u => new PlatformMeRow
            {
                Id = u.Id,
                Username = u.Username,
                Email = u.Email,
                DisplayName = u.DisplayName,
                Role = u.Role,
            });
        return await Spec.PageAsync(rows, query, ct);
    }
}
