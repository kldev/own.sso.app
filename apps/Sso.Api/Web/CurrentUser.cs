using Sso.ApiClient.Generated;
using Sso.ApiClient.Generated.Models;

namespace Sso.Api.Web;

/// <summary>The signed-in user with the organization (/api/platform/me), loaded once per circuit.</summary>
public sealed class CurrentUser(SsoApi api)
{
    private PlatformMeRow? _me;

    public async Task<PlatformMeRow?> GetAsync(CancellationToken ct = default)
    {
        if (_me is not null)
            return _me;
        var page = await api.Api.Platform.Me.GetAsync(cancellationToken: ct);
        _me = page?.Items?.FirstOrDefault();
        return _me;
    }
}

/// <summary>Roles of the operator's people (identity.users.role).</summary>
public static class Roles
{
    // SSO's own roles (ADR 0011): a new role is a constant here, a label in Labels.Role and the CHECK in the model (libs/Sso.Data).
    public const string Admin = "admin";
    public const string Member = "member";
}
