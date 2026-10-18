using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Messaging;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harness.Tests.Host;

/// <summary>
/// WHAT EVERY AGENT RUN DID, RECORDED WHETHER OR NOT ANYBODY WATCHES. Each test runs a member on the
/// real Host, whose test runner writes a transcript and begins its live run as the real runner's
/// worker does (<see cref="LiveRuns.Begin"/>); the run is followed on the Host's own worker, and what
/// <see cref="AgentStepFollow"/> recorded is read back: each step's kind, tool name and time, in order,
/// and nothing else. The per-second cap, a plugin member and a preset with no live view, an
/// unreadable transcript, the scan for words, and the push to a person and never a machine principal.
/// </summary>
public sealed class AgentStepRecordTests(AgentStepBed bed) : IClassFixture<AgentStepBed>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static IEnumerable<TheoryDataRow<string, string>> Fixtures() =>
    [
        new(LiveView.CopilotEvents, "copilot-events.jsonl"),
        new(LiveView.CodexRollout, "codex-rollout.jsonl"),
        new(LiveView.GrokUpdates, "grok-updates.jsonl"),
    ];

    /// <summary>What each redacted fixture's run records, in order: kind, label, MCP tool, time, count.</summary>
    private static readonly Dictionary<string, (string Kind, string Label, string? Mcp, string At, int Count)[]> Expected = new()
    {
        [LiveView.CopilotEvents] =
        [
            ("user", "", null, "2026-09-26T15:18:04.260Z", 1),
            ("tool", "Read", null, "2026-09-26T15:18:05.988Z", 1),
            ("tool", "Bash", null, "2026-09-26T15:18:05.989Z", 1),
            ("tool", "MCP", "team_current", "2026-09-26T15:18:05.990Z", 1),
            ("result", "", null, "2026-09-26T15:18:06.011Z", 1),
            ("result", "", null, "2026-09-26T15:18:06.012Z", 1),
            ("result", "", null, "2026-09-26T15:18:06.014Z", 1),
            ("text", "", null, "2026-09-26T15:18:07.355Z", 1),
        ],

        // Six steps in 15:18:32: the sixth, MCP's result, is counted into the last result recorded in that second.
        [LiveView.CodexRollout] =
        [
            ("user", "", null, "2026-09-26T15:18:27.089Z", 1),
            ("text", "", null, "2026-09-26T15:18:29.746Z", 1),
            ("tool", "Read", null, "2026-09-26T15:18:32.725Z", 1),
            ("result", "", null, "2026-09-26T15:18:32.725Z", 1),
            ("tool", "Bash", null, "2026-09-26T15:18:32.771Z", 1),
            ("result", "", null, "2026-09-26T15:18:32.771Z", 2),
            ("tool", "MCP", "team_current", "2026-09-26T15:18:32.786Z", 1),
            ("text", "", null, "2026-09-26T15:18:37.746Z", 1),
        ],

        // Grok's times are whole seconds. Seven steps in 15:16:11: its two results, with no result
        // recorded in that second, are counted into the second's last recorded step.
        [LiveView.GrokUpdates] =
        [
            ("user", "", null, "2026-09-26T15:16:03.000Z", 1),
            ("text", "", null, "2026-09-26T15:16:11.000Z", 1),
            ("tool", "todo_write", null, "2026-09-26T15:16:11.000Z", 1),
            ("tool", "Read", null, "2026-09-26T15:16:11.000Z", 1),
            ("tool", "Bash", null, "2026-09-26T15:16:11.000Z", 1),
            ("tool", "search_tool", null, "2026-09-26T15:16:11.000Z", 3),
            ("text", "", null, "2026-09-26T15:16:21.000Z", 1),
            ("tool", "MCP", "status", "2026-09-26T15:16:21.000Z", 1),
            ("tool", "Bash", null, "2026-09-26T15:16:21.000Z", 1),
            ("result", "", null, "2026-09-26T15:16:21.000Z", 1),
            ("result", "", null, "2026-09-26T15:16:23.000Z", 1),
            ("text", "", null, "2026-09-26T15:18:39.000Z", 1),
        ],
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task A_run_nobody_watches_records_each_step_of_its_transcript_with_its_kind_tool_and_time_in_order_and_nothing_else(string format, string file)
    {
        var team = await bed.TeamAsync("Steps " + format, "Dev");
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LiveView", file));

        var run = await bed.RunAsync(team, "Dev", bed.Followed(format, lines));

        var steps = await bed.StepsAsync(team);
        Assert.Equal(
            Expected[format].Select(e => (e.Kind, e.Label, e.Mcp, DateTimeOffset.Parse(e.At), e.Count)),
            steps.Select(s => (s.Kind, s.Label, s.McpTool, s.At, s.Count)));
        Assert.All(steps, s => Assert.Equal(("Dev", run.Terminal, run.Workflow), (s.Member, s.Run, s.Workflow)));
        Assert.Equal(steps.OrderBy(s => s.Id).Select(s => s.Id), steps.Select(s => s.Id));
    }

    [Fact]
    public async Task A_claude_run_records_its_tool_calls_by_name_its_mcp_tool_its_results_text_user_lines_and_notes()
    {
        var team = await bed.TeamAsync("Steps claude", "Dev");

        var run = await bed.RunAsync(team, "Dev", bed.Followed(LiveView.ClaudeJsonl,
        [
            """{"type":"user","message":{"role":"user","content":"Do the card."},"timestamp":"2026-09-26T13:34:41.454Z"}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Reading it."},{"type":"tool_use","name":"Read","input":{"file_path":"README.md"}}]},"timestamp":"2026-09-26T13:34:42.5Z"}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","content":"# Widget"}]},"timestamp":"2026-09-26T13:34:43Z"}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"mcp__harness__tell","input":{"member":"Dev"}},{"type":"tool_use","name":"Edit","input":{"file_path":"a.cs"}}]},"timestamp":"2026-09-26T13:34:44Z"}""",
            """{"type":"attachment","attachment":{"type":"file","filename":"notes.md"},"timestamp":"2026-09-26T13:34:45Z"}""",
            """{"type":"queue-operation","timestamp":"2026-09-26T13:34:46Z"}""",
            "not json",
        ]));

        var steps = await bed.StepsAsync(team);
        Assert.Equal(
            [
                ("user", "", null, DateTimeOffset.Parse("2026-09-26T13:34:41.454Z")),
                ("text", "", null, DateTimeOffset.Parse("2026-09-26T13:34:42.5Z")),
                ("tool", "Read", null, DateTimeOffset.Parse("2026-09-26T13:34:42.5Z")),
                ("result", "", null, DateTimeOffset.Parse("2026-09-26T13:34:43Z")),
                ("tool", "MCP", "tell", DateTimeOffset.Parse("2026-09-26T13:34:44Z")),
                ("tool", "Edit", null, DateTimeOffset.Parse("2026-09-26T13:34:44Z")),
                ("note", "", (string?)null, DateTimeOffset.Parse("2026-09-26T13:34:45Z")),
            ],
            steps.Select(s => (s.Kind, s.Label, s.McpTool, s.At)));
        Assert.All(steps, s => Assert.Equal((1, run.Terminal), (s.Count, s.Run)));
    }

    [Fact]
    public async Task No_word_of_the_transcript_is_in_any_row_or_any_file_under_the_data_root()
    {
        var secret = "sk-steps-" + Guid.NewGuid().ToString("N");
        var prose = "Quietly-" + Guid.NewGuid().ToString("N") + " the agent mused about the weather";
        var team = await bed.TeamAsync("Steps secret", "Dev");

        // Now, so the route, which reads nothing older than the oldest log row, reads them too.
        var now = LiveView.When(DateTimeOffset.UtcNow);

        await bed.RunAsync(team, "Dev", bed.Followed(LiveView.ClaudeJsonl,
        [
            $$"""{"type":"user","message":{"role":"user","content":"Use {{secret}} please."},"timestamp":"{{now}}"}""",
            $$$"""{"type":"assistant","message":{"content":[{"type":"text","text":"{{{prose}}}"},{"type":"tool_use","name":"Bash","input":{"command":"curl -H 'Authorization: {{{secret}}}' /tmp/{{{prose}}}"}}]},"timestamp":"{{{now}}}"}""",
            $$"""{"type":"user","message":{"content":[{"type":"tool_result","content":"{{secret}} {{prose}}"}]},"timestamp":"{{now}}"}""",
            $$"""{"type":"attachment","attachment":{"type":"file","filename":"{{prose}}.md"},"timestamp":"{{now}}"}""",
        ]));

        var steps = await bed.StepsAsync(team);
        Assert.Equal(["user", "text", "tool", "result", "note"], steps.Select(s => s.Kind));

        var read = await bed.Person.GetStringAsync("/api/run-steps?take=500", Ct);
        Assert.Contains(team, read, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, read, StringComparison.Ordinal);
        Assert.DoesNotContain("Quietly-", read, StringComparison.Ordinal);

        SqliteConnection.ClearAllPools();
        foreach (var path in Directory.EnumerateFiles(bed.DataRoot, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, Ct);
                bytes = memory.ToArray();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var text = System.Text.Encoding.UTF8.GetString(bytes);
            Assert.False(text.Contains(secret, StringComparison.Ordinal), $"{path} holds the secret");
            Assert.False(text.Contains("Quietly-", StringComparison.Ordinal), $"{path} holds the prose");
        }
    }

    [Fact]
    public async Task Fifty_steps_in_one_second_record_at_most_the_cap_and_their_counts_add_up_to_fifty()
    {
        var team = await bed.TeamAsync("Steps capped", "Dev");
        var lines = Enumerable.Range(1, 50).Select(i =>
            $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"step {{{i}}}"}}]},"timestamp":"2026-09-26T13:34:41.{{{i:000}}}Z"}""");

        await bed.RunAsync(team, "Dev", bed.Followed(LiveView.ClaudeJsonl, [.. lines]));

        var steps = await bed.StepsAsync(team);
        Assert.InRange(steps.Count, 1, AgentStepCap.PerSecond);
        Assert.Equal(AgentStepCap.PerSecond, steps.Count);
        Assert.Equal(50, steps.Sum(s => s.Count));
        Assert.All(steps, s => Assert.Equal(("tool", "Bash"), (s.Kind, s.Label)));
    }

    [Fact]
    public void The_cap_is_five_a_second_and_folds_the_rest_into_the_last_of_its_kind_never_dropping_one()
    {
        Assert.Equal(5, AgentStepCap.PerSecond);

        var cap = new AgentStepCap();
        var at = DateTimeOffset.Parse("2026-09-26T13:34:41Z");
        var kept = new List<NewAgentStep>();
        foreach (var kind in new[] { "tool", "result", "tool", "text", "tool", "result", "tool", "user" })
        {
            if (cap.Take(new NewAgentStep(at.AddMilliseconds(kept.Count), kind, kind == "tool" ? "Bash" : "", null)).Added is { } added)
            {
                kept.Add(added);
            }
        }

        // The next second starts afresh.
        Assert.NotNull(cap.Take(new NewAgentStep(at.AddSeconds(1), "user", "", null)).Added);

        Assert.Equal(["tool", "result", "tool", "text", "tool"], kept.Select(s => s.Kind));
        Assert.Equal([1, 2, 1, 1, 3], kept.Select(s => s.Count));
        Assert.Equal(8, kept.Sum(s => s.Count));
    }

    [Fact]
    public async Task A_plugin_members_run_and_a_run_whose_preset_has_no_live_view_record_nothing_and_log_nothing()
    {
        var team = await bed.TeamAsync("Steps none", "Dev");
        var echo = await bed.Person.PostAsJsonAsync($"/api/teams/{team}/containers", new
        {
            name = "Echo",
            agent = "plugin:sample-echo",
            config = new { mode = "reverse" },
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, echo.StatusCode);
        bed.Logs.Clear();

        await bed.RunAsync(team, "Echo", null);
        await bed.RunAsync(team, "Dev", invocation =>
        {
            // What the worker says for a preset with no live view, as the real runner relays it.
            using (bed.Live.Begin(invocation.Container, null, null, "This member's agent (codex) has no live view; only its progress lines show what it is doing."))
            {
            }

            return Task.FromResult(new AgentResult(0, "done"));
        });

        Assert.Empty(await bed.StepsAsync(team));
        Assert.Empty(bed.Logs.Where(l => l.Category == typeof(AgentStepFollow).FullName));
    }

    [Fact]
    public async Task A_transcript_that_is_not_found_or_cannot_be_read_is_logged_once_naming_team_member_and_why_and_the_run_completes()
    {
        var team = await bed.TeamAsync("Steps unreadable", "Dev", "Ops");
        bed.Logs.Clear();

        var notFound = await bed.RunAsync(team, "Dev", invocation =>
        {
            using (var run = bed.Live.Begin(invocation.Container, null, LiveView.CodexRollout, "", finding: true))
            {
                run.Found(null, "Its agent's session transcript was not found within 30 seconds of launch.");
            }

            return Task.FromResult(new AgentResult(0, "done"));
        });

        var missing = Path.Combine(bed.Outside, $"never-{Guid.NewGuid():N}.jsonl");
        var never = await bed.RunAsync(team, "Ops", invocation =>
        {
            using (bed.Live.Begin(invocation.Container, missing, LiveView.ClaudeJsonl, ""))
            {
            }

            return Task.FromResult(new AgentResult(0, "done"));
        });

        Assert.Equal(MessageTypes.Completed, notFound.Type);
        Assert.Equal(MessageTypes.Completed, never.Type);
        Assert.Empty(await bed.StepsAsync(team));

        var said = bed.Logs.Where(l => l.Category == typeof(AgentStepFollow).FullName).ToList();
        Assert.Equal(2, said.Count);
        Assert.All(said, l => Assert.Equal(LogLevel.Warning, l.Level));
        Assert.Single(said, l => l.Message.Contains($"{team}/Dev", StringComparison.Ordinal)
            && l.Message.Contains("was not found within 30 seconds", StringComparison.Ordinal));
        Assert.Single(said, l => l.Message.Contains($"{team}/Ops", StringComparison.Ordinal)
            && l.Message.Contains(missing, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_push_reaches_a_watching_person_and_never_a_machine_principal()
    {
        var team = await bed.TeamAsync("Steps pushed", "Dev");

        var person = bed.Hub(options => options.Headers["Cookie"] = bed.Cookie);
        var pushed = Channel.CreateUnbounded<string>();
        person.On<JsonElement>(AgentStepFollow.Message, push => pushed.Writer.TryWrite(push.GetRawText()));
        await person.StartAsync(Ct);
        await person.InvokeAsync("JoinFileWatch", Ct);

        var key = await bed.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(team, "Dev").ToString(), PrincipalKind.Container, team, Permits.All, ct: Ct);
        var machine = bed.Hub(options => options.Headers[ApiKeyAuthenticationHandler.Header] = key);
        var overheard = 0;
        machine.On<JsonElement>(AgentStepFollow.Message, _ => Interlocked.Increment(ref overheard));
        await machine.StartAsync(Ct);
        await Assert.ThrowsAsync<HubException>(() => machine.InvokeAsync("JoinFileWatch", Ct));

        var run = await bed.RunAsync(team, "Dev", bed.Followed(LiveView.ClaudeJsonl,
        [
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"mcp__harness__kanban","input":{"action":"board"}}]},"timestamp":"2026-09-26T13:34:44Z"}""",
        ]));

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        wait.CancelAfter(TimeSpan.FromSeconds(30));
        var raw = await pushed.Reader.ReadAsync(wait.Token);
        var push = JsonDocument.Parse(raw).RootElement;

        Assert.Equal(team, push.GetProperty("team").GetString());
        Assert.Equal("Dev", push.GetProperty("member").GetString());
        var step = Assert.Single(push.GetProperty("steps").EnumerateArray());
        Assert.Equal("tool", step.GetProperty("kind").GetString());
        Assert.Equal("MCP", step.GetProperty("label").GetString());
        Assert.Equal("kanban", step.GetProperty("mcpTool").GetString());
        Assert.Equal(1, step.GetProperty("count").GetInt32());
        Assert.Equal(DateTimeOffset.Parse("2026-09-26T13:34:44Z"), step.GetProperty("at").GetDateTimeOffset());
        Assert.Equal(run.Workflow, step.GetProperty("workflow").GetInt64());
        Assert.True(step.GetProperty("id").GetInt64() > 0);
        Assert.True(step.GetProperty("run").GetInt64() > 0);
        Assert.DoesNotContain("board", raw, StringComparison.Ordinal);

        await Task.Delay(200, Ct);
        Assert.Equal(0, overheard);
    }

    [Fact]
    public async Task The_live_view_carries_the_hosts_kind_on_every_line()
    {
        var folder = Directory.CreateTempSubdirectory("harness-steps-live-").FullName;
        var team = await bed.TeamAsync("Steps live kinds", "Dev");
        var member = new ContainerId(team, "Dev");
        var transcript = Path.Combine(folder, "s.jsonl");
        await File.WriteAllLinesAsync(transcript,
        [
            """{"type":"user","message":{"role":"user","content":"Do the card."},"timestamp":"2026-09-26T13:34:41.454Z"}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Note: this is prose."},{"type":"tool_use","name":"ToolSearch","input":{"query":"x"}}]},"timestamp":"2026-09-26T13:34:42.5Z"}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","content":"found"}]},"timestamp":"2026-09-26T13:34:43Z"}""",
            """{"type":"attachment","attachment":{"type":"file","filename":"notes.md"},"timestamp":"2026-09-26T13:34:45Z"}""",
            "garbage",
        ], Ct);

        var run = bed.Live.Begin(member, transcript, LiveView.ClaudeJsonl, "");
        try
        {
            using var response = await bed.Person.GetAsync($"/api/teams/{team}/members/Dev/live", HttpCompletionOption.ResponseHeadersRead, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));

            Assert.Equal("2026-09-26T13:34:41.454Z\tuser\tUser: Do the card.", await reader.ReadLineAsync(Ct));
            Assert.Equal("2026-09-26T13:34:42.500Z\ttext\tNote: this is prose.", await reader.ReadLineAsync(Ct));
            Assert.Equal("2026-09-26T13:34:42.500Z\ttool\tToolSearch: x", await reader.ReadLineAsync(Ct));
            Assert.Equal("2026-09-26T13:34:43.000Z\tresult\tfound (5 bytes)", await reader.ReadLineAsync(Ct));
            Assert.Equal("2026-09-26T13:34:45.000Z\tnote\tAttachment: notes.md", await reader.ReadLineAsync(Ct));
            Assert.Equal("\traw\tgarbage", await reader.ReadLineAsync(Ct));

            run.Dispose();
            Assert.Equal("", await reader.ReadToEndAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct));
        }
        finally
        {
            run.Dispose();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_follow_whose_worker_never_answers_holds_neither_the_runs_start_nor_its_end()
    {
        var member = new ContainerId("Alpha", "Dev");
        var live = new LiveRuns();
        var worker = new SilentWorker();
        var follow = new AgentStepFollow(
            live, _ => new AgentStepRun(1, 1), _ => ((worker, new RunId(member, "")), null),
            new WorkerStreams(), null, (_, _) => Task.CompletedTask);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var run = live.Begin(member, "/nowhere/s.jsonl", LiveView.ClaudeJsonl, "");
        run.Dispose();
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1), $"the run's start and end took {timer.Elapsed}");

        // The follow asked its worker and is still waiting, on its own.
        await worker.Asked.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(follow.Settled().IsCompleted);

        worker.Close();
        await follow.Settled().WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    /// <summary>A worker that takes every command and answers none until its connection closes.</summary>
    private sealed class SilentWorker : IRunWorker
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkerId Id => WorkerId.Local;

        public Task Closed => _closed.Task;

        public void Close() => _closed.TrySetResult();

        public async Task SendAsync(ControlMessage message, CancellationToken ct = default)
        {
            if (message is FollowTranscript) Asked.TrySetResult();
            await _closed.Task.WaitAsync(ct);
            throw new InvalidOperationException("The worker's connection closed.");
        }
    }

    [Theory]
    [InlineData("", "?")]
    [InlineData("Bash", "Bash")]
    [InlineData("todo_write", "todo_write")]
    [InlineData("harness-team.current", "harness-team.current")]
    [InlineData("rm -rf /", "?")]
    [InlineData("Bash: ls", "?")]
    public void A_tool_name_is_kept_only_as_a_plain_name(string name, string kept)
    {
        Assert.Equal(kept, AgentStepFollow.Name(name));
        Assert.Equal("?", AgentStepFollow.Name(new string('a', AgentStepFollow.MaxName + 1)));
    }
}

