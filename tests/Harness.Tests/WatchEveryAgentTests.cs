using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Tests.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// Every built-in headless agent can be watched, and every finished run can be read again.
/// The renderers are tested against real transcripts kept under <c>Fixtures/LiveView</c>, each from
/// a real run that read a file, ran a command and called an MCP tool, redacted of account and request identifiers. The Grok one is a subset of whole lines from
/// its run's <c>updates.jsonl</c>, which is 656 KB.
/// </summary>
public sealed class WatchEveryAgentCatalogTests
{
    [Fact]
    public void Every_built_in_headless_agent_has_a_live_view_this_host_accepts()
    {
        var views = AgentCatalogFile.BuiltIns()
            .Where(d => d.Mode == AgentMode.Headless && d.Launch.LanguageModel)
            .ToDictionary(d => d.Name, d => d.LiveView);

        Assert.Equal(
            new AgentLiveView("~/.grok/sessions/{workspaceEncoded}/{sessionId}/updates.jsonl", LiveView.GrokUpdates),
            views["grok-headless"]);
        Assert.Equal(
            new AgentLiveView("~/.copilot/session-state/{sessionId}/events.jsonl", LiveView.CopilotEvents),
            views["copilot-headless"]);
        Assert.Equal(
            new AgentLiveView(null, LiveView.CodexRollout,
                new AgentLiveViewFind("~/.codex/sessions", "*/*/*/rollout-*.jsonl", LiveView.CwdFromFirstLine)),
            views["codex-headless"]);

        Assert.All(views, pair =>
        {
            Assert.NotNull(pair.Value);
            Assert.Null(LiveView.Refusal(pair.Value!));
        });
    }

    [Theory]
    [InlineData("grok-headless")]
    [InlineData("copilot-headless")]
    public void An_agent_that_takes_a_session_id_is_passed_this_runs_one_before_its_prompt(string preset)
    {
        var arguments = AgentCatalogFile.BuiltIns().Single(d => d.Name == preset).Launch.Arguments.ToList();

        var at = arguments.IndexOf("--session-id");
        Assert.Equal("{sessionId}", arguments[at + 1]);
        Assert.True(at < arguments.IndexOf("-p"));
    }

