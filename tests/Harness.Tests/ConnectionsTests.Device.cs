using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Web;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// CONNECTIONS, on the real Host with a FAKED provider - no test calls a real one. A person
/// sets Google's client, connects an account through the web flow (start, the provider's redirect
/// back to the callback), binds it to a plugin member, and the member's run receives a fresh access
/// token on stdin; refreshes, rotation, a refused refresh, the binding rules, the callback's refusals
/// and the no-leak rule are each pinned here.
/// </summary>
public sealed partial class ConnectionsTests
{
    [Fact]
    public async Task A_state_started_by_one_person_cannot_be_completed_by_another()
    {
        const string Other = "other@example.test";
        await Services.GetRequiredService<IUserStore>().CreateAsync(Other, Password, ct: Ct);
        using var other = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await other.PostAsJsonAsync("/api/auth/login", new { email = Other, password = Password }, Ct)).EnsureSuccessStatusCode();

        var (status, body) = await SendAsync(HttpMethod.Post, "/api/connections/start",
            new { provider = "google", scopes = new[] { MailScope }, redirectUri = "http://127.0.0.1:53121/" });
        Assert.Equal(HttpStatusCode.OK, status);
        var state = JsonDocument.Parse(body).RootElement.GetProperty("state").GetString()!;

        var stolen = await other.PostAsJsonAsync("/api/connections/complete", new { state, code = FakeProvider.GoodCode }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, stolen.StatusCode);
        Assert.Empty(_provider.Exchanges);
        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());

        // Still the first person's to finish.
        var mine = await SendAsync(HttpMethod.Post, "/api/connections/complete", new { state, code = FakeProvider.GoodCode });
        Assert.True(mine.Status == HttpStatusCode.OK, mine.Body);
    }

    // ---- the operator exchange's folder -----------------------------------------------------------

    [Fact]
    public async Task A_connect_request_in_a_folder_others_can_write_or_through_a_link_is_not_answered()
    {
        var root = Path.Combine(_outside, "exchange");
        var logger = new Warnings();
        var exchange = new ConnectRequests(Services.GetRequiredService<Connections>(), root, logger);
        exchange.Prepare();

        var folder = exchange.Root;
        var report = Path.Combine(folder, ConnectRequests.ReportFile);
        async Task Ask(string path) => await File.WriteAllTextAsync(path, $$"""{"request":"{{Guid.NewGuid():N}}","op":"list"}""", Ct);

        // Group-writable: refused, and said once however often it is looked at.
        File.SetUnixFileMode(folder, File.GetUnixFileMode(folder) | UnixFileMode.GroupWrite | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        await Ask(Path.Combine(folder, ConnectRequests.RequestFile));
        Assert.False(await exchange.AnswerAsync(Ct));
        Assert.False(await exchange.AnswerAsync(Ct));
        Assert.False(File.Exists(report));
        Assert.Contains("writable by others", Assert.Single(logger.Lines));

        // Put right, it is answered.
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.True(await exchange.AnswerAsync(Ct));
        Assert.True(File.Exists(report));
        File.Delete(report);

        // A request that is a link to a file elsewhere is not read.
        var elsewhere = Path.Combine(_outside, "planted.json");
        await Ask(elsewhere);
        File.CreateSymbolicLink(Path.Combine(folder, ConnectRequests.RequestFile), elsewhere);
        Assert.False(await exchange.AnswerAsync(Ct));
        Assert.False(File.Exists(report));
        File.Delete(Path.Combine(folder, ConnectRequests.RequestFile));

        // Nor is the folder itself a link to one another user made.
        var planted = Path.Combine(_outside, "planted-folder");
        Directory.CreateDirectory(planted, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(folder, recursive: true);
        Directory.CreateSymbolicLink(folder, planted);
        await Ask(Path.Combine(planted, ConnectRequests.RequestFile));
        Assert.False(await exchange.AnswerAsync(Ct));
        Assert.False(File.Exists(Path.Combine(planted, ConnectRequests.ReportFile)));
        Assert.Contains(logger.Lines, l => l.Contains("symbolic link"));
    }

    [Fact]
    public async Task Removing_by_a_name_two_connections_share_is_refused_naming_the_id_way()
    {
        var first = await ConnectAsync();
        var second = await ConnectAsync();
        Assert.NotEqual(first, second);

        var removed = await ExchangeAsync(new { op = "remove", id = Account });

        Assert.Equal(409, removed.GetProperty("status").GetInt32());
        Assert.Contains("remove it by id", removed.GetProperty("error").GetString());
        Assert.Equal(2, (await GetAsync("/api/connections")).GetArrayLength());
    }

    // ---- sign in with a code (device flow) ----------------------------------------------------------

    [Fact]
    public async Task A_device_sign_in_answers_only_the_code_and_link_and_its_device_code_reaches_no_answer_row_or_file()
    {
        await SetUpMicrosoftAsync();

        var start = await StartDeviceAsync();
        Assert.Equal(["expiresAt", "flowId", "userCode", "verificationUri"], start.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("U1-WXYZ", start.GetProperty("userCode").GetString());
        Assert.Equal("https://microsoft.com/devicelogin", start.GetProperty("verificationUri").GetString());
        var flowId = start.GetProperty("flowId").GetString()!;

        var waiting = await FlowAsync(flowId);
        Assert.Equal("waiting", waiting.GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(waiting.GetProperty("sentence").GetString()));

        _provider.DeviceAnswers.Writer.TryWrite("approve");
        await FlowAsync(flowId, until: "done");

        // And through the operator's exchange, whose report is a file under the data root.
        var cli = await ExchangeAsync(new { op = "start", provider = "microsoft", scopes = new[] { GraphMail }, flow = "device" });
        Assert.Equal(200, cli.GetProperty("status").GetInt32());
        _provider.DeviceAnswers.Writer.TryWrite("access_denied");
        await ExchangeFlowAsync(cli.GetProperty("start").GetProperty("flowId").GetString()!, until: "refused");

        await GetAsync("/api/connections");
        await GetAsync("/api/tenant-log");
        await GetAsync("/api/diagnostics");

        Assert.Equal(2, _provider.DeviceCodes.Count);
        foreach (var code in _provider.DeviceCodes)
        {
            Assert.DoesNotContain(_responses, r => r.Contains(code, StringComparison.Ordinal));
            Assert.DoesNotContain(await TenantDetailsAsync(TenantActions.ConnectionConnected), d => d.Contains(code, StringComparison.Ordinal));
        }

        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(_dataRoot, "*", SearchOption.AllDirectories))
        {
            if (file.StartsWith(PluginInstall.PluginsRoot(_dataRoot), StringComparison.Ordinal)) continue;

            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, Ct); }
            catch (IOException) { continue; }

            var text = Encoding.Latin1.GetString(bytes);
            foreach (var code in _provider.DeviceCodes)
            {
                Assert.False(text.Contains(code, StringComparison.Ordinal), $"{Path.GetRelativePath(_dataRoot, file)} holds a device code.");
            }
        }
    }

    [Fact]
    public async Task Device_polling_waits_the_providers_interval_and_five_seconds_more_after_each_slow_down()
    {
        await SetUpMicrosoftAsync();
        _provider.DeviceInterval = 7;

        var flowId = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;
        foreach (var answer in new[] { "pending", "slow_down", "pending", "slow_down", "approve" }) _provider.DeviceAnswers.Writer.TryWrite(answer);
        await FlowAsync(flowId, until: "done");

        lock (_delays) Assert.Equal([7, 7, 12, 12, 17], _delays.Select(d => d.TotalSeconds));
        Assert.Equal(5, _provider.DevicePolls.Count);
        Assert.All(_provider.DevicePolls, p => Assert.Equal(_provider.DeviceCodes[0], p.DeviceCode));
    }

    [Fact]
    public async Task An_approved_device_sign_in_stores_the_connection_with_its_tenant_row()
    {
        await SetUpMicrosoftAsync();

        var flowId = (await StartDeviceAsync(name: "Outlook")).GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite("approve");

        var done = await FlowAsync(flowId, until: "done");
        Assert.False(string.IsNullOrWhiteSpace(done.GetProperty("sentence").GetString()));
        var connection = done.GetProperty("connection");
        Assert.Equal("microsoft", connection.GetProperty("provider").GetString());
        Assert.Equal(Account, connection.GetProperty("account").GetString());
        Assert.Equal("Outlook", connection.GetProperty("name").GetString());
        var scopes = connection.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Contains(GraphMail, scopes);
        Assert.Contains("offline_access", scopes);

        // Closing the dialog cancels nothing: reading again gives the same.
        var again = await FlowAsync(flowId);
        Assert.Equal("done", again.GetProperty("state").GetString());
        Assert.Equal(connection.GetProperty("id").GetString(), again.GetProperty("connection").GetProperty("id").GetString());

        var id = connection.GetProperty("id").GetString()!;
        Assert.Equal(id, Assert.Single((await GetAsync("/api/connections")).EnumerateArray()).GetProperty("id").GetString());
        Assert.Contains(await TenantRowsAsync(), r => r.Action == TenantActions.ConnectionConnected && r.Subject == id && r.ActorEmail == Email);

        var tokens = (await Services.GetRequiredService<ConnectionStore>().TokensAsync(id, Ct))!;
        Assert.Equal(_provider.Issued[0].Refresh, tokens.RefreshToken);
        Assert.Equal(_provider.Issued[0].Access, tokens.AccessToken);
    }

    [Fact]
    public async Task A_refused_device_sign_in_ends_with_a_sentence_and_stores_nothing()
    {
        await SetUpMicrosoftAsync();

        var flowId = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite("access_denied");

        var refused = await FlowAsync(flowId, until: "refused");
        Assert.False(string.IsNullOrWhiteSpace(refused.GetProperty("sentence").GetString()));
        Assert.False(refused.TryGetProperty("connection", out var none) && none.ValueKind != JsonValueKind.Null);

        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());
        Assert.DoesNotContain(TenantActions.ConnectionConnected, await TenantActionsAsync());
        Assert.Single(_provider.DevicePolls);
    }

    [Fact]
    public async Task An_expired_device_sign_in_ends_with_a_sentence_and_stores_nothing()
    {
        await SetUpMicrosoftAsync();

        // Past its expiry the Host stops polling by itself: two polls fit in 12 seconds at 5.
        _provider.DeviceExpiresIn = 12;
        var first = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite("pending");
        _provider.DeviceAnswers.Writer.TryWrite("pending");
        var expired = await FlowAsync(first, until: "expired");
        Assert.False(string.IsNullOrWhiteSpace(expired.GetProperty("sentence").GetString()));
        Assert.Equal(2, _provider.DevicePolls.Count);

        // And when the provider says so first.
        _provider.DeviceExpiresIn = 900;
        var second = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite("expired_token");
        Assert.False(string.IsNullOrWhiteSpace((await FlowAsync(second, until: "expired")).GetProperty("sentence").GetString()));

        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());
        Assert.DoesNotContain(TenantActions.ConnectionConnected, await TenantActionsAsync());
    }

    [Fact]
    public async Task Another_person_reads_a_device_flow_exactly_as_a_missing_one()
    {
        await SetUpMicrosoftAsync();
        var flowId = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;

        const string Other = "other@example.test";
        await Services.GetRequiredService<IUserStore>().CreateAsync(Other, Password, ct: Ct);
        using var other = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await other.PostAsJsonAsync("/api/auth/login", new { email = Other, password = Password }, Ct)).EnsureSuccessStatusCode();

        using var theirs = await other.GetAsync($"/api/connections/flows/{flowId}", Ct);
        using var missing = await other.GetAsync("/api/connections/flows/no-such-flow", Ct);

        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(Ct), await theirs.Content.ReadAsStringAsync(Ct));

        // Still the starter's.
        Assert.Equal("waiting", (await FlowAsync(flowId)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_machine_principal_cannot_read_a_device_flow()
    {
        await SetUpMicrosoftAsync();
        var flowId = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;

        using var manager = _factory.CreateClient();
        manager.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, await ManagerKeyAsync());

        using var response = await manager.GetAsync($"/api/connections/flows/{flowId}", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("U1-WXYZ", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_person_lists_only_their_own_waiting_device_sign_ins_and_never_a_device_code()
    {
        await SetUpMicrosoftAsync();

        var settled = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite("approve");
        await FlowAsync(settled, until: "done");

        // The poll now waits on the provider, so this one stays waiting.
        var start = await StartDeviceAsync();
        var waiting = start.GetProperty("flowId").GetString()!;

        const string Other = "other@example.test";
        await Services.GetRequiredService<IUserStore>().CreateAsync(Other, Password, ct: Ct);
        using var other = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await other.PostAsJsonAsync("/api/auth/login", new { email = Other, password = Password }, Ct)).EnsureSuccessStatusCode();
        using var theirStart = await other.PostAsJsonAsync("/api/connections/start",
            new { provider = "microsoft", scopes = new[] { GraphMail }, flow = "device" }, Ct);
        Assert.Equal(HttpStatusCode.OK, theirStart.StatusCode);
        var theirs = JsonDocument.Parse(await theirStart.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("flowId").GetString()!;

        var open = Assert.Single((await GetAsync("/api/connections/flows/open")).EnumerateArray());
        Assert.Equal(
            ["expiresAt", "flowId", "provider", "state", "userCode", "verificationUri"],
            open.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(waiting, open.GetProperty("flowId").GetString());
        Assert.Equal("microsoft", open.GetProperty("provider").GetString());
        Assert.Equal(start.GetProperty("userCode").GetString(), open.GetProperty("userCode").GetString());
        Assert.Equal(start.GetProperty("verificationUri").GetString(), open.GetProperty("verificationUri").GetString());
        Assert.Equal(start.GetProperty("expiresAt").GetDateTimeOffset(), open.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal("waiting", open.GetProperty("state").GetString());

        using var theirList = await other.GetAsync("/api/connections/flows/open", Ct);
        var theirOpen = Assert.Single(JsonDocument.Parse(await theirList.Content.ReadAsStringAsync(Ct)).RootElement.EnumerateArray());
        Assert.Equal(theirs, theirOpen.GetProperty("flowId").GetString());

        // Past its expiry a flow is no longer open, even before its poll has noticed.
        _clock.Advance(TimeSpan.FromSeconds(_provider.DeviceExpiresIn + 1));
        Assert.Empty((await GetAsync("/api/connections/flows/open")).EnumerateArray());

        foreach (var code in _provider.DeviceCodes)
        {
            Assert.DoesNotContain(_responses, r => r.Contains(code, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_machine_principal_cannot_list_open_device_sign_ins()
    {
        await SetUpMicrosoftAsync();
        await StartDeviceAsync();

        using var manager = _factory.CreateClient();
        manager.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, await ManagerKeyAsync());

        using var response = await manager.GetAsync("/api/connections/flows/open", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("U1-WXYZ", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_device_reconnect_that_signs_in_as_another_account_is_refused_and_changes_nothing()
    {
        await SetUpMicrosoftAsync();
        var first = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite("approve");
        var id = (await FlowAsync(first, until: "done")).GetProperty("connection").GetProperty("id").GetString()!;

        var reconnect = (await StartDeviceAsync(reconnectId: id)).GetProperty("flowId").GetString()!;
        _provider.Account = "someone-else@example.com";
        _provider.DeviceAnswers.Writer.TryWrite("approve");

        var refused = await FlowAsync(reconnect, until: "refused");
        Assert.Contains("someone-else@example.com", refused.GetProperty("sentence").GetString());

        var listed = Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
        Assert.Equal(Account, listed.GetProperty("account").GetString());
        Assert.Equal(_provider.Issued[0].Refresh, (await Services.GetRequiredService<ConnectionStore>().TokensAsync(id, Ct))!.RefreshToken);
        Assert.DoesNotContain(TenantActions.ConnectionReconnected, await TenantActionsAsync());
    }
}
