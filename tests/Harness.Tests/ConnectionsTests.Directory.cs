using System.Net;
using System.Text.Json;
using Harness.Host;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// A MICROSOFT CONNECTION'S OWN DIRECTORY, on the real Host with a FAKED Microsoft that records each
/// URL it is asked at. A connection naming a directory (a tenant ID or a domain) signs in, redeems,
/// refreshes and gets each API's token at that directory, with the provider's client; one with none
/// asks exactly what it asked before, and its rows are as they were.
/// </summary>
public sealed partial class ConnectionsTests
{
    private const string Directory2 = "7d41a0c2-5b3e-4c8f-a19d-0e6f2b8c4d53";
    private const string ManagementScope = "https://management.azure.com/user_impersonation";
    private const string SqlScope = "https://database.windows.net/user_impersonation";
    private const string StorageScope = "https://storage.azure.com/user_impersonation";

    private const string DirectoryRefused =
        "Directory (tenant) ID or domain is a directory ID such as xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx or a domain such as example.onmicrosoft.com.";

    private static string At(string directory, string endpoint) => $"https://login.microsoftonline.com/{directory}/oauth2/v2.0/{endpoint}";

    [Theory]
    [InlineData(TenantGuid, TenantGuid)]
    [InlineData(" Example.OnMicrosoft.com ", "example.onmicrosoft.com")]
    public async Task A_code_sign_in_naming_a_directory_asks_that_directorys_device_and_token_endpoints_and_stores_it(string named, string stored)
    {
        await SetUpMicrosoftAsync();

        var id = await ConnectInDirectoryAsync(named);

        Assert.Equal(At(stored, "devicecode"), Assert.Single(_provider.DeviceStarts).Url);
        Assert.Equal(At(stored, "token"), Assert.Single(_provider.DevicePolls).TokenUrl);

        var listed = Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
        Assert.Equal(stored, listed.GetProperty("tenant").GetString());
        Assert.Equal([stored], await TenantColumnAsync("connections"));

        // Not a secret: the connected row names it.
        var connected = JsonDocument.Parse(Assert.Single(await TenantDetailsAsync("connections.connected"))).RootElement;
        Assert.Equal(stored, connected.GetProperty("tenant").GetString());

        // Its refresh goes there too, with the provider's client.
        _clock.Advance(TimeSpan.FromHours(2));
        var (grant, refusal) = await Services.GetRequiredService<Connections>().GrantAsync(id, "mail", Ct);
        Assert.Null(refusal);
        Assert.NotNull(grant);
        Assert.Equal(At(stored, "token"), Assert.Single(_provider.RefreshUrls));
    }

    [Fact]
    public async Task A_several_API_connections_refresh_and_each_APIs_token_go_to_its_directory()
    {
        await SetUpMicrosoftAsync();

        var id = await ConnectInDirectoryAsync(TenantGuid, [ManagementScope, SqlScope, StorageScope]);

        var (grant, refusal) = await Services.GetRequiredService<Connections>().GrantAsync(id, "azure", Ct);
        Assert.Null(refusal);
        Assert.Equal(3, grant!.Tokens!.Count);
        Assert.Equal(3, _provider.RefreshUrls.Count);
        Assert.All(_provider.RefreshUrls, url => Assert.Equal(At(TenantGuid, "token"), url));
    }

    [Fact]
    public async Task A_connection_without_a_directory_asks_exactly_what_it_asks_today()
    {
        await SetUpMicrosoftAsync();

        var flowId = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite("approve");
        var id = (await FlowAsync(flowId, until: "done")).GetProperty("connection").GetProperty("id").GetString()!;

        Assert.Equal(At("common", "devicecode"), Assert.Single(_provider.DeviceStarts).Url);
        Assert.Equal(At("common", "token"), Assert.Single(_provider.DevicePolls).TokenUrl);

        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Null((await Services.GetRequiredService<Connections>().GrantAsync(id, "mail", Ct)).Refusal);
        Assert.Equal(At("common", "token"), Assert.Single(_provider.RefreshUrls));

        // A reconnect asks the same, and keeps no directory.
        var again = (await StartDeviceAsync(reconnectId: id)).GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite("approve");
        await FlowAsync(again, until: "done");
        Assert.Equal(At("common", "devicecode"), _provider.DeviceStarts[^1].Url);

        // Its rows are as they were: no tenant named, the column NULL.
        Assert.Equal(JsonValueKind.Null, Assert.Single((await GetAsync("/api/connections")).EnumerateArray()).GetProperty("tenant").ValueKind);
        Assert.Equal([null], await TenantColumnAsync("connections"));
        Assert.Equal(["connection", "provider", "account", "scopes"], Keys(Assert.Single(await TenantDetailsAsync("connections.connected"))));
        Assert.Equal(["connection", "provider", "account", "scopes", "wasNeedingReconnect"], Keys(Assert.Single(await TenantDetailsAsync("connections.reconnected"))));

        // The browser sign-in, under Advanced, alike: the provider's tenant, and a flow row with none.
        await SetUpAdvancedMicrosoftAsync();
        var start = await StartBrowserAsync(null);
        Assert.StartsWith("https://login.microsoftonline.com/organisation.example/oauth2/v2.0/authorize?", start.GetProperty("authorizationUrl").GetString());
        Assert.Equal([null], await TenantColumnAsync("connection_flows"));
        Assert.Contains("connection=connected", await CallbackLocationAsync(start.GetProperty("state").GetString()!));
        Assert.Equal("https://login.microsoftonline.com/organisation.example/oauth2/v2.0/token", Assert.Single(_provider.ExchangeUrls));
    }

