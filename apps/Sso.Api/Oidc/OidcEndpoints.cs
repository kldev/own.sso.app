using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Sso.Api.Auth;

namespace Sso.Api.Oidc;

/// <summary>
/// The OpenID Connect provider for the team's applications - our own narrow profile, no library (ADR 0012): OIDC Core 1.0
/// with the authorization code flow only, PKCE S256 (RFC 7636, required unless the client is the conformance test's),
/// RFC 9700's rules (exact redirect URIs, single-use short codes, rotating refresh tokens) and RFC 9207's iss in the
/// response. Discovery, JWKS, authorize, token, userinfo - nothing else, and anything else is refused with the
/// protocol's error. The state lives in the database (oidc.*, OidcStore); tokens are signed with the framework's JWT handler.
/// </summary>
public static class OidcEndpoints
{
    public const string AuthorizePath = "/connect/authorize";
    private const string TokenPath = "/connect/token";
    private const string UserInfoPath = "/connect/userinfo";

    private static readonly string[] SupportedScopes =
    [
        "openid",
        "profile",
        "email",
        "offline_access",
    ];

    public static IEndpointRouteBuilder MapOidc(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/openid-configuration", Discovery).AllowAnonymous();
        app.MapGet(
                "/.well-known/jwks.json",
                (SigningKeys keys) => Results.Json(new { keys = keys.Jwks() })
            )
            .AllowAnonymous();
        app.MapMethods(AuthorizePath, ["GET", "POST"], AuthorizeAsync)
            .AllowAnonymous()
            .DisableAntiforgery();
        app.MapPost(TokenPath, TokenAsync).AllowAnonymous().DisableAntiforgery();
        app.MapMethods(UserInfoPath, ["GET", "POST"], UserInfoAsync)
            .AllowAnonymous()
            .DisableAntiforgery();
        return app;
    }

    private static IResult Discovery(IOptions<OidcOptions> options)
    {
        var issuer = options.Value.Issuer;
        return Results.Json(
            new Dictionary<string, object>
            {
                ["issuer"] = issuer,
                ["authorization_endpoint"] = issuer + AuthorizePath,
                ["token_endpoint"] = issuer + TokenPath,
                ["userinfo_endpoint"] = issuer + UserInfoPath,
                ["jwks_uri"] = issuer + "/.well-known/jwks.json",
                ["scopes_supported"] = SupportedScopes,
                ["response_types_supported"] = new[] { "code" },
                ["response_modes_supported"] = new[] { "query", "form_post" },
                ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
                ["subject_types_supported"] = new[] { "public" },
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                ["token_endpoint_auth_methods_supported"] = new[]
                {
                    "client_secret_basic",
                    "client_secret_post",
                },
                ["code_challenge_methods_supported"] = new[] { "S256" },
                ["claims_supported"] = new[]
                {
                    "sub",
                    "iss",
                    "aud",
                    "exp",
                    "iat",
                    "auth_time",
                    "nonce",
                    "sid",
                    "preferred_username",
                    "name",
                    "email",
                    "email_verified",
                },
                ["authorization_response_iss_parameter_supported"] = true,
                ["claims_parameter_supported"] = false,
                ["request_parameter_supported"] = false,
                ["request_uri_parameter_supported"] = false,
            }
        );
    }

    // ---- authorize ----------------------------------------------------------------------------------------------

