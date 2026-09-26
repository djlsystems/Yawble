using System.Net;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Tests.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// A headless Claude run can be watched: the launch passes a per-invocation
/// <c>{sessionId}</c>, the catalog says where that run's transcript is, the Host reads it as the
/// agent, and <c>GET /api/teams/{team}/members/{member}/live</c> streams one readable line per
/// event to a person, and to nothing else.
/// </summary>
public sealed class LiveViewCatalogTests
{
    [Fact]
    public void Claude_headless_passes_the_session_id_token_and_keeps_its_json_output()
    {
        var headless = AgentCatalogFile.BuiltIns().Single(d => d.Name == "claude-headless");
        var arguments = headless.Launch.Arguments.ToList();

        Assert.Equal("{sessionId}", arguments[arguments.IndexOf("--session-id") + 1]);
        Assert.Equal("json", arguments[arguments.IndexOf("--output-format") + 1]);
        Assert.Equal("claude-json", headless.Launch.UsageFormat);
    }

    [Fact]
    public void Claude_headless_has_its_live_view_and_no_interactive_preset_has_one()
    {
        var presets = AgentCatalogFile.BuiltIns();

        Assert.Equal(
            new AgentLiveView("~/.claude/projects/{workspaceDashed}/{sessionId}.jsonl", "claude-jsonl"),
            presets.Single(d => d.Name == "claude-headless").LiveView);
        Assert.All(presets.Where(d => d.Mode == AgentMode.Interactive), d => Assert.Null(d.LiveView));
    }

    [Fact]
    public void The_transcript_path_is_the_agent_home_the_dashed_workspace_and_the_session()
    {
        var path = LiveView.Resolve(
            new AgentLiveView("~/.claude/projects/{workspaceDashed}/{sessionId}.jsonl", "claude-jsonl"),
            "/data/agent-home", "/data/teams/Team-1/workspaces/Tester.Colin", "d4c0cdb7-0000-4000-8000-000000000000");

        Assert.Equal(
            "/data/agent-home/.claude/projects/-data-teams-Team-1-workspaces-Tester-Colin/d4c0cdb7-0000-4000-8000-000000000000.jsonl",
            path);
    }

    [Theory]
    [InlineData("/etc/passwd", "claude-jsonl")]
    [InlineData("~/../harness/messages.db", "claude-jsonl")]
    [InlineData("~/.claude/projects/x.jsonl", "stream-json")]
    public void A_live_view_outside_the_agent_home_or_in_an_unknown_format_is_refused(string path, string format)
    {
        Assert.NotNull(LiveView.Refusal(new AgentLiveView(path, format)));
        Assert.Null(LiveView.Resolve(new AgentLiveView(path, format), "/data/agent-home", "/w", "s"));
    }
}

/// <summary>
/// Each Claude event type renders to its one line. The fixture is lines in the shapes Claude Code
/// 2.1 writes to <c>~/.claude/projects/.../&lt;session&gt;.jsonl</c> (read from a real session),
/// with identifiers and long fields cut.
/// </summary>
public sealed class ClaudeTranscriptLinesTests
{
    private const string Fixture = """
        {"type":"queue-operation","operation":"enqueue","content":"Card 29"}
        {"type":"user","message":{"role":"user","content":"Card 12, backlog B0012.\nDo this card in your own worktree."},"uuid":"u1"}
        {"type":"attachment","attachment":{"type":"total_tokens_reminder","text":"<total_tokens>15000000 tokens left</total_tokens>"},"uuid":"a1"}
        {"type":"attachment","attachment":{"type":"file","filename":"src/Harness.Host/RepoClone.cs","content":{"type":"text"}},"uuid":"a2"}
        {"type":"attachment","attachment":{"type":"credential_org","organizationUuid":"x"},"uuid":"a3"}
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"","signature":"abc"}]},"uuid":"t1"}
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Reading the repo's rules\nbefore touching the launch code."}]},"uuid":"t2"}
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Read","input":{"file_path":"src/Harness.Host/RepoClone.cs"}}]},"uuid":"t3"}
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_2","name":"Bash","input":{"command":"dotnet test ...","description":"Run the tests"}}]},"uuid":"t4"}
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_3","name":"mcp__harness__kanban","input":{"action":"board"}}]},"uuid":"t5"}
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_4","name":"Grep","input":{"pattern":"LiveView","path":"src"}}]},"uuid":"t6"}
        {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"namespace Harness.Host;\n\npublic sealed class RepoClone"}]},"uuid":"r1"}
        {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_3","content":[{"type":"text","text":"HTTP 200\n{}"}]}]},"uuid":"r2"}
        {"type":"last-prompt","lastPrompt":"Card 29","leafUuid":"x"}
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"half a line with no end
        """;

