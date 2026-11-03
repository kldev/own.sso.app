using System.Text.RegularExpressions;

namespace Sso.Api.Features.Common;

/// <summary>The normalisation and the shapes the rules check, the same everywhere (people, clients, marketing).</summary>
public static partial class Text
{
    /// <summary>Trimmed, or null for null.</summary>
    public static string? Trim(string? value) => value?.Trim();

    /// <summary>Trimmed and lower case, or null for null.</summary>
    public static string? Lower(string? value) => value?.Trim().ToLowerInvariant();

    /// <summary>Trimmed and lower case; null for null or blank.</summary>
    public static string? LowerOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    /// <summary>A username or a client id: 2-64 characters of lower case letters, digits, dot, dash, underscore.</summary>
    public static bool IsName(string? value) => value is not null && NamePattern().IsMatch(value);

    /// <summary>An e-mail of a person: something@domain.tld, up to 256 characters, no spaces.</summary>
    public static bool IsEmail(string? value) =>
        value is not null && value.Length <= 256 && EmailPattern().IsMatch(value);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,63}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}
