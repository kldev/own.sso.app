using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Sso.Api.Auth;
using Sso.Api.Oidc;

namespace Sso.Tests;

/// <summary>
/// The OpenID Connect provider (ADR 0012). Our own protocol code, so the application in these tests is the real
/// ASP.NET Core OpenID Connect handler - the one applications use - not hand-made requests; the refusals are
/// tested with requests that break one rule each.
/// </summary>
[Trait("verify", "oidc")]
[Collection(ApiCollection.Name)]
public sealed class OidcTests(ApiFixture api)
{
    private const string RedirectUri = OidcTestBrowser.AppOrigin + "/signin-oidc";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Discovery_and_jwks_describe_the_narrow_profile()
    {
        var discovery = (
            await api.Browser()
                .GetFromJsonAsync<JsonObject>("/.well-known/openid-configuration", Ct)
        )!;
        Assert.Equal("http://localhost", discovery.Str("issuer"));
        Assert.Equal("http://localhost/connect/authorize", discovery.Str("authorization_endpoint"));
        Assert.Equal("[\"code\"]", discovery["response_types_supported"]!.ToJsonString());
        Assert.Equal("[\"S256\"]", discovery["code_challenge_methods_supported"]!.ToJsonString());
        Assert.True(discovery["authorization_response_iss_parameter_supported"]!.GetValue<bool>());

        var jwks = (
            await api.Browser().GetFromJsonAsync<JsonObject>("/.well-known/jwks.json", Ct)
        )!;
        var key = Assert.Single(jwks["keys"]!.AsArray());
        Assert.Equal("RSA", key.Str("kty"));
        Assert.Null(key!["d"]); // never the private part
    }

