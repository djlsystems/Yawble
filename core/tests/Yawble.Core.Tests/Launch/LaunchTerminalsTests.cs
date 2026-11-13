using System.Text;
using Yawble.Core.Bus;
using Yawble.Core.Launch;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Launch;

/// <summary>
/// The launcher's sandboxes reach the terminal windows (RunTerminalBridge), and Open interactive
/// CLI is the launcher's slot-taking session (LauncherInteractiveSessions). Real launcher and bus; scripted backend.
/// </summary>
public class LaunchTerminalsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (TerminalHub Hub, RunTerminalBridge Bridge) Wire(LaunchKit kit)
    {
        var hub = new TerminalHub(new AppEvents());
        var members = new SqliteMemberDirectory(kit.Db.ConnectionString);
        return (hub, new RunTerminalBridge(kit.Registry, hub, (t, m) => members.Find(t, m)?.Id));
    }

    private static long Id(LaunchKit kit, string member) => new SqliteMemberDirectory(kit.Db.ConnectionString).Find("alpha", member)!.Id;

    private static string Replay(TerminalHub hub, string channel) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(hub.Attach(channel)["replay"]!.GetValue<string>()));

    [Fact]
    public async Task AHeadlessRunStreamsIntoTheMembersChannelAndStopsAsStopped()
    {
        await using var kit = await LaunchKit.StartAsync(wallSec: 3600);
        var (hub, bridge) = Wire(kit);
        using var _ = bridge;
        kit.Backend.Stdout = "{\"type\":\"assistant\",\"text\":\"hello from the sandbox\"}\n";
        kit.Backend.Script = (_, p) => Task.CompletedTask; // runs until stopped
        var channel = TerminalHub.MemberChannel(Id(kit, "dev1"));

        await kit.Bus.PersonTellAsync("alpha", "dev1", "do the thing", ct: Ct);
        await Wait.UntilAsync(() => Replay(hub, channel).Contains("hello from the sandbox"), "run output in the channel");
        var session = hub.SessionOf(channel)!;
        Assert.Equal("headless", session.Kind);
        Assert.False(session.InputEnabled);
        Assert.False(hub.Input(channel, "x"u8)); // a headless run does not read the keyboard

        // The window's Stop is the launcher's Stop: the run ends "stopped", not interrupted.
        Assert.True(await hub.StopAsync(channel));
        var outcome = await kit.OutcomeAsync("dev1");
        Assert.Equal(MessageTypes.Failed, outcome.Type);
        Assert.Contains(FailureClasses.Stopped, outcome.Payload);
        await Wait.UntilAsync(() => hub.SessionOf(channel) is { Ended: true }, "session ended");
        Assert.Contains("hello from the sandbox", Replay(hub, channel)); // replay survives the run
    }

    [Fact]
    public async Task OpenInteractiveTakesASlotThroughTheBusAndCarriesInputAndStop()
    {
        await using var kit = await LaunchKit.StartAsync(limit: 1, wallSec: 3600);
        var (hub, bridge) = Wire(kit);
        using var _ = bridge;
        kit.Backend.Stdout = "grok> ";
        kit.Backend.Script = (_, p) => Task.CompletedTask;
        var sessions = new LauncherInteractiveSessions(new InteractiveSessions(kit.Bus, kit.Launcher), hub);
        var dev2 = (await kit.Bus.MemberAsync(Id(kit, "dev2"), Ct))!;
        var channel = TerminalHub.MemberChannel(dev2.Id);

        await sessions.StartAsync(dev2, channel, Ct);
        await Wait.UntilAsync(() => hub.SessionOf(channel) is { Kind: "interactive", Ended: false }, "interactive session live");
        Assert.Equal("session", (await kit.Bus.TeamStatusAsync("alpha", Ct)).Single(m => m.Name == "dev2").State);
        Assert.Equal(Sandboxing.RunKind.Interactive, kit.Backend.Requests.Single().Kind);
        await Wait.UntilAsync(() => Replay(hub, channel).Contains("grok> "), "PTY output in the channel");

        Assert.True(hub.Input(channel, "hello\r"u8));
        hub.Resize(channel, 160, 50);
        var process = kit.Backend.Spawned.Single();
        Assert.Equal("hello\r", Encoding.UTF8.GetString(process.InputBytes));
        Assert.Equal(new PtySize(50, 160), process.Resizes.Single());

        // The session holds the only slot: dev1's session waits, and the window says so.
        var dev1 = (await kit.Bus.MemberAsync(Id(kit, "dev1"), Ct))!;
        var channel1 = TerminalHub.MemberChannel(dev1.Id);
        await sessions.StartAsync(dev1, channel1, Ct);
        Assert.Contains("waiting for a run slot (position 1)", Replay(hub, channel1));
        Assert.Single(kit.Backend.Spawned);
        await Assert.ThrowsAsync<BusRefusal>(() => sessions.StartAsync(dev2, channel, Ct));

        Assert.True(await hub.StopAsync(channel));
        await Wait.UntilAsync(() => hub.SessionOf(channel) is { Ended: true }, "dev2's session ended");
        await Wait.UntilAsync(() => hub.SessionOf(channel1) is { Kind: "interactive", Ended: false }, "dev1 admitted once the slot is free");
        Assert.True(await hub.StopAsync(channel1));
        await Wait.UntilAsync(() => hub.SessionOf(channel1) is { Ended: true }, "dev1's session ended");
    }

    [Fact]
    public async Task ASessionThatCannotStartSaysWhyInTheWindow()
    {
        await using var kit = await LaunchKit.StartAsync(credentials: new FakeCredentialModes(grok: null));
        var (hub, bridge) = Wire(kit);
        using var _ = bridge;
        var sessions = new LauncherInteractiveSessions(new InteractiveSessions(kit.Bus, kit.Launcher), hub);
        var dev2 = (await kit.Bus.MemberAsync(Id(kit, "dev2"), Ct))!;
        var channel = TerminalHub.MemberChannel(dev2.Id);
        await sessions.StartAsync(dev2, channel, Ct);
        await Wait.UntilAsync(() => Replay(hub, channel).Contains("did not start: no model credential"), "start failure shown");
    }
}
