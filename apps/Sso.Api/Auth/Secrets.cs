using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Sso.Api.Auth;

/// <summary>
/// Random values and their hashes - only the framework's primitives, no cryptography of our own (ADR 0012). Session
/// tokens, authorization codes, refresh tokens and client secrets are 256 random bits; the database keeps SHA-256 of
/// them (a hash of a random value needs no salt or slow hashing - it is not a password).
/// </summary>
public static class Secrets
{
    /// <summary>A new random value: 32 bytes as base64url (43 characters).</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256 of the value's UTF-8 bytes - what the database stores for tokens and codes.</summary>
    public static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    /// <summary>base64url(SHA-256(value)) - the form of an application's secret hash.</summary>
    public static string HashText(string value) => Base64Url.EncodeToString(Hash(value));

    /// <summary>The PKCE S256 challenge of a verifier (RFC 7636 4.6): base64url(SHA-256(ASCII(verifier))).</summary>
    public static string S256(string verifier) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>Compares two strings in time that does not depend on where they differ.</summary>
    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a),
            Encoding.UTF8.GetBytes(b)
        );
}
