using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Harness.Pty;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// MCP 4 - THE CONCIERGE USES ITS ACTIVE TEAM'S MCP SERVERS, on the real Host against the fake web address server
/// of <see cref="McpServerToolsBed"/> (Alpha holds it, Beta does not). The Concierge's terminal is a silent one that
/// keeps the launch it was given, so its own key calls <c>/mcp</c> as its CLI would. One test per Done-when line.
/// </summary>
public sealed class McpConciergeToolsTests
{
    private static CancellationToken Ct => McpServerToolsBed.Ct;

    private const string Echo = "fake_echo_credential";

    [Fact]
    public async Task Started_on_A_it_calls_its_tool_then_on_B_the_call_is_refused_naming_B_and_Reload_and_after_Reload_it_is_not_listed()
    {
        var terminals = new Terminals();
        await using var bed = await McpServerToolsBed.StartAsync(terminals: terminals);
        await SwitchAsync(bed, "Alpha");

        using (var mcp = await StartAsync(bed, terminals))
        {
            Assert.Contains(await mcp.ListAsync(), t => Name(t) == Echo);

            var called = await mcp.CallAsync(Echo, new JsonObject { ["message"] = "hello" });
            Assert.False(HarnessMcp.IsError(called), called.ToJsonString());
            Assert.Contains("you said hello", HarnessMcp.Text(called), StringComparison.Ordinal);

            // THE PERSON OPENS BETA: the same session, still listing Alpha's tools, is refused at the call.
            await SwitchAsync(bed, "Beta");

            var refused = await mcp.CallAsync(Echo, new JsonObject { ["message"] = "hello" });
            Assert.True(HarnessMcp.IsError(refused), refused.ToJsonString());
            Assert.Equal("fake is not one of Beta's MCP servers. Reload the Concierge to get Beta's tools.", HarnessMcp.Text(refused));
            Assert.Contains(await mcp.ListAsync(), t => Name(t) == Echo);
            Assert.Equal(["echo_credential"], bed.Upstream.Called);

            // WHAT THE HEADER'S NOTE READS: the session's tools are still Alpha's.
            var held = await HeldAsync(bed);
            Assert.True(held.GetProperty("started").GetBoolean());
            Assert.Equal("Alpha", held.GetProperty("team").GetString());
        }

        // RELOAD, AS THE PANEL'S BUTTON DOES IT: end the session, and the next attach starts one.
        (await bed.Person.DeleteAsync("/api/concierge", Ct)).EnsureSuccessStatusCode();
        using (var reloaded = await StartAsync(bed, terminals))
        {
            Assert.DoesNotContain(await reloaded.ListAsync(), t => Name(t).StartsWith("fake_", StringComparison.Ordinal));
            Assert.Contains(await reloaded.ListAsync(), t => Name(t) == "skills_get");
            Assert.Equal("Beta", (await HeldAsync(bed)).GetProperty("team").GetString());
        }
    }

    [Fact]
    public async Task With_no_current_team_it_lists_no_MCP_server_tools_and_a_call_says_to_open_a_team()
    {
        var terminals = new Terminals();
        await using var bed = await McpServerToolsBed.StartAsync(terminals: terminals);

        using var mcp = await StartAsync(bed, terminals);

        var tools = await mcp.ListAsync();
        Assert.DoesNotContain(tools, t => Name(t).StartsWith("fake_", StringComparison.Ordinal));
        Assert.Contains(tools, t => Name(t) == "skills_get");

        var refused = await mcp.CallAsync(Echo, new JsonObject { ["message"] = "hello" });
        Assert.True(HarnessMcp.IsError(refused), refused.ToJsonString());
        Assert.Equal(McpRunTools.NoConciergeTeam("fake"), HarnessMcp.Text(refused));
        Assert.Empty(bed.Upstream.Called);

        var held = await HeldAsync(bed);
        Assert.True(held.GetProperty("started").GetBoolean());
        Assert.Equal(JsonValueKind.Null, held.GetProperty("team").ValueKind);
    }

