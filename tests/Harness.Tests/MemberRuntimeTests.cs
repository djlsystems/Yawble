using System.Runtime.CompilerServices;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;

namespace Harness.Tests;

/// <summary>
/// The member runtime against a runner that is NOT an agent: no prompt, no usage, no transcript,
/// no agent words. What an agent member sees is pinned by <see cref="MemberGoldenTests"/>.
/// </summary>
public sealed class MemberRuntimeTests
{
    private static readonly ContainerId Plug = new("alpha", "plug");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Scripted(Func<MemberInvocation, MemberResult> answer) : IMemberRunner
    {
        public List<MemberInvocation> Seen { get; } = [];

        public Task<MemberResult> RunAsync(MemberInvocation invocation, CancellationToken ct = default)
        {
            Seen.Add(invocation);
            return Task.FromResult(answer(invocation));
        }
    }

    private static async Task<(Message Row, MemberRuntime Member, Scripted Runner)> RunOnceAsync(
        ContainerTestBed bed, Func<MemberInvocation, MemberResult> answer, string payload = """{"instruction":"go"}""")
    {
        var runner = new Scripted(answer);
        var member = await bed.Host.AddAsync(ContainerTestBed.Definition(Plug), runner, Ct);

        await bed.Store.AppendAsync(new NewMessage(MessageTypes.InstructionFor(Plug), payload, "console"), Ct);

        Assert.True(await bed.PumpUntilAsync(async () =>
            (await bed.Store.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], 10, Ct)).Count > 0));

        var row = (await bed.Store.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], 10, Ct)).Single();
        return (row, member, runner);
    }

    [Fact]
    public async Task The_runner_is_handed_the_work_as_data_not_a_prompt()
    {
        await using var bed = new ContainerTestBed();

        var (_, _, runner) = await RunOnceAsync(bed, _ => new MemberResult(true, 0, "ok"), """{"instruction":"go","card":"7"}""");

        var invocation = Assert.Single(runner.Seen);
        var work = Assert.Single(invocation.Work);
        Assert.Equal(MessageTypes.InstructionFor(Plug), work.Type);
        Assert.Equal("""{"instruction":"go","card":"7"}""", work.Payload);
        Assert.Equal(work.Seq, invocation.Causation);
        Assert.Equal("fake", invocation.Implementation);
        Assert.Equal("", invocation.Context.Instructions);
    }

    [Fact]
    public async Task A_run_with_no_usage_writes_no_figures_and_no_transcript_key()
    {
        await using var bed = new ContainerTestBed();

        var (row, _, _) = await RunOnceAsync(bed, _ => new MemberResult(true, 0, "HELLO"));

        Assert.Equal(MessageTypes.Completed, row.Type);
        using var payload = JsonDocument.Parse(row.Payload);
        Assert.Equal("HELLO", payload.RootElement.GetProperty("output").GetString());
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("tokensIn").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("tokensOut").ValueKind);
        Assert.False(payload.RootElement.TryGetProperty(PayloadFields.AgentTranscript, out _));
    }

    [Fact]
    public async Task A_failure_without_a_sentence_says_nothing_about_agents()
    {
        await using var bed = new ContainerTestBed();

        var (row, member, _) = await RunOnceAsync(bed, _ => new MemberResult(false, 2, "partial"));

        Assert.Equal(MessageTypes.Failed, row.Type);
        Assert.Equal("This run did not complete (exit 2).", member.Snapshot().Failed);
        Assert.Equal(FailureClasses.Unknown, member.Snapshot().FailureClass);
        Assert.DoesNotContain("agent", member.Snapshot().Failed!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_runners_own_sentence_and_class_are_the_mark()
    {
        await using var bed = new ContainerTestBed();

        var (_, member, _) = await RunOnceAsync(bed, _ =>
            new MemberResult(false, 1, "", FailureReason: "The plugin said no.", FailureClass: FailureClasses.Transport));

        Assert.Equal("The plugin said no.", member.Snapshot().Failed);
        Assert.Equal(FailureClasses.Transport, member.Snapshot().FailureClass);
    }

    /// <summary>
    /// THE RUNTIME IS AGENT-FREE, mechanically: none of the agent seam's types, and no prompt
    /// rendering or history building, appear in the member runtime or the pump.
    /// </summary>
    [Theory]
    [InlineData("MemberRuntime.cs")]
    [InlineData("ContainerHost.cs")]
    public void The_runtime_and_the_pump_name_no_agent_seam(string file)
    {
        var source = File.ReadAllText(ContainersSource(file));

        foreach (var forbidden in new[]
                 {
                     "IAgentRunner", "AgentInvocation", "AgentResult", "IContextBuilder",
                     "MessageText.Of(", "DidNothing", "ReachedThePlatform",
                 })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
    }

    internal static string ContainersSource(string file, [CallerFilePath] string caller = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(caller)!, "..", "..", "src", "Harness.Containers", file));
}
