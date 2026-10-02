using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// Compares text with a file under <c>Fixtures/Goldens</c>, byte for byte.
///
/// THE GOLDENS ARE THE REFACTOR'S SAFETY NET, recorded before the generic member runtime existed:
/// an agent member's terminal rows, the invocation it is handed, the child's argv/stdin/environment,
/// the persisted member row and the snapshot JSON. A step that changes one byte of any of them
/// changed what an existing team sees. Re-record only for a change that is MEANT to be visible, with
/// <c>HARNESS_UPDATE_GOLDENS=1</c>, and say so in the commit.
/// </summary>
internal static class Golden
{
    public static void Match(string name, string actual, [CallerFilePath] string caller = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(caller)!, "Fixtures", "Goldens", name + ".txt");
        actual = actual.Replace("\r\n", "\n", StringComparison.Ordinal);

        if (Environment.GetEnvironmentVariable("HARNESS_UPDATE_GOLDENS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"No golden at {path}. Record it once with HARNESS_UPDATE_GOLDENS=1.");
        Assert.Equal(File.ReadAllText(path), actual);
    }
}

/// <summary>Step 0 of the member-runtime refactor: what an AGENT member produces today.</summary>
public sealed class MemberGoldenTests
{
    private static readonly ContainerId Dev = new("alpha", "dev");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Rows(IEnumerable<Message> rows) =>
        string.Join("\n", rows.Select(m =>
            $"{m.Seq} {m.Type} source={m.Source} cause={m.CausationSeq} corr={m.CorrelationId}\n{m.Payload}")) + "\n";

    private static async Task<string> TerminalRowsAsync(ContainerTestBed bed) =>
        Rows((await bed.Store.ReadAfterAsync(
            0, [MessageTypes.Started, MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct))
            .OrderBy(m => m.Seq));

    private static async Task<string> OneRunAsync(Func<AgentInvocation, Task<AgentResult>> behaviour)
    {
        await using var bed = new ContainerTestBed();
        bed.Agent.Behaviour = behaviour;
        await bed.AddAsync(Dev);

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Dev), """{"instruction":"go"}""", "console"), Ct);