    [Fact]
    public void Each_event_type_renders_to_its_one_line_and_bookkeeping_shows_nothing()
    {
        var lines = Fixture.Split('\n').SelectMany(ClaudeTranscriptLines.Render).ToList();

        Assert.Equal(
            [
                "User: Card 12, backlog B0012.",
                "Attachment: <total_tokens>15000000 tokens left</total_tokens>",
                "Attachment: src/Harness.Host/RepoClone.cs",
                "Attachment: credential_org",
                "Reading the repo's rules before touching the launch code.",
                "Read src/Harness.Host/RepoClone.cs",
                "Bash: dotnet test ...",
                "MCP: kanban",
                "Grep: LiveView",
                "namespace Harness.Host; (54 bytes)",
                "HTTP 200 (11 bytes)",
                """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"half a line with no end""",
            ],
            lines);
    }

    [Fact]
    public void An_events_time_is_read_from_its_timestamp_and_a_line_with_none_has_no_time()
    {
        Assert.Equal(
            new DateTimeOffset(2026, 9, 26, 13, 34, 41, 454, TimeSpan.Zero),
            ClaudeTranscriptLines.Timestamp("""{"type":"user","timestamp":"2026-09-26T13:34:41.454Z"}"""));
        Assert.Null(ClaudeTranscriptLines.Timestamp("""{"type":"user"}"""));
        Assert.Null(ClaudeTranscriptLines.Timestamp("""{"type":"user","timestamp":"soon"}"""));
        Assert.Null(ClaudeTranscriptLines.Timestamp("garbage"));
    }

    [Fact]
    public void An_unparseable_line_is_shown_raw_and_clipped_never_dropped()
    {
        var raw = "not json at all " + new string('x', 400);

        var line = Assert.Single(ClaudeTranscriptLines.Render(raw));

        Assert.Equal(LiveView.MaxLine, line.Length);
        Assert.EndsWith("…", line);
        Assert.StartsWith("not json at all xxx", line);
    }

    [Fact]
    public void A_long_tool_result_is_clipped_and_keeps_its_size()
    {
        var content = new string('y', 1000);
        var raw = JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = "t", content } } },
        });

        var line = Assert.Single(ClaudeTranscriptLines.Render(raw));

        Assert.Equal(LiveView.MaxLine, line.Length);
        Assert.EndsWith("… (1000 bytes)", line);
    }

    [Fact]
    public void A_long_bash_command_is_one_line_clipped_with_an_ellipsis()
    {
        var raw = JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new
            {
                role = "assistant",
                content = new[] { new { type = "tool_use", name = "Bash", input = new { command = "echo a\n" + new string('z', 300) } } },
            },
        });

        var line = Assert.Single(ClaudeTranscriptLines.Render(raw));

        Assert.Equal(LiveView.MaxLine, line.Length);
        Assert.StartsWith("Bash: echo a zzz", line);
        Assert.EndsWith("…", line);
    }
}

