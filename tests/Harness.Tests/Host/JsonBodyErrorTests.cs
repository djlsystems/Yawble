using System.Net;
using System.Text;
using System.Text.Json;

namespace Harness.Tests.Host;

/// <summary>
/// A JSON body a route cannot bind - a wrong type, malformed JSON - is answered 400 with a
/// sentence naming the field and the type expected, through one place for every route
/// (`JsonBodyErrors`), never an empty body.
/// </summary>
public sealed class JsonBodyErrorTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_tell_with_a_numeric_causation_is_answered_400_naming_causation()
    {
        using var client = await host.PersonAsync();

        var (status, error) = await PostAsync(
            client, $"/api/teams/{host.Alpha}/containers/Manager/tell",
            """{ "instruction": "carry on", "causation": 4126 }""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("causation must be a string, as \"4126\".", error);
    }

    [Fact]
    public async Task A_tell_with_malformed_json_is_answered_400_with_a_sentence()
    {
        using var client = await host.PersonAsync();

        var (status, error) = await PostAsync(
            client, $"/api/teams/{host.Alpha}/containers/Manager/tell", """{ "instruction": """);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.StartsWith("The request body is not valid JSON", error);
    }

    [Fact]
    public async Task A_blocked_report_with_a_text_item_is_answered_400_naming_item()
    {
        using var client = host.Container(host.AlphaContainerKey);

        var (status, error) = await PostAsync(
            client, $"/api/teams/{host.Alpha}/containers/Worker/blocked",
            """{ "reason": "waiting", "item": "two" }""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("item must be a whole number, as 42.", error);
    }

    [Fact]
    public async Task A_blocked_report_with_a_text_defer_is_answered_400_naming_defer()
    {
        using var client = host.Container(host.AlphaContainerKey);

        var (status, error) = await PostAsync(
            client, $"/api/teams/{host.Alpha}/containers/Worker/blocked",
            """{ "reason": "waiting", "item": 2, "defer": "yes" }""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("defer must be true or false.", error);
    }

    [Fact]
    public async Task A_new_folder_with_a_numeric_path_is_answered_400_naming_path()
    {
        using var client = await host.PersonAsync();

        var (status, error) = await PostAsync(
            client, $"/api/teams/{host.Alpha}/documents/folders", """{ "path": 7 }""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("path must be a string, as \"7\".", error);
    }

    private static async Task<(HttpStatusCode Status, string? Error)> PostAsync(HttpClient client, string uri, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(uri, content, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.False(string.IsNullOrWhiteSpace(body), $"{(int)response.StatusCode} with an empty body");

        using var document = JsonDocument.Parse(body);
        return (response.StatusCode, document.RootElement.GetProperty("error").GetString());
    }
}
