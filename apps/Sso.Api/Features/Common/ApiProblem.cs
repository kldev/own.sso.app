using System.Text.Json;
using System.Text.Json.Serialization;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Sso.Api.Features.Common;

/// <summary>
/// The shape of every API error (RFC 9457) in the OpenAPI document - named ProblemDetails there, so the generated
/// client throws it. <see cref="Code"/> is a stable descriptive code, never a database code.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)]
public sealed record ApiProblem(
    string? Type,
    string? Title,
    int? Status,
    string? Detail,
    string? Code,
    Dictionary<string, string[]>? Errors
);

public static class Problems
{
    private static string Title(int status) =>
        status switch
        {
            400 => "The request is not valid.",
            401 => "Sign in again.",
            403 => "You cannot do this.",
            404 => "Not found.",
            409 => "Conflict with the current state.",
            503 => "Try again.",
            _ => "Server error.",
        };

    public static IResult From(RuleException rule)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = rule.Code };
        if (rule.Errors is { Count: > 0 } errors)
            extensions["errors"] = errors;
        return Results.Problem(
            statusCode: rule.Status,
            title: Title(rule.Status),
            detail: rule.Message,
            extensions: extensions
        );
    }

    /// <summary>
    /// A rule of the schema the C# code does not check itself (a CHECK, NOT NULL, a foreign key, a unique index, a
    /// type): the status and a sentence with the constraint or column name - never a value. Null for anything that
    /// is not the request's fault (a missing permission, anything unexpected).
    /// </summary>
    public static RuleException? FromDatabase(PostgresException ex) =>
        ex.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation => Rule.Conflict(
                "database.unique",
                $"This value is already used ({ex.ConstraintName})."
            ),
            PostgresErrorCodes.ExclusionViolation => Rule.Conflict(
                "database.overlap",
                $"It overlaps with an existing record ({ex.ConstraintName})."
            ),
            PostgresErrorCodes.NotNullViolation => Rule.Invalid(
                "database.required",
                $"A required value is missing: {ex.ColumnName ?? "a field"}."
            ),
            PostgresErrorCodes.CheckViolation => Rule.Invalid(
                "database.check",
                $"A value breaks the rule {ex.ConstraintName ?? "of the table"}."
            ),
            PostgresErrorCodes.ForeignKeyViolation => Rule.Invalid(
                "database.reference",
                $"The referenced record does not exist or is still in use ({ex.ConstraintName})."
            ),
            PostgresErrorCodes.StringDataRightTruncation => Rule.Invalid(
                "database.too_long",
                "A text is longer than allowed."
            ),
            PostgresErrorCodes.NumericValueOutOfRange => Rule.Invalid(
                "database.out_of_range",
                "A number is out of the allowed range."
            ),
            PostgresErrorCodes.InvalidTextRepresentation
            or PostgresErrorCodes.CharacterNotInRepertoire
            or PostgresErrorCodes.InvalidDatetimeFormat
            or PostgresErrorCodes.DatetimeFieldOverflow => Rule.Invalid(
                "database.format",
                "A value has the wrong format."
            ),
            PostgresErrorCodes.DeadlockDetected
            or PostgresErrorCodes.SerializationFailure
            or PostgresErrorCodes.LockNotAvailable
            or PostgresErrorCodes.QueryCanceled => new RuleException(
                503,
                "database.busy",
                "The database is busy - try again."
            ),
            _ => null,
        };

    /// <summary>A body that is not JSON or has a field of the wrong type: 400 with the field in errors.</summary>
    public static RuleException FromJson(JsonException ex)
    {
        var field = ex.Path is { Length: > 2 } path ? path.TrimStart('$', '.') : "$";
        var message = ex.Message.Contains("could not be mapped", StringComparison.Ordinal)
            ? "Unknown field."
            : $"Expected a valid value for {field}.";
        return new RuleException(400, "request.invalid_json", $"{field}: {message}")
        {
            Errors = new Dictionary<string, string[]> { [field] = [message] },
        };
    }
}

/// <summary>
/// Turns the refusals of the API into problems (ADR 0008): a rule, a stale row version, a rule of the schema, a body
/// of the wrong shape. Handled exceptions are not logged as errors (.NET 10's default).
/// </summary>
internal sealed class ApiExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext http,
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        var rule = exception switch
        {
            RuleException r => r,
            DbUpdateConcurrencyException => Rule.Conflict(
                "concurrency",
                "Someone changed this record in the meantime - reload and try again."
            ),
            DbUpdateException { InnerException: PostgresException pg } => Problems.FromDatabase(pg),
            PostgresException pg => Problems.FromDatabase(pg),
            BadHttpRequestException { InnerException: JsonException json } => Problems.FromJson(
                json
            ),
            BadHttpRequestException bad => Rule.Invalid("request.invalid", bad.Message),
            _ => null,
        };
        if (rule is null)
            return false;
        await Problems.From(rule).ExecuteAsync(http);
        return true;
    }
}

/// <summary>
/// JSON of the API: request bodies refuse unknown fields (a typo never passes silently) and numbers are numbers (no
/// "1" for 1 - the OpenAPI document says integer, not "integer or string").
/// </summary>
internal static class ApiJson
{
    public static void Configure(JsonSerializerOptions options)
    {
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.NumberHandling = JsonNumberHandling.Strict;
    }
}