/// <summary>The reader follows the transcript from its start and stops when the run ends.</summary>
public sealed class LiveTranscriptReaderTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("harness-live-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void When_the_host_switches_users_the_reader_is_tail_through_the_agents_setpriv_prefix()
    {
        var runAs = AgentLaunchUser.Decide("agent", (1001, 1001), 1000, 0xc0, "/usr/bin/setpriv", _ => true);
        Assert.True(runAs.Switches);

        var command = LiveTranscriptReader.ReaderCommand(runAs, "/data/agent-home/.claude/projects/-w/s.jsonl");

        Assert.NotNull(command);
        Assert.Equal(runAs.Prefix, command!.Take(runAs.Prefix.Count));
        Assert.Equal(
            [SystemCommand.Find("tail")!, "-c", "+1", "-F", "/data/agent-home/.claude/projects/-w/s.jsonl"],
            command.Skip(runAs.Prefix.Count));
    }

    [Fact]
    public void When_the_host_cannot_switch_it_reads_the_file_itself()
    {
        Assert.Null(LiveTranscriptReader.ReaderCommand(AgentLaunchUser.Same("agent", "test"), "/x.jsonl"));
        Assert.Null(LiveTranscriptReader.ReaderCommand(null, "/x.jsonl"));
    }

    [Fact]
    public async Task Read_directly_it_gets_every_line_from_the_start_then_new_ones_and_ends_with_the_run()
    {
        var path = Path.Combine(_folder, "s.jsonl");
        await File.WriteAllTextAsync(path, "one\ntwo\n", Ct);
        using var run = new CancellationTokenSource();

        var seen = new List<string>();
        var reading = Task.Run(async () =>
        {
            await foreach (var line in LiveTranscriptReader.LinesAsync(path, null, run.Token, Ct)) lock (seen) seen.Add(line);
        }, Ct);

        await EventuallyAsync(() => Count(seen) == 2);
        await File.AppendAllTextAsync(path, "three\nfou", Ct);
        await EventuallyAsync(() => Count(seen) == 3);
        await File.AppendAllTextAsync(path, "r\n", Ct);
        run.Cancel();

        await reading.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(["one", "two", "three", "four"], seen);
    }

    [Fact]
    public async Task Through_tail_it_waits_for_the_file_reads_it_from_the_start_and_ends_after_the_run()
    {
        var path = Path.Combine(_folder, "later.jsonl");
        using var run = new CancellationTokenSource();

        var seen = new List<string>();
        var reading = Task.Run(async () =>
        {
            await foreach (var line in LiveTranscriptReader.FollowCommandAsync(
                [SystemCommand.Find("tail")!, "-c", "+1", "-F", path], run.Token, Ct)) lock (seen) seen.Add(line);
        }, Ct);

        // Created after the reader started, as a run's transcript is.
        await Task.Delay(300, Ct);
        await File.WriteAllTextAsync(path, "one\ntwo\n", Ct);
        await EventuallyAsync(() => Count(seen) == 2);
        await File.AppendAllTextAsync(path, "three\n", Ct);
        run.Cancel();

        await reading.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(["one", "two", "three"], seen);
    }

    /// <summary>
    /// The same seam as <see cref="AgentLaunchUserLaunchTests"/>: a real switch to <c>nobody</c>,
    /// reading a file only <c>nobody</c> may read, skipped with the reason where this process
    /// cannot switch users.
    /// </summary>
    [Fact]
    public async Task Launched_as_the_agent_it_reads_a_transcript_only_the_agent_may_read()
    {
        var runAs = AgentLaunchUser.Resolve("nobody");
        if (!runAs.Switches) Assert.Skip($"This process cannot switch users: {runAs.Reason}.");

        File.SetUnixFileMode(_folder, (UnixFileMode)0b111_101_101);
        var path = Path.Combine(_folder, "s.jsonl");
        await File.WriteAllTextAsync(path, "one\ntwo\n", Ct);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Chown(path, runAs.Uid, runAs.Gid);

        using var run = new CancellationTokenSource();
        var seen = new List<string>();
        var reading = Task.Run(async () =>
        {
            await foreach (var line in LiveTranscriptReader.LinesAsync(path, runAs, run.Token, Ct)) lock (seen) seen.Add(line);
        }, Ct);

        await EventuallyAsync(() => Count(seen) == 2);
        run.Cancel();
        await reading.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(["one", "two"], seen);
        Assert.Equal("600", Stat(path));
    }

    private static int Count(List<string> seen) { lock (seen) return seen.Count; }

    private static void Chown(string path, int uid, int gid)
    {
        using var process = System.Diagnostics.Process.Start("chown", [$"{uid}:{gid}", path])!;
        process.WaitForExit();
    }

    private static string Stat(string path)
    {
        var start = new System.Diagnostics.ProcessStartInfo("stat", ["-c", "%a", path]) { RedirectStandardOutput = true };
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }

    internal static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition never held.");
            await Task.Delay(25);
        }
    }
}

