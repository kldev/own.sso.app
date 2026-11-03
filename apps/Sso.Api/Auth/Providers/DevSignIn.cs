using System.Security.Claims;
using System.Text.Encodings.Web;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Sso.Api.Auth.Providers;

/// <summary>
/// The development sign-in stub (ADR 0018), section "DevSignIn": stands in for GitHub, Google and Microsoft when
/// DevSignIn__Enabled=true - a local try-out and the end-to-end screenshots need no real provider. Only in the
/// Development environment: anywhere else the flag stops the start. Instead of the provider's page a small form asks for
/// the account's e-mail; what it hands on is exactly what a provider's handler leaves behind (the external cookie with
/// a verified e-mail), so the matching of the person, the refusals and the session are the real code.
/// </summary>
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.Members)]
public sealed class DevSignInOptions
{
    public const string SectionName = "DevSignIn";

    public bool Enabled { get; init; }

    /// <summary>The organisation a stubbed Microsoft account belongs to (must be an allowed one to sign in).</summary>
    public string MicrosoftTenantId { get; init; } = "00000000-0000-4000-8000-000000000000";
}

/// <summary>A provider's scheme in the stub: the challenge goes to the stub's form instead of the provider.</summary>
internal sealed class DevProviderHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var returnUrl = properties.Items.TryGetValue(
            ExternalSignInEndpoints.ReturnUrlItem,
            out var url
        )
            ? url
            : "/admin";
        Response.Redirect(
            $"{DevSignIn.Path}/{Scheme.Name}?returnUrl={Uri.EscapeDataString(returnUrl ?? "/admin")}"
        );
        return Task.CompletedTask;
    }
}

public static class DevSignIn
{
    public const string Path = "/dev/sign-in";

    /// <summary>The account id the stub gives an e-mail - stable, as a provider's own id is (the demo seed uses it).</summary>
    public static string Subject(string email) =>
        $"dev-{Convert.ToHexStringLower(Secrets.Hash(email.Trim().ToLowerInvariant()))[..16]}";

    /// <summary>The stub's schemes for the three providers; refuses to run outside Development.</summary>
    public static bool AddDevSignIn(
        this AuthenticationBuilder auth,
        IConfiguration configuration,
        IHostEnvironment environment,
        List<Provider> configured
    )
    {
        var options =
            configuration.GetSection(DevSignInOptions.SectionName).Get<DevSignInOptions>()
            ?? new DevSignInOptions();
        if (!options.Enabled)
            return false;
        if (!environment.IsDevelopment())
            throw new InvalidOperationException(
                "DevSignIn:Enabled is only allowed in the Development environment - it lets anybody sign in as "
                    + "any entered person. Remove DevSignIn__Enabled."
            );
        foreach (var provider in ProviderCatalog.All)
        {
            configured.Add(provider);
            auth.AddScheme<AuthenticationSchemeOptions, DevProviderHandler>(provider.Key, null);
        }
        auth.Services.AddSingleton(Options.Create(options));
        return true;
    }

    /// <summary>The stub's form and its answer (only mapped when the stub is on).</summary>
    public static IEndpointRouteBuilder MapDevSignIn(this IEndpointRouteBuilder app)
    {
        app.MapGet($"{Path}/{{provider}}", Form).AllowAnonymous();
        app.MapPost($"{Path}/{{provider}}", SignInAsync).AllowAnonymous().DisableAntiforgery();
        return app;
    }

    private static IResult Form(string provider, string? returnUrl)
    {
        if (ProviderCatalog.All.All(p => p.Key != provider))
            return Results.NotFound();
        var enc = HtmlEncoder.Default;
        var name = ProviderCatalog.DisplayName(provider);
        var html = $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{enc.Encode(name)}} (development) - Great SSO</title>
            <link rel="stylesheet" href="/css/app.css"></head>
            <body class="flex min-h-svh items-center justify-center bg-background p-4 text-foreground">
            <form method="post" class="card w-full max-w-sm space-y-4 p-6" data-testid="dev-sign-in">
              <h1 class="text-lg font-semibold">{{enc.Encode(name)}} - development sign-in</h1>
              <p class="text-sm text-muted-foreground">Stands in for {{enc.Encode(
                name
            )}} on this machine only. The
                account's e-mail is taken as verified; Great SSO matches it with the people the admin entered.</p>
              <label class="label" for="email">The account's e-mail</label>
              <input class="input" id="email" name="email" type="email" required autofocus value="admin@example.com">
              <input type="hidden" name="returnUrl" value="{{enc.Encode(returnUrl ?? "/admin")}}">
              <button class="btn btn-primary w-full" type="submit">Continue as this account</button>
            </form></body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static async Task<IResult> SignInAsync(
        string provider,
        HttpContext http,
        IOptions<DevSignInOptions> options
    )
    {
        if (ProviderCatalog.All.All(p => p.Key != provider))
            return Results.NotFound();
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var email = form["email"].ToString().Trim().ToLowerInvariant();
        if (email.Length == 0)
            return Results.Redirect($"{Path}/{provider}");
        var tenant = provider == "microsoft" ? options.Value.MicrosoftTenantId : null;
        var subject = Subject(email);
        if (tenant is not null)
            subject = $"{tenant}:{subject}";
        var account = new ExternalAccount(provider, subject, tenant, email, EmailVerified: true);
        var properties = new AuthenticationProperties
        {
            Items =
            {
                [ExternalSignInEndpoints.ReturnUrlItem] = AuthEndpoints.LocalUrl(
                    form["returnUrl"].ToString()
                ),
            },
        };
        await http.SignInAsync(
            ProviderSetup.ExternalScheme,
            new ClaimsPrincipal(account.ToIdentity(ProviderSetup.ExternalScheme)),
            properties
        );
        return Results.Redirect($"/auth/external/{provider}/done");
    }
}
