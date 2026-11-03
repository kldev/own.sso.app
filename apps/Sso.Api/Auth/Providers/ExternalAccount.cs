using System.Security.Claims;
using System.Text.Json;

namespace Sso.Api.Auth.Providers;

/// <summary>
/// What a provider said about the person, reduced to what SSO decides on: the provider's stable id (Subject), the
/// organisation (Microsoft's tid), the e-mail and whether the provider verified it. Refusal: a reason to stop before
/// the database is asked (a personal Microsoft account, an organisation not on the list).
/// </summary>
public sealed record ExternalAccount(
    string Provider,
    string Subject,
    string? TenantId,
    string? Email,
    bool EmailVerified,
    string? Refusal = null
)
{
    // Claims of the short-lived external cookie between the provider's callback and /auth/external/{provider}/done.
    private const string SubjectClaim = "sso:subject";
    private const string TenantClaim = "sso:tenant";
    private const string EmailClaim = "sso:email";
    private const string VerifiedClaim = "sso:email_verified";
    private const string RefusalClaim = "sso:refusal";

    /// <summary>The account for the external cookie's identity (replaces whatever the provider's handler put there).</summary>
    public ClaimsIdentity ToIdentity(string authenticationType)
    {
        var claims = new List<Claim>
        {
            new(SubjectClaim, Subject),
            new(VerifiedClaim, EmailVerified ? "true" : "false"),
        };
        if (TenantId is not null)
            claims.Add(new Claim(TenantClaim, TenantId));
        if (Email is not null)
            claims.Add(new Claim(EmailClaim, Email));
        if (Refusal is not null)
            claims.Add(new Claim(RefusalClaim, Refusal));
        return new ClaimsIdentity(claims, authenticationType);
    }

    public static ExternalAccount? FromPrincipal(string provider, ClaimsPrincipal principal)
    {
        var subject = principal.FindFirstValue(SubjectClaim);
        if (string.IsNullOrEmpty(subject))
            return null;
        return new ExternalAccount(
            provider,
            subject,
            principal.FindFirstValue(TenantClaim),
            principal.FindFirstValue(EmailClaim),
            principal.FindFirstValue(VerifiedClaim) == "true",
            principal.FindFirstValue(RefusalClaim)
        );
    }
}

/// <summary>
/// GitHub (OAuth 2.0, no OpenID Connect): the numeric id from GET /user, and the e-mail from GET /user/emails - only
/// the primary one, and verified only when GitHub says so (a profile's public e-mail is never trusted).
/// </summary>
public static class GitHubAccount
{
    public static ExternalAccount From(JsonElement user, JsonElement emails)
    {
        var id = user.GetProperty("id")
            .GetInt64()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        string? email = null;
        var verified = false;
        if (emails.ValueKind == JsonValueKind.Array)
            foreach (var item in emails.EnumerateArray())
            {
                if (!item.TryGetProperty("primary", out var primary) || !primary.GetBoolean())
                    continue;
                email = item.GetProperty("email").GetString();
                verified = item.TryGetProperty("verified", out var v) && v.GetBoolean();
                break;
            }
        return new ExternalAccount("github", id, null, email, verified);
    }
}

/// <summary>Google (OpenID Connect): sub, email and email_verified of the validated ID token.</summary>
public static class GoogleAccount
{
    public static ExternalAccount From(ClaimsPrincipal principal) =>
        new(
            "google",
            principal.FindFirstValue("sub") ?? "",
            null,
            principal.FindFirstValue("email"),
            string.Equals(
                principal.FindFirstValue("email_verified"),
                "true",
                StringComparison.OrdinalIgnoreCase
            )
        );
}

/// <summary>
/// Microsoft (OpenID Connect, endpoint "organizations"): work accounts of the allowed organisations only. The key is
/// tid + oid (oid is the person's id in the organisation). Microsoft sends no email_verified, and its email claim is
/// whatever the organisation's admin set: the e-mail counts as verified only because the organisation is on our list -
/// the claim email, or the sign-in name (preferred_username, the UPN) when the organisation sends no email (ADR 0011).
/// </summary>
public static class MicrosoftAccount
{
    /// <summary>The tenant of personal Microsoft accounts (outlook.com, hotmail) - never accepted.</summary>
    public const string ConsumersTenantId = "9188040d-6c67-4c5b-b112-36a304b66dad";

    public static ExternalAccount From(
        ClaimsPrincipal principal,
        IReadOnlyCollection<string> allowedTenants
    )
    {
        var tid = principal.FindFirstValue("tid") ?? "";
        var oid = principal.FindFirstValue("oid") ?? "";
        var email =
            principal.FindFirstValue("email") ?? principal.FindFirstValue("preferred_username");
        var refusal =
            tid.Length == 0 || oid.Length == 0 ? "no_tenant"
            : string.Equals(tid, ConsumersTenantId, StringComparison.OrdinalIgnoreCase)
                ? "personal_account"
            : !IsAllowed(tid, allowedTenants) ? "tenant_not_allowed"
            : null;
        return new ExternalAccount(
            "microsoft",
            $"{tid}:{oid}",
            tid,
            email,
            refusal is null && email is not null,
            refusal
        );
    }

    private static bool IsAllowed(string tenantId, IReadOnlyCollection<string> allowedTenants) =>
        !string.Equals(tenantId, ConsumersTenantId, StringComparison.OrdinalIgnoreCase)
        && allowedTenants.Any(t =>
            string.Equals(t.Trim(), tenantId, StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>
    /// The issuer of a token from the multi-tenant endpoint is the person's organisation; accepted only for an allowed
    /// one (the token's tid must be in it).
    /// </summary>
    public static bool IsValidIssuer(
        string issuer,
        string? tenantId,
        IReadOnlyCollection<string> allowedTenants
    ) =>
        tenantId is { Length: > 0 }
        && IsAllowed(tenantId, allowedTenants)
        && string.Equals(
            issuer,
            $"https://login.microsoftonline.com/{tenantId}/v2.0",
            StringComparison.OrdinalIgnoreCase
        );
}