/// <summary>
/// The runner records each run for the live route, with the transcript its preset names, and
/// what it records changes nothing about the run: usage and output are byte-identical with a
/// watcher attached.
/// </summary>
public sealed class LiveViewRunnerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harness-live-run-").FullName;

    private string Home => Path.Combine(_root, "agent-home");

    // A dot in the folder, so the path proves `.` becomes `-` as well as `/`.
    private string Workspace => Path.Combine(_root, "ws.one");

    public LiveViewRunnerTests()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Workspace);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    // A stand-in for `claude -p --session-id {sessionId} --output-format json`: it writes its
    // transcript where Claude does, waits for `go` (at most 20 seconds, so a failed test leaves
    // nothing running), and prints one result document.
    private const string FakeClaude = """
        cat >/dev/null
        d="$HOME/.claude/projects/$(pwd | tr '/.' '--')"; mkdir -p "$d"; f="$d/$1.jsonl"
        echo "$1" > session-id
        printf '%s\n' '{"type":"assistant","message":{"content":[{"type":"text","text":"working"}]}}' >> "$f"
        i=0; while [ ! -e go ] && [ $i -lt 400 ]; do sleep 0.05; i=$((i+1)); done
        printf '%s\n' '{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"ls"}}]}}' >> "$f"
        printf '%s\n' 'this is not json' >> "$f"
        echo '{"type":"result","result":"done","usage":{"input_tokens":3,"output_tokens":5,"cache_read_input_tokens":7,"cache_creation_input_tokens":11}}'
        """;

    private static AgentCatalog Catalog(AgentLiveView? view) => new(
    [
        new AgentDefinition(
            "fake", AgentMode.Headless,
            new AgentLaunch("bash", ["-c", FakeClaude, "fake", "{sessionId}"], UsageFormat: "claude-json"),
            LiveView: view),
    ]);

    private static readonly AgentLiveView ClaudeView =
        new("~/.claude/projects/{workspaceDashed}/{sessionId}.jsonl", "claude-jsonl");

    private static readonly ContainerId Member = new("alpha", "worker");

    private Task<AgentResult> RunAsync(AgentCatalog catalog, LiveRuns live) =>
        new ProcessAgentRunner(catalog, new RunHeartbeat(), live: live).RunAsync(new AgentInvocation(
            Member, "You are a fake.", "hello", Workspace,
            new Dictionary<string, string> { ["HOME"] = Home }, Agent: "fake"), Ct);

    [Fact]
    public async Task Each_run_gets_its_own_session_id_and_its_transcript_is_where_claude_writes_it()
    {
        var live = new LiveRuns();
        var paths = new List<string>();

        for (var i = 0; i < 2; i++)
        {
            File.Delete(Path.Combine(Workspace, "go"));
            var running = RunAsync(Catalog(ClaudeView), live);

            await LiveTranscriptReaderTests.EventuallyAsync(() => live.Find(Member) is not null && File.Exists(Path.Combine(Workspace, "session-id")));
            var session = File.ReadAllText(Path.Combine(Workspace, "session-id")).Trim();
            var run = live.Find(Member)!;

            Assert.True(Guid.TryParse(session, out _), session);
            Assert.Equal(
                Path.Combine(Home, ".claude", "projects", LiveView.Dashed(Workspace), session + ".jsonl"),
                run.Transcript);
            Assert.Contains("-ws-one", run.Transcript);
            paths.Add(run.Transcript!);

            File.WriteAllText(Path.Combine(Workspace, "go"), "");
            await running;
            File.Delete(Path.Combine(Workspace, "session-id"));

            Assert.Null(live.Find(Member));
            Assert.True(run.Ended.IsCancellationRequested);
        }

        Assert.NotEqual(paths[0], paths[1]);
    }

    [Fact]
    public async Task A_preset_without_a_live_view_is_recorded_as_running_with_a_reason()
    {
        var live = new LiveRuns();
        var running = RunAsync(Catalog(null), live);

        await LiveTranscriptReaderTests.EventuallyAsync(() => live.Find(Member) is not null);
        var run = live.Find(Member)!;

        Assert.Null(run.Transcript);
        Assert.Contains("no live view", run.Reason);

        File.WriteAllText(Path.Combine(Workspace, "go"), "");
        await running;
    }

    [Fact]
    public async Task Usage_and_output_are_byte_identical_with_and_without_a_watcher()
    {
        // Without a watcher.
        File.WriteAllText(Path.Combine(Workspace, "go"), "");
        var alone = await RunAsync(Catalog(ClaudeView), new LiveRuns());
        File.Delete(Path.Combine(Workspace, "go"));

        // With one, attached for the whole run and reading every line.
        var live = new LiveRuns();
        var running = RunAsync(Catalog(ClaudeView), live);
        await LiveTranscriptReaderTests.EventuallyAsync(() => live.Find(Member) is not null);
        var run = live.Find(Member)!;

        var watched = new List<string>();
        var watching = Task.Run(async () =>
        {
            await foreach (var line in LiveTranscriptReader.LinesAsync(run.Transcript!, null, run.Ended, Ct))
            {
                lock (watched) watched.AddRange(ClaudeTranscriptLines.Render(line));
            }
        }, Ct);

        await LiveTranscriptReaderTests.EventuallyAsync(() => { lock (watched) return watched.Count == 1; });
        File.WriteAllText(Path.Combine(Workspace, "go"), "");
        var observed = await running;
        await watching.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(0, alone.ExitCode);
        Assert.Equal(alone.ExitCode, observed.ExitCode);
        Assert.Equal(alone.Output, observed.Output);
        Assert.Equal(alone.Usage, observed.Usage);
        Assert.NotNull(alone.Usage);
        Assert.Equal(["working", "Bash: ls", "this is not json"], watched);
    }
}