    [Fact]
    public async Task An_application_signs_a_person_in_with_the_framework_handler_and_gets_sub_username_and_email()
    {
        var (clientId, secret) = await RegisterAsync();
        await using var app = await StartApplicationAsync(clientId, secret);
        using var browser = await SignedInBrowserAsync(app, ApiFixture.Admin);

        var (response, visited) = await browser.GoAsync(OidcTestBrowser.AppOrigin + "/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(visited, u => u.AbsolutePath == "/connect/authorize");
        var me = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.Equal(ApiFixture.AdminId.ToString(), me.Str("sub"));
        Assert.Equal(ApiFixture.Admin, me.Str("preferred_username"));
        Assert.Equal("test.admin@test.example", me.Str("email"));
        Assert.False(me.ContainsKey("role"));

        var idToken = new JsonWebToken(me.Str("id_token"));
        Assert.Equal("http://localhost", idToken.Issuer);
        Assert.Equal(clientId, Assert.Single(idToken.Audiences));
        Assert.False(idToken.TryGetClaim("role", out _));
        Assert.False(idToken.TryGetClaim("groups", out _));
        Assert.NotNull(me["refresh_token"]);
        Assert.Equal(
            1L,
            await api.ScalarAsync<long>(
                $"SELECT count(*) FROM oidc.events WHERE client_id = '{clientId}' AND event_type = 'tokens_issued'"
            )
        );
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_a_used_one_revokes_the_family()
    {
        var (clientId, secret) = await RegisterAsync();
        var (first, _) = await CodeAndTokensAsync(clientId, secret);
        var refresh1 = first.Str("refresh_token");

        var second = await TokenAsync(
            clientId,
            secret,
            new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh1 }
        );
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var refresh2 = (await second.Content.ReadFromJsonAsync<JsonObject>(Ct))!.Str(
            "refresh_token"
        );
        Assert.NotEqual(refresh1, refresh2);

        // The old one again: refused, and the new one with it.
        await AssertGrantErrorAsync(
            await TokenAsync(
                clientId,
                secret,
                new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh1 }
            ),
            "invalid_grant"
        );
        await AssertGrantErrorAsync(
            await TokenAsync(
                clientId,
                secret,
                new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh2 }
            ),
            "invalid_grant"
        );
        // Both refusals are logged as reuse: the old token, and the new one of the revoked family.
        Assert.Equal(
            2L,
            await api.ScalarAsync<long>(
                $"SELECT count(*) FROM oidc.events WHERE client_id = '{clientId}' AND reason = 'refresh_reused'"
            )
        );
    }

    [Fact]
    public async Task A_code_is_single_use_short_lived_and_bound_to_its_verifier()
    {
        var (clientId, secret) = await RegisterAsync();

        // Wrong verifier: refused, and the code is used up.
        var (code, verifier) = await CodeAsync(clientId);
        await AssertGrantErrorAsync(
            await RedeemAsync(clientId, secret, code, Secrets.NewToken()),
            "invalid_grant"
        );
        await AssertGrantErrorAsync(
            await RedeemAsync(clientId, secret, code, verifier),
            "invalid_grant"
        );

        // Used twice: the second time refused, and the refresh token of the first is revoked.
        var (tokens, again) = await CodeAndTokensAsync(clientId, secret);
        await AssertGrantErrorAsync(
            await RedeemAsync(clientId, secret, again.Code, again.Verifier),
            "invalid_grant"
        );
        await AssertGrantErrorAsync(
            await TokenAsync(
                clientId,
                secret,
                new()
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = tokens.Str("refresh_token"),
                }
            ),
            "invalid_grant"
        );

        // After 61 seconds.
        var (late, lateVerifier) = await CodeAsync(clientId);
        await api.ExecuteAsync(
            $"UPDATE oidc.authorization_codes SET expires_at = now() - interval '1 second' WHERE code_hash = '\\x{Convert.ToHexString(Secrets.Hash(late))}'"
        );
        await AssertGrantErrorAsync(
            await RedeemAsync(clientId, secret, late, lateVerifier),
            "invalid_grant"
        );

        // Another redirect_uri at the token endpoint than at authorize.
        var (other, otherVerifier) = await CodeAsync(clientId);
        await AssertGrantErrorAsync(
            await RedeemAsync(clientId, secret, other, otherVerifier, RedirectUri + "/"),
            "invalid_grant"
        );
    }

    [Fact]
    public async Task Two_parallel_redeems_of_one_code_issue_tokens_once()
    {
        var (clientId, secret) = await RegisterAsync();
        var (code, verifier) = await CodeAsync(clientId);
        var answers = await Task.WhenAll(
            RedeemAsync(clientId, secret, code, verifier),
            RedeemAsync(clientId, secret, code, verifier)
        );
        // The code is taken under a row lock: one request gets tokens, the other finds it used.
        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Two_parallel_refreshes_of_one_token_rotate_it_once()
    {
        var (clientId, secret) = await RegisterAsync();
        var (first, _) = await CodeAndTokensAsync(clientId, secret);
        var refresh = first.Str("refresh_token");
        var answers = await Task.WhenAll(
            TokenAsync(
                clientId,
                secret,
                new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh }
            ),
            TokenAsync(
                clientId,
                secret,
                new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh }
            )
        );
        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Client_authentication_and_unsupported_grants()
    {
        var (clientId, secret) = await RegisterAsync();
        var (code, verifier) = await CodeAsync(clientId);
        var wrong = await RedeemAsync(clientId, "wrong-secret", code, verifier);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(
            "invalid_client",
            (await wrong.Content.ReadFromJsonAsync<JsonObject>(Ct))!.Str("error")
        );

        // client_secret_basic works as well.
        var (code2, verifier2) = await CodeAsync(clientId);
        using var basic = new HttpRequestMessage(HttpMethod.Post, "/connect/token");
        basic.Content = new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code2,
                ["redirect_uri"] = RedirectUri,
                ["code_verifier"] = verifier2,
            }
        );
        basic.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{clientId}:{secret}"))
        );
        var ok = await api.Browser().SendAsync(basic, Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("no-store", ok.Headers.CacheControl!.ToString());

        await AssertGrantErrorAsync(
            await TokenAsync(
                clientId,
                secret,
                new() { ["grant_type"] = "password", ["username"] = "x" }
            ),
            "unsupported_grant_type"
        );
        await AssertGrantErrorAsync(
            await TokenAsync(clientId, secret, new() { ["grant_type"] = "client_credentials" }),
            "unsupported_grant_type"
        );
    }

    [Fact]
    public async Task Authorize_refuses_what_the_profile_does_not_allow()
    {
        var (clientId, _) = await RegisterAsync();
        var session = await api.AsAsync(ApiFixture.Admin);
        var challenge = Secrets.S256(Secrets.NewToken());

        // A redirect_uri not registered exactly: 400 here, never a redirect to it.
        foreach (
            var uri in new[]
            {
                RedirectUri + "/",
                RedirectUri.ToUpperInvariant(),
                RedirectUri + "?x=1",
                "https://evil.example/cb",
            }
        )
        {
            var response = await session.GetAsync(
                Authorize(clientId, redirectUri: uri, challenge: challenge),
                Ct
            );
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await session.GetAsync(Authorize("no-such-client", challenge: challenge), Ct)
            ).StatusCode
        );

        await AssertAuthorizeErrorAsync(
            session,
            Authorize(clientId, responseType: "token", challenge: challenge),
            "unsupported_response_type"
        );
        await AssertAuthorizeErrorAsync(
            session,
            Authorize(clientId, challenge: null),
            "invalid_request"
        ); // no PKCE
        await AssertAuthorizeErrorAsync(
            session,
            Authorize(clientId, challenge: challenge, method: "plain"),
            "invalid_request"
        );
        await AssertAuthorizeErrorAsync(
            session,
            Authorize(clientId, challenge: challenge, scope: "profile"),
            "invalid_scope"
        );
        await AssertAuthorizeErrorAsync(
            session,
            Authorize(clientId, challenge: challenge) + "&request=eyJ",
            "request_not_supported"
        );

        // No session and prompt=none: login_required; without prompt: the sign-in page with the way back.
        await AssertAuthorizeErrorAsync(
            api.Browser(),
            Authorize(clientId, challenge: challenge) + "&prompt=none",
            "login_required"
        );
        var signIn = await api.Browser().GetAsync(Authorize(clientId, challenge: challenge), Ct);
        Assert.StartsWith(
            "/sign-in?returnUrl=%2Fconnect%2Fauthorize%3F",
            signIn.Headers.Location!.OriginalString
        );
    }

    [Fact]
    public async Task A_session_of_a_provider_the_application_does_not_allow_is_not_enough()
    {
        var (clientId, _) = await RegisterAsync(providers: ["google"]);
        var github = await api.AsAsync(ApiFixture.Admin, provider: "github");
        var response = await github.GetAsync(
            Authorize(clientId, challenge: Secrets.S256(Secrets.NewToken())),
            Ct
        );
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/sign-in?returnUrl=", response.Headers.Location!.OriginalString);

        // The sign-in page then offers Google only.
        var page = await api.Browser()
            .GetStringAsync(response.Headers.Location!.OriginalString, Ct);
        Assert.Contains("data-provider=\"google\"", page);
        Assert.DoesNotContain("data-provider=\"github\"", page);
    }

    [Fact]
    public async Task Userinfo_needs_a_valid_token_of_an_active_person()
    {
        var (clientId, secret) = await RegisterAsync();
        var username = await api.AddPersonAsync(role: "member");
        var (tokens, _) = await CodeAndTokensAsync(clientId, secret, username);
        var access = tokens.Str("access_token");

        var client = api.Browser();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/connect/userinfo", Ct)).StatusCode
        );
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            access
        );
        var info = (await client.GetFromJsonAsync<JsonObject>("/connect/userinfo", Ct))!;
        Assert.Equal(username, info.Str("preferred_username"));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            access[..^2] + "xx"
        );
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/connect/userinfo", Ct)).StatusCode
        );

        // Deactivated: userinfo and refresh stop at once.
        await api.ExecuteAsync(
            $"UPDATE identity.users SET status = 'inactive' WHERE username = '{username}'"
        );
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            access
        );
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/connect/userinfo", Ct)).StatusCode
        );
        await AssertGrantErrorAsync(
            await TokenAsync(
                clientId,
                secret,
                new()
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = tokens.Str("refresh_token"),
                }
            ),
            "invalid_grant"
        );
    }

    [Fact]
    public void A_new_key_signs_and_the_old_one_is_still_published()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sso-keys-{Guid.NewGuid():N}");
        try
        {
            var options = Options.Create(
                new OidcOptions { Issuer = "http://localhost", KeysPath = dir }
            );
            var first = new SigningKeys(options, NullLogger<SigningKeys>.Instance);
            var oldKid = first.Current.Key.KeyId;
            using (var rsa = System.Security.Cryptography.RSA.Create(2048))
                File.WriteAllText(
                    Path.Combine(dir, "29991231235959.pem"),
                    rsa.ExportPkcs8PrivateKeyPem()
                );
            var rotated = new SigningKeys(options, NullLogger<SigningKeys>.Instance);
            Assert.NotEqual(oldKid, rotated.Current.Key.KeyId);
            Assert.Contains(rotated.ValidationKeys, k => k.KeyId == oldKid);
            Assert.Equal(2, rotated.Jwks().Count());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private async Task<(string ClientId, string Secret)> RegisterAsync(string[]? providers = null)
    {
        var clientId = $"app-{Guid.NewGuid():N}"[..20];
        var secret = Secrets.NewToken();
        await api.ExecuteAsync(
            $"""
            INSERT INTO oidc.clients (id, client_id, name, secret_hash, redirect_uris, providers, require_pkce)
            VALUES (uuidv7(), '{clientId}', 'Test application', '{Secrets.HashText(
                secret
            )}', ARRAY['{RedirectUri}'],
                    ARRAY[{string.Join(
                ", ",
                (providers ?? ["github", "google"]).Select(p => $"'{p}'")
            )}], true)
            """
        );
        return (clientId, secret);
    }

    private static string Authorize(
        string clientId,
        string? challenge,
        string redirectUri = RedirectUri,
        string responseType = "code",
        string method = "S256",
        string scope = "openid profile email offline_access"
    )
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = responseType,
            ["scope"] = scope,
            ["state"] = "st-1",
            ["nonce"] = "n-1",
        };
        if (challenge is not null)
        {
            query["code_challenge"] = challenge;
            query["code_challenge_method"] = method;
        }
        return "/connect/authorize" + QueryString.Create(query);
    }

    /// <summary>An authorization code for a signed-in person, as a browser gets it at the redirect URI.</summary>
    private async Task<(string Code, string Verifier)> CodeAsync(
        string clientId,
        string username = ApiFixture.Admin
    )
    {
        var verifier = Secrets.NewToken() + Secrets.NewToken()[..20];
        var session = await api.AsAsync(username);
        var response = await session.GetAsync(Authorize(clientId, Secrets.S256(verifier)), Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(RedirectUri + "?code=", location.ToString());
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("st-1", query["state"].ToString());
        Assert.Equal("http://localhost", query["iss"].ToString());
        return (query["code"].ToString(), verifier);
    }

    private async Task<(JsonObject Tokens, (string Code, string Verifier) Used)> CodeAndTokensAsync(
        string clientId,
        string secret,
        string username = ApiFixture.Admin
    )
    {
        var (code, verifier) = await CodeAsync(clientId, username);
        var response = await RedeemAsync(clientId, secret, code, verifier);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ((await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!, (code, verifier));
    }

    private Task<HttpResponseMessage> RedeemAsync(
        string clientId,
        string secret,
        string code,
        string verifier,
        string redirectUri = RedirectUri
    ) =>
        TokenAsync(
            clientId,
            secret,
            new()
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
            }
        );

    private Task<HttpResponseMessage> TokenAsync(
        string clientId,
        string secret,
        Dictionary<string, string> form
    )
    {
        form["client_id"] = clientId;
        form["client_secret"] = secret;
        return api.Browser().PostAsync("/connect/token", new FormUrlEncodedContent(form), Ct);
    }

    private static async Task AssertGrantErrorAsync(HttpResponseMessage response, string error)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            error,
            (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!.Str("error")
        );
    }

    private static async Task AssertAuthorizeErrorAsync(HttpClient client, string url, string error)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(RedirectUri + "?", location.ToString());
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(location.Query);
        Assert.Equal(error, query["error"].ToString());
        Assert.Equal("st-1", query["state"].ToString());
        Assert.Equal("http://localhost", query["iss"].ToString());
    }

    /// <summary>A browser with an SSO session of the person.</summary>
    private async Task<OidcTestBrowser> SignedInBrowserAsync(WebApplication app, string username)
    {
        var session = await api.AsAsync(username);
        var cookie = session.DefaultRequestHeaders.GetValues("Cookie").Single().Split('=', 2);
        var browser = new OidcTestBrowser(
            api.Server.CreateHandler(),
            app.GetTestServer().CreateHandler()
        );
        browser.SetSsoCookie(cookie[0], cookie[1]);
        return browser;
    }

    /// <summary>An application as applications are usually written: cookie + the framework's OpenID Connect handler.</summary>
    private async Task<WebApplication> StartApplicationAsync(string clientId, string secret)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(o => o.BaseAddress = new Uri(OidcTestBrowser.AppOrigin));
        var ssoHandler = api.Server.CreateHandler();
        builder
            .Services.AddAuthentication(o =>
            {
                o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie()
            .AddOpenIdConnect(o =>
            {
                o.Authority = "http://localhost";
                o.RequireHttpsMetadata = false;
                o.BackchannelHttpHandler = ssoHandler;
                o.ClientId = clientId;
                o.ClientSecret = secret;
                o.ResponseType = "code";
                o.UsePkce = true;
                o.Scope.Clear();
                foreach (var scope in new[] { "openid", "profile", "email", "offline_access" })
                    o.Scope.Add(scope);
                o.MapInboundClaims = false;
                o.SaveTokens = true;
                o.GetClaimsFromUserInfoEndpoint = true;
                o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            });
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet(
                "/me",
                async (HttpContext http) =>
                {
                    var claims = http
                        .User.Claims.GroupBy(c => c.Type)
                        .ToDictionary(g => g.Key, g => (object)g.First().Value);
                    claims["id_token"] = (await http.GetTokenAsync("id_token"))!;
                    claims["refresh_token"] = (await http.GetTokenAsync("refresh_token"))!;
                    return Results.Json(claims);
                }
            )
            .RequireAuthorization();
        await app.StartAsync(Ct);
        return app;
    }
}