    [Fact]
    public async Task A_browser_sign_in_naming_a_directory_carries_it_on_its_flow_row_and_redeems_its_code_there()
    {
        await SetUpAdvancedMicrosoftAsync();

        var start = await StartBrowserAsync(TenantGuid);
        Assert.StartsWith(At(TenantGuid, "authorize") + "?", start.GetProperty("authorizationUrl").GetString());
        Assert.Contains("client_id=advanced-client", start.GetProperty("authorizationUrl").GetString());
        Assert.Equal([TenantGuid], await TenantColumnAsync("connection_flows"));

        Assert.Contains("connection=connected", await CallbackLocationAsync(start.GetProperty("state").GetString()!));
        Assert.Equal(At(TenantGuid, "token"), Assert.Single(_provider.ExchangeUrls));
        Assert.Equal("msecret-advanced-9Kz", Assert.Single(_provider.Exchanges).Secret);
        Assert.Equal([TenantGuid], await TenantColumnAsync("connections"));
    }

    [Fact]
    public async Task Reconnect_keeps_the_directory_and_a_reconnect_as_another_account_is_refused_and_changes_nothing()
    {
        await SetUpMicrosoftAsync();
        var id = await ConnectInDirectoryAsync(TenantGuid);

        // Left out: the connection's own directory.
        var kept = (await StartDeviceAsync(reconnectId: id)).GetProperty("flowId").GetString()!;
        Assert.Equal(At(TenantGuid, "devicecode"), _provider.DeviceStarts[^1].Url);
        _provider.DeviceAnswers.Writer.TryWrite("approve");
        Assert.Equal("done", (await FlowAsync(kept, until: "done")).GetProperty("state").GetString());
        Assert.Equal(At(TenantGuid, "token"), _provider.DevicePolls[^1].TokenUrl);
        Assert.Equal([TenantGuid], await TenantColumnAsync("connections"));
        var reconnected = JsonDocument.Parse(Assert.Single(await TenantDetailsAsync("connections.reconnected"))).RootElement;
        Assert.Equal(TenantGuid, reconnected.GetProperty("tenant").GetString());

        // Another account: refused, and nothing about the connection changes.
        var before = Assert.Single((await GetAsync("/api/connections")).EnumerateArray()).GetRawText();
        _provider.Account = "someone-else@example.com";
        var other = (await StartDeviceInAsync(Directory2, reconnectId: id)).GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite("approve");
        Assert.Contains("You signed in as someone-else@example.com", (await FlowAsync(other, until: "refused")).GetProperty("sentence").GetString());
        Assert.Equal(before, Assert.Single((await GetAsync("/api/connections")).EnumerateArray()).GetRawText());
        Assert.Equal([TenantGuid], await TenantColumnAsync("connections"));
        Assert.Single(await TenantDetailsAsync("connections.reconnected"));

        // Named: the reconnect moves it to that directory.
        _provider.Account = Account;
        var moved = (await StartDeviceInAsync(Directory2, reconnectId: id)).GetProperty("flowId").GetString()!;
        Assert.Equal(At(Directory2, "devicecode"), _provider.DeviceStarts[^1].Url);
        _provider.DeviceAnswers.Writer.TryWrite("approve");
        await FlowAsync(moved, until: "done");
        Assert.Equal([Directory2], await TenantColumnAsync("connections"));
    }

    [Fact]
    public async Task The_same_account_in_two_directories_is_two_connections()
    {
        await SetUpMicrosoftAsync();

        var first = await ConnectInDirectoryAsync(TenantGuid);
        var second = await ConnectInDirectoryAsync(Directory2);

        Assert.NotEqual(first, second);
        var listed = (await GetAsync("/api/connections")).EnumerateArray().ToList();
        Assert.Equal(2, listed.Count);
        Assert.All(listed, c => Assert.Equal(Account, c.GetProperty("account").GetString()));
        Assert.Equal(
            new[] { TenantGuid, Directory2 }.Order(),
            listed.Select(c => c.GetProperty("tenant").GetString()!).Order());
    }

