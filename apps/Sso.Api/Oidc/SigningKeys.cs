using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Sso.Api.Oidc;

/// <summary>
/// The provider's signing keys: RSA 2048 in PEM files of Oidc:KeysPath, named by their creation time. The newest signs
/// (RS256), every key is in the JWKS - a rotation is a new file; an old key is removed once no token signed with it
/// can still be valid. The key id (kid) is the key's JWK thumbprint (RFC 7638).
/// </summary>
public sealed class SigningKeys
{
    private readonly IReadOnlyList<RsaSecurityKey> _keys;

    public SigningKeys(IOptions<OidcOptions> options, ILogger<SigningKeys> logger)
    {
        var directory = new DirectoryInfo(options.Value.KeysPath);
        directory.Create();
        var files = directory
            .GetFiles("*.pem")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            using var rsa = RSA.Create(2048);
            var path = Path.Combine(
                directory.FullName,
                DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".pem"
            );
            File.WriteAllText(path, rsa.ExportPkcs8PrivateKeyPem());
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            logger.LogInformation("Made the first OIDC signing key {File}", Path.GetFileName(path));
            files = [new FileInfo(path)];
        }
        _keys = files.Select(Load).ToList();
    }

    public SigningCredentials Current => new(_keys[0], SecurityAlgorithms.RsaSha256);

    public IEnumerable<SecurityKey> ValidationKeys => _keys;

    /// <summary>The public part of every key, for /.well-known/jwks.json.</summary>
    public IEnumerable<object> Jwks() =>
        _keys.Select(key =>
        {
            var p = key.Rsa.ExportParameters(false);
            return new
            {
                kty = "RSA",
                use = "sig",
                alg = "RS256",
                kid = key.KeyId,
                n = Base64UrlEncoder.Encode(p.Modulus),
                e = Base64UrlEncoder.Encode(p.Exponent),
            };
        });

    private static RsaSecurityKey Load(FileInfo file)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(file.FullName));
        var key = new RsaSecurityKey(rsa);
        key.KeyId = Base64UrlEncoder.Encode(key.ComputeJwkThumbprint());
        return key;
    }
}
