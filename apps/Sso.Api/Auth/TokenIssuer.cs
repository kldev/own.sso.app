using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Sso.Api.Auth;

/// <summary>
/// The panel's internal access token: the Blazor circuit sends it to the contract endpoints as a bearer. Signed with
/// the host's own HMAC key and accepted only by this host - the applications' tokens are something else (Oidc/).
/// </summary>
public sealed class TokenIssuer(IOptions<JwtOptions> options, TimeProvider time)
{
    public (string Token, DateTimeOffset ExpiresAt) Issue(SessionUser user)
    {
        var jwt = options.Value;
        var now = time.GetUtcNow();
        var expiresAt = now.Add(jwt.Lifetime);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(
                AuthSetup.SigningKey(jwt),
                SecurityAlgorithms.HmacSha256
            ),
            Subject = new ClaimsIdentity([
                new Claim(SessionClaims.UserId, user.UserId.ToString()),
                new Claim(SessionClaims.Name, user.DisplayName),
                new Claim(SessionClaims.Username, user.Username),
                new Claim(SessionClaims.Role, user.Role),
            ]),
        };
        return (new JsonWebTokenHandler().CreateToken(descriptor), expiresAt);
    }
}
