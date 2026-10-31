using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// AN MCP SERVER THE PLATFORM RUNS, END TO END THROUGH THE HOST (MCP 3), against a fake stdio server behind a fake
/// <c>npx</c>: the entry's check starts it on a worker, lists and stops it, with a web address's answer and health; a
/// member of a team it is given to lists and calls its tools through <c>/mcp</c>, sharing one process; a call during a
/// crash says it stopped and the next starts it again; editing the entry or its connection stops the process and the
/// next call starts it with the new value; removing the entry stops it. Results are redacted of the values it was given.
/// </summary>
public sealed class McpStdioHostTests : IAsyncLifetime
{
    private const string Token = "runs-token-77aa11";

    private static CancellationToken Ct => McpServerToolsBed.Ct;

    private readonly FakeStdioServer _fake = new();
    private McpStdioServers _servers = null!;
    private McpServerToolsBed _bed = null!;
    private string _connection = "";

    public async ValueTask InitializeAsync()
    {
        _servers = _fake.Servers();
        _bed = await McpServerToolsBed.StartAsync(stdio: _servers);
        var key = await _bed.PostAsync("/api/connections/credentials", new
        {
            kind = "apikey", preset = "other", name = "Runs key", fields = new[] { new { name = "token", value = Token, masked = true } },
        });
        _connection = key.GetProperty("connection").GetProperty("id").GetString()!;
    }

    public async ValueTask DisposeAsync()
    {
        await _bed.DisposeAsync();
        await _servers.DisposeAsync();
        _fake.Dispose();
    }

    private async Task<JsonElement> AddAsync(string name = "local") => await _bed.PostAsync("/api/mcp-servers", new
    {
        name, kind = "runs", command = "npx", package = "@fake/server", version = "1.2.3", args = new[] { "--stdio" },
        env = new[] { new { name = "API_TOKEN", connection = _connection, field = "token" } },
    });

    [Fact]
    public async Task Saving_an_entry_the_platform_runs_checks_it_on_a_worker_with_a_web_addresss_answer_and_health()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");

        var saved = await AddAsync();

        var check = saved.GetProperty("check");
        Assert.Equal("ok", check.GetProperty("outcome").GetString());
        Assert.Equal("local started and listed 5 tools.", check.GetProperty("sentence").GetString());
        Assert.Equal(5, check.GetProperty("toolCount").GetInt32());
        var server = saved.GetProperty("server");
        Assert.Equal("ok", server.GetProperty("health").GetString());
        Assert.Equal(["whoami", "echo", "crash", "sleep", "pingme"], server.GetProperty("tools").EnumerateArray().Select(t => t.GetString()!));

        // THE CHECK'S PROCESS IS STOPPED: nothing runs for the entry until a member calls it.
        var id = server.GetProperty("id").GetString()!;
        Assert.Null(_servers.Pid(id));

        var tested = await _bed.Person.PostAsync($"/api/mcp-servers/{id}/test", null, Ct);
        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
        Assert.Equal("ok", (await tested.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("check").GetProperty("outcome").GetString());

        var rows = await TenantRowsAsync();
        Assert.Equal(2, rows.Count(r => r.StartsWith(TenantActions.McpServerChecked + " ", StringComparison.Ordinal) && r.Contains("\"outcome\":\"ok\"", StringComparison.Ordinal) && r.Contains("local started and listed 5 tools.", StringComparison.Ordinal)));
        Assert.DoesNotContain(rows.Concat(await _bed.LogTextAsync()), row => row.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_member_lists_and_calls_it_through_harness_sharing_one_process_with_its_values_redacted()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        var id = (await AddAsync()).GetProperty("server").GetProperty("id").GetString()!;
        await _bed.PostAsync("/api/teams/Alpha/mcp-servers", new { server = id });

        await _bed.RunAsync("Alpha", "Dev", async invocation =>
        {
            using var mcp = _bed.Mcp(McpServerToolsBed.Key(invocation));
            var names = (await mcp.ListAsync()).Select(t => t["name"]!.GetValue<string>()).ToList();
            Assert.Contains("local_whoami", names);
            Assert.Contains("local_pingme", names);

            var first = await mcp.CallAsync("local_whoami", NoEnv);
            Assert.False(HarnessMcp.IsError(first), HarnessMcp.Text(first));
            var who = JsonDocument.Parse(HarnessMcp.Text(first)).RootElement;
            var second = JsonDocument.Parse(HarnessMcp.Text(await mcp.CallAsync("local_whoami", NoEnv))).RootElement;

            Assert.Equal(who.GetProperty("pid").GetInt32(), second.GetProperty("pid").GetInt32());
            Assert.Equal(who.GetProperty("pid").GetInt32(), _servers.Pid(id));
            Assert.Equal(Sha(Token), who.GetProperty("token_sha").GetString());
            Assert.Equal(["-y", "@fake/server@1.2.3", "--stdio"], who.GetProperty("argv").EnumerateArray().Select(a => a.GetString()!));

            // THE VALUE IT WAS GIVEN never reaches the member, though the server prints its whole environment.
            var printed = HarnessMcp.Text(await mcp.CallAsync("local_whoami"));
            Assert.Contains("API_TOKEN", printed);
            Assert.DoesNotContain(Token, printed);
            Assert.DoesNotContain("HARNESS_KEY", printed);

            var echoed = await mcp.CallAsync("local_echo", new JsonObject { ["said"] = "hello" });
            Assert.Equal("{\"said\": \"hello\"}", HarnessMcp.Text(echoed));
        });
    }

    [Fact]
    public async Task A_call_during_a_crash_says_it_stopped_and_the_next_call_starts_it_again()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        var id = (await AddAsync()).GetProperty("server").GetProperty("id").GetString()!;
        await _bed.PostAsync("/api/teams/Alpha/mcp-servers", new { server = id });

        await _bed.RunAsync("Alpha", "Dev", async invocation =>
        {
            using var mcp = _bed.Mcp(McpServerToolsBed.Key(invocation));
            var before = Pid(await mcp.CallAsync("local_whoami", NoEnv));

            var crashed = await mcp.CallAsync("local_crash");
            Assert.True(HarnessMcp.IsError(crashed));
            Assert.Equal("local stopped; the next call starts it again.", HarnessMcp.Text(crashed));

            var after = Pid(await mcp.CallAsync("local_whoami", NoEnv));
            Assert.NotEqual(before, after);

            // KILLED from outside, the same: the next call starts it and succeeds.
            Assert.Equal(0, FakeStdioServer.kill(after, 9));
            for (var i = 0; i < 100 && _servers.Pid(id) is not null; i++) await Task.Delay(50, Ct);
            Assert.NotEqual(after, Pid(await mcp.CallAsync("local_whoami", NoEnv)));
        });
    }