    [Fact]
    public void The_grok_workspace_folder_is_the_working_directory_percent_encoded()
    {
        // As Grok writes it: /data/agent-home/.grok/sessions/%2Fdata%2Fteams%2F...%2FResearcherOttilie/<id>/.
        Assert.Equal(
            "/home/.grok/sessions/%2Fdata%2Fteams%2Fsample-team-for-transcript-run%2Fworkspaces%2FResearcherOttilie/s1/updates.jsonl",
            LiveView.Resolve(
                new AgentLiveView("~/.grok/sessions/{workspaceEncoded}/{sessionId}/updates.jsonl", LiveView.GrokUpdates),
                "/home", "/data/teams/sample-team-for-transcript-run/workspaces/ResearcherOttilie", "s1"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("~/x.jsonl", "~/sessions")]
    [InlineData(null, "/etc")]
    [InlineData(null, "~/../x")]
    public void A_live_view_names_exactly_one_of_path_and_find_both_under_the_agent_home(string? path, string? folder)
    {
        var view = new AgentLiveView(path, LiveView.CodexRollout,
            folder is null ? null : new AgentLiveViewFind(folder, "*.jsonl", LiveView.CwdFromFirstLine));

        Assert.NotNull(LiveView.Refusal(view));
    }

    [Theory]
    [InlineData("../x.jsonl", LiveView.CwdFromFirstLine)]
    [InlineData("/x.jsonl", LiveView.CwdFromFirstLine)]
    [InlineData("*.jsonl", "guess")]
    public void A_find_pattern_that_climbs_or_an_unknown_cwd_rule_is_refused(string pattern, string cwdFrom)
    {
        Assert.NotNull(LiveView.Refusal(new AgentLiveView(null, LiveView.CodexRollout, new AgentLiveViewFind("~/s", pattern, cwdFrom))));
    }

    [Fact]
    public void Watchable_is_true_for_a_readable_live_view_and_false_for_none()
    {
        var claude = AgentCatalogFile.BuiltIns().Single(d => d.Name == "claude-headless").LiveView;
        var echo = AgentCatalogFile.BuiltIns().Single(d => d.Name == "echo").LiveView;

        Assert.True(LiveView.Watchable(claude, AgentLaunchUser.Same("agent", "test")));
        Assert.False(LiveView.Watchable(echo, AgentLaunchUser.Same("agent", "test")));
        Assert.False(LiveView.Watchable(new AgentLiveView("/etc/passwd", LiveView.ClaudeJsonl), null));
    }
}

/// <summary>Each renderer turns its recorded fixture into the expected display lines, including a line that does not parse.</summary>
public sealed class AgentTranscriptRenderTests
{
    // Split, as BrandLeakTests' own file does, so this file does not carry the brand the fixture does.
    private static readonly string Brand = "Yaw" + "ble";

    private static string Wire(string format, string file, string extra = "") =>
        string.Concat(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LiveView", file))
            .Append(extra)
            .Select(raw => TranscriptLines.Wire(format, raw)));

    [Fact]
    public void Copilot_events_render_to_their_lines()
    {
        Assert.Equal(
            """
            2026-09-26T15:18:04.260Z	User: Do exactly three things, in order: 1) read the file notes.txt in this folder with your file viewing tool; 2) run the shell command: echo measured-run && ls; 3) call the MCP tool team_current from the harness server. Then reply with o…
            2026-09-26T15:18:05.988Z	Read /data/teams/sample-team-for-transcript-run/workspaces/DeveloperCasimir/measure/copilot/notes.txt
            2026-09-26T15:18:05.989Z	Bash: echo measured-run && ls
            2026-09-26T15:18:05.990Z	MCP: team_current
            2026-09-26T15:18:06.011Z	alpha line (21 bytes)
            2026-09-26T15:18:06.012Z	HTTP 403 (98 bytes)
            2026-09-26T15:18:06.014Z	measured-run (98 bytes)
            2026-09-26T15:18:07.355Z	I saw notes.txt contents “alpha line” and “beta line,” the shell printed “measured-run” and listed the folder, and the team current check returned no current team for this principal.
            	{"type":"assistant.message","data":{"content":"cut off

            """.ReplaceLineEndings("\n"),
            Wire(LiveView.CopilotEvents, "copilot-events.jsonl", """{"type":"assistant.message","data":{"content":"cut off"""));
    }

    [Fact]
    public void Codex_rollout_renders_to_its_lines()
    {
        Assert.Equal(
            """
            2026-09-26T15:18:27.089Z	User: Do exactly three things, in order: 1) read the file notes.txt in this folder; 2) run the shell command: echo measured-run && ls; 3) call the MCP tool team_current from the harness server. Then reply with one short sentence summarisin…
            2026-09-26T15:18:29.746Z	I’ll perform those three steps in order.
            2026-09-26T15:18:32.725Z	Read notes.txt
            2026-09-26T15:18:32.725Z	alpha line (21 bytes)
            2026-09-26T15:18:32.771Z	Bash: echo measured-run && ls
            2026-09-26T15:18:32.771Z	measured-run (51 bytes)
            2026-09-26T15:18:32.786Z	MCP: team_current
            2026-09-26T15:18:32.786Z	HTTP 403 (98 bytes)
            2026-09-26T15:18:37.746Z	notes.txt contained “alpha line” and “beta line”; the command printed “measured-run” and four filenames; team_current returned HTTP 403 because this principal has no current team.
            	not json

            """.ReplaceLineEndings("\n"),
            Wire(LiveView.CodexRollout, "codex-rollout.jsonl", "not json"));
    }

    [Fact]
    public void Grok_updates_render_to_their_lines()
    {
        var lines = Wire(LiveView.GrokUpdates, "grok-updates.jsonl", "{\"timestamp\":").Split('\n');

        Assert.Equal(
            [
                $"2026-09-26T15:16:03.000Z\tUser: You are the measurement run for Grok (a short task: read one file, run one command, call one platform tool and report what each one returned to you.). Your own session is the thing being measured, so do these steps for real, in this …",
                "2026-09-26T15:16:11.000Z\tI'll run the measurement steps in this session: read a file, run the shell commands, call harness `status`, then locate this session folder and write the report.",
                $"2026-09-26T15:16:11.000Z\ttodo_write: {{\"todos\":[{{\"id\":\"1\",\"content\":\"Ensure worktree exists; read a file from {Brand} clone\",\"status\":\"in_progress\"}},{{\"id\":\"2\",\"content\":\"Run ls sessions + pwd; call harness MCP status\",\"status\":\"pending\"}},{{\"id\":\"3\",\"content\":\"Find ow…",
                $"2026-09-26T15:16:11.000Z\tRead /data/teams/sample-team-for-transcript-run/repos/{Brand}/main/README.md",
                $"2026-09-26T15:16:11.000Z\tBash: ls -la /data/teams/sample-team-for-transcript-run/repos/{Brand}/wt_ResearcherOttilie_435 2>/dev/null; echo \"---\"; ls /data/agent-home/.grok/sessions; echo \"---PWD---\"; pwd; echo \"---DATE---\"; date -u +\"%Y-%m-%dT%H:%M:%SZ\"; echo \"---WH…",
                "2026-09-26T15:16:11.000Z\tsearch_tool: harness status",
                $"2026-09-26T15:16:11.000Z\t- [in_progress] 1: Ensure worktree exists; read a file from {Brand} clone (297 bytes)",
                $"2026-09-26T15:16:11.000Z\t1→# {Brand} core (6547 bytes)",
                "2026-09-26T15:16:21.000Z\tREADME and shell checks are done. Next I’ll call harness `status`, inspect this session folder, and confirm the worktree.",
                "2026-09-26T15:16:21.000Z\tMCP: status",
                "2026-09-26T15:16:21.000Z\tBash: echo \"=== session workspace dir ===\"; ls -la \"/data/agent-home/.grok/sessions/%2Fdata%2Fteams%2Fsample-team-for-transcript-run%2Fworkspaces%2FResearcherOttilie\"; echo \"=== nested session ids ===\"; find \"/data/agent-home/.grok/session…",
                "2026-09-26T15:16:21.000Z\tHTTP 200 (2712 bytes)",
                "2026-09-26T15:16:23.000Z\t=== session workspace dir === (4436 bytes)",
                "2026-09-26T15:18:39.000Z\tMeasurement report is at `/data/teams/sample-team-for-transcript-run/shared/grok-measurement.md`. **Session folder:** `/data/agent-home/.grok/sessions/%2Fdata%2Fteams%2Fsample-team-for-transcript-run%2Fworkspaces%2FResearcherOttilie/01a0de…",
                "\t{\"timestamp\":",
                "",
            ],
            lines);
    }
}

/// <summary>
/// <c>find</c> picks the file written after launch for this workspace, and not a file from another
/// workspace or from an earlier run in the same one, for each of the three layouts - read directly
/// and through the commands the Host runs as the agent (here with no prefix).
/// </summary>
public sealed class TranscriptFindTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("harness-find-").FullName;

    private const string Workspace = "/data/teams/Team-1/workspaces/Worker.One";
    private const string Other = "/data/teams/Team-1/workspaces/Worker.Two";

    private static readonly DateTimeOffset Launch = DateTimeOffset.UtcNow.AddMinutes(-1);

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Write(string relative, string text, DateTimeOffset written)
    {
        var path = Path.Combine(_home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, written.UtcDateTime);
        return path;
    }

    private Task<string?> FindAsync(AgentLiveViewFind find, bool asAgent) =>
        asAgent
            ? AgentFiles.FindByCommandAsync([], find, _home, Workspace, Launch, Ct)
            : AgentFiles.FindAsync(find, _home, Workspace, Launch, null, Ct);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Grok_by_the_percent_encoded_workspace_folder(bool asAgent)
    {
        var mine = Uri.EscapeDataString(Workspace);
        Write($".grok/sessions/{mine}/earlier/updates.jsonl", "{}\n", Launch.AddMinutes(-5));
        Write($".grok/sessions/{Uri.EscapeDataString(Other)}/theirs/updates.jsonl", "{}\n", Launch.AddSeconds(3));
        var expected = Write($".grok/sessions/{mine}/this-run/updates.jsonl", "{}\n", Launch.AddSeconds(1));

        Assert.Equal(expected, await FindAsync(
            new AgentLiveViewFind("~/.grok/sessions", "*/*/updates.jsonl", LiveView.CwdFromFolderName), asAgent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Copilot_by_workspace_yaml(bool asAgent)
    {
        Write(".copilot/session-state/earlier/workspace.yaml", $"id: earlier\ncwd: {Workspace}\n", Launch.AddMinutes(-5));
        Write(".copilot/session-state/earlier/events.jsonl", "{}\n", Launch.AddMinutes(-5));
        Write(".copilot/session-state/theirs/workspace.yaml", $"id: theirs\ncwd: {Other}\n", Launch.AddSeconds(3));
        Write(".copilot/session-state/theirs/events.jsonl", "{}\n", Launch.AddSeconds(3));
        Write(".copilot/session-state/this-run/workspace.yaml", $"id: this-run\ncwd: {Workspace}\nclient_name: github/cli\n", Launch.AddSeconds(1));
        var expected = Write(".copilot/session-state/this-run/events.jsonl", "{}\n", Launch.AddSeconds(1));

        Assert.Equal(expected, await FindAsync(
            new AgentLiveViewFind("~/.copilot/session-state", "*/events.jsonl", LiveView.CwdFromWorkspaceYaml), asAgent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Codex_by_the_first_lines_cwd(bool asAgent)
    {
        static string Meta(string cwd) =>
            JsonSerializer.Serialize(new { timestamp = "2026-09-26T15:18:25.522Z", type = "session_meta", payload = new { id = "x", cwd } }) + "\n{}\n";

        Write(".codex/sessions/2026/09/26/rollout-a-earlier.jsonl", Meta(Workspace), Launch.AddMinutes(-5));
        Write(".codex/sessions/2026/09/26/rollout-b-theirs.jsonl", Meta(Other), Launch.AddSeconds(3));
        Write(".codex/sessions/2026/09/26/notes.jsonl", Meta(Workspace), Launch.AddSeconds(4));
        var expected = Write(".codex/sessions/2026/09/26/rollout-c-this-run.jsonl", Meta(Workspace), Launch.AddSeconds(1));

        Assert.Equal(expected, await FindAsync(
            new AgentLiveViewFind("~/.codex/sessions", "*/*/*/rollout-*.jsonl", LiveView.CwdFromFirstLine), asAgent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nothing_written_since_launch_for_this_workspace_is_nothing(bool asAgent)
    {
        Write(".codex/sessions/2026/09/26/rollout-a.jsonl", """{"type":"session_meta","payload":{"cwd":"/elsewhere"}}""", Launch.AddSeconds(1));

        Assert.Null(await FindAsync(
            new AgentLiveViewFind("~/.codex/sessions", "*/*/*/rollout-*.jsonl", LiveView.CwdFromFirstLine), asAgent));
        Assert.Null(await FindAsync(
            new AgentLiveViewFind("~/.codex/missing", "*/*/*/rollout-*.jsonl", LiveView.CwdFromFirstLine), asAgent));
    }

    [Fact]
    public async Task A_finished_transcript_is_read_whole_and_a_gone_one_says_so_both_ways()
    {
        var path = Write("t.jsonl", "one\ntwo\n", Launch);

        Assert.Equal("one\ntwo\n", (await AgentFiles.ReadAllAsync(path, null, Ct)).Text);
        Assert.Equal("one\ntwo\n", (await AgentFiles.ReadAllByCommandAsync([], path, Ct)).Text);
        Assert.True((await AgentFiles.ReadAllAsync(path + ".gone", null, Ct)).Gone);
        Assert.True((await AgentFiles.ReadAllByCommandAsync([], path + ".gone", Ct)).Gone);
    }

    [Fact]
    public async Task A_transcript_there_but_unreadable_says_why_both_ways_and_is_not_gone()
    {
        Assert.SkipWhen(Environment.UserName == "root", "root reads a mode-000 file.");
        var path = Write("locked.jsonl", "secret\n", Launch);
        File.SetUnixFileMode(path, UnixFileMode.None);

        foreach (var read in new[] { await AgentFiles.ReadAllAsync(path, null, Ct), await AgentFiles.ReadAllByCommandAsync([], path, Ct) })
        {
            Assert.Null(read.Text);
            Assert.False(read.Gone);
            Assert.False(string.IsNullOrWhiteSpace(read.Unreadable));
        }
    }
}

/// <summary>
/// The runner records the transcript each run wrote on its result, found after launch when the
/// preset says so, and nothing else about the run changes: output and usage are byte-identical with
/// and without a live view.
/// </summary>
public sealed class TranscriptRecordedRunnerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harness-recorded-").FullName;

    private string Home => Path.Combine(_root, "agent-home");

    private string Workspace => Path.Combine(_root, "ws.one");

    public TranscriptRecordedRunnerTests()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Workspace);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    // Writes a Claude transcript named by the session id, and a Codex-style rollout it names itself,
    // then prints one Claude result document.
    private const string Fake = """
        cat >/dev/null
        d="$HOME/.claude/projects/$(pwd | tr '/.' '--')"; mkdir -p "$d"
        printf '%s\n' '{"type":"assistant","message":{"content":[{"type":"text","text":"working"}]}}' >> "$d/$1.jsonl"
        r="$HOME/.codex/sessions/2026/09/26"; mkdir -p "$r"
        printf '{"type":"session_meta","payload":{"cwd":"%s"}}\n' "$(pwd)" > "$r/rollout-x-$1.jsonl"
        echo '{"type":"result","result":"done","usage":{"input_tokens":3,"output_tokens":5,"cache_read_input_tokens":7,"cache_creation_input_tokens":11}}'
        """;

    private Task<AgentResult> RunAsync(AgentLiveView? view, LiveRuns? live = null) =>
        new ProcessAgentRunner(
            new AgentCatalog([new AgentDefinition("fake", AgentMode.Headless,
                new AgentLaunch("bash", ["-c", Fake, "fake", "{sessionId}"], UsageFormat: "claude-json"), LiveView: view)]),
            new RunHeartbeat(), live: live)
            .RunAsync(new AgentInvocation(
                new ContainerId("alpha", "worker"), "You are a fake.", "hello", Workspace,
                new Dictionary<string, string> { ["HOME"] = Home }, Agent: "fake"), Ct);

    [Fact]
    public async Task Output_and_usage_are_byte_identical_with_and_without_a_live_view_and_only_the_transcript_differs()
    {
        var without = await RunAsync(null, new LiveRuns());
        var with = await RunAsync(new AgentLiveView("~/.claude/projects/{workspaceDashed}/{sessionId}.jsonl", LiveView.ClaudeJsonl), new LiveRuns());

        Assert.Equal(0, without.ExitCode);
        Assert.Equal(without.ExitCode, with.ExitCode);
        Assert.Equal(without.Output, with.Output);
        Assert.Equal(without.Usage, with.Usage);
        Assert.NotNull(without.Usage);

        Assert.Null(without.AgentTranscript);
        Assert.NotNull(with.AgentTranscript);
        Assert.Equal(LiveView.ClaudeJsonl, with.AgentTranscript!.Format);
        Assert.StartsWith(Path.Combine(Home, ".claude", "projects", LiveView.Dashed(Workspace)), with.AgentTranscript.Path);
        Assert.True(File.Exists(with.AgentTranscript.Path));

        Assert.Equal(without with { AgentTranscript = with.AgentTranscript, ProcessId = with.ProcessId }, with);
    }

    [Fact]
    public async Task A_transcript_the_agent_names_itself_is_found_and_recorded_without_a_watcher()
    {
        var result = await RunAsync(new AgentLiveView(null, LiveView.CodexRollout,
            new AgentLiveViewFind("~/.codex/sessions", "*/*/*/rollout-*.jsonl", LiveView.CwdFromFirstLine)));

        Assert.Equal(LiveView.CodexRollout, result.AgentTranscript?.Format);
        Assert.Equal(Path.Combine(Home, ".codex", "sessions", "2026", "09", "26"), Path.GetDirectoryName(result.AgentTranscript!.Path));
    }

    [Fact]
    public async Task A_path_the_agent_never_wrote_is_not_recorded()
    {
        var result = await RunAsync(new AgentLiveView("~/.nowhere/{sessionId}.jsonl", LiveView.ClaudeJsonl));

        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.AgentTranscript);
    }

    [Fact]
    public async Task A_live_run_being_found_has_no_transcript_until_it_is_found()
    {
        var run = new LiveRuns().Begin(new ContainerId("alpha", "worker"), null, LiveView.CodexRollout, "", finding: true);

        Assert.True(run.Finding);
        Assert.Null(run.Transcript);
        Assert.False(run.Located.IsCompleted);

        run.Found("/home/.codex/sessions/2026/09/26/rollout-x.jsonl");

        Assert.False(run.Finding);
        Assert.Equal("/home/.codex/sessions/2026/09/26/rollout-x.jsonl", await run.Located);
        Assert.Equal("/home/.codex/sessions/2026/09/26/rollout-x.jsonl", run.Transcript);
    }

    [Fact]
    public async Task A_run_that_ends_while_still_finding_says_why_there_is_no_transcript()
    {
        var run = new LiveRuns().Begin(new ContainerId("alpha", "worker"), null, LiveView.CodexRollout, "", finding: true);

        run.Dispose();

        Assert.Null(await run.Located);
        Assert.False(run.Finding);
        Assert.Equal("The run ended before its agent's session transcript was found.", run.Reason);
    }
}

/// <summary>A finished run's terminal row carries the agent's transcript, and is otherwise the same row.</summary>
public sealed class TranscriptOnTheTerminalRowTests
{
    private static readonly ContainerId Dev = new("Alpha", "Dev");

    private static async Task<string> PayloadAsync(AgentTranscript? transcript)
    {
        await using var bed = new ContainerTestBed();
        var ct = TestContext.Current.CancellationToken;
        await bed.AddAsync(Dev);
        bed.Agent.Behaviour = _ => Task.FromResult(new AgentResult(
            0, "done", Usage: new InvocationUsage(100, 40, "claude"), AgentTranscript: transcript));

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Dev), JsonSerializer.Serialize(new { instruction = "go" }), "console"), ct);

        Assert.True(await bed.PumpUntilAsync(async () => (await bed.OfTypeAsync(MessageTypes.Completed)).Count == 1));
        return (await bed.OfTypeAsync(MessageTypes.Completed)).Single().Payload;
    }

    [Fact]
    public async Task The_row_gains_the_transcript_and_its_format_and_every_other_byte_is_the_same()
    {
        var without = await PayloadAsync(null);
        var with = await PayloadAsync(new AgentTranscript("/data/agent-home/.claude/projects/-w/s.jsonl", LiveView.ClaudeJsonl));

        Assert.DoesNotContain(PayloadFields.AgentTranscript, without, StringComparison.Ordinal);
        Assert.Equal(
            without[..^1] + ""","agentTranscript":"/data/agent-home/.claude/projects/-w/s.jsonl","agentTranscriptFormat":"claude-jsonl"}""",
            with);
    }

    [Fact]
    public async Task A_batched_later_delivery_carries_no_transcript_only_the_row_with_the_usage_does()
    {
        await using var bed = new ContainerTestBed();
        var ct = TestContext.Current.CancellationToken;
        await bed.AddAsync(Dev);

        var firstRunRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        bed.Agent.Behaviour = async _ =>
        {
            if (Interlocked.Increment(ref runs) == 1) await firstRunRelease.Task;
            return new AgentResult(0, "done", Usage: new InvocationUsage(100, 40, "claude"),
                AgentTranscript: new AgentTranscript("/data/agent-home/.claude/projects/-w/s.jsonl", LiveView.ClaudeJsonl));
        };

        var root = await bed.Store.AppendAsync(Instruction("first"), ct);
        Assert.True(await bed.PumpUntilAsync(() => Task.FromResult(Volatile.Read(ref runs) == 1)));

        // Two deliveries arrive while the first run is going; the next run answers both as one batch.
        await bed.Store.AppendAsync(Instruction("second", root.Seq), ct);
        await bed.Store.AppendAsync(Instruction("third", root.Seq), ct);
        await bed.SettleAsync();
        firstRunRelease.SetResult();

        Assert.True(await bed.PumpUntilAsync(async () => (await bed.OfTypeAsync(MessageTypes.Completed)).Count == 3));

        var rows = (await bed.OfTypeAsync(MessageTypes.Completed)).Select(row => JsonDocument.Parse(row.Payload).RootElement).ToList();
        var later = rows.Where(row => row.TryGetProperty(PayloadFields.UsageCountedOn, out var on) && on.ValueKind == JsonValueKind.Number).ToList();

        Assert.Single(later);
        Assert.False(later[0].TryGetProperty(PayloadFields.AgentTranscript, out _));
        Assert.False(later[0].TryGetProperty(PayloadFields.AgentTranscriptFormat, out _));
        Assert.Equal(2, rows.Count(row => row.TryGetProperty(PayloadFields.AgentTranscript, out _)));
    }

    private static NewMessage Instruction(string text, long? causation = null) =>
        new(MessageTypes.InstructionFor(Dev), JsonSerializer.Serialize(new { instruction = text }), "console", causation);

    [Fact]
    public async Task A_snapshot_is_watchable_when_its_agents_preset_is()
    {
        await using var bed = new ContainerTestBed();
        var log = bed.Store;
        var watched = new AgentContainer(ContainerTestBed.Definition(Dev) with { Agent = "claude-headless" }, new FakeAgent(), log,
            watchable: agent => agent == "claude-headless");
        var unwatched = new AgentContainer(ContainerTestBed.Definition(Dev) with { Agent = "echo" }, new FakeAgent(), log,
            watchable: agent => agent == "claude-headless");

        Assert.True(watched.Snapshot().Watchable);
        Assert.False(unwatched.Snapshot().Watchable);
        Assert.False(new AgentContainer(ContainerTestBed.Definition(Dev), new FakeAgent(), log).Snapshot().Watchable);
    }
}

/// <summary>
/// <c>runs</c> and <c>runs/{seq}/transcript</c> on the real Host: this member's runs only, newest
/// first, 20 a page; a transcript rendered whole; a gone one says so; people only; 404 for a seq
/// that is not this member's run. Neither writes to the log.
/// </summary>
public sealed class EarlierRunsRouteTests(CrossTeamFixture host) : IClassFixture<CrossTeamFixture>, IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("harness-runs-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Runs(string team) => $"/api/teams/{team}/members/{host.ManagerName}/runs";

    private IMessageLog Log => host.Services.GetRequiredService<IMessageLog>();

    /// <summary>One run as the container writes it: `started`, optionally `blocked`, then the terminal row.</summary>
    private async Task<Message> RunAsync(
        string team, string? transcript, string type = MessageTypes.Completed, bool blocked = false, bool handedBack = false,
        string? member = null, bool blockedItem = false)
    {
        var source = new ContainerId(team, member ?? host.ManagerName).ToString();
        await Log.AppendAsync(new NewMessage(MessageTypes.Started, """{"trigger":"x"}""", source), Ct);
        if (blocked) await Log.AppendAsync(new NewMessage(MessageTypes.Blocked, """{"reason":"stuck"}""", source), Ct);
        if (blockedItem) await Log.AppendAsync(new NewMessage(MessageTypes.Blocked, """{"reason":"card stuck","item":"B000Z"}""", source), Ct);

        var payload = JsonSerializer.Serialize(new { exitCode = 0, output = "done", handedBack });
        if (transcript is not null)
        {
            payload = payload[..^1] + $",\"agentTranscript\":{JsonSerializer.Serialize(transcript)},\"agentTranscriptFormat\":\"claude-jsonl\"}}";
        }

        return await Log.AppendAsync(new NewMessage(type, payload, source), Ct);
    }

    [Fact]
    public async Task Runs_lists_only_this_members_runs_with_a_transcript_newest_first_twenty_a_page()
    {
        using var person = await host.PersonAsync();

        // A member of its own, so the other tests' runs on the Manager are not on its pages.
        const string pager = "Pager";
        (await person.PostAsJsonAsync($"/api/teams/{host.Alpha}/containers", new { name = pager }, Ct)).EnsureSuccessStatusCode();

        var first = await RunAsync(host.Alpha, "/t/first.jsonl", blocked: true, member: pager);
        await RunAsync(host.Alpha, null, member: pager);
        await RunAsync(host.Alpha, "/t/manager.jsonl");
        var failed = await RunAsync(host.Alpha, "/t/failed.jsonl", type: MessageTypes.Failed, member: pager);
        var handedBack = await RunAsync(host.Alpha, "/t/handed.jsonl", handedBack: true, member: pager);

        var ours = new List<long> { first.Seq, failed.Seq, handedBack.Seq };
        for (var i = 0; i < 19; i++) ours.Add((await RunAsync(host.Alpha, $"/t/{i}.jsonl", member: pager)).Seq);
        ours.Reverse();

        var route = $"/api/teams/{host.Alpha}/members/{pager}/runs";
        var page = JsonDocument.Parse(await person.GetStringAsync(route, Ct)).RootElement;
        var runs = page.GetProperty("runs").EnumerateArray().ToList();

        Assert.Equal(20, runs.Count);
        Assert.Equal(ours.Take(20), runs.Select(run => run.GetProperty("seq").GetInt64()));
        Assert.Equal(runs[^1].GetProperty("seq").GetInt64(), page.GetProperty("nextBefore").GetInt64());

        var newest = runs[0];
        Assert.Equal("completed", newest.GetProperty("outcome").GetString());
        Assert.Equal(newest.GetProperty("seq").GetInt64(), newest.GetProperty("workflow").GetInt64());
        var started = newest.GetProperty("startedAt").GetDateTimeOffset();
        var ended = newest.GetProperty("endedAt").GetDateTimeOffset();
        Assert.True(started <= ended);
        Assert.Equal((long)(ended - started).TotalMilliseconds, newest.GetProperty("durationMs").GetInt64());

        var next = JsonDocument.Parse(await person.GetStringAsync(
            $"{route}?before={page.GetProperty("nextBefore").GetInt64()}", Ct)).RootElement;
        var older = next.GetProperty("runs").EnumerateArray().ToList();

        Assert.Equal(ours.Skip(20), older.Select(run => run.GetProperty("seq").GetInt64()));
        Assert.Equal(JsonValueKind.Null, next.GetProperty("nextBefore").ValueKind);
        Assert.Equal("handedBack", runs[^1].GetProperty("outcome").GetString());
        Assert.Equal(["failed", "blocked"], older.Select(run => run.GetProperty("outcome").GetString()));
    }

    [Fact]
    public async Task Blocking_one_card_does_not_make_the_run_blocked_and_the_list_writes_nothing()
    {
        using var person = await host.PersonAsync();
        const string member = "Carder";
        (await person.PostAsJsonAsync($"/api/teams/{host.Alpha}/containers", new { name = member }, Ct)).EnsureSuccessStatusCode();

        var carded = await RunAsync(host.Alpha, "/t/carded.jsonl", member: member, blockedItem: true);
        var stuck = await RunAsync(host.Alpha, "/t/stuck.jsonl", member: member, blocked: true);

        var before = await Log.HighestSeqAsync(Ct);
        var runs = JsonDocument.Parse(await person.GetStringAsync($"/api/teams/{host.Alpha}/members/{member}/runs", Ct))
            .RootElement.GetProperty("runs").EnumerateArray()
            .ToDictionary(run => run.GetProperty("seq").GetInt64(), run => run.GetProperty("outcome").GetString());

        Assert.Equal("completed", runs[carded.Seq]);
        Assert.Equal("blocked", runs[stuck.Seq]);
        Assert.Equal(before, await Log.HighestSeqAsync(Ct));
    }

    [Fact]
    public async Task A_finished_transcript_is_rendered_whole_and_the_response_ends_writing_nothing()
    {
        var transcript = Path.Combine(_folder, "s.jsonl");
        await File.WriteAllTextAsync(transcript,
            """
            {"type":"user","message":{"role":"user","content":"Do the card."},"timestamp":"2026-09-26T13:34:41.454Z"}
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"README.md"}}]},"timestamp":"2026-09-26T13:34:42.5Z"}
            garbage

            """, Ct);
        var run = await RunAsync(host.Alpha, transcript);

        using var person = await host.PersonAsync();
        var before = await Log.HighestSeqAsync(Ct);

        var response = await person.GetAsync($"{Runs(host.Alpha)}/{run.Seq}/transcript", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(
            "2026-09-26T13:34:41.454Z\tUser: Do the card.\n2026-09-26T13:34:42.500Z\tRead README.md\n\tgarbage\n",
            await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(before, await Log.HighestSeqAsync(Ct));
    }

    [Fact]
    public async Task A_transcript_that_is_gone_answers_410_with_the_sentence_and_the_run_stays_listed()
    {
        var run = await RunAsync(host.Alpha, Path.Combine(_folder, "deleted.jsonl"));
        using var person = await host.PersonAsync();

        var response = await person.GetAsync($"{Runs(host.Alpha)}/{run.Seq}/transcript", Ct);

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal("This run's transcript is no longer on disk.", await response.Content.ReadAsStringAsync(Ct));

        var listed = JsonDocument.Parse(await person.GetStringAsync(Runs(host.Alpha), Ct)).RootElement;
        Assert.Contains(run.Seq, listed.GetProperty("runs").EnumerateArray().Select(r => r.GetProperty("seq").GetInt64()));
    }

    [Fact]
    public async Task A_transcript_there_but_unreadable_answers_500_with_a_sentence()
    {
        Assert.SkipWhen(Environment.UserName == "root", "root reads a mode-000 file.");
        var transcript = Path.Combine(_folder, "locked.jsonl");
        await File.WriteAllTextAsync(transcript, "{}\n", Ct);
        File.SetUnixFileMode(transcript, UnixFileMode.None);
        var run = await RunAsync(host.Alpha, transcript);
        using var person = await host.PersonAsync();

        var response = await person.GetAsync($"{Runs(host.Alpha)}/{run.Seq}/transcript", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.StartsWith("This run's transcript is on disk but could not be read: ", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_seq_that_is_not_this_members_run_is_a_404()
    {
        var theirs = await RunAsync(host.Beta, Path.Combine(_folder, "beta.jsonl"));
        var untracked = await RunAsync(host.Alpha, null);
        var started = await Log.AppendAsync(new NewMessage(
            MessageTypes.Started, """{"trigger":"x"}""", new ContainerId(host.Alpha, host.ManagerName).ToString()), Ct);
        using var person = await host.PersonAsync();

        foreach (var seq in new[] { theirs.Seq, untracked.Seq, started.Seq, long.MaxValue })
        {
            var response = await person.GetAsync($"{Runs(host.Alpha)}/{seq}/transcript", Ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await person.GetAsync($"/api/teams/{host.Alpha}/members/Nobody/runs", Ct)).StatusCode);
    }

    [Fact]
    public async Task Both_routes_refuse_a_machine_principal()
    {
        var run = await RunAsync(host.Alpha, Path.Combine(_folder, "x.jsonl"));
        using var client = host.WithKey(host.ContainerKeys[CrossTeamFixture.Key(host.Alpha, host.ManagerName)]);

        foreach (var route in new[] { Runs(host.Alpha), $"{Runs(host.Alpha)}/{run.Seq}/transcript" })
        {
            var response = await client.GetAsync(route, Ct);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal(PermitGate.HumansOnlyMessage, body.RootElement.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task The_live_route_waits_for_a_transcript_being_found_then_streams_it_in_its_format()
    {
        var transcript = Path.Combine(_folder, "rollout.jsonl");
        await File.WriteAllTextAsync(transcript,
            """
            {"timestamp":"2026-09-26T15:18:32.786Z","type":"event_msg","payload":{"type":"item_completed","item":{"type":"McpToolCall","tool":"team_current","result":{"content":[{"type":"text","text":"HTTP 200"}]}}}}

            """, Ct);

        using var person = await host.PersonAsync();
        var run = host.Services.GetRequiredService<LiveRuns>().Begin(
            new ContainerId(host.Alpha, host.ManagerName), null, LiveView.CodexRollout, "", finding: true);

        var responding = person.GetAsync($"/api/teams/{host.Alpha}/members/{host.ManagerName}/live", HttpCompletionOption.ResponseHeadersRead, Ct);
        await Task.Delay(300, Ct);
        Assert.False(responding.IsCompleted);

        run.Found(transcript);
        using var response = await responding;
        Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType?.ToString());

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
        Assert.Equal("2026-09-26T15:18:32.786Z\tMCP: team_current", await reader.ReadLineAsync(Ct));
        Assert.Equal("2026-09-26T15:18:32.786Z\tHTTP 200 (8 bytes)", await reader.ReadLineAsync(Ct));

        run.Dispose();
        Assert.Equal("", await reader.ReadToEndAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task A_transcript_never_found_answers_live_false_with_the_reason()
    {
        using var person = await host.PersonAsync();
        using var run = host.Services.GetRequiredService<LiveRuns>().Begin(
            new ContainerId(host.Alpha, host.ManagerName), null, LiveView.CodexRollout, "", finding: true);
        run.Found(null, "Not found within 30 seconds.");

        var response = await person.GetAsync($"/api/teams/{host.Alpha}/members/{host.ManagerName}/live", Ct);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.False(body.RootElement.GetProperty("live").GetBoolean());
        Assert.Equal("Not found within 30 seconds.", body.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void A_members_snapshot_on_the_host_is_watchable_by_its_agents_live_view()
    {
        var container = host.Services.GetRequiredService<ContainerHost>().Find(new ContainerId(host.Alpha, host.ManagerName))!;
        var catalog = host.Services.GetRequiredService<AgentCatalog>();
        var snapshot = container.Snapshot();

        Assert.Equal(catalog.Definition(snapshot.Agent)?.LiveView is not null, snapshot.Watchable);
        Assert.Contains("\"watchable\":", JsonSerializer.Serialize(snapshot, JsonSerializerOptions.Web), StringComparison.Ordinal);
    }
}
