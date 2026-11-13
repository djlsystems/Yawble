using System.Text;
using Yawble.Core.Bus;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Launch;

/// <summary>
/// A headless run ends once the CLI's final answer is out, even when a leftover process keeps the CLI from exiting
/// (a CLI that printed its result and then waited on a hung build would otherwise hold the run until the 2 h limit).
/// </summary>
public class FinalAnswerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Feed(FinalAnswer f, string text, int chunk = 7)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var i = 0; i < bytes.Length; i += chunk) f.Write(bytes.AsMemory(i, Math.Min(chunk, bytes.Length - i)));
    }

    private const string ClaudeResult =
        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"num_turns\":8,\"result\":\"Done. Ünïcode ✓\",\"terminal_reason\":\"completed\"}\n";

    private const string GrokSummary = """
        {
          "type": "result",
          "text": "All done { not a brace line }",
          "usage": {
            "input_tokens": 57346,
            "output_tokens": 1809
          },
          "num_turns": 38
        }

        """;

    [Fact]
    public void ClaudesResultEventIsTheFinalAnswer_ReadInSmallChunks()
    {
        var f = new FinalAnswer(AgentCli.Claude);
        Feed(f, "{\"type\":\"system\",\"subtype\":\"init\"}\n{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"the type is \\\"result\\\"\"}]}}\n");
        Assert.Null(f.At);
        Feed(f, ClaudeResult, chunk: 3); // a multi-byte character split across chunks
        Assert.NotNull(f.At);
    }

    [Theory]
    [InlineData("not json \"type\":\"result\"\n")]
    [InlineData("{\"type\":\"assistant\",\"note\":\"type result\"}\n")]
    [InlineData("{\"type\":\"result\"")] // no newline yet: not a complete line
    public void OtherClaudeOutputIsNot(string text)
    {
        var f = new FinalAnswer(AgentCli.Claude);
        Feed(f, text);
        Assert.Null(f.At);
    }

    [Fact]
    public void GroksClosingSummaryIsTheFinalAnswer()
    {
        var f = new FinalAnswer(AgentCli.Grok);
        Feed(f, "thinking...\n{\n  \"partial\": true\n");
        Assert.Null(f.At);
        Feed(f, "}\n"); // an object without num_turns or usage is not the summary
        Assert.Null(f.At);
        Feed(f, GrokSummary.Replace("\r\n", "\n"));
        Assert.NotNull(f.At);
    }

    [Theory]
    [InlineData(AgentCli.Codex)]
    [InlineData(AgentCli.Copilot)]
    [InlineData(AgentCli.Antigravity)]
    public void PlainTextClisHaveNoMarker(AgentCli cli)
    {
        Assert.False(FinalAnswer.Detects(cli));
        var f = new FinalAnswer(cli);
        Feed(f, ClaudeResult + GrokSummary);
        Assert.Null(f.At);
    }

    [Fact]
    public async Task TheSupervisorEndsACliStillRunningAfterItsAnswer_AsCompleted()
    {
        var p = new FakeProcess(RunKind.Headless) { Stdout = new MemoryStream(Encoding.UTF8.GetBytes(ClaudeResult)) };
        var output = new RunOutputBuffer();
        var answer = new FinalAnswer(AgentCli.Claude);
        output.Data += answer.Write;
        var s = new RunSupervisor(p, 3600, 0, new RunActivity(TimeProvider.System), output,
            options: new SupervisorOptions { Poll = TimeSpan.FromMilliseconds(10), AfterAnswer = TimeSpan.FromMilliseconds(300) },
            answeredAt: () => answer.At);
        var exit = await s.RunAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(RunEnd.Completed, exit.End);
        Assert.True(exit.EndedAfterAnswer);
        Assert.Equal(1, p.KillCalls);
    }

    [Fact]
    public async Task ACliThatExitsByItselfAfterItsAnswerEndsNormally()
    {
        var p = new FakeProcess(RunKind.Headless) { Stdout = new MemoryStream(Encoding.UTF8.GetBytes(ClaudeResult)) };
        var output = new RunOutputBuffer();
        var answer = new FinalAnswer(AgentCli.Claude);
        output.Data += answer.Write;
        var s = new RunSupervisor(p, 3600, 0, new RunActivity(TimeProvider.System), output,
            options: new SupervisorOptions { Poll = TimeSpan.FromMilliseconds(10), AfterAnswer = TimeSpan.FromSeconds(30) },
            answeredAt: () => answer.At);
        var run = s.RunAsync(Ct);
        await Wait.UntilAsync(() => answer.At is not null, "the answer seen");
        p.Exit(0);
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(RunEnd.Completed, exit.End);
        Assert.False(exit.EndedAfterAnswer);
        Assert.Equal(0, p.KillCalls);
    }

    [Theory]
    [InlineData("dev1", ClaudeResult)] // dev1 runs Claude
    [InlineData("dev2", GrokSummary)]  // dev2 runs Grok
    public async Task AMemberRunWhoseCliHangsAfterItsAnswerCompletes(string member, string stdout)
    {
        await using var kit = await LaunchKit.StartAsync(wallSec: 3600);
        kit.Backend.Stdout = "working...\n" + stdout.Replace("\r\n", "\n");
        kit.Backend.Script = (_, _) => Task.CompletedTask; // never exits by itself
        await kit.Bus.PersonTellAsync("alpha", member, "do the thing", ct: Ct);
        var outcome = await kit.OutcomeAsync(member);
        Assert.Equal(MessageTypes.Completed, outcome.Type);
        Assert.Equal(1, kit.Backend.Spawned.Single().KillCalls);
    }

    [Fact]
    public async Task WithoutAFinalAnswerTheRunKeepsGoing()
    {
        await using var kit = await LaunchKit.StartAsync(idleSec: 3600, wallSec: 3600);
        kit.Backend.Stdout = "{\"type\":\"assistant\",\"text\":\"still working\"}\n";
        kit.Backend.Script = (_, _) => Task.CompletedTask;
        await kit.Bus.PersonTellAsync("alpha", "dev1", "do the thing", ct: Ct);
        await Wait.UntilAsync(() => kit.Backend.Spawned.Count == 1, "the run started");
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct); // well past the 30 s allowance on the kit's scaled clock
        Assert.Equal(0, kit.Backend.Spawned.Single().KillCalls);
        Assert.True(kit.Registry.Stop("alpha", "dev1"));
        Assert.Equal(MessageTypes.Failed, (await kit.OutcomeAsync("dev1")).Type);
    }
}