/// <summary>
/// HOW LONG A RECORDED STEP LIVES, AND WHO READS IT, as <see cref="WorkspaceFileChangeReplayTests"/>
/// pins for file changes: the log's purge removes the steps older than the oldest log row it keeps; a
/// team's deletion removes its steps, its row counts them and a same-name successor reads none of its
/// predecessor's; Reset's Delete memory removes the ticked members' steps only; and
/// <c>GET /api/run-steps</c> is a person's read, oldest first and paged.
/// </summary>
public sealed class AgentStepReplayTests(AgentStepBed bed) : IClassFixture<AgentStepBed>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_log_purge_removes_the_steps_older_than_the_oldest_log_row_kept_and_keeps_the_rest()
    {
        var directory = Directory.CreateTempSubdirectory("harness-run-steps-purge-").FullName;
        try
        {
            var database = Path.Combine(directory, "messages.db");
            await new SchemaMigrator(database).ApplyAsync(SchemaModules.All, ct: Ct);
            var log = new SqliteMessageStore(database);
            var store = new AgentStepStore(database);

            var first = await log.AppendAsync(new NewMessage("test.run-steps", "{}", "test"), Ct);
            await Task.Delay(20, Ct);
            var second = await log.AppendAsync(new NewMessage("test.run-steps", "{}", "test"), Ct);
            Assert.True(second.OccurredAt > first.OccurredAt);

            await Write(store, "Alpha", "Dev", first.OccurredAt.AddMinutes(-1), "before");
            await Write(store, "Alpha", "Dev", first.OccurredAt.AddTicks(1), "between");
            await Write(store, "Alpha", "Dev", second.OccurredAt, "at");
            await Write(store, "Beta", "Ops", second.OccurredAt.AddMinutes(1), "after");

            var report = await log.DeleteAsync([first.Seq], Ct);

            Assert.Equal(1, report.Purged);
            Assert.Equal(2, report.RunSteps);
            Assert.Equal(["at", "after"], await LabelsAsync(store, Everyone("Alpha", "Beta")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Deleting_a_team_removes_its_steps_leaves_another_teams_and_its_deleted_row_counts_them()
    {
        var gone = await bed.TeamAsync("Steps deleted", "Dev");
        var kept = await bed.TeamAsync("Steps kept", "Dev");
        var at = DateTimeOffset.UtcNow;

        await Write(bed.Store, gone, "Dev", at, "one");
        await Write(bed.Store, gone, "Manager", at, "two");
        await Write(bed.Store, kept, "Dev", at, "kept");

        var deleted = await bed.Person.DeleteAsync($"/api/teams/{gone}", Ct);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(2, (await deleted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("runSteps").GetInt32());

        Assert.Empty(await LabelsAsync(bed.Store, Everyone(gone)));
        Assert.Equal(["kept"], await LabelsAsync(bed.Store, Everyone(kept)));

        var row = await bed.TenantRowAsync(TenantActions.TeamDeleted, gone);
        Assert.Equal(2, JsonDocument.Parse(row.Detail!).RootElement.GetProperty("runSteps").GetInt32());
    }

    [Fact]
    public async Task A_team_created_again_under_the_same_name_reads_none_of_its_predecessors_steps()
    {
        const string label = "Steps successor";
        var first = await bed.TeamAsync(label, "Dev");
        var deleted = await bed.Person.DeleteAsync($"/api/teams/{first}", Ct);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        // A predecessor's step the deletion did not reach: one its last flush wrote as it went.
        var leftover = DateTimeOffset.UtcNow;
        await Write(bed.Store, first, "Dev", leftover, "predecessor");
        await Task.Delay(20, Ct);

        var second = await bed.TeamAsync(label, "Dev");
        Assert.Equal(first, second);
        var created = await bed.CreatedAsync(second);
        Assert.True(created > leftover);
        await Write(bed.Store, second, "Dev", created.AddSeconds(1), "successor");

        var read = await bed.ReadAsync($"?to={Instant(created.AddMinutes(1))}");
        Assert.Equal(["successor"], Labels(read, second));
    }

    [Fact]
    public async Task Reset_with_delete_memory_removes_only_the_ticked_members_steps_and_its_reset_row_counts_them()
    {
        var team = await bed.TeamAsync("Steps reset", "Dev", "Ops");
        var other = await bed.TeamAsync("Steps reset other", "Dev");
        var at = DateTimeOffset.UtcNow;

        await Write(bed.Store, team, "Dev", at, "dev1");
        await Write(bed.Store, team, "Dev", at, "dev2");
        await Write(bed.Store, team, "Ops", at, "ops");
        await Write(bed.Store, other, "Dev", at, "other");

        // Without Delete memory nothing goes.
        var floored = await bed.Person.PostAsJsonAsync($"/api/teams/{team}/reset", new { members = new[] { "Dev" } }, Ct);
        Assert.Equal(HttpStatusCode.OK, floored.StatusCode);
        Assert.Equal(3, (await LabelsAsync(bed.Store, Everyone(team))).Count);

        var reset = await bed.Person.PostAsJsonAsync(
            $"/api/teams/{team}/reset", new { members = new[] { "Dev" }, purge = true }, Ct);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(2, (await reset.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("runSteps").GetInt32());

        Assert.Equal(["ops"], await LabelsAsync(bed.Store, Everyone(team)));
        Assert.Equal(["other"], await LabelsAsync(bed.Store, Everyone(other)));

        var row = await bed.TenantRowAsync(TenantActions.TeamReset, team);
        Assert.Equal(2, JsonDocument.Parse(row.Detail!).RootElement.GetProperty("runSteps").GetInt32());
    }

    [Fact]
    public async Task The_route_refuses_a_machine_principal()
    {
        var team = await bed.TeamAsync("Steps machine", "Dev");
        using var member = await bed.ContainerAsync(team, "Dev");

        var refused = await member.GetAsync("/api/run-steps", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task The_route_answers_a_periods_steps_oldest_first_paged_in_the_push_shape()
    {
        var team = await bed.TeamAsync("Steps paged", "Dev", "Ops");

        // A period of its own, after every other test's rows, so the pages are this test's alone.
        var t0 = DateTimeOffset.UtcNow.AddDays(30).AddMinutes(Random.Shared.Next(100_000));
        await Write(bed.Store, team, "Dev", t0.AddSeconds(-10), "before");
        await bed.Store.WriteAsync(team, "Dev", 77, 7, [new NewAgentStep(t0, "tool", "MCP", "tell") { Count = 3 }], [], Ct);
        await bed.Store.WriteAsync(team, "Ops", 78, null, [new NewAgentStep(t0.AddSeconds(1), "text", "", null)], [], Ct);
        await Write(bed.Store, team, "Dev", t0.AddSeconds(2), "three");
        await Write(bed.Store, team, "Dev", t0.AddSeconds(3), "four");
        await Write(bed.Store, team, "Dev", t0.AddSeconds(30), "after");

        var period = $"from={Instant(t0)}&to={Instant(t0.AddSeconds(3))}";
        var first = await bed.ReadAsync($"?{period}&take=2");
        Assert.Equal(["MCP", ""], Labels(first, team));
        var next = first.GetProperty("next").GetInt64();

        var second = await bed.ReadAsync($"?{period}&take=2&after={next}");
        Assert.Equal(["three", "four"], Labels(second, team));

        var last = await bed.ReadAsync($"?{period}&take=2&after={second.GetProperty("next").GetInt64()}");
        Assert.Empty(last.GetProperty("steps").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("next").ValueKind);

        var one = first.GetProperty("steps")[0];
        Assert.True(one.GetProperty("id").GetInt64() > 0);
        Assert.Equal(team, one.GetProperty("team").GetString());
        Assert.Equal("Dev", one.GetProperty("member").GetString());
        Assert.Equal(77, one.GetProperty("run").GetInt64());
        Assert.Equal(7, one.GetProperty("workflow").GetInt64());
        Assert.Equal(t0, one.GetProperty("at").GetDateTimeOffset());
        Assert.Equal("tool", one.GetProperty("kind").GetString());
        Assert.Equal("MCP", one.GetProperty("label").GetString());
        Assert.Equal("tell", one.GetProperty("mcpTool").GetString());
        Assert.Equal(3, one.GetProperty("count").GetInt32());

        var two = first.GetProperty("steps")[1];
        Assert.Equal(("Ops", "text", 78L), (two.GetProperty("member").GetString(), two.GetProperty("kind").GetString(), two.GetProperty("run").GetInt64()));
        Assert.Equal(JsonValueKind.Null, two.GetProperty("workflow").ValueKind);
        Assert.Equal(JsonValueKind.Null, two.GetProperty("mcpTool").ValueKind);

        var backwards = await bed.Person.GetAsync($"/api/run-steps?from={Instant(t0)}&to={Instant(t0.AddSeconds(-1))}", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
    }

    [Fact]
    public async Task A_finished_runs_steps_name_its_terminal_row_and_a_running_ones_its_delivery()
    {
        var team = await bed.TeamAsync("Steps run seq", "Dev");
        var gate = new TaskCompletionSource();
        var line = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Grep","input":{"pattern":"x"}}]},"timestamp":"2026-09-26T13:34:44Z"}""";

        var running = bed.RunAsync(team, "Dev", async invocation =>
        {
            var path = Path.Combine(bed.Outside, $"{Guid.NewGuid():N}.jsonl");
            await File.WriteAllLinesAsync(path, [line], Ct);
            using (bed.Live.Begin(invocation.Container, path, LiveView.ClaudeJsonl, ""))
            {
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            }

            return new AgentResult(0, "done");
        });

        var delivery = 0L;
        await AgentStepBed.UntilAsync(async () =>
        {
            var steps = await bed.StepsAsync(team);
            if (steps.Count == 0) return false;
            delivery = steps[0].Run;
            return true;
        });

        // While it runs, the step names the run's delivery: the instruction the member was told.
        var told = (await bed.Services.GetRequiredService<IMessageLog>().ReadRangeAsync(delivery - 1, 1, Ct)).Single();
        Assert.Equal(delivery, told.Seq);
        Assert.Contains("Do the card.", told.Payload, StringComparison.Ordinal);

        gate.SetResult();
        var run = await running;
        Assert.Equal(told.CorrelationId, run.Workflow);

        var finished = Assert.Single(await bed.StepsAsync(team));
        Assert.Equal(run.Terminal, finished.Run);
        Assert.NotEqual(delivery, finished.Run);
        Assert.Equal(("tool", "Grep"), (finished.Kind, finished.Label));
    }

    private static Task Write(AgentStepStore store, string team, string member, DateTimeOffset at, string label) =>
        store.WriteAsync(team, member, 1, 1, [new NewAgentStep(at, "tool", label, null)], [], Ct);

    private static Dictionary<string, DateTimeOffset> Everyone(params string[] teams) =>
        teams.ToDictionary(team => team, _ => DateTimeOffset.MinValue, StringComparer.OrdinalIgnoreCase);

    private static async Task<List<string>> LabelsAsync(AgentStepStore store, Dictionary<string, DateTimeOffset> teams) =>
        [.. (await store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 500, teams, Ct)).Select(s => s.Label)];

    private static List<string> Labels(JsonElement page, string team) =>
        [.. page.GetProperty("steps").EnumerateArray()
            .Where(s => s.GetProperty("team").GetString() == team)
            .Select(s => s.GetProperty("label").GetString()!)];

    private static string Instant(DateTimeOffset at) => Uri.EscapeDataString(at.UtcDateTime.ToString("o"));
}

/// <summary>
/// A real Host whose agent runner is the test's: each member's run does what its test gives it, and a
/// followed one writes a transcript outside the data root and begins its live run as the real
/// runner's worker does. Warnings are kept, for the tests that say what is logged.
/// </summary>
public sealed class AgentStepBed : IAsyncLifetime
{
    private const string Email = "person@example.test";

    private readonly ConcurrentDictionary<ContainerId, Func<AgentInvocation, Task<AgentResult>>> _runs = new();
    private readonly FakeAgent _agent = new();
    private readonly List<HubConnection> _hubs = [];
    private WebApplicationFactory<Program> _factory = null!;

    public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), $"harness-run-steps-{Guid.NewGuid():N}");

    /// <summary>Where the test transcripts are written: outside the data root, as an agent's home may be.</summary>
    public string Outside { get; } = Directory.CreateTempSubdirectory("harness-run-steps-transcripts-").FullName;

    public HttpClient Person { get; private set; } = null!;

    public string Cookie { get; private set; } = "";

    public IServiceProvider Services => _factory.Services;

    public AgentStepStore Store => Services.GetRequiredService<AgentStepStore>();

    public LiveRuns Live => Services.GetRequiredService<LiveRuns>();

    public CapturedLogs Logs { get; } = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(DataRoot);
        PluginMemberEndToEndTests.InstallSampleEcho(DataRoot);

        // Once: a member told directly owns its workflow and is offered another turn when it ends
        // with the workflow open, and that turn does nothing.
        _agent.Behaviour = invocation => _runs.TryRemove(invocation.Container, out var run)
            ? run(invocation)
            : Task.FromResult(new AgentResult(0, "done"));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", DataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(_agent);
                services.AddSingleton<ILoggerProvider>(Logs);
            }));

        // Made at run time: no sign-in secret is written into the test.
        var password = $"pw-{Guid.NewGuid():N}";
        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, password);
        Person = _factory.CreateClient();
        var login = await Person.PostAsJsonAsync("/api/auth/login", new { email = Email, password });
        login.EnsureSuccessStatusCode();
        Cookie = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));

        // The route answers nothing older than the oldest log row, so the log holds one first.
        await Services.GetRequiredService<IMessageLog>().AppendAsync(new NewMessage("test.run-steps", "{}", "test"));
        await Task.Delay(20);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hub in _hubs) await hub.DisposeAsync();
        Person.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataRoot, recursive: true); } catch (IOException) { }
        try { Directory.Delete(Outside, recursive: true); } catch (IOException) { }
    }

    /// <summary>A new team with these members beside its Manager; answers its id.</summary>
    public async Task<string> TeamAsync(string label, params string[] members)
    {
        var team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync(label, "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        foreach (var member in members)
        {
            var added = await Person.PostAsJsonAsync(
                $"/api/teams/{team}/containers", new { name = member, agent = "claude-headless" }, Ct);
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        }

        return team;
    }

    /// <summary>A run that writes <paramref name="lines"/> as its transcript and is followed from its start to its end.</summary>
    public Func<AgentInvocation, Task<AgentResult>> Followed(string format, IReadOnlyList<string> lines) => async invocation =>
    {
        var path = Path.Combine(Outside, $"{Guid.NewGuid():N}.jsonl");
        await File.WriteAllLinesAsync(path, lines, Ct);
        using (Live.Begin(invocation.Container, path, format, ""))
        {
            await Task.Delay(100, Ct);
        }

        return new AgentResult(0, "done");
    };

    /// <summary>
    /// Tells <paramref name="member"/> to work, with <paramref name="run"/> as what its run does (null
    /// for a member this runner does not run, a plugin), and waits for its terminal row and for every
    /// follow to finish. Answers the terminal row's seq, type and workflow: the first after the tell.
    /// </summary>
    public async Task<(long Terminal, string Type, long Workflow)> RunAsync(
        string team, string member, Func<AgentInvocation, Task<AgentResult>>? run)
    {
        var id = new ContainerId(team, member);
        if (run is not null) _runs[id] = run;

        var log = Services.GetRequiredService<IMessageLog>();
        var before = await log.HighestSeqAsync(Ct);
        var told = await Person.PostAsJsonAsync($"/api/teams/{team}/containers/{member}/tell", new { instruction = "Do the card." }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));

        Message? terminal = null;
        await UntilAsync(async () =>
        {
            terminal = (await log.ReadAfterAsync(before, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct))
                .FirstOrDefault(m => m.Source == id.ToString());
            return terminal is not null;
        });

        await Services.GetRequiredService<AgentStepFollow>().Settled().WaitAsync(TimeSpan.FromSeconds(30), Ct);
        return (terminal!.Seq, terminal.Type, terminal.CorrelationId);
    }

    /// <summary>Every recorded step of the team, oldest first, whatever its time.</summary>
    public async Task<IReadOnlyList<AgentStep>> StepsAsync(string team) =>
        await Store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 500,
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase) { [team] = DateTimeOffset.MinValue }, Ct);

    public async Task<DateTimeOffset> CreatedAsync(string team) =>
        await Services.GetRequiredService<ITeamStore>().CreatedAtAsync(team, Ct)
            ?? throw new InvalidOperationException($"{team} has no creation time.");

    public async Task<HttpClient> ContainerAsync(string team, string member)
    {
        var key = await Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(team, member).ToString(), PrincipalKind.Container, team, Permits.All);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);
        return client;
    }

    public async Task<JsonElement> ReadAsync(string query)
    {
        var answer = await Person.GetAsync($"/api/run-steps{query}", Ct);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        return await answer.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    public async Task<TenantEvent> TenantRowAsync(string action, string team) =>
        (await Services.GetRequiredService<ITenantLog>().ReadAsync(null, ITenantLog.MaxTake, Ct)).Events
            .First(e => e.Action == action && string.Equals(e.Subject, team, StringComparison.OrdinalIgnoreCase));

    public HubConnection Hub(Action<HttpConnectionOptions> credential)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hub/containers"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                credential(options);
            })
            .Build();
        _hubs.Add(hub);
        return hub;
    }

    public static async Task UntilAsync(Func<Task<bool>> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not hold within 30 seconds.");
            await Task.Delay(25, Ct);
        }
    }
}

/// <summary>Every warning and above the Host logs, kept for a test to read.</summary>
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _lines = new();

    public IEnumerable<(string Category, LogLevel Level, string Message)> Where(Func<(string Category, LogLevel Level, string Message), bool> which) =>
        _lines.Where(which);

    public void Clear() => _lines.Clear();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<(string, LogLevel, string)> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) lines.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }
}