    private static async Task<IResult> AuthorizeAsync(
        HttpContext http,
        ClientStore clients,
        SsoSessions sessions,
        ProtocolLog db,
        OidcStore store,
        IOptions<OidcOptions> options,
        IOptions<SsoSessionOptions> sessionOptions,
        CancellationToken ct
    )
    {
        var request = RequestInfo.From(http);
        var p = await ParametersAsync(http, ct);
        if (p.Duplicate is { } duplicate)
            return await BadRequestAsync(
                db,
                "authorize",
                "invalid_request",
                $"{duplicate} is given more than once.",
                p.Get("client_id"),
                request,
                ct
            );

        var client = await clients.FindAsync(p.Get("client_id"), ct);
        if (client is not { IsActive: true })
            return await BadRequestAsync(
                db,
                "authorize",
                "invalid_client",
                "Unknown or inactive client_id.",
                p.Get("client_id"),
                request,
                ct
            );
        var redirectUri = p.Get("redirect_uri");
        // Never redirect to an address that is not registered exactly (RFC 9700 4.1.3) - the error stays here.
        if (!client.AllowsRedirect(redirectUri))
            return await BadRequestAsync(
                db,
                "authorize",
                "invalid_request",
                "redirect_uri is not registered for this client.",
                client.ClientId,
                request,
                ct
            );

        var state = p.Get("state");
        var issuer = options.Value.Issuer;
        // form_post (OAuth 2.0 Form Post Response Mode) is what ASP.NET Core's handler asks for by default: the answer
        // never sits in the browser's history or in a log of addresses.
        var formPost = p.Get("response_mode") == "form_post";
        IResult Respond(params (string Name, string? Value)[] values) =>
            formPost
                ? FormPost(redirectUri!, values)
                : Results.Redirect(Append(redirectUri!, values));
        IResult Error(string error, string description) =>
            Respond(
                ("error", error),
                ("error_description", description),
                ("state", state),
                ("iss", issuer)
            );
        async Task<IResult> RefuseAsync(string error, string description)
        {
            await RecordAsync(db, "authorize", error, client.ClientId, request, ct);
            return Error(error, description);
        }

        if (p.Get("request") is not null)
            return await RefuseAsync("request_not_supported", "Request objects are not supported.");
        if (p.Get("request_uri") is not null)
            return await RefuseAsync("request_uri_not_supported", "request_uri is not supported.");
        if (p.Get("response_type") != "code")
            return await RefuseAsync(
                "unsupported_response_type",
                "Only the authorization code flow (response_type=code) is supported."
            );
        if (p.Get("response_mode") is { } and not ("query" or "form_post"))
            return await RefuseAsync("invalid_request", "response_mode is query or form_post.");
        var scopes = (p.Get("scope") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains("openid"))
            return await RefuseAsync("invalid_scope", "The scope must contain openid.");
        var granted = SupportedScopes.Where(scopes.Contains).ToArray();

        var challenge = p.Get("code_challenge");
        var method = p.Get("code_challenge_method");
        if (challenge is null && client.RequirePkce)
            return await RefuseAsync(
                "invalid_request",
                "PKCE is required: code_challenge with code_challenge_method=S256."
            );
        if (challenge is not null && (method != "S256" || !IsPkceValue(challenge, exactLength: 43)))
            return await RefuseAsync(
                "invalid_request",
                "code_challenge_method must be S256 with a 43-character challenge."
            );

        var prompts = (p.Get("prompt") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (prompts.Contains("none") && prompts.Length > 1)
            return await RefuseAsync(
                "invalid_request",
                "prompt=none cannot be combined with other values."
            );
        int? maxAge = null;
        if (p.Get("max_age") is { } maxAgeText)
        {
            if (!int.TryParse(maxAgeText, out var seconds) || seconds < 0)
                return await RefuseAsync("invalid_request", "max_age must be a number of seconds.");
            maxAge = seconds;
        }

        // The browser's SSO session, if it may sign in to this application: an allowed provider, fresh enough.
        var token = http.Request.Cookies[sessionOptions.Value.CookieName];
        var user = string.IsNullOrEmpty(token)
            ? null
            : await sessions.TouchAsync(token, client.ClientId, request, ct);
        var fresh =
            user is not null && DateTimeOffset.UtcNow - user.AuthTime < TimeSpan.FromMinutes(2);
        var usable =
            user is not null
            && client.Providers.Contains(user.Provider)
            && (!prompts.Contains("login") || fresh)
            && (
                maxAge is null
                || DateTimeOffset.UtcNow - user.AuthTime <= TimeSpan.FromSeconds(maxAge.Value)
            );
        if (!usable)
        {
            if (prompts.Contains("none"))
                return await RefuseAsync("login_required", "The person has to sign in.");
            // Back here after the sign-in, without prompt=login (it is done then) - no loop.
            var again =
                AuthorizePath
                + QueryString.Create(
                    p.All.Where(kv => kv.Key != "prompt" || !prompts.Contains("login"))
                        .Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value))
                );
            return Results.Redirect($"/sign-in?returnUrl={Uri.EscapeDataString(again)}");
        }