    [Fact]
    public async Task Editing_its_connection_stops_it_and_the_next_call_starts_it_with_the_new_value()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        var id = (await AddAsync()).GetProperty("server").GetProperty("id").GetString()!;
        await _bed.PostAsync("/api/teams/Alpha/mcp-servers", new { server = id });
        int before = 0;
        await _bed.RunAsync("Alpha", "Dev", async invocation =>
        {
            using var mcp = _bed.Mcp(McpServerToolsBed.Key(invocation));
            before = Pid(await mcp.CallAsync("local_whoami", NoEnv));
        });

        const string rotated = "rotated-token-5c5c";
        var edited = await _bed.Person.PutAsJsonAsync($"/api/connections/{_connection}/credentials",
            new { fields = new[] { new { name = "token", value = rotated, masked = true } } }, Ct);
        Assert.True(edited.IsSuccessStatusCode, await edited.Content.ReadAsStringAsync(Ct));

        Assert.False(FakeStdioServer.Running(before));
        Assert.Null(_servers.Pid(id));

        await _bed.RunAsync("Alpha", "Dev", async invocation =>
        {
            using var mcp = _bed.Mcp(McpServerToolsBed.Key(invocation));
            var who = JsonDocument.Parse(HarnessMcp.Text(await mcp.CallAsync("local_whoami", NoEnv))).RootElement;
            Assert.NotEqual(before, who.GetProperty("pid").GetInt32());
            Assert.Equal(Sha(rotated), who.GetProperty("token_sha").GetString());
            Assert.DoesNotContain(rotated, HarnessMcp.Text(await mcp.CallAsync("local_whoami")));
        });
    }

    [Fact]
    public async Task Editing_the_entry_stops_it_and_removing_the_entry_stops_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        var id = (await AddAsync()).GetProperty("server").GetProperty("id").GetString()!;
        await _bed.PostAsync("/api/teams/Alpha/mcp-servers", new { server = id });
        var pids = new List<int>();
        async Task CallAsync() => await _bed.RunAsync("Alpha", "Dev", async invocation =>
        {
            using var mcp = _bed.Mcp(McpServerToolsBed.Key(invocation));
            pids.Add(Pid(await mcp.CallAsync("local_whoami", NoEnv)));
        });

        await CallAsync();
        var edited = await _bed.Person.PutAsJsonAsync($"/api/mcp-servers/{id}", new
        {
            name = "local", kind = "runs", command = "npx", package = "@fake/server", version = "1.2.4", args = new[] { "--stdio" },
            env = new[] { new { name = "API_TOKEN", connection = _connection, field = "token" } },
        }, Ct);
        Assert.True(edited.IsSuccessStatusCode, await edited.Content.ReadAsStringAsync(Ct));
        Assert.False(FakeStdioServer.Running(pids[0]));

        await CallAsync();
        Assert.NotEqual(pids[0], pids[1]);

        var removed = await _bed.Person.DeleteAsync($"/api/mcp-servers/{id}", Ct);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.False(FakeStdioServer.Running(pids[1]));
        Assert.Null(_servers.Pid(id));
    }

    private async Task<IReadOnlyList<string>> TenantRowsAsync()
    {
        var rows = new List<string>();
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_bed.DataRoot, "messages.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT action || ' ' || coalesce(detail, '') FROM tenant_events ORDER BY seq";
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) rows.Add(reader.GetString(0));
        return rows;
    }

    private static int Pid(JsonObject answer)
    {
        Assert.False(HarnessMcp.IsError(answer), HarnessMcp.Text(answer));
        return JsonDocument.Parse(HarnessMcp.Text(answer)).RootElement.GetProperty("pid").GetInt32();
    }


    /// <summary>whoami without its environment: the result is redacted of the token, and a redacted environment is not JSON.</summary>
    private static JsonObject NoEnv => new() { ["env"] = false };

    private static string Sha(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
