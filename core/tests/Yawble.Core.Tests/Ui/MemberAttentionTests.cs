using Yawble.Core.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Ui;

/// <summary>What a member needs from a person, as its face's ring shows it: a question beats blocked beats a failed run.</summary>
public class MemberAttentionTests
{
    private static AgentCard Card(string name, string state = "idle", string? lastRun = null) =>
        new(new MemberRow(1, 1, "payments", name, "member", "claude", "", "", "default"), state, null, 0, 0, null, null, null, lastRun, null);

    private static Message Report(string member, string type) =>
        new(10, type, "{}", $"payments/{member}", 1, null, 0, DateTimeOffset.UtcNow);

    private static readonly HashSet<string> Nobody = [];

    [Fact]
    public void AnOpenQuestionComesFirst_EvenWhileRunning() =>
        Assert.Equal("decision", UiRpc.Attention(Card("DeveloperPriya", "running"), new HashSet<string>(["developerpriya"], StringComparer.OrdinalIgnoreCase), new Dictionary<string, Message>()));

    [Fact]
    public void BlockedWhenItsLastReportSaysSo()
    {
        var reports = new Dictionary<string, Message>(StringComparer.OrdinalIgnoreCase) { ["DeveloperPriya"] = Report("DeveloperPriya", MessageTypes.Blocked) };
        Assert.Equal("blocked", UiRpc.Attention(Card("DeveloperPriya", lastRun: "failed"), Nobody, reports));
        Assert.Null(UiRpc.Attention(Card("DeveloperPriya", "running"), Nobody, reports)); // running again: it is working on it
    }

    [Fact]
    public void FailedWhenItsLastRunFailed_AndNothingWhenItIsFine()
    {
        var reports = new Dictionary<string, Message>(StringComparer.OrdinalIgnoreCase) { ["TesterWren"] = Report("TesterWren", MessageTypes.Progress) };
        Assert.Equal("failed", UiRpc.Attention(Card("TesterWren", lastRun: "failed"), Nobody, reports));
        Assert.Null(UiRpc.Attention(Card("TesterWren", lastRun: "completed"), Nobody, reports));
        Assert.Null(UiRpc.Attention(Card("TesterWren", "queued"), Nobody, reports));
    }
}