        var code = Secrets.NewToken();
        await store.IssueCodeAsync(
            Secrets.Hash(code),
            client.ClientId,
            user!.SessionId,
            redirectUri!,
            challenge,
            p.Get("nonce"),
            granted,
            options.Value.CodeLifetime,
            request,
            ct
        );
        return Respond(("code", code), ("state", state), ("iss", issuer));
    }

    // ---- token --------------------------------------------------------------------------------------------------

    private static async Task<IResult> TokenAsync(
        HttpContext http,
        ClientStore clients,
        ProtocolLog db,
        OidcStore store,
        SigningKeys keys,
        IOptions<OidcOptions> options,
        CancellationToken ct
    )
    {
        NoStore(http);
        var request = RequestInfo.From(http);
        if (!http.Request.HasFormContentType)
            return TokenError(
                "invalid_request",
                "The body must be application/x-www-form-urlencoded."
            );
        var p = await ParametersAsync(http, ct);
        if (p.Duplicate is { } duplicate)
            return TokenError("invalid_request", $"{duplicate} is given more than once.");

        // Client authentication: HTTP Basic (RFC 6749 2.3.1, form-encoded id and secret) or the body - not both.
        var (clientId, secret, basic) = ClientCredentials(http, p);
        if (
            basic is null
            && p.Get("client_secret") is not null
            && http.Request.Headers.Authorization.Count > 0
        )
            return TokenError("invalid_request", "Use one way of client authentication.");
        var client = await clients.FindAsync(clientId, ct);
        if (
            client is not { IsActive: true }
            || secret is null
            || !Secrets.FixedTimeEquals(Secrets.HashText(secret), client.SecretHash)
        )
        {
            await RecordAsync(db, "token", "invalid_client", clientId, request, ct);
            if (basic == true)
                http.Response.Headers.WWWAuthenticate = "Basic realm=\"great-sso\"";
            return Results.Json(
                new
                {
                    error = "invalid_client",
                    error_description = "Client authentication failed.",
                },
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        var grant = p.Get("grant_type");
        return grant switch
        {
            "authorization_code" => await CodeGrantAsync(
                p,
                client,
                store,
                keys,
                options.Value,
                request,
                ct
            ),
            "refresh_token" => await RefreshGrantAsync(
                p,
                client,
                store,
                keys,
                options.Value,
                request,
                ct
            ),
            null => TokenError("invalid_request", "grant_type is missing."),
            _ => TokenError(
                "unsupported_grant_type",
                "Only authorization_code and refresh_token are supported."
            ),
        };
    }

    private static async Task<IResult> CodeGrantAsync(
        Parameters p,
        OidcClient client,
        OidcStore store,
        SigningKeys keys,
        OidcOptions options,
        RequestInfo request,
        CancellationToken ct
    )
    {
        var code = p.Get("code");
        if (string.IsNullOrEmpty(code))
            return TokenError("invalid_request", "code is missing.");
        var verifier = p.Get("code_verifier");
        if (verifier is not null && !IsPkceValue(verifier, exactLength: null))
            return TokenError("invalid_grant", "code_verifier is not valid (RFC 7636 4.1).");

        var refresh = Secrets.NewToken();
        var subject = await store.RedeemCodeAsync(
            Secrets.Hash(code),
            client.ClientId,
            p.Get("redirect_uri"),
            verifier is null ? null : Secrets.S256(verifier),
            Secrets.Hash(refresh),
            request,
            ct
        );
        if (subject is null)
            return TokenError(
                "invalid_grant",
                "The code is invalid, used, expired or does not match."
            );
        return Tokens(
            subject,
            client,
            subject.Nonce,
            subject.RefreshIssued ? refresh : null,
            keys,
            options
        );
    }

    private static async Task<IResult> RefreshGrantAsync(
        Parameters p,
        OidcClient client,
        OidcStore store,
        SigningKeys keys,
        OidcOptions options,
        RequestInfo request,
        CancellationToken ct
    )
    {
        var old = p.Get("refresh_token");
        if (string.IsNullOrEmpty(old))
            return TokenError("invalid_request", "refresh_token is missing.");
        var next = Secrets.NewToken();
        var subject = await store.RotateRefreshAsync(
            Secrets.Hash(old),
            Secrets.Hash(next),
            client.ClientId,
            request,
            ct
        );
        if (subject is null)
            return TokenError(
                "invalid_grant",
                "The refresh token is invalid, used or revoked, or the session ended."
            );
        return Tokens(subject, client, null, next, keys, options);
    }

    private static IResult Tokens(
        TokenSubject person,
        OidcClient client,
        string? nonce,
        string? refreshToken,
        SigningKeys keys,
        OidcOptions options
    )
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(options.TokenLifetime);
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        var scope = string.Join(' ', person.Scopes);

        var access = handler.CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = options.Issuer,
                Audience = client.ClientId,
                IssuedAt = now.UtcDateTime,
                NotBefore = now.UtcDateTime,
                Expires = expires.UtcDateTime,
                SigningCredentials = keys.Current,
                TokenType = "at+jwt",
                Claims = new Dictionary<string, object>
                {
                    ["sub"] = person.UserId.ToString(),
                    ["client_id"] = client.ClientId,
                    ["sid"] = person.SessionId.ToString(),
                    ["scope"] = scope,
                    ["jti"] = Guid.CreateVersion7().ToString(),
                },
            }
        );

        // The person's identity: sub always; username and name with profile, the work e-mail with email. Never roles -
        // a role is the application's own (ADR 0012).
        var claims = new Dictionary<string, object>
        {
            ["sub"] = person.UserId.ToString(),
            ["auth_time"] = person.AuthTime.ToUnixTimeSeconds(),
            ["sid"] = person.SessionId.ToString(),
            ["at_hash"] = AtHash(access),
        };
        if (nonce is not null)
            claims["nonce"] = nonce;
        AddIdentityClaims(claims, person.Scopes, person.Username, person.DisplayName, person.Email);
        var id = handler.CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = options.Issuer,
                Audience = client.ClientId,
                IssuedAt = now.UtcDateTime,
                Expires = expires.UtcDateTime,
                SigningCredentials = keys.Current,
                Claims = claims,
            }
        );

        var body = new Dictionary<string, object>
        {
            ["access_token"] = access,
            ["token_type"] = "Bearer",
            ["expires_in"] = (int)options.TokenLifetime.TotalSeconds,
            ["id_token"] = id,
            ["scope"] = scope,
        };
        if (refreshToken is not null)
            body["refresh_token"] = refreshToken;
        return Results.Json(body);
    }

    private static void AddIdentityClaims(
        IDictionary<string, object> claims,
        IReadOnlyCollection<string> scopes,
        string username,
        string displayName,
        string email
    )
    {
        if (scopes.Contains("profile"))
        {
            claims["preferred_username"] = username;
            claims["name"] = displayName;
        }
        if (scopes.Contains("email"))
        {
            claims["email"] = email;
            // The work e-mail was entered by the SSO admin, not by the person: verified by them.
            claims["email_verified"] = true;
        }
    }

    /// <summary>OIDC Core 3.1.3.6: base64url of the left half of SHA-256 of the access token's ASCII bytes.</summary>
    private static string AtHash(string accessToken)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(accessToken));
        return Base64UrlEncoder.Encode(hash[..(hash.Length / 2)]);
    }

    // ---- userinfo -----------------------------------------------------------------------------------------------

    private static async Task<IResult> UserInfoAsync(
        HttpContext http,
        OidcStore store,
        SigningKeys keys,
        IOptions<OidcOptions> options,
        CancellationToken ct
    )
    {
        NoStore(http);
        var header = http.Request.Headers.Authorization.ToString();
        string? token = header.StartsWith("Bearer ", StringComparison.Ordinal)
            ? header[7..].Trim()
            : null;
        if (token is null && http.Request.HasFormContentType)
            token = (await http.Request.ReadFormAsync(ct))["access_token"].ToString()
                is { Length: > 0 } t
                ? t
                : null;
        if (token is null)
            return InvalidToken(http, "No access token.");

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(
            token,
            new TokenValidationParameters
            {
                ValidIssuer = options.Value.Issuer,
                ValidateAudience = false,
                IssuerSigningKeys = keys.ValidationKeys,
                ValidTypes = ["at+jwt"],
                ClockSkew = TimeSpan.FromSeconds(30),
            }
        );
        if (
            !result.IsValid
            || !Guid.TryParse(Claim(result.Claims, "sub"), out var userId)
            || !Guid.TryParse(Claim(result.Claims, "sid"), out var sessionId)
        )
            return InvalidToken(http, "The access token is not valid.");

        var person = await store.UserInfoAsync(userId, sessionId, ct);
        if (person is null)
            return InvalidToken(http, "The session or the person is no longer active.");
        var claims = new Dictionary<string, object> { ["sub"] = userId.ToString() };
        var scopes = (Claim(result.Claims, "scope") ?? "").Split(' ');
        AddIdentityClaims(claims, scopes, person.Username, person.DisplayName, person.Email);
        return Results.Json(claims);
    }

    private static string? Claim(IDictionary<string, object> claims, string name) =>
        claims.TryGetValue(name, out var value) ? value as string : null;

    private static IResult InvalidToken(HttpContext http, string description)
    {
        http.Response.Headers.WWWAuthenticate =
            $"Bearer error=\"invalid_token\", error_description=\"{description}\"";
        return Results.StatusCode(StatusCodes.Status401Unauthorized);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    /// <summary>The request's parameters (query, or the form of a POST), the first repeated name if any.</summary>
    private sealed record Parameters(IReadOnlyDictionary<string, string> All, string? Duplicate)
    {
        public string? Get(string name) =>
            All.TryGetValue(name, out var v) && v.Length > 0 ? v : null;
    }

    private static async Task<Parameters> ParametersAsync(HttpContext http, CancellationToken ct)
    {
        IEnumerable<KeyValuePair<string, StringValues>> source =
            HttpMethods.IsPost(http.Request.Method) && http.Request.HasFormContentType
                ? await http.Request.ReadFormAsync(ct)
                : http.Request.Query;
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        string? duplicate = null;
        foreach (var (key, values) in source)
        {
            if (values.Count > 1)
                duplicate ??= key;
            all[key] = values.ToString();
        }
        return new Parameters(all, duplicate);
    }

    private static (string? ClientId, string? Secret, bool? Basic) ClientCredentials(
        HttpContext http,
        Parameters p
    )
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                var colon = decoded.IndexOf(':');
                if (colon > 0)
                    return (
                        Uri.UnescapeDataString(decoded[..colon].Replace('+', ' ')),
                        Uri.UnescapeDataString(decoded[(colon + 1)..].Replace('+', ' ')),
                        true
                    );
            }
            catch (FormatException) { }
            return (null, null, true);
        }
        return (
            p.Get("client_id"),
            p.Get("client_secret"),
            p.Get("client_secret") is null ? null : false
        );
    }

    /// <summary>RFC 7636 4.1: 43-128 characters of [A-Z a-z 0-9 - . _ ~]; a S256 challenge is exactly 43.</summary>
    private static bool IsPkceValue(string value, int? exactLength) =>
        (exactLength is { } n ? value.Length == n : value.Length is >= 43 and <= 128)
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~');

    private static string Append(string uri, IEnumerable<(string Name, string? Value)> values)
    {
        var query = string.Join(
            '&',
            values
                .Where(v => v.Value is not null)
                .Select(v => $"{v.Name}={Uri.EscapeDataString(v.Value!)}")
        );
        return uri + (uri.Contains('?') ? "&" : "?") + query;
    }

    /// <summary>A page that posts the answer to the application at once (the button only without JavaScript).</summary>
    private static IResult FormPost(
        string redirectUri,
        IEnumerable<(string Name, string? Value)> values
    )
    {
        var enc = System.Text.Encodings.Web.HtmlEncoder.Default;
        var inputs = string.Concat(
            values
                .Where(v => v.Value is not null)
                .Select(v =>
                    $"<input type=\"hidden\" name=\"{enc.Encode(v.Name)}\" value=\"{enc.Encode(v.Value!)}\"/>"
                )
        );
        var html =
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Great SSO</title></head>"
            + "<body onload=\"document.forms[0].submit()\">"
            + $"<form method=\"post\" action=\"{enc.Encode(redirectUri)}\">{inputs}"
            + "<noscript><button type=\"submit\">Continue</button></noscript></form></body></html>";
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static void NoStore(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
    }

    private static IResult TokenError(string error, string description) =>
        Results.Json(
            new { error, error_description = description },
            statusCode: StatusCodes.Status400BadRequest
        );

    private static async Task<IResult> BadRequestAsync(
        ProtocolLog db,
        string type,
        string error,
        string description,
        string? clientId,
        RequestInfo request,
        CancellationToken ct
    )
    {
        await RecordAsync(db, type, error, clientId, request, ct);
        return Results.Json(
            new { error, error_description = description },
            statusCode: StatusCodes.Status400BadRequest
        );
    }

    private static Task RecordAsync(
        ProtocolLog db,
        string type,
        string reason,
        string? clientId,
        RequestInfo request,
        CancellationToken ct
    ) => db.RecordAsync(type, reason, clientId, null, request, ct);
}
