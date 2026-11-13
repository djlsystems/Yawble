using System.Text;
using Yawble.Core.Agents;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Launch;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Agents;

/// <summary>
/// The Agents dialog's Test in a REAL sandbox: the stand-in CLI's TUI on the sandbox's PTY as the _agent-test stand-in
/// under the global policy, its output in the dialog's channel, keyboard input reaching it. Skipped where the sandbox cannot
/// run here (RealSandbox.SkipUnlessAvailable says why).
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class RealSandboxAgentTestTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Detected : ICliDetector
    {
        public Task<IReadOnlyList<DetectedCli>> DetectAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DetectedCli>>(
            [new DetectedCli("claude", true, "/usr/bin/claude", "/usr/bin/claude", "2.1.318", "other", ["/usr/bin"], ["/home/u/.claude"], [], "")]);
    }

    [Fact]
    public async Task TestRunsTheCliTuiInARealSandboxAsTheStandIn()
    {
        RealSandbox.SkipUnlessAvailable();
        await using var kit = await RealSandboxLauncherTests.Kit.StartAsync(new { say = "unused" });
        var hub = new TerminalHub(new AppEvents());
        using var bridge = new RunTerminalBridge(kit.Launch.Registry, hub, (_, _) => null, AgentTestSessions.ChannelFor);
        var tests = new AgentTestSessions(kit.Launch.Launcher, null, hub, new Detected());
        var channel = AgentTestSessions.Channel("claude");
        string Replay() => Encoding.UTF8.GetString(Convert.FromBase64String(hub.Attach(channel)["replay"]!.GetValue<string>()));

        await tests.StartAsync("claude", diagnostic: true, Ct);

        await Wait.UntilAsync(() => Replay().Contains("stand-in, interactive, for _agent-test/claude"), "the TUI on the sandbox's PTY", TimeSpan.FromSeconds(60));
        Assert.True(hub.SessionOf(channel) is { Kind: "interactive", InputEnabled: true });
        Assert.True(hub.Input(channel, "hello from the dialog\r"u8));
        await Wait.UntilAsync(() => Replay().Contains("you said: hello from the dialog"), "keyboard input reached the sandbox", TimeSpan.FromSeconds(30));
        await Wait.UntilAsync(() => hub.SessionOf(channel) is { Ended: true }, "the sandbox ended", TimeSpan.FromSeconds(30));
    }
}
