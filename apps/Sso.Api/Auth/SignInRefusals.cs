using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Sso.Api.Oidc;

namespace Sso.Api.Auth;

/// <summary>Why a sign-in was refused, and with which account - what the refusal page tells the person.</summary>
public sealed record Refusal(string Reason, string? Provider, string? Account);

/// <summary>
/// Refusals before a person is looked up (the provider's answer): logged in oidc.events, and handed to the refusal page
/// in a protected query value - the account's e-mail is the person's own data and never appears in a URL in clear.
/// </summary>
public sealed class SignInRefusals(ProtocolLog log, IDataProtectionProvider protection)
{
    private const string PagePath = "/sign-in/refused";

    private readonly ITimeLimitedDataProtector _protector = protection
        .CreateProtector("sso.sign-in-refusal")
        .ToTimeLimitedDataProtector();

    public Task RecordAsync(
        string reason,
        string? clientId,
        string? provider,
        RequestInfo request,
        CancellationToken ct
    ) => log.RecordAsync("sign_in", reason, clientId, provider, request, ct);

    /// <summary>The refusal page's address for this refusal (valid for 10 minutes).</summary>
    public string PageUrl(Refusal refusal, string? returnUrl)
    {
        var value = _protector.Protect(JsonSerializer.Serialize(refusal), TimeSpan.FromMinutes(10));
        var url = $"{PagePath}?r={Uri.EscapeDataString(value)}";
        return returnUrl is null ? url : $"{url}&returnUrl={Uri.EscapeDataString(returnUrl)}";
    }

    public Refusal? Read(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        try
        {
            return JsonSerializer.Deserialize<Refusal>(_protector.Unprotect(value));
        }
        catch (Exception e)
            when (e is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null;
        }
    }
}