    [Theory]
    [InlineData("not a directory")]
    [InlineData("common")]
    [InlineData("organizations")]
    [InlineData("example")]
    [InlineData("example.onmicrosoft.com/oauth2")]
    [InlineData("3b9e1c55-7a20-4f61-8d4e")]
    public async Task A_directory_that_is_neither_a_guid_nor_a_domain_is_refused_naming_the_field_and_nothing_is_stored(string named)
    {
        await SetUpMicrosoftAsync();

        var device = await SendAsync(HttpMethod.Post, "/api/connections/start",
            new { provider = "microsoft", scopes = new[] { GraphMail }, flow = "device", tenant = named });
        Assert.Equal(HttpStatusCode.BadRequest, device.Status);
        Assert.Equal(DirectoryRefused, JsonDocument.Parse(device.Body).RootElement.GetProperty("error").GetString());

        var exchange = await ExchangeAsync(new { op = "start", provider = "microsoft", scopes = new[] { GraphMail }, flow = "device", tenant = named });
        Assert.Equal(400, exchange.GetProperty("status").GetInt32());
        Assert.Equal(DirectoryRefused, exchange.GetProperty("error").GetString());

        await SetUpAdvancedMicrosoftAsync();
        var browser = await SendAsync(HttpMethod.Post, "/api/connections/start", new { provider = "microsoft", scopes = new[] { GraphMail }, tenant = named });
        Assert.Equal(HttpStatusCode.BadRequest, browser.Status);
        Assert.Equal(DirectoryRefused, JsonDocument.Parse(browser.Body).RootElement.GetProperty("error").GetString());

        Assert.Empty(_provider.DeviceStarts);
        Assert.Empty(await TenantColumnAsync("connection_flows"));
        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());
    }

    [Fact]
    public async Task The_operator_exchange_signs_in_with_a_code_through_the_directory_it_names()
    {
        await SetUpMicrosoftAsync();

        var report = await ExchangeAsync(new { op = "start", provider = "microsoft", scopes = new[] { GraphMail }, flow = "device", tenant = TenantGuid });
        Assert.Equal(200, report.GetProperty("status").GetInt32());
        Assert.Equal(At(TenantGuid, "devicecode"), Assert.Single(_provider.DeviceStarts).Url);

        _provider.DeviceAnswers.Writer.TryWrite("approve");
        var flow = await ExchangeFlowAsync(report.GetProperty("start").GetProperty("flowId").GetString()!, until: "done");
        Assert.Equal(TenantGuid, flow.GetProperty("connection").GetProperty("tenant").GetString());
        Assert.Equal(At(TenantGuid, "token"), Assert.Single(_provider.DevicePolls).TokenUrl);

        var listed = await ExchangeAsync(new { op = "list" });
        Assert.Equal(TenantGuid, Assert.Single(listed.GetProperty("connections").EnumerateArray()).GetProperty("tenant").GetString());
    }

    // ---- helpers ----------------------------------------------------------------------------------

    /// <summary>Microsoft under Advanced: a client with a secret for the browser sign-in, its tenant a domain.</summary>
    private async Task SetUpAdvancedMicrosoftAsync()
    {
        var (status, body) = await SendAsync(HttpMethod.Put, "/api/connections/providers/microsoft",
            new { clientId = "advanced-client", clientSecret = "msecret-advanced-9Kz", tenant = "organisation.example" });
        Assert.True(status == HttpStatusCode.OK, body);
    }

    private async Task<JsonElement> StartBrowserAsync(string? tenant)
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "/api/connections/start", new { provider = "microsoft", scopes = new[] { GraphMail }, tenant });
        Assert.True(status == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<JsonElement> StartDeviceInAsync(string tenant, string? reconnectId = null, IReadOnlyList<string>? scopes = null)
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "/api/connections/start",
            new { provider = reconnectId is null ? "microsoft" : null, scopes = scopes ?? [GraphMail], reconnectId, flow = "device", tenant });
        Assert.True(status == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>A sign-in with a code through <paramref name="tenant"/>, approved: the connection's id.</summary>
    private async Task<string> ConnectInDirectoryAsync(string tenant, IReadOnlyList<string>? scopes = null)
    {
        var flowId = (await StartDeviceInAsync(tenant, scopes: scopes)).GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite("approve");
        return (await FlowAsync(flowId, until: "done")).GetProperty("connection").GetProperty("id").GetString()!;
    }

    /// <summary>The <c>tenant</c> column of every row of <paramref name="table"/>.</summary>
    private async Task<IReadOnlyList<string?>> TenantColumnAsync(string table)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT tenant FROM {table} ORDER BY rowid";
        var values = new List<string?>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) values.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        return values;
    }

    private static IReadOnlyList<string> Keys(string json) =>
        [.. JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name)];
}
