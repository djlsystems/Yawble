using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// FOUR THINGS THAT STOPPED A PERSON WHO DID WHAT THE SCREENS ASKED, each pinned on the real Host with
/// the faked provider: a sign-in with a code is cancelled at the Host (and stores nothing if Microsoft
/// approves afterwards); a built-in provider's saved client is cleared with its tenant row, or not at
/// all; a Microsoft client ID that is a directory is refused naming both fields, and Microsoft not
/// knowing the saved app ends in one sentence; and a reconnect naming a directory signs in through it.
/// </summary>
public sealed partial class ConnectionsTests
{
    // ---- 1. cancel a sign-in with a code ------------------------------------------------------------

    [Fact]
    public async Task A_cancelled_sign_in_with_a_code_is_gone_from_the_open_list_and_reads_missing_and_only_its_starter_may_cancel_it()
    {
        await SetUpMicrosoftAsync();
        var flowId = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;
        Assert.Equal([flowId], (await GetAsync("/api/connections/flows/open")).EnumerateArray().Select(f => f.GetProperty("flowId").GetString()));

        // Somebody else reads exactly what a missing flow reads, and cancels nothing.
        const string Other = "other@example.test";
        await Services.GetRequiredService<IUserStore>().CreateAsync(Other, Password, ct: Ct);
        using var other = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await other.PostAsJsonAsync("/api/auth/login", new { email = Other, password = Password }, Ct)).EnsureSuccessStatusCode();
        var theirs = await other.DeleteAsync($"/api/connections/flows/{flowId}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
        Assert.Equal(ConnectionEndpoints.MissingFlow, JsonDocument.Parse(await theirs.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("error").GetString());
        Assert.Equal("waiting", (await FlowAsync(flowId)).GetProperty("state").GetString());

        var cancelled = await SendAsync(HttpMethod.Delete, $"/api/connections/flows/{flowId}", null);
        Assert.True(cancelled.Status == HttpStatusCode.NoContent, cancelled.Body);

        Assert.Empty((await GetAsync("/api/connections/flows/open")).EnumerateArray());
        var read = await SendAsync(HttpMethod.Get, $"/api/connections/flows/{flowId}", null);
        Assert.Equal(HttpStatusCode.NotFound, read.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/api/connections/flows/{flowId}", null)).Status);

        // Microsoft approving now finds no poll to take it: nothing is stored.
        _provider.DeviceAnswers.Writer.TryWrite("approve");
        await Task.Delay(200, Ct);
        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());
        Assert.DoesNotContain(TenantActions.ConnectionConnected, await TenantActionsAsync());
    }

    [Fact]
    public async Task Microsoft_approving_after_the_cancel_while_the_poll_is_at_the_token_endpoint_stores_nothing()
    {
        await SetUpMicrosoftAsync();
        var held = _provider.HoldDevicePoll();
        var flowId = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;
        await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        var cancelled = await SendAsync(HttpMethod.Delete, $"/api/connections/flows/{flowId}", null);
        Assert.True(cancelled.Status == HttpStatusCode.NoContent, cancelled.Body);

        // The provider answers the poll it was holding: approved, with tokens.
        held.Answer.SetResult("approve");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (_provider.DevicePolls.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
        Assert.Single(_provider.DevicePolls);
        await Task.Delay(200, Ct);

        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());
        Assert.DoesNotContain(TenantActions.ConnectionConnected, await TenantActionsAsync());
        Assert.Empty((await GetAsync("/api/connections/flows/open")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/api/connections/flows/{flowId}", null)).Status);
    }

    [Fact]
    public async Task A_sign_in_with_a_code_that_already_ended_is_not_cancelled()
    {
        await SetUpMicrosoftAsync();
        var flowId = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite("approve");
        await FlowAsync(flowId, until: "done");

        var refused = await SendAsync(HttpMethod.Delete, $"/api/connections/flows/{flowId}", null);

        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("done", JsonDocument.Parse(refused.Body).RootElement.GetProperty("state").GetString());
        Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
        Assert.Equal("done", (await FlowAsync(flowId)).GetProperty("state").GetString());
    }

    // ---- 2. clear a built-in provider's saved client -------------------------------------------------

    [Fact]
    public async Task Clearing_googles_saved_client_writes_its_row_reads_not_set_up_and_names_the_connections_needing_reconnect()
    {
        var id = await ConnectAsync();

        var (status, body) = await SendAsync(HttpMethod.Delete, "/api/connections/providers/google/client", null);

        Assert.True(status == HttpStatusCode.OK, body);
        var answer = JsonDocument.Parse(body).RootElement;
        var provider = answer.GetProperty("provider");
        Assert.False(provider.GetProperty("configured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, provider.GetProperty("clientSource").ValueKind);
        Assert.False(provider.GetProperty("clientSecretSet").GetBoolean());
        Assert.Equal([id], answer.GetProperty("reconnect").EnumerateArray().Select(c => c.GetProperty("id").GetString()));
        Assert.Null(await Services.GetRequiredService<ConnectionStore>().ClientSecretAsync("google", Ct));
        Assert.DoesNotContain(ClientSecret, body);

        var row = Assert.Single(await TenantRowsAsync(), r => r.Action == TenantActions.ConnectionProviderCleared);
        Assert.Equal((Email, "google"), (row.ActorEmail, row.Subject));
        Assert.Contains(id, row.Detail);

        // The connection stays; a new sign-in reads not set up.
        Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
        var start = await SendAsync(HttpMethod.Post, "/api/connections/start", new { provider = "google", scopes = new[] { MailScope } });
        Assert.Equal(HttpStatusCode.BadRequest, start.Status);
        Assert.Contains("no OAuth client yet", start.Body);
    }

    [Fact]
    public async Task Nothing_changes_when_the_cleared_rows_cannot_be_written()
    {
        await SetUpMicrosoftAsync();
        await ExecuteAsync(
            $"CREATE TRIGGER refuse_clear BEFORE INSERT ON tenant_events WHEN NEW.action = '{TenantActions.ConnectionProviderCleared}' "
            + "BEGIN SELECT RAISE(ABORT, 'forced failure'); END;");

        var (status, body) = await SendAsync(HttpMethod.Delete, "/api/connections/providers/microsoft/client", null);

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Contains("The client was not cleared", body);
        var microsoft = (await GetAsync("/api/connections/providers")).EnumerateArray().Single(p => p.GetProperty("id").GetString() == "microsoft");
        Assert.Equal(("saved", MicrosoftClientId), (microsoft.GetProperty("clientSource").GetString(), microsoft.GetProperty("clientId").GetString()));
        Assert.DoesNotContain(TenantActions.ConnectionProviderCleared, await TenantActionsAsync());
    }

    [Fact]
    public async Task Clearing_is_for_a_built_in_with_a_saved_client_only()
    {
        // Microsoft has none saved here, and the instance names none.
        var none = await SendAsync(HttpMethod.Delete, "/api/connections/providers/microsoft/client", null);
        Assert.Equal(HttpStatusCode.Conflict, none.Status);
        Assert.Contains("no saved client", none.Body);

        var custom = await SendAsync(HttpMethod.Delete, "/api/connections/providers/custom-acme/client", null);
        Assert.Equal(HttpStatusCode.BadRequest, custom.Status);

        // Removing a built-in still says to clear its client.
        var removed = await SendAsync(HttpMethod.Delete, "/api/connections/providers/google", null);
        Assert.Equal(HttpStatusCode.BadRequest, removed.Status);
        Assert.Contains("clear its client", removed.Body);
        Assert.DoesNotContain(TenantActions.ConnectionProviderCleared, await TenantActionsAsync());
    }

    // ---- 3. a directory ID saved as the client ID ---------------------------------------------------

    [Fact]
    public async Task A_microsoft_client_id_equal_to_its_own_tenant_is_refused_naming_both_fields_and_nothing_is_saved()
    {
        var guided = await SendAsync(HttpMethod.Put, "/api/connections/providers/microsoft",
            new { clientId = TenantGuid, audience = "tenant", tenantId = TenantGuid.ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.BadRequest, guided.Status);
        var error = JsonDocument.Parse(guided.Body).RootElement.GetProperty("error").GetString()!;
        Assert.Equal(ConnectionProviders.ClientIdIsDirectory("tenantId", null), error);
        Assert.Contains("`clientId`", error);
        Assert.Contains("`tenantId`", error);

        var advanced = await SendAsync(HttpMethod.Put, "/api/connections/providers/microsoft",
            new { clientId = "contoso.onmicrosoft.com", tenant = "contoso.onmicrosoft.com" });
        Assert.Equal(HttpStatusCode.BadRequest, advanced.Status);
        Assert.Contains("`tenant`", advanced.Body);

        var microsoft = (await GetAsync("/api/connections/providers")).EnumerateArray().Single(p => p.GetProperty("id").GetString() == "microsoft");
        Assert.False(microsoft.GetProperty("configured").GetBoolean());
        Assert.DoesNotContain(await TenantRowsAsync(), r => r.Action == TenantActions.ConnectionProviderSaved && r.Subject == "microsoft");
    }

    [Fact]
    public async Task A_microsoft_client_id_equal_to_a_connections_directory_is_refused_naming_that_connection()
    {
        await SetUpMicrosoftAsync();
        var id = await ConnectInDirectoryAsync(TenantGuid);
        var name = Assert.Single((await GetAsync("/api/connections")).EnumerateArray()).GetProperty("name").GetString()!;

        var (status, body) = await SendAsync(HttpMethod.Put, "/api/connections/providers/microsoft",
            new { clientId = TenantGuid.ToUpperInvariant(), audience = "common" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var error = JsonDocument.Parse(body).RootElement.GetProperty("error").GetString()!;
        Assert.Contains("`clientId`", error);
        Assert.Contains("Directory (tenant) ID", error);
        Assert.Contains(name, error);
        var microsoft = (await GetAsync("/api/connections/providers")).EnumerateArray().Single(p => p.GetProperty("id").GetString() == "microsoft");
        Assert.Equal(MicrosoftClientId, microsoft.GetProperty("clientId").GetString());
        Assert.NotNull(id);
    }

    [Theory]
    [InlineData("AADSTS700016")]
    [InlineData("unauthorized_client")]
    public async Task Microsoft_not_knowing_the_saved_apps_client_id_ends_the_code_with_one_sentence_and_no_code(string refusal)
    {
        await SetUpMicrosoftAsync();
        var flowId = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;

        _provider.DeviceAnswers.Writer.TryWrite(refusal);
        var refused = await FlowAsync(flowId, until: "refused");

        var sentence = refused.GetProperty("sentence").GetString()!;
        Assert.Equal(ConnectionProviders.MicrosoftUnknownClient, sentence);
        Assert.DoesNotContain("AADSTS", sentence);
        Assert.DoesNotContain("unauthorized_client", sentence);
        Assert.Contains("directory ID pasted in the wrong box", sentence);
        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());
    }

    [Theory]
    [InlineData("invalid_request", "AADSTS700016: Application with identifier '3b9e1c55-7a20-4f61-8d4e-2c6a9f0b1e77' was not found in the directory.")]
    [InlineData("unauthorized_client", "The client is not authorized.")]
    public async Task Microsoft_refusing_the_saved_apps_code_start_says_the_sentence(string error, string description)
    {
        await SetUpMicrosoftAsync();
        _provider.DeviceStartRefusal = (error, description);

        var (status, body) = await SendAsync(HttpMethod.Post, "/api/connections/start",
            new { provider = "microsoft", scopes = new[] { GraphMail }, flow = "device" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(ConnectionProviders.MicrosoftUnknownClient, JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
        Assert.Empty((await GetAsync("/api/connections/flows/open")).EnumerateArray());
    }

    // ---- 4. a reconnect naming a directory ------------------------------------------------------------

    [Fact]
    public async Task A_reconnect_of_a_connection_made_with_no_directory_signs_in_through_the_one_named_and_through_common_when_none_is()
    {
        await SetUpMicrosoftAsync();
        var first = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite("approve");
        var id = (await FlowAsync(first, until: "done")).GetProperty("connection").GetProperty("id").GetString()!;
        Assert.Equal(new string?[] { null }, await TenantColumnAsync("connections"));

        // None named: the connection's own, which is none - Microsoft's common endpoints.
        var none = (await StartDeviceAsync(reconnectId: id)).GetProperty("flowId").GetString()!;
        Assert.Equal(At("common", "devicecode"), _provider.DeviceStarts[^1].Url);
        _provider.DeviceAnswers.Writer.TryWrite("approve");
        await FlowAsync(none, until: "done");
        Assert.Equal(At("common", "token"), _provider.DevicePolls[^1].TokenUrl);
        Assert.Equal(new string?[] { null }, await TenantColumnAsync("connections"));

        // Named, as the slot picker's Reconnect sends it: that directory's endpoints, and kept.
        var named = (await StartDeviceInAsync(TenantGuid, reconnectId: id)).GetProperty("flowId").GetString()!;
        Assert.Equal(At(TenantGuid, "devicecode"), _provider.DeviceStarts[^1].Url);
        _provider.DeviceAnswers.Writer.TryWrite("approve");
        await FlowAsync(named, until: "done");
        Assert.Equal(At(TenantGuid, "token"), _provider.DevicePolls[^1].TokenUrl);
        Assert.Equal([TenantGuid], await TenantColumnAsync("connections"));
    }
}
