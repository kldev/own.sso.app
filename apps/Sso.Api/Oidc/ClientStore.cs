using Microsoft.EntityFrameworkCore;
using Sso.Data;
using Sso.Data.Identity;

namespace Sso.Api.Oidc;

/// <summary>A registered application (oidc.clients) as the protocol sees it.</summary>
public sealed record OidcClient(
    string ClientId,
    string Name,
    string SecretHash,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> Providers,
    bool RequirePkce,
    bool IsActive
)
{
    /// <summary>Redirect URIs match exactly (RFC 9700 4.1.3) - no prefix, no wildcard, no case folding.</summary>
    public bool AllowsRedirect(string? uri) =>
        uri is not null && RedirectUris.Any(r => string.Equals(r, uri, StringComparison.Ordinal));
}

public sealed class ClientStore(IDbContextFactory<SsoDbContext> factory)
{
    public async Task<OidcClient?> FindAsync(string? clientId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(clientId) || clientId.Length > 64)
            return null;
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db
            .Clients.AsNoTracking()
            .Where(c => c.ClientId == clientId)
            .Select(c => new OidcClient(
                c.ClientId,
                c.Name,
                c.SecretHash,
                c.RedirectUris,
                c.Providers,
                c.RequirePkce,
                c.Status == Statuses.Active
            ))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The application a sign-in is for, from the return URL of the sign-in page (/connect/authorize?client_id=...):
    /// its providers limit the buttons, its name is shown. Null for the SSO panel itself.
    /// </summary>
    public async Task<OidcClient?> FromReturnUrlAsync(string? returnUrl, CancellationToken ct)
    {
        var clientId = ClientIdOf(returnUrl);
        if (clientId is null)
            return null;
        var client = await FindAsync(clientId, ct);
        return client is { IsActive: true } ? client : null;
    }

    public static string? ClientIdOf(string? returnUrl)
    {
        if (
            returnUrl is null
            || !returnUrl.StartsWith(OidcEndpoints.AuthorizePath + "?", StringComparison.Ordinal)
        )
            return null;
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(
            returnUrl[(OidcEndpoints.AuthorizePath.Length + 1)..]
        );
        return query.TryGetValue("client_id", out var value) && value.Count == 1 ? value[0] : null;
    }
}