        Assert.True(await bed.PumpUntilAsync(async () =>
            (await bed.Store.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed ], 10, Ct)).Count > 0));

        return await TerminalRowsAsync(bed);
    }

    public static TheoryData<string> Arms() =>
    [
        "success", "success-usage-transcript", "exit-nonzero", "launch-error", "did-nothing",
        "classified-rate", "timeout", "runner-throws",
    ];

    [Theory]
    [MemberData(nameof(Arms))]
    public async Task Terminal_rows_for_each_arm_are_unchanged(string arm)
    {
        var retry = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var text = await OneRunAsync(_ => arm switch
        {
            "success" => Task.FromResult(new AgentResult(0, "done")),
            "success-usage-transcript" => Task.FromResult(new AgentResult(
                0, "all done", Usage: new InvocationUsage(1200, 340, "claude-json", cachedIn: 1000, reasoning: 5, cacheCreation: 7),
                ProcessId: 4242, ReachedThePlatform: true,
                AgentTranscript: new AgentTranscript("/home/agent/.claude/projects/x/1.jsonl", "claude-jsonl"))),
            "exit-nonzero" => Task.FromResult(new AgentResult(3, "it broke")),
            "launch-error" => Task.FromResult(new AgentResult(-1, string.Empty, "`nope` is not an executable file on PATH, so this member could not be started.")),
            "did-nothing" => Task.FromResult(new AgentResult(0, "I did nothing", ReachedThePlatform: false)),
            "classified-rate" => Task.FromResult(new AgentResult(
                1, "429 slow down", Usage: new InvocationUsage(10, 0, "claude-json"),
                FailureClass: FailureClasses.Rate, RetryAfter: retry)),
            "timeout" => Task.FromResult(new AgentResult(
                -1, string.Empty, "This run went 60s without reporting progress and was stopped.",
                ProcessId: 99, FailureClass: FailureClasses.Timeout)),
            "runner-throws" => throw new InvalidOperationException("the runner fell over"),
            _ => throw new ArgumentOutOfRangeException(nameof(arm)),
        });

        Golden.Match($"rows-{arm}", text);
    }

    /// <summary>
    /// THE CARD'S `failed` MARK, for the two arms whose words moved out of the runtime and into
    /// <see cref="AgentMemberRunner"/>. Recorded after that move, as a
    /// NEW golden, so the rows goldens above stay exactly as step 0 recorded them.
    /// </summary>
    [Theory]
    [InlineData("exit-nonzero")]
    [InlineData("did-nothing")]
    public async Task The_failed_mark_for_each_agent_failure_arm_is_unchanged(string arm)
    {
        await using var bed = new ContainerTestBed();
        bed.Agent.Behaviour = _ => Task.FromResult(arm switch
        {
            "exit-nonzero" => new AgentResult(3, "it broke"),
            "did-nothing" => new AgentResult(0, "I did nothing", ReachedThePlatform: false),
            _ => throw new ArgumentOutOfRangeException(nameof(arm)),
        });
        var member = await bed.AddAsync(Dev);

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Dev), """{"instruction":"go"}""", "console"), Ct);

        Assert.True(await bed.PumpUntilAsync(() => member.Snapshot().Failed is not null));

        var snapshot = member.Snapshot();
        Golden.Match($"failed-mark-{arm}", $"failed={snapshot.Failed}\nfailureClass={snapshot.FailureClass}\n");
    }

    [Fact]
    public async Task A_person_stopping_a_run_writes_unchanged_rows()
    {
        await using var bed = new ContainerTestBed();
        var started = new TaskCompletionSource();

        // A runner that honours cancellation the way a fake does: by throwing.
        var cancelling = new CancellingAgent(bed.Agent, started);
        var container = await bed.Host.AddAsync(ContainerTestBed.Definition(Dev), bed.AsMember(cancelling), Ct);

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Dev), """{"instruction":"go"}""", "console"), Ct);

        await bed.Host.PumpOnceAsync(Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(container.Stop());

        Assert.True(await bed.PumpUntilAsync(async () =>
            (await bed.Store.ReadAfterAsync(0, [MessageTypes.Failed ], 10, Ct)).Count > 0));

        Golden.Match("rows-stopped", await TerminalRowsAsync(bed));
    }

    private sealed class CancellingAgent(FakeAgent inner, TaskCompletionSource started) : IAgentRunner
    {
        public async Task<AgentResult> RunAsync(AgentInvocation invocation, CancellationToken ct = default)
        {
            inner.Invocations.Enqueue(invocation);
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new AgentResult(0, "never");
        }
    }

    /// <summary>
    /// The invocation for a BATCH of two, with history in the ledger and a card's worktree: the
    /// prompt text, the context, the environment. Moving prompt rendering out of the runtime must
    /// not change a character of it.
    /// </summary>
    [Fact]
    public async Task The_invocation_for_a_batch_with_history_and_a_worktree_is_unchanged()
    {
        await using var bed = new ContainerTestBed(worktrees: (id, key) =>
            [new RepoWorktree("Repo", "/r/Repo/main", $"/r/Repo/wt_{id.Name}_{key}")]);

        var release = new TaskCompletionSource();
        var runs = 0;

        bed.Agent.Behaviour = async _ =>
        {
            if (Interlocked.Increment(ref runs) == 2) await release.Task;
            return new AgentResult(0, $"answer {runs}");
        };

        await bed.Host.AddAsync(
            ContainerTestBed.Definition(Dev) with
            {
                SystemPrompt = "You are Dev.",
                Environment = new Dictionary<string, string> { ["HARNESS_URL"] = "http://h", ["TEAM_VAR"] = "1" },
            },
            bed.Runner, Ct);

        // Run 1: history for the ledger.
        var first = await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Dev), """{"instruction":"first job","card":"41"}""", "alpha/manager"), Ct);
        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.Invocations.Count == 1));
        Assert.True(await bed.PumpUntilAsync(async () =>
            (await bed.Store.ReadAfterAsync(0, [MessageTypes.Completed ], 10, Ct)).Count == 1));

        // Run 2 holds while two more of ONE workflow arrive, so they batch into run 3.
        var second = await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Dev), """{"instruction":"hold on","card":"42"}""", "alpha/manager", first.Seq), Ct);
        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.Invocations.Count == 2));

        var third = await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Dev), """{"instruction":"part one","card":"43"}""", "alpha/manager", first.Seq), Ct);
        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Dev), """{"instruction":"part two"}""", "alpha/manager", third.Seq), Ct);
        await bed.SettleAsync();
        release.SetResult();

        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.Invocations.Count == 3));
        await bed.SettleAsync();

        var text = new StringBuilder();

        foreach (var invocation in bed.Agent.Invocations)
        {
            text.Append("=== invocation\n");
            text.Append($"container={invocation.Container} agent={invocation.Agent} cwd={invocation.WorkingDirectory} unreachable={invocation.UnreachableRoot}\n");
            text.Append("--- system\n").Append(invocation.SystemPrompt).Append('\n');
            text.Append("--- environment\n");
            foreach (var (k, v) in invocation.Environment.OrderBy(p => p.Key, StringComparer.Ordinal)) text.Append($"{k}={v}\n");
            text.Append("--- prompt\n").Append(invocation.Prompt).Append('\n');
            text.Append("--- context\n").Append(invocation.Context).Append('\n');
        }

        text.Append("=== rows\n").Append(await TerminalRowsAsync(bed));

        Golden.Match("invocation-batch", Normalise(text.ToString()));
        _ = first;
        _ = second;
    }

    /// <summary>The ledger context carries times; nothing else in these goldens does.</summary>
    private static string Normalise(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            text, @"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})?", "<time>");

    /// <summary>
    /// What the CHILD receives from <see cref="ProcessAgentRunner"/>: argv, stdin and environment,
    /// inline and through a prompt file. Extracting the process launcher must not change it.
    /// </summary>
    [Theory]
    [InlineData("stdin")]
    [InlineData("file")]
    public async Task The_child_is_launched_with_unchanged_argv_stdin_and_environment(string form)
    {
        var workspace = Directory.CreateTempSubdirectory("harness-golden-").FullName;

        try
        {
            string[] arguments = form == "file"
                ? ["-c", "printf 'argv:%s|' \"$0\" \"$@\"; echo; echo '--- file'; cat \"$1\"; echo; echo '--- stdin'; cat; echo; echo '--- env'; env | grep -E '^(HARNESS_CAUSATION|TEAM_VAR|FORCE_COLOR)=' | sort", "golden", "{userPromptFile}"]
                : ["-c", "printf 'argv:%s|' \"$0\" \"$@\"; echo; echo '--- stdin'; cat; echo; echo '--- env'; env | grep -E '^(HARNESS_CAUSATION|TEAM_VAR|FORCE_COLOR)=' | sort", "golden", "{workspace}"];

            var catalog = new AgentCatalog(
            [
                new AgentDefinition("probe", AgentMode.Headless, new AgentLaunch("bash", arguments, LanguageModel: false)),
            ]);

            var result = await new ProcessAgentRunner(catalog, new RunHeartbeat()).RunAsync(
                new AgentInvocation(
                    Dev,
                    "You are a probe.",
                    "the instruction\nline two",
                    workspace,
                    new Dictionary<string, string> { ["HARNESS_CAUSATION"] = "7", ["TEAM_VAR"] = "x" },
                    Context: "earlier history",
                    Agent: "probe"),
                TestContext.Current.CancellationToken);

            var text = $"exit={result.ExitCode} launchError={result.LaunchError} class={result.FailureClass} succeeded={result.Succeeded}\n"
                + result.Output.Replace(workspace, "<workspace>", StringComparison.Ordinal);

            // The runner writes the prompt file under Path.GetTempPath(), which honours TMPDIR: the
            // release runs this suite with TMPDIR in a scratch folder, not /tmp.
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                System.Text.RegularExpressions.Regex.Escape(Path.GetTempPath()) + @"os-[0-9a-f]{32}\.prompt\.txt",
                "<promptfile>");

            Golden.Match($"child-{form}", text + "\n");
        }
        finally
        {
            MemberTempCleanup.Remove(workspace);
            Directory.Delete(workspace, recursive: true);
        }
    }

    /// <summary>
    /// THE REAL HIRE PATH: an agent member hired into a team through <see cref="TeamRegistry"/>,
    /// then woken. Pins the persisted row, the composed system prompt, the environment
    /// <see cref="AgentEnvironment"/> built, and the snapshot JSON the browser receives.
    /// </summary>
    [Fact]
    public async Task A_hired_agent_members_row_prompt_environment_and_snapshot_are_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var dataRoot = Path.Combine(Path.GetTempPath(), $"harness-golden-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        var fake = new FakeAgent();

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(fake)));

        try
        {
            var services = factory.Services;
            var registry = services.GetRequiredService<TeamRegistry>();

            var team = (await registry.CreateAsync("Golden", "claude-headless", memberAgent: "claude-headless", ct: ct)).Id;
            var member = new ContainerId(team, "Worker");

            await registry.AddContainerAsync(team, "Worker", "claude-headless", "", [], ct: ct);

            var log = services.GetRequiredService<IMessageLog>();
            var host = services.GetRequiredService<ContainerHost>();

            // The snapshot AFTER the run is the one the runtime PUBLISHES as it goes Idle, never a
            // second read. The run's end is not the member's last wake: the workflow is left open,
            // so the idle-workflow offer delivers it one more instruction, and a check that saw it
            // Idle followed by a read of its own could land on that second run (seen: Running,
            // queue 1). Idle is published after the terminal row is written and with every run
            // field cleared, so this snapshot is Idle with its `completed` row by construction.
            // Only one published after a Running one counts, and only with nothing queued: an
            // Idle publish from before the run, or one with the offer already waiting, is not it.
            //
            // AND ONLY ONE WITH NO RUN IN IT. Not every frame is the consumer's: `OfferAsync`
            // publishes from the pump's thread, and `Snapshot()` reads `_state` before
            // `QueueDepth` and `_currentCorrelation`. A pump thread preempted between those reads
            // while the consumer starts the run publishes Idle (read before), queue 0 and the run's
            // correlation (read after) - seen as a settled snapshot taken mid-run, with no
            // `completed` row yet. The correlation is read last, so null there means the run had
            // not been taken (queue still counted it) or had already ended.
            //
            // `currentCorrelation` is also pinned null in the golden, so filtering on it hides
            // nothing: an Idle member that kept a correlation still fails here, as the timeout below.
            var settled = new TaskCompletionSource<ContainerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new Lock();
            var sawRunning = false;
            void OnChanged(ContainerSnapshot s)
            {
                if (s.Team != member.Team || s.Id != member.Name) return;

                lock (gate)
                {
                    if (s.State == ContainerState.Running) sawRunning = true;
                    else if (sawRunning && s.State == ContainerState.Idle && s.QueueDepth == 0 && s.CurrentCorrelation is null) settled.TrySetResult(s);
                }
            }

            host.Changed += OnChanged;

            ContainerSnapshot snapshot;
            try
            {
                await log.AppendAsync(new NewMessage(
                    MessageTypes.InstructionFor(member), """{"instruction":"go"}""", "console"), ct);

                // A deadline of its own, and a failure that says so when it passes: under a full
                // suite's load the start alone can use most of a shorter one.
                var idle = await Task.WhenAny(settled.Task, Task.Delay(TimeSpan.FromSeconds(60), ct));
                Assert.True(idle == settled.Task, "The member did not return to Idle after its run within 60 seconds.");
                snapshot = await settled.Task;
            }
            finally
            {
                host.Changed -= OnChanged;
            }

            Assert.NotEmpty(await log.ReadAfterAsync(0, [MessageTypes.Completed], 10, ct));

            // The FIRST run is the one pinned; the offer's run, when it has started, is a second.
            var run = fake.Invocations.First(i => i.Container == member);

            var row = (await services.GetRequiredService<ITeamStore>().MembersAsync(ct))
                .Single(m => m.Team == team && m.Name == "Worker");

            var text = new StringBuilder();
            text.Append("--- row\n");
            text.Append($"label={row.Label} agent={row.Agent} prompt={row.SystemPrompt} subscribes=[{string.Join(",", row.Subscribes.Order(StringComparer.Ordinal))}] permits=[{string.Join(",", row.Permits.Order(StringComparer.Ordinal))}] hiredFor={row.HiredFor}\n");
            text.Append("--- invocation\n");
            text.Append($"agent={run.Agent} cwd={run.WorkingDirectory} unreachable={run.UnreachableRoot}\n");
            text.Append("--- environment\n");
            foreach (var (k, v) in run.Environment.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                text.Append(k).Append('=').Append(k == "HARNESS_KEY" || k.Contains("PASSWORD", StringComparison.Ordinal) ? "<secret>" : v).Append('\n');
            }
            text.Append("--- system\n").Append(run.SystemPrompt).Append('\n');
            text.Append("--- prompt\n").Append(run.Prompt).Append('\n');
            text.Append("--- snapshot\n");
            text.Append(SnapshotWithoutAdditions(snapshot)).Append('\n');

            Golden.Match("hired-agent-member", text.ToString().Replace(dataRoot, "<root>", StringComparison.Ordinal));
        }
        finally
        {
            await factory.DisposeAsync();
            MemberTempCleanup.Remove(dataRoot);
            try { Directory.Delete(dataRoot, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// The snapshot as the browser receives it, less any field ADDED after step 0. Additive fields
    /// are the one change the wire rules allow; everything that was there must keep its name and
    /// its value, and <see cref="AddedSinceStepZero"/> is the explicit list of what was added.
    /// </summary>
    internal static string SnapshotWithoutAdditions(ContainerSnapshot snapshot)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, JsonSerializerOptions.Web));
        var kept = document.RootElement.EnumerateObject()
            .Where(p => !AddedSinceStepZero.Contains(p.Name))
            .ToDictionary(p => p.Name, p => p.Value.Clone());

        return JsonSerializer.Serialize(kept);
    }

    internal static readonly IReadOnlySet<string> AddedSinceStepZero = new HashSet<string>(StringComparer.Ordinal)
    {
        // Step 4: what kind of member this is. Its own value for an agent is pinned below.
        "kind",
    };

    [Fact]
    public async Task An_agent_members_added_kind_is_agent()
    {
        await using var bed = new ContainerTestBed();
        var member = await bed.AddAsync(Dev);

        Assert.Contains("\"kind\":\"agent\"", JsonSerializer.Serialize(member.Snapshot(), JsonSerializerOptions.Web), StringComparison.Ordinal);
    }
}

/// <summary>
/// WHICH CREDENTIAL VARIABLES REACH A HOME RUN'S CHILD, by name, for each built-in headless preset
/// launched through the real path for a team with a GitHub remote while the Host holds every
/// declared variable, every provider key and the git token. Its own class because it changes this
/// process's environment.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class MemberCredentialGoldenTests
{
    [Fact]
    public async Task Credential_variable_names_each_built_in_headless_preset_receives_on_home()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake CLIs are shell scripts.");

        using var bed = new HomeLaunchBed();
        var text = new StringBuilder();

        foreach (var preset in HomeLaunchBed.HeadlessPresets())
        {
            var seen = await bed.RunAsync(preset.Data);
            var names = seen.Keys.Where(HomeLaunchBed.HostCredentials.Contains).Order(StringComparer.Ordinal);
            text.Append(preset.Data).Append(':').AppendJoin("", names.Select(n => " " + n)).Append('\n');
        }

        Golden.Match("home-credential-variables", text.ToString());
    }
}
