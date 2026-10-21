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
    public async Task A_microsoft_public_client_is_saved_with_no_secret_and_its_exchange_and_refresh_send_none()
    {
        // The redirect flow with a secret, under Advanced, is as it was.
        var advanced = await SendAsync(HttpMethod.Put, "/api/connections/providers/microsoft",
            new { clientId = "advanced-client", clientSecret = "msecret-advanced-9Kz", tenant = "contoso.onmicrosoft.com" });
        Assert.True(advanced.Status == HttpStatusCode.OK, advanced.Body);
        Assert.True(JsonDocument.Parse(advanced.Body).RootElement.GetProperty("clientSecretSet").GetBoolean());

        // The guided save takes the client ID and who can sign in, and no secret.
        var withSecret = await SendAsync(HttpMethod.Put, "/api/connections/providers/microsoft",
            new { clientId = MicrosoftClientId, audience = "common", clientSecret = "msecret-guided-3Qw" });
        Assert.Equal(HttpStatusCode.BadRequest, withSecret.Status);

        var notGuid = await SendAsync(HttpMethod.Put, "/api/connections/providers/microsoft", new { clientId = "advanced-client", audience = "common" });
        Assert.Equal(HttpStatusCode.BadRequest, notGuid.Status);
        Assert.Contains("clientId", notGuid.Body);

        var saved = await SetUpMicrosoftAsync();
        Assert.False(saved.GetProperty("clientSecretSet").GetBoolean());
        Assert.Equal(MicrosoftClientId, saved.GetProperty("clientId").GetString());
        Assert.Null(await Services.GetRequiredService<ConnectionStore>().ClientSecretAsync("microsoft", Ct));

        var flowId = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite("approve");
        var id = (await FlowAsync(flowId, until: "done")).GetProperty("connection").GetProperty("id").GetString()!;
        Assert.All(_provider.DevicePolls, p => Assert.Null(p.Secret));

        _clock.Advance(TimeSpan.FromHours(2));
        var (grant, refusal) = await Services.GetRequiredService<Connections>().GrantAsync(id, "mail", Ct);
        Assert.Null(refusal);
        Assert.NotNull(grant);
        Assert.Null(Assert.Single(_provider.Refreshes).Secret);
    }

    [Fact]
    public async Task The_microsoft_tenant_follows_who_can_sign_in()
    {
        foreach (var (audience, tenantId, tenant) in new[]
        {
            ("common", (string?)null, "common"),
            ("organizations", null, "organizations"),
            ("tenant", TenantGuid, TenantGuid),
        })
        {
            var saved = await SetUpMicrosoftAsync(audience, tenantId);
            Assert.Equal(tenant, saved.GetProperty("tenant").GetString());
            Assert.Equal(audience, saved.GetProperty("audience").GetString());

            await StartDeviceAsync();
            Assert.Equal($"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/devicecode", _provider.DeviceStarts[^1].Url);
        }

        foreach (var body in new object[]
        {
            new { clientId = MicrosoftClientId, audience = "tenant" },
            new { clientId = MicrosoftClientId, audience = "tenant", tenantId = "contoso.onmicrosoft.com" },
            new { clientId = MicrosoftClientId, audience = "everyone" },
        })
        {
            var refused = await SendAsync(HttpMethod.Put, "/api/connections/providers/microsoft", body);
            Assert.True(refused.Status == HttpStatusCode.BadRequest, refused.Body);
        }

        // A refused save leaves the last choice standing.
        Assert.Equal(TenantGuid, (await GetAsync("/api/connections/providers")).EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == "microsoft").GetProperty("tenant").GetString());
    }

    [Fact]
    public async Task A_provider_with_no_device_endpoint_refuses_a_device_sign_in_with_a_sentence()
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "/api/connections/start",
            new { provider = "google", scopes = new[] { MailScope }, flow = "device" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("Google", JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
        Assert.Empty(_provider.DeviceStarts);

        var unknown = await SendAsync(HttpMethod.Post, "/api/connections/start", new { provider = "google", scopes = new[] { MailScope }, flow = "carrier-pigeon" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.Status);
    }

    [Fact]
    public async Task The_cli_device_flow_starts_and_reads_through_the_operator_exchange()
    {
        await SetUpMicrosoftAsync();
        var folder = Path.Combine(_dataRoot, ConnectRequests.Folder);
        await WaitUntilAsync(() => Directory.Exists(folder));

        var started = await ExchangeAsync(new { op = "start", provider = "microsoft", scopes = new[] { GraphMail }, name = "Outlook", flow = "device" });
        Assert.Equal(200, started.GetProperty("status").GetInt32());
        var start = started.GetProperty("start");
        Assert.Equal(["expiresAt", "flowId", "userCode", "verificationUri"], start.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var flowId = start.GetProperty("flowId").GetString()!;

        Assert.Equal("waiting", (await ExchangeFlowAsync(flowId)).GetProperty("state").GetString());

        // The operator's flow is not a person's to read.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/api/connections/flows/{flowId}", null)).Status);

        _provider.DeviceAnswers.Writer.TryWrite("approve");
        var done = await ExchangeFlowAsync(flowId, until: "done");
        Assert.Equal("Outlook", done.GetProperty("connection").GetProperty("name").GetString());
        Assert.Contains(await TenantRowsAsync(), r => r.Action == TenantActions.ConnectionConnected && r.ActorEmail == ConnectionActor.Operator.Email);

        var missing = await ExchangeAsync(new { op = "flow", flowId = "no-such-flow" });
        Assert.Equal(404, missing.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task The_cli_device_code_reaches_no_exchange_answer_and_no_file_under_the_data_root()
    {
        await SetUpMicrosoftAsync();
        var folder = Path.Combine(_dataRoot, ConnectRequests.Folder);
        await WaitUntilAsync(() => Directory.Exists(folder));

        // No redirectUri: the CLI opens no port for a sign-in with a code.
        var started = await ExchangeAsync(new { op = "start", provider = "microsoft", scopes = new[] { GraphMail }, flow = "device" });
        Assert.Equal(200, started.GetProperty("status").GetInt32());
        var start = started.GetProperty("start");
        Assert.Equal(["expiresAt", "flowId", "userCode", "verificationUri"], start.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var flowId = start.GetProperty("flowId").GetString()!;

        var reports = new List<string> { started.GetRawText() };
        reports.Add(File.ReadAllText(Path.Combine(folder, ConnectRequests.ReportFile)));
        reports.Add((await ExchangeAsync(new { op = "flow", flowId })).GetRawText());
        _provider.DeviceAnswers.Writer.TryWrite("approve");
        reports.Add((await ExchangeFlowAsync(flowId, until: "done")).GetRawText());
        reports.Add(File.ReadAllText(Path.Combine(folder, ConnectRequests.ReportFile)));

        var code = Assert.Single(_provider.DeviceCodes);
        Assert.DoesNotContain(reports, r => r.Contains(code, StringComparison.Ordinal));

        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(_dataRoot, "*", SearchOption.AllDirectories))
        {
            if (file.StartsWith(PluginInstall.PluginsRoot(_dataRoot), StringComparison.Ordinal)) continue;

            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, Ct); }
            catch (IOException) { continue; }

            Assert.False(Encoding.Latin1.GetString(bytes).Contains(code, StringComparison.Ordinal), $"{Path.GetRelativePath(_dataRoot, file)} holds a device code.");
        }
    }

    [Fact]
    public async Task The_cli_reads_a_persons_device_flow_exactly_as_a_missing_one()
    {
        await SetUpMicrosoftAsync();
        var folder = Path.Combine(_dataRoot, ConnectRequests.Folder);
        await WaitUntilAsync(() => Directory.Exists(folder));

        var flowId = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;
        Assert.Equal("waiting", (await FlowAsync(flowId)).GetProperty("state").GetString());

        var theirs = await ExchangeAsync(new { op = "flow", flowId });
        var missing = await ExchangeAsync(new { op = "flow", flowId = "no-such-flow" });

        Assert.Equal(404, theirs.GetProperty("status").GetInt32());
        Assert.Equal(missing.GetProperty("status").GetInt32(), theirs.GetProperty("status").GetInt32());
        Assert.Equal(missing.GetProperty("error").GetString(), theirs.GetProperty("error").GetString());
        Assert.False(theirs.TryGetProperty("flow", out _));
    }

    [Fact]
    public async Task The_cli_device_start_for_a_provider_with_no_device_endpoint_answers_400_with_the_sentence()
    {
        var folder = Path.Combine(_dataRoot, ConnectRequests.Folder);
        await WaitUntilAsync(() => Directory.Exists(folder));

        var refused = await ExchangeAsync(new { op = "start", provider = "google", scopes = new[] { MailScope }, flow = "device" });

        Assert.Equal(400, refused.GetProperty("status").GetInt32());
        Assert.Contains("Google", refused.GetProperty("error").GetString());
        Assert.False(refused.TryGetProperty("start", out var start) && start.ValueKind != JsonValueKind.Null);
        Assert.Empty(_provider.DeviceStarts);
    }

    // ---- no leaks ---------------------------------------------------------------------------------
}