    [Fact]
    public async Task A_team_switch_never_restarts_the_session()
    {
        var terminals = new Terminals();
        await using var bed = await McpServerToolsBed.StartAsync(terminals: terminals);
        await SwitchAsync(bed, "Alpha");
        using var mcp = await StartAsync(bed, terminals);
        var key = new ConciergeSessionKey(bed.PersonId);
        var consoles = bed.Services.GetRequiredService<ConciergeSessionStore>();

        await SwitchAsync(bed, "Beta");
        await SwitchAsync(bed, null);
        await SwitchAsync(bed, "Alpha");

        Assert.Equal(1, terminals.Spawned);
        Assert.Equal(0, terminals.Ended);
        Assert.True(consoles.Has(key));
        Assert.Equal("Alpha", (await HeldAsync(bed)).GetProperty("team").GetString());

        // AND THE KEY IT STARTED WITH STILL WORKS: nothing rotated it behind the session's back.
        var called = await mcp.CallAsync(Echo, new JsonObject { ["message"] = "again" });
        Assert.False(HarnessMcp.IsError(called), called.ToJsonString());
    }

    [Fact]
    public async Task A_binary_result_is_written_in_the_concierges_own_workspace()
    {
        var terminals = new Terminals();
        await using var bed = await McpServerToolsBed.StartAsync(terminals: terminals);
        await SwitchAsync(bed, "Alpha");
        using var mcp = await StartAsync(bed, terminals);
        var workspace = ConciergeWorkspaces.TryExisting(bed.Services.GetRequiredService<TeamPaths>(), bed.PersonId)!;

        var drawn = await mcp.CallAsync("fake_draw");
        Assert.False(HarnessMcp.IsError(drawn), drawn.ToJsonString());

        var written = JsonNode.Parse(HarnessMcp.Text(drawn).Split('\n')[0])!;
        var path = written["path"]!.GetValue<string>();
        Assert.StartsWith(Path.Combine(workspace, McpRunTools.Folder, "fake") + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
        if (OperatingSystem.IsLinux()) Assert.Equal(FakeToolServer.Png, await File.ReadAllBytesAsync(path, Ct));

        // A NAMED PATH IS INSIDE THE WORKSPACE, as a member's is inside its working copy.
        var outside = await mcp.CallAsync("fake_draw", new JsonObject { ["outputPath"] = "../elsewhere.png" });
        Assert.True(HarnessMcp.IsError(outside), outside.ToJsonString());
    }

    [Fact]
    public async Task The_list_goes_with_the_session_and_a_person_who_never_started_one_holds_none()
    {
        var terminals = new Terminals();
        await using var bed = await McpServerToolsBed.StartAsync(terminals: terminals);
        Assert.False((await HeldAsync(bed)).GetProperty("started").GetBoolean());

        await SwitchAsync(bed, "Alpha");
        using var mcp = await StartAsync(bed, terminals);
        Assert.Contains(await mcp.ListAsync(), t => Name(t) == Echo);

        await bed.Services.GetRequiredService<ConciergeSessionStore>().EndAsync(new ConciergeSessionKey(bed.PersonId));

        Assert.False((await HeldAsync(bed)).GetProperty("started").GetBoolean());
        Assert.Null(bed.Services.GetRequiredService<McpRunTools>().RunOf(ConciergeLaunchFactory.PrincipalId(bed.PersonId)));
    }

    private static string Name(JsonObject tool) => tool["name"]!.GetValue<string>();

    private static async Task SwitchAsync(McpServerToolsBed bed, string? team) =>
        (await bed.Person.PutAsJsonAsync("/api/me/current-team", new { team }, Ct)).EnsureSuccessStatusCode();

    /// <summary>Attaches the person's Concierge, starting it, and answers a client of <c>/mcp</c> with its own key.</summary>
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

    /// <summary>A terminal that prints nothing and never exits, keeping the last spec it was spawned with.</summary>
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
#pragma warning disable CS0067 // never raised: this terminal is silent
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
