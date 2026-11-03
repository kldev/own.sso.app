using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Sso.Tests;

/// <summary>Small helpers over the API's JSON: read a list, call an operation, check a problem's code.</summary>
internal static class TestApi
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<JsonArray> ListAsync(this HttpClient client, string path)
    {
        var response = await client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        return page!["items"]!.AsArray();
    }

    public static async Task<JsonObject> CallAsync(this HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync(path, body, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {text}");
        return text.Length == 0 ? [] : JsonNode.Parse(text)!.AsObject();
    }

    public static async Task AssertProblemAsync(
        this HttpClient client,
        string path,
        object body,
        HttpStatusCode status,
        string code
    )
    {
        var response = await client.PostAsJsonAsync(path, body, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(
            response.StatusCode == status,
            $"{path}: expected {status}, got {(int)response.StatusCode} {text}"
        );
        Assert.Equal(code, JsonNode.Parse(text)!["code"]!.GetValue<string>());
    }

    public static string Str(this JsonNode? node, string name) => node![name]!.GetValue<string>();

    public static long RowVersion(this JsonObject result) => result["rowVersion"]!.GetValue<long>();
}
