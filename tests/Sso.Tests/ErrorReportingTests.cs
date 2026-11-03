using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Kiota.Abstractions.Serialization;
using Npgsql;
using Sso.Api.Features.Common;
using Sso.Api.Web;
using Sso.ApiClient.Generated.Models;

namespace Sso.Tests;

/// <summary>
/// A rejected request always says what is wrong: the field, the rule or the rule's own sentence with its code - never
/// only "the request is not valid" (Problems on the server, ApiErrors on the page). Never a database code.
/// </summary>
[Trait("verify", "web")]
[Collection(ApiCollection.Name)]
public sealed class ErrorReportingTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_omitted_field_reaches_the_rule_which_says_what_it_needs()
    {
        // Kiota leaves null fields out of the body: omitted = null, the rule's own sentence explains it.
        var admin = await api.AsAsync(ApiFixture.Admin);
        var response = await admin.PostAsJsonAsync(
            "/api/people/person-create",
            new
            {
                id = Guid.CreateVersion7(),
                username = "someone",
                email = "someone@test.example",
                role = "member",
                githubEmail = "someone@github.example",
            },
            Ct
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal("people.name_invalid", problem!["code"]!.GetValue<string>());
        Assert.Contains("name", problem["detail"]!.GetValue<string>());
        Assert.Null(problem["dbError"]);
    }

    [Fact]
    public async Task A_field_of_the_wrong_type_is_named_in_the_detail_and_in_errors()
    {
        var admin = await api.AsAsync(ApiFixture.Admin);
        var response = await admin.PostAsJsonAsync(
            "/api/people/person-create",
            new
            {
                id = "not-a-uuid",
                username = "someone",
                email = "someone@test.example",
                displayName = "Someone",
            },
            Ct
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.StartsWith("id: Expected", problem!["detail"]!.GetValue<string>());
        Assert.StartsWith("Expected", problem["errors"]!["id"]![0]!.GetValue<string>());
        Assert.Equal("request.invalid_json", problem["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unknown_field_is_refused_by_name()
    {
        var admin = await api.AsAsync(ApiFixture.Admin);
        var response = await admin.PostAsJsonAsync(
            "/api/activity/session-end",
            new { id = Guid.CreateVersion7(), sessionid = Guid.CreateVersion7() },
            Ct
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal("Unknown field.", problem!["errors"]!["sessionid"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task A_wrong_list_parameter_is_named()
    {
        var admin = await api.AsAsync(ApiFixture.Admin);
        var response = await admin.GetAsync("/api/people/people?createdAtFrom=yesterday", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal("list.invalid_parameter", problem!["code"]!.GetValue<string>());
        Assert.StartsWith("createdAtFrom: Expected", problem["detail"]!.GetValue<string>());
    }

    [Fact]
    public void A_rule_of_the_schema_is_described_with_its_name_never_with_values()
    {
        var check = new PostgresException(
            "new row violates check constraint",
            "ERROR",
            "ERROR",
            PostgresErrorCodes.CheckViolation,
            constraintName: "ck_users_email"
        );
        var rule = Problems.FromDatabase(check)!;
        Assert.Equal(400, rule.Status);
        Assert.Equal("database.check", rule.Code);
        Assert.Equal("A value breaks the rule ck_users_email.", rule.Message);

        var notNull = new PostgresException(
            "null value",
            "ERROR",
            "ERROR",
            PostgresErrorCodes.NotNullViolation,
            columnName: "name"
        );
        Assert.Equal("A required value is missing: name.", Problems.FromDatabase(notNull)!.Message);

        // Not the request's fault (a missing permission): no rule, a plain 500.
        Assert.Null(Problems.FromDatabase(new PostgresException("x", "ERROR", "ERROR", "42501")));
    }

    [Fact]
    public void The_page_says_which_field_or_rule_in_its_own_words()
    {
        var fields = new ProblemDetails
        {
            Title = "The request is not valid.",
            Errors = new ProblemDetails_errors
            {
                AdditionalData = new Dictionary<string, object>
                {
                    ["displayName"] = new UntypedArray([new UntypedString("Unknown field.")]),
                },
            },
        };
        Assert.Equal("Check: displayName - unknown field.", ApiErrors.Describe(fields));

        var own = new ProblemDetails
        {
            Code = "people.username_taken",
            Detail = "This username is taken.",
        };
        Assert.Equal("This username is taken.", ApiErrors.Describe(own));
    }
}
