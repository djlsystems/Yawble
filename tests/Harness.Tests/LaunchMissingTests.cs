using System.Collections.Concurrent;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A PROGRAM MISSING AT LAUNCH IS LOOKED FOR AGAIN, and one that never appears is a launch failure
/// of its own class, not an agent fault. The shared install every member launches from is replaced
/// in place while a CLI updates, so a launch can land in a gap of a few seconds with no program.
///
/// Stub programs only, at absolute paths in a folder of this test's own, so no real agent starts
/// and no other test's PATH changes. The lookup window is the test's, so nothing waits 30 seconds.
/// </summary>
public sealed class LaunchMissingTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("harness-launch-missing-").FullName;
    private readonly string _bin = Directory.CreateTempSubdirectory("harness-launch-missing-bin-").FullName;

    public void Dispose()
    {
        MemberTempCleanup.Remove(_workspace);
        Directory.Delete(_workspace, recursive: true);
        Directory.Delete(_bin, recursive: true);
    }

    private static readonly LaunchLookup Short = new(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100));

    private sealed class RecordedReports : IMemberReports
    {
        public ConcurrentQueue<(ContainerId Member, string Status)> Progress { get; } = new();

        public Task<MemberReportOutcome> ProgressAsync(ContainerId member, string status, CancellationToken ct = default)
        {
            Progress.Enqueue((member, status));
            return Task.FromResult(MemberReportOutcome.Ok);
        }

        private static Task<MemberReportOutcome> Unexpected() => throw new InvalidOperationException("only progress is reported");

        public Task<MemberReportOutcome> BlockedAsync(ContainerId member, string reason, int? item = null, CancellationToken ct = default) => Unexpected();
        public Task<MemberReportOutcome> DeferAsync(ContainerId member, int item, string reason, CancellationToken ct = default) => Unexpected();
        public Task<MemberReportOutcome> NeedsDecisionAsync(ContainerId member, string question, CancellationToken ct = default) => Unexpected();
        public Task<MemberReportOutcome> HandbackAsync(ContainerId member, string delivered, CancellationToken ct = default) => Unexpected();
        public Task<MemberReportOutcome> PublishAsync(ContainerId member, string type, string payload, CancellationToken ct = default) => Unexpected();
    }

    private Task<AgentResult> RunAsync(string fileName, LaunchLookup lookup, IMemberReports? reports, CancellationToken ct)
    {
        var catalog = new AgentCatalog(
        [
            new AgentDefinition("probe", AgentMode.Headless, new AgentLaunch(fileName, [], LanguageModel: false)),
        ]);

        return new ProcessAgentRunner(catalog, new RunHeartbeat(), reports: reports, lookup: lookup).RunAsync(
            new AgentInvocation(
                new ContainerId("alpha", "worker"),
                "You are a probe.",
                "hello",
                _workspace,
                new Dictionary<string, string>(),
                Agent: "probe"),
            ct);
    }

    /// <summary>Puts an executable stub at <paramref name="path"/> in one rename, as an install
    /// does, so no look ever sees half a file.</summary>
    private static void Install(string path)
    {
        var staged = path + ".staged";
        File.WriteAllText(staged, "#!/bin/sh\ncat >/dev/null\necho started\n");
        File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Move(staged, path);
    }

    [Fact]
    public async Task A_program_missing_at_launch_that_appears_two_seconds_later_starts_the_run_normally()
    {
        var ct = TestContext.Current.CancellationToken;
        var program = Path.Combine(_bin, "stub-cli");
        var reports = new RecordedReports();

        var appears = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            Install(program);
        }, ct);

        var result = await RunAsync(program, new LaunchLookup(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200)), reports, ct);
        await appears;

        Assert.Null(result.LaunchError);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.FailureClass);
        Assert.Contains("started", result.Output);

        // ONE PROGRESS LINE AND NOTHING ELSE: said once, before the first pause.
        var (member, status) = Assert.Single(reports.Progress);
        Assert.Equal(new ContainerId("alpha", "worker"), member);
        Assert.Contains("stub-cli", status);
        Assert.Contains("looking again", status);
    }

    [Fact]
    public async Task A_program_present_at_launch_is_not_waited_for_and_says_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var program = Path.Combine(_bin, "stub-cli");
        Install(program);
        var reports = new RecordedReports();

        var result = await RunAsync(program, LaunchLookup.Default, reports, ct);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(reports.Progress);
    }

    [Fact]
    public async Task A_program_that_never_appears_fails_launch_missing_and_is_not_an_agent_fault()
    {
        var result = await RunAsync(Path.Combine(_bin, "never-there"), Short, new RecordedReports(), TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(FailureClasses.LaunchMissing, result.FailureClass);
        Assert.Contains("not found when this run started", result.LaunchError);
        Assert.Contains("may be being installed or updated", result.LaunchError);
        Assert.Contains("re-sending the instruction will try again", result.LaunchError);
        Assert.DoesNotContain("repair", result.LaunchError, StringComparison.OrdinalIgnoreCase);

        // STILL launch-missing once the member adapter has classed it, even though the run never
        // reached the platform - which on its own would make it agent-fault.
        var member = AgentMemberRunner.ToMemberResult(result with { ReachedThePlatform = false });
        Assert.False(member.Succeeded);
        Assert.Equal(FailureClasses.LaunchMissing, member.FailureClass);
        Assert.Equal(result.LaunchError, member.FailureReason);
    }

    [Fact]
    public async Task A_stop_while_the_launch_is_looking_is_an_interruption()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMilliseconds(300));

        var result = await RunAsync(
            Path.Combine(_bin, "never-there"),
            new LaunchLookup(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100)),
            null,
            stop.Token);

        Assert.Equal(FailureClasses.Interrupted, result.FailureClass);
        Assert.Contains("stopped while it waited", result.LaunchError);
    }

    [Fact]
    public void Launch_missing_is_a_known_class_the_platform_does_not_resume_by_itself()
    {
        Assert.True(FailureClasses.IsKnown(FailureClasses.LaunchMissing));
        Assert.False(FailureClasses.ResumesAutomatically(FailureClasses.LaunchMissing));
        Assert.Equal("launch-missing", FailureClasses.LaunchMissing);
    }

    [Fact]
    public void The_Managers_context_for_a_launch_missing_failure_says_re_send_and_names_nobody_to_repair_anything()
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [PayloadFields.ExitCode] = -1,
            [PayloadFields.Output] = string.Empty,
            [PayloadFields.LaunchError] = ProcessAgentRunner.LaunchMissingText("claude", LaunchLookup.Default),
            [PayloadFields.FailureClass] = FailureClasses.LaunchMissing,
        });

        var text = MessageText.Of(new Message(
            7, MessageTypes.Failed, payload, "alpha/Worker", 1, 6, 1, DateTimeOffset.UtcNow));

        Assert.StartsWith("Worker FAILED. [launch-missing] ", text);
        Assert.Contains("not found when the run started", text);
        Assert.Contains("may be being installed or updated", text);
        Assert.Contains("re-sending the instruction will try again", text);
        Assert.DoesNotContain("repair", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("person", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("agent itself failed", text);

        // WHAT THE MANAGER DOES WITH IT: re-send, and a person only after twice.
        var skill = BuiltInSkills.Find("manager")!.Body;
        Assert.Contains("## A program missing at launch is re-sent, not escalated", skill);
        Assert.Contains("already failed this way twice", skill);

        var prompt = BuiltInPrompts.For(SkillRoles.Manager);
        Assert.Contains("`[launch-missing]`", prompt);
        Assert.Contains("Re-send the instruction", prompt);
        Assert.Contains("already happened twice", prompt);
    }
}
