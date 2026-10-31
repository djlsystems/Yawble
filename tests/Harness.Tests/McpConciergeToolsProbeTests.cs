using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Host;
using Harness.Pty;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// Tester probes for B007J (MCP 4), beside <see cref="McpConciergeToolsTests"/>: B's own servers after Reload, a call
/// straight after a switch (to none, and back to A), a Host restart's lost list, a platform-name clash, and binary
/// results that try to leave the Concierge's workspace.
/// </summary>
public sealed class McpConciergeToolsProbeTests
{
    private static CancellationToken Ct => McpServerToolsBed.Ct;

    private const string Echo = "fake_echo_credential";
    private const string OtherEcho = "other_echo_credential";

    [Fact]
    public async Task After_Reload_on_B_Bs_own_server_is_listed_and_As_is_not()
    {
        var terminals = new Terminals();
        await using var bed = await McpServerToolsBed.StartAsync(terminals: terminals);
        await AddServerAsync(bed, "other", "Beta");
        await bed.Services.GetRequiredService<TeamRegistry>().SetLabelAsync("Beta", "Beta Team", Ct);
        await SwitchAsync(bed, "Alpha");

        using (var mcp = await StartAsync(bed, terminals))
        {
            var tools = await mcp.ListAsync();
            Assert.Contains(tools, t => Name(t) == Echo);
            Assert.DoesNotContain(tools, t => Name(t).StartsWith("other_", StringComparison.Ordinal));

            // A held by A but not B: on Beta, Alpha's tool is refused, naming Beta by its label.
            await SwitchAsync(bed, "Beta");
            var refused = await mcp.CallAsync(Echo, new JsonObject { ["message"] = "x" });
            Assert.True(HarnessMcp.IsError(refused), refused.ToJsonString());
            Assert.Equal("fake is not one of Beta Team's MCP servers. Reload the Concierge to get Beta Team's tools.", HarnessMcp.Text(refused));

            var held = await HeldAsync(bed);
            Assert.Equal("Alpha", held.GetProperty("team").GetString());

            // Not listed yet, but Beta holds it: a call by name reaches it (current team's server; recorded, not a defect).
            var unlisted = await mcp.CallAsync(OtherEcho, new JsonObject { ["message"] = "y" });
            Assert.False(HarnessMcp.IsError(unlisted), unlisted.ToJsonString());
        }

        (await bed.Person.DeleteAsync("/api/concierge", Ct)).EnsureSuccessStatusCode();
        using (var reloaded = await StartAsync(bed, terminals))
        {
            var tools = await reloaded.ListAsync();
            Assert.Contains(tools, t => Name(t) == OtherEcho);
            Assert.DoesNotContain(tools, t => Name(t).StartsWith("fake_", StringComparison.Ordinal));
            var called = await reloaded.CallAsync(OtherEcho, new JsonObject { ["message"] = "z" });
            Assert.False(HarnessMcp.IsError(called), called.ToJsonString());
            var held = await HeldAsync(bed);
            Assert.Equal("Beta", held.GetProperty("team").GetString());
            Assert.Equal("Beta Team", held.GetProperty("name").GetString());
        }
    }

    [Fact]
    public async Task A_call_straight_after_switching_to_no_team_is_refused_and_back_on_A_without_Reload_it_works()
    {
        var terminals = new Terminals();
        await using var bed = await McpServerToolsBed.StartAsync(terminals: terminals);
        await SwitchAsync(bed, "Alpha");
        using var mcp = await StartAsync(bed, terminals);

        await SwitchAsync(bed, null);
        var none = await mcp.CallAsync(Echo, new JsonObject { ["message"] = "a" });
        Assert.True(HarnessMcp.IsError(none), none.ToJsonString());
        Assert.Equal(McpRunTools.NoConciergeTeam("fake"), HarnessMcp.Text(none));

        await SwitchAsync(bed, "Beta");
        var beta = await mcp.CallAsync(Echo, new JsonObject { ["message"] = "b" });
        Assert.Equal(McpRunTools.NotTheConciergeTeams("fake", "Beta"), HarnessMcp.Text(beta));

        await SwitchAsync(bed, "Alpha");
        var back = await mcp.CallAsync(Echo, new JsonObject { ["message"] = "c" });
        Assert.False(HarnessMcp.IsError(back), back.ToJsonString());
        Assert.Contains("you said c", HarnessMcp.Text(back), StringComparison.Ordinal);

        // Only the two calls on Alpha reached the server.
        Assert.Equal(["echo_credential"], bed.Upstream.Called.Distinct());
        Assert.Single(bed.Upstream.Called);
        Assert.Equal(1, terminals.Spawned);
        Assert.Equal(0, terminals.Ended);
    }

