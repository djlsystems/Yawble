using System.Net;
using System.Text;
using System.Text.Json;
using Harness.Host;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>Tester probes: edge cases of a Microsoft connection's own directory.</summary>
public sealed partial class ConnectionsTests
{
    [Theory]
    [InlineData("{3b9e1c55-7a20-4f61-8d4e-2c6a9f0b1e77}")]
    [InlineData("example.onmicrosoft.com.")]
    [InlineData("exa_mple.onmicrosoft.com")]
    [InlineData("https://login.microsoftonline.com/example.onmicrosoft.com")]
    [InlineData("10.0.0.1")]
    [InlineData("consumers")]
    public async Task Probe_more_values_that_are_neither_are_refused_with_the_sentence(string named)
    {
        await SetUpMicrosoftAsync();
        var device = await SendAsync(HttpMethod.Post, "/api/connections/start",
            new { provider = "microsoft", scopes = new[] { GraphMail }, flow = "device", tenant = named });
        Assert.Equal(HttpStatusCode.BadRequest, device.Status);
        Assert.Equal(DirectoryRefused, JsonDocument.Parse(device.Body).RootElement.GetProperty("error").GetString());
        Assert.Empty(_provider.DeviceStarts);
        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());
    }

    [Theory]
    [InlineData("3B9E1C55-7A20-4F61-8D4E-2C6A9F0B1E77 ", "3b9e1c55-7a20-4f61-8d4e-2c6a9f0b1e77")]
    [InlineData("\tSample.OnMicrosoft.COM  ", "sample.onmicrosoft.com")]
    public async Task Probe_uppercase_and_surrounding_space_are_stored_lowercased_and_asked_there(string named, string stored)
    {
        await SetUpMicrosoftAsync();
        await ConnectInDirectoryAsync(named);
        Assert.Equal(At(stored, "devicecode"), Assert.Single(_provider.DeviceStarts).Url);
        Assert.Equal([stored], await TenantColumnAsync("connections"));
    }

    [Fact]
    public async Task Probe_clearing_the_directory_on_a_connection_that_has_none_changes_nothing_and_its_row_is_as_today()
    {
        await SetUpMicrosoftAsync();
        var flowId = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite("approve");
        var id = (await FlowAsync(flowId, until: "done")).GetProperty("connection").GetProperty("id").GetString()!;

        var again = (await SendAsync(HttpMethod.Post, "/api/connections/start",
            new { scopes = new[] { GraphMail }, reconnectId = id, flow = "device", tenant = "" }));
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal(At("common", "devicecode"), _provider.DeviceStarts[^1].Url);
        _provider.DeviceAnswers.Writer.TryWrite("approve");
        await FlowAsync(JsonDocument.Parse(again.Body).RootElement.GetProperty("flowId").GetString()!, until: "done");

        Assert.Equal([null], await TenantColumnAsync("connections"));
        Assert.Equal(["connection", "provider", "account", "scopes", "wasNeedingReconnect"],
            Keys(Assert.Single(await TenantDetailsAsync("connections.reconnected"))));
    }

    [Fact]
    public async Task Probe_clearing_a_directory_on_reconnect_goes_back_to_the_providers_tenant()
    {
        await SetUpMicrosoftAsync();
        var id = await ConnectInDirectoryAsync(TenantGuid);

        var again = await SendAsync(HttpMethod.Post, "/api/connections/start",
            new { scopes = new[] { GraphMail }, reconnectId = id, flow = "device", tenant = "  " });
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal(At("common", "devicecode"), _provider.DeviceStarts[^1].Url);
        _provider.DeviceAnswers.Writer.TryWrite("approve");
        await FlowAsync(JsonDocument.Parse(again.Body).RootElement.GetProperty("flowId").GetString()!, until: "done");

        Assert.Equal([null], await TenantColumnAsync("connections"));
        var row = JsonDocument.Parse(Assert.Single(await TenantDetailsAsync("connections.reconnected"))).RootElement;
        Assert.Equal(JsonValueKind.Null, row.GetProperty("tenant").ValueKind);

        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Null((await Services.GetRequiredService<Connections>().GrantAsync(id, "mail", Ct)).Refusal);
        Assert.Equal(At("common", "token"), _provider.RefreshUrls[^1]);
    }

    [Fact]
    public async Task Probe_an_invalid_directory_on_reconnect_is_refused_and_keeps_the_connections()
    {
        await SetUpMicrosoftAsync();
        var id = await ConnectInDirectoryAsync(TenantGuid);
        var starts = _provider.DeviceStarts.Count;

        var again = await SendAsync(HttpMethod.Post, "/api/connections/start",
            new { scopes = new[] { GraphMail }, reconnectId = id, flow = "device", tenant = "common" });
        Assert.Equal(HttpStatusCode.BadRequest, again.Status);
        Assert.Equal(DirectoryRefused, JsonDocument.Parse(again.Body).RootElement.GetProperty("error").GetString());
        Assert.Equal(starts, _provider.DeviceStarts.Count);
        Assert.Equal([TenantGuid], await TenantColumnAsync("connections"));
    }

    [Fact]
    public async Task Probe_a_directory_on_google_is_refused()
    {
        var saved = await SendAsync(HttpMethod.Put, "/api/connections/providers/google", new { clientId = "client-123.apps.googleusercontent.com", clientSecret = ClientSecret });
        Assert.Equal(HttpStatusCode.OK, saved.Status);
        var start = await SendAsync(HttpMethod.Post, "/api/connections/start", new { provider = "google", scopes = new[] { MailScope }, tenant = TenantGuid });
        Assert.Equal(HttpStatusCode.BadRequest, start.Status);
        Assert.StartsWith("Directory (tenant) ID or domain is for a Microsoft connection only", JsonDocument.Parse(start.Body).RootElement.GetProperty("error").GetString());
        Assert.Empty(await TenantColumnAsync("connection_flows"));
    }

    [Fact]
    public async Task Probe_no_token_code_or_secret_of_a_directory_sign_in_reaches_an_answer_or_a_file()
    {
        await SetUpMicrosoftAsync();
        var id = await ConnectInDirectoryAsync(TenantGuid, [ManagementScope, SqlScope]);
        Assert.Null((await Services.GetRequiredService<Connections>().GrantAsync(id, "azure", Ct)).Refusal);

        await SetUpAdvancedMicrosoftAsync();
        var start = await StartBrowserAsync(Directory2);
        Assert.Contains("connection=connected", await CallbackLocationAsync(start.GetProperty("state").GetString()!));

        await GetAsync("/api/connections");
        await GetAsync("/api/tenant-log");

        string[] secrets =
        [
            "msecret-advanced-9Kz",
            .. _provider.DeviceCodes,
            .. _provider.Issued.Select(t => t.Access),
            .. _provider.Issued.Select(t => t.Refresh),
        ];
        Assert.NotEmpty(_provider.DeviceCodes);

        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(_responses, r => r.Contains(secret, StringComparison.Ordinal));
        }

        SqliteConnection.ClearAllPools();
        foreach (var file in System.IO.Directory.EnumerateFiles(_dataRoot, "*", SearchOption.AllDirectories))
        {
            if (file.StartsWith(PluginInstall.PluginsRoot(_dataRoot), StringComparison.Ordinal)) continue;
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, Ct); }
            catch (IOException) { continue; }
            var text = Encoding.Latin1.GetString(bytes);
            foreach (var secret in secrets)
            {
                Assert.False(text.Contains(secret, StringComparison.Ordinal), $"{Path.GetRelativePath(_dataRoot, file)} holds a credential in plain text.");
            }
        }
    }
}
