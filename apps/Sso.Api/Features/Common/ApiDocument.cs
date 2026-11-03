using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;

namespace Sso.Api.Features.Common;

/// <summary>
/// Names of the schemas in the OpenAPI document - stable names the generated client's classes are made from:
/// <c>Page&lt;PeoplePeopleRow&gt;</c> is PeoplePeoplePage, the error shape is ProblemDetails.
/// </summary>
public static class ApiDocument
{
    public static string? SchemaName(JsonTypeInfo type)
    {
        if (type.Type == typeof(ApiProblem))
            return "ProblemDetails";
        if (type.Type.IsGenericType && type.Type.GetGenericTypeDefinition() == typeof(ListPage<>))
        {
            var row = type.Type.GetGenericArguments()[0].Name;
            return (row.EndsWith("Row", StringComparison.Ordinal) ? row[..^3] : row) + "Page";
        }
        return OpenApiOptions.CreateDefaultSchemaReferenceId(type);
    }
}