    [Fact]
    public async Task Calls_racing_team_switches_never_reach_the_server_while_a_team_without_it_is_current()
    {
        var terminals = new Terminals();
        await using var bed = await McpServerToolsBed.StartAsync(terminals: terminals);
        await SwitchAsync(bed, "Alpha");
        using var mcp = await StartAsync(bed, terminals);

        for (var i = 0; i < 10; i++)
        {
            await SwitchAsync(bed, i % 2 == 0 ? "Beta" : null);
            var answer = await mcp.CallAsync(Echo, new JsonObject { ["message"] = $"m{i}" });
            Assert.True(HarnessMcp.IsError(answer), answer.ToJsonString());
            await SwitchAsync(bed, "Alpha");
        }

        Assert.Empty(bed.Upstream.Called);
    }

    [Fact]
    public async Task After_the_list_is_lost_as_at_a_Host_restart_nothing_is_listed_and_calls_are_still_checked_against_the_current_team()
    {
        var terminals = new Terminals();
        await using var bed = await McpServerToolsBed.StartAsync(terminals: terminals);
        await SwitchAsync(bed, "Alpha");
        using var mcp = await StartAsync(bed, terminals);
        var tools = bed.Services.GetRequiredService<McpRunTools>();

        // What a restarted Host holds for a surviving session: no list in memory.
        tools.EndConcierge(bed.PersonId);

        Assert.DoesNotContain(await mcp.ListAsync(), t => Name(t).StartsWith("fake_", StringComparison.Ordinal));
        Assert.False((await HeldAsync(bed)).GetProperty("started").GetBoolean());

        await SwitchAsync(bed, "Beta");
        var refused = await mcp.CallAsync(Echo, new JsonObject { ["message"] = "x" });
        Assert.Equal(McpRunTools.NotTheConciergeTeams("fake", "Beta"), HarnessMcp.Text(refused));
        Assert.Empty(bed.Upstream.Called);

        await SwitchAsync(bed, null);
        var none = await mcp.CallAsync(Echo, new JsonObject { ["message"] = "x" });
        Assert.Equal(McpRunTools.NoConciergeTeam("fake"), HarnessMcp.Text(none));

        // On Alpha the unlisted tool still answers (Alpha holds it), and a binary result has no workspace to go to.
        await SwitchAsync(bed, "Alpha");
        var drawn = await mcp.CallAsync("fake_draw");
        Assert.Contains("not in a run with a working copy", HarnessMcp.Text(drawn), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tool_that_now_takes_a_platform_tools_name_is_not_listed_and_the_platforms_answers()
    {
        var terminals = new Terminals();
        await using var bed = await McpServerToolsBed.StartAsync(terminals: terminals);
        await AddServerAsync(bed, "skills", "Alpha");
        bed.Upstream.Extra.Add("get"); // after the save, so the save's own clash check did not see it
        await SwitchAsync(bed, "Alpha");
        using var mcp = await StartAsync(bed, terminals);

        var listed = await mcp.ListAsync();
        Assert.Single(listed, t => Name(t) == "skills_get");
        Assert.Contains(listed, t => Name(t) == "skills_echo_credential");

        var answer = await mcp.CallAsync("skills_get", new JsonObject { ["name"] = "concierge" });
        Assert.DoesNotContain("get", bed.Upstream.Called);
        Assert.NotNull(answer["result"]);
    }

    [Fact]
    public async Task A_server_name_with_dots_or_a_slash_is_refused_at_save()
    {
        await using var bed = await McpServerToolsBed.StartAsync();
        var connection = await CredentialAsync(bed);
        foreach (var name in new[] { "..", "../x", "a/b", "a\\b", "..%2f" })
        {
            var response = await bed.Person.PostAsJsonAsync("/api/mcp-servers", new { name, kind = "url", url = McpServerToolsBed.Url, connection }, Ct);
            Assert.False(response.IsSuccessStatusCode, $"{name}: {await response.Content.ReadAsStringAsync(Ct)}");
        }
    }

    [Fact]
    public async Task A_binary_result_through_a_linked_folder_in_the_workspace_is_not_written_outside()
    {
        if (!OperatingSystem.IsLinux()) return;

        var terminals = new Terminals();
        await using var bed = await McpServerToolsBed.StartAsync(terminals: terminals);
        await SwitchAsync(bed, "Alpha");
        using var mcp = await StartAsync(bed, terminals);
        var workspace = ConciergeWorkspaces.TryExisting(bed.Services.GetRequiredService<TeamPaths>(), bed.PersonId)!;
        var outside = Path.Combine(bed.DataRoot, "outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);

        // The server's own default folder is a link out.
        Directory.CreateDirectory(Path.Combine(workspace, McpRunTools.Folder));
        Directory.CreateSymbolicLink(Path.Combine(workspace, McpRunTools.Folder, "fake"), outside);
        var viaDefault = await mcp.CallAsync("fake_draw");
        Assert.True(HarnessMcp.IsError(viaDefault), viaDefault.ToJsonString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));

        // A named output path through a link out.
        Directory.CreateSymbolicLink(Path.Combine(workspace, "out"), outside);
        var viaNamed = await mcp.CallAsync("fake_draw", new JsonObject { ["outputPath"] = "out/x.png" });
        Assert.True(HarnessMcp.IsError(viaNamed), viaNamed.ToJsonString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));

        // A named output path that is itself a link to a file outside.
        var target = Path.Combine(outside, "victim.png");
        await File.WriteAllBytesAsync(target, [1, 2, 3], Ct);
        File.CreateSymbolicLink(Path.Combine(workspace, "victim.png"), target);
        var viaFile = await mcp.CallAsync("fake_draw", new JsonObject { ["outputPath"] = "victim.png" });
        Assert.True(HarnessMcp.IsError(viaFile), viaFile.ToJsonString());
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(target, Ct));

        // Dot-dot and absolute paths.
        foreach (var path in new[] { "../x.png", "a/../../x.png", "/tmp/x.png", ".harness/mcp/../../../x.png" })
        {
            var answer = await mcp.CallAsync("fake_draw", new JsonObject { ["outputPath"] = path });
            Assert.True(HarnessMcp.IsError(answer), $"{path}: {answer.ToJsonString()}");
        }

        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(workspace)!, "x.png")));
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static async Task<string> CredentialAsync(McpServerToolsBed bed)
    {
        var key = await bed.PostAsync("/api/connections/credentials", new
        {
            kind = "apikey", preset = "other", name = "Fake key " + Guid.NewGuid().ToString("N")[..6],
            fields = new[] { new { name = "token", value = McpServerToolsBed.Token, masked = true } },
        });
        return key.GetProperty("connection").GetProperty("id").GetString()!;
    }

    private static async Task AddServerAsync(McpServerToolsBed bed, string name, string team)
    {
        var connection = await CredentialAsync(bed);
        var saved = await bed.PostAsync("/api/mcp-servers", new { name, kind = "url", url = McpServerToolsBed.Url, connection });
        Assert.Equal("ok", saved.GetProperty("check").GetProperty("outcome").GetString());
        await bed.PostAsync($"/api/teams/{team}/mcp-servers", new { server = saved.GetProperty("server").GetProperty("id").GetString() });
    }

    private static string Name(JsonObject tool) => tool["name"]!.GetValue<string>();

    private static async Task SwitchAsync(McpServerToolsBed bed, string? team) =>
        (await bed.Person.PutAsJsonAsync("/api/me/current-team", new { team }, Ct)).EnsureSuccessStatusCode();

    private static async Task<HarnessMcp> StartAsync(McpServerToolsBed bed, Terminals terminals)
    {
        await bed.Services.GetRequiredService<ConciergeSessionStore>().AttachAsync(new ConciergeSessionKey(bed.PersonId), "", 80, 24, Ct);
        return bed.Mcp(terminals.Spec!.Env!["HARNESS_KEY"]);
    }

    private static async Task<JsonElement> HeldAsync(McpServerToolsBed bed)
    {
        var response = await bed.Person.GetAsync("/api/concierge/mcp-tools", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private sealed class Terminals : IPtyEngine
    {
        private int _spawned;
        private int _ended;

        public PtySpec? Spec { get; private set; }

        public int Spawned => _spawned;

        public int Ended => _ended;

        public Task<IPtySession> SpawnAsync(PtySpec spec, CancellationToken ct)
        {
            Spec = spec;
            Interlocked.Increment(ref _spawned);
            return Task.FromResult<IPtySession>(new Silent(this));
        }

        private sealed class Silent(Terminals owner) : IPtySession
        {
#pragma warning disable CS0067
            public event Action<byte[]>? Output;
            public event Action<int>? Exited;
#pragma warning restore CS0067

            public void Write(ReadOnlySpan<byte> bytes) { }

            public void Resize(int cols, int rows) { }

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner._ended);
                return ValueTask.CompletedTask;
            }
        }
    }
}
