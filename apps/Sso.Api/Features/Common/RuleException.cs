namespace Sso.Api.Features.Common;

/// <summary>
/// A rule of Great SSO that refuses a request (ADR 0008): the HTTP status, a stable descriptive code (e.g.
/// people.username_invalid) and the sentence a person reads. The API answers it as a problem; MCP as a tool error.
/// </summary>
public sealed class RuleException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;

    public string Code { get; } = code;

    /// <summary>Field name -> messages, for a request that is malformed (wrong type, unknown field).</summary>
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }
}

/// <summary>The refusals by status.</summary>
public static class Rule
{
    public static RuleException Invalid(string code, string message) => new(400, code, message);

    public static RuleException Unauthorized(string code, string message) =>
        new(401, code, message);

    public static RuleException Forbidden(string code, string message) => new(403, code, message);

    public static RuleException NotFound(string code, string message) => new(404, code, message);

    public static RuleException Conflict(string code, string message) => new(409, code, message);
}
