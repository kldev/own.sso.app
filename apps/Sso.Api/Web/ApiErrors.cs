using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;
using Sso.ApiClient.Generated.Models;

namespace Sso.Api.Web;

/// <summary>
/// The user's message for a failed API call - always saying what is wrong, never only "the request is not valid": a
/// rule's own sentence (every refusal carries a code and its sentence, ADR 0008); a malformed field as
/// "field - reason"; anything else with the server's detail and the HTTP status.
/// </summary>
public static class ApiErrors
{
    private const string SomethingWentWrong = "Something went wrong. Try again.";

    public static string Describe(Exception ex) =>
        ex switch
        {
            ProblemDetails p => Describe(p),
            ApiException { ResponseStatusCode: 401 } => "Your session has expired. Sign in again.",
            ApiException { ResponseStatusCode: 403 } => "You are not allowed to do this.",
            ApiException { ResponseStatusCode: 404 } =>
                "Not found - the record may have been deleted.",
            ApiException { ResponseStatusCode: var status } =>
                $"{SomethingWentWrong} (error {status})",
            HttpRequestException => "The server is not reachable. Try again in a moment.",
            _ => SomethingWentWrong,
        };

    public static bool IsSessionExpired(Exception ex) =>
        ex is ApiException { ResponseStatusCode: 401 };

    private static string Describe(ProblemDetails p)
    {
        var fields = FieldErrors(p);
        if (fields.Count > 0)
            return "Check: "
                + string.Join("; ", fields.Select(f => $"{f.Field} - {Reason(f.Message)}"))
                + ".";

        // A rule's sentence, written for people.
        if (p.Code is not null && !string.IsNullOrWhiteSpace(p.Detail))
            return p.Detail;

        var text = p.Detail ?? p.Title;
        return string.IsNullOrWhiteSpace(text)
            ? $"{SomethingWentWrong} (error {p.Status ?? p.ResponseStatusCode})"
            : $"{text} (error {p.Status ?? p.ResponseStatusCode})";
    }

    /// <summary>The problem's "errors" object: field name -> messages (a mistyped or unknown field).</summary>
    private static List<(string Field, string Message)> FieldErrors(ProblemDetails p) =>
        p.Errors?.AdditionalData is not { Count: > 0 } errors
            ? []
            : [.. errors.SelectMany(e => Texts(e.Value).Select(m => (e.Key, m)))];

    private static string Reason(string message) =>
        message switch
        {
            "Required." => "required",
            _ when message.StartsWith("Expected ", StringComparison.Ordinal) => "wrong type ("
                + message
                + ")",
            "Unknown field." => "unknown field",
            _ => message,
        };

    private static IEnumerable<string> Texts(object? value) =>
        value switch
        {
            null => [],
            string s => [s],
            UntypedString s => s.GetValue() is { } text ? [text] : [],
            UntypedArray a => a.GetValue().SelectMany(Texts),
            IEnumerable<object> items => items.SelectMany(Texts),
            _ => [value.ToString() ?? ""],
        };
}