/// <summary>The route, on the real Host: people only, 404 when not running, JSON when there is nothing to watch.</summary>
public sealed class LiveViewRouteTests(CrossTeamFixture host) : IClassFixture<CrossTeamFixture>
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Route => $"/api/teams/{host.Alpha}/members/{host.ManagerName}/live";

    private ContainerId Manager => new(host.Alpha, host.ManagerName);

    [Fact]
    public async Task A_container_key_is_refused()
    {
        using var client = host.WithKey(host.ContainerKeys[CrossTeamFixture.Key(host.Alpha, host.ManagerName)]);

        var response = await client.GetAsync(Route, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(PermitGate.HumansOnlyMessage, body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_member_that_is_not_running_is_a_404_with_a_sentence()
    {
        using var person = await host.PersonAsync();

        var response = await person.GetAsync($"/api/teams/{host.Alpha}/members/Nobody/live", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        response = await person.GetAsync(Route, Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal($"{host.ManagerName} is not running.", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_preset_without_a_live_view_answers_live_false_with_a_reason_not_an_empty_stream()
    {
        using var person = await host.PersonAsync();
        using var run = host.Services.GetRequiredService<LiveRuns>().Begin(
            Manager, null, null, "This member's agent (codex) has no live view.");

        var response = await person.GetAsync(Route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.False(body.RootElement.GetProperty("live").GetBoolean());
        Assert.Equal("This member's agent (codex) has no live view.", body.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_live_run_streams_its_lines_as_text_and_ends_with_the_run_writing_nothing_to_the_log()
    {
        var folder = Directory.CreateTempSubdirectory("harness-live-route-").FullName;
        try
        {
            var transcript = Path.Combine(folder, "s.jsonl");
            await File.WriteAllTextAsync(transcript,
                """
                {"type":"user","message":{"role":"user","content":"Do the card."},"timestamp":"2026-09-26T13:34:41.454Z"}
                {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"README.md"}}]},"timestamp":"2026-09-26T13:34:42.5Z"}

                """, Ct);

            var messages = host.Services.GetRequiredService<IMessageLog>();
            var before = await messages.HighestSeqAsync(Ct);

            using var person = await host.PersonAsync();
            var run = host.Services.GetRequiredService<LiveRuns>().Begin(Manager, transcript, LiveView.ClaudeJsonl, "");

            using var response = await person.GetAsync(Route, HttpCompletionOption.ResponseHeadersRead, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType?.ToString());

            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
            // Each line is `<when, UTC>` TAB `<line>`; a line whose event names no time has an empty one.
            Assert.Equal("2026-09-26T13:34:41.454Z\tUser: Do the card.", await reader.ReadLineAsync(Ct));
            Assert.Equal("2026-09-26T13:34:42.500Z\tRead README.md", await reader.ReadLineAsync(Ct));

            await File.AppendAllTextAsync(transcript, "garbage\n", Ct);
            Assert.Equal("\tgarbage", await reader.ReadLineAsync(Ct));

            run.Dispose();
            Assert.Equal("", await reader.ReadToEndAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct));

            Assert.Equal(before, await messages.HighestSeqAsync(Ct));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
