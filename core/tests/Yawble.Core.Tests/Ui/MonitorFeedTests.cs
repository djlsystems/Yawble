using Yawble.Core.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Ui;

/// <summary>The Monitor's feed: each bus message in plain words, about a team and a member.</summary>
public class MonitorFeedTests
{
    private static Message M(string type, string payload, string source, long seq = 7) =>
        new(seq, type, payload, source, 3, null, 0, DateTimeOffset.Parse("2026-11-08T12:00:00Z"));

    [Fact]
    public void AnInstructionIsAboutItsAddressee_FromItsSender()
    {
        var row = UiRpc.MonitorRow(M("agent.instruction.payments/DeveloperPriya", """{"text":"Add a README"}""", "payments/Manager"))!;
        Assert.Equal("instruction", row["kind"]!.GetValue<string>());
        Assert.Equal("payments", row["team"]!.GetValue<string>());
        Assert.Equal("DeveloperPriya", row["member"]!.GetValue<string>());
        Assert.Equal("Manager", row["from"]!.GetValue<string>());
        Assert.Equal("Add a README", row["text"]!.GetValue<string>());
        Assert.Equal(3, row["workflow"]!.GetValue<long>());
    }

    [Theory]
    [InlineData(MessageTypes.Progress, """{"status":"writing tests"}""", "progress", "writing tests")]
    [InlineData(MessageTypes.Blocked, """{"reason":"no access"}""", "blocked", "no access")]
    [InlineData(MessageTypes.NeedsDecision, """{"question":"MIT or Apache?"}""", "decision", "MIT or Apache?")]
    [InlineData(MessageTypes.Handback, """{"delivered":"README on members/developerpriya"}""", "handback", "README on members/developerpriya")]
    [InlineData(MessageTypes.Failed, """{"reason":"timed out"}""", "failed", "timed out")]
    [InlineData(MessageTypes.WorkflowCompleted, """{"delivered":"done"}""", "done", "done")]
    public void AReportIsAboutItsSender(string type, string payload, string kind, string text)
    {
        var row = UiRpc.MonitorRow(M(type, payload, "payments/TesterWren"))!;
        Assert.Equal(kind, row["kind"]!.GetValue<string>());
        Assert.Equal("TesterWren", row["member"]!.GetValue<string>());
        Assert.Equal(text, row["text"]!.GetValue<string>());
    }

    [Fact]
    public void ThePersonIsNamedAsSuch_AndOtherMessagesAreLeftOut()
    {
        Assert.Equal("person", UiRpc.MonitorRow(M("agent.instruction.payments/Manager", """{"text":"hi"}""", "person"))!["from"]!.GetValue<string>());
        Assert.Null(UiRpc.MonitorRow(M("trigger.fired", "{}", "host")));
        Assert.Null(UiRpc.MonitorRow(M(MessageTypes.Completed, "{}", "host"))); // no team to put it on
    }
}
