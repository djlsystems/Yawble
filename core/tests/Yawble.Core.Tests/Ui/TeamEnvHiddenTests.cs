using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Ui;

/// <summary>
/// A team's environment variables over the Cloud Link: a viewer (full or read) gets the names with every value hidden,
/// and a save from there keeps a value it never saw. In the window the values are shown and saved as they are.
/// </summary>
public class TeamEnvHiddenTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public BusEngine Engine { get; private set; } = null!;
        public RpcDispatcher Rpc { get; private set; } = null!;
        public long TeamId { get; private set; }

        public static async Task<Rig> StartAsync()
        {
            var rig = new Rig();
            rig.Engine = new BusEngine(rig.Db.OpenStore(), new ScriptedRunner(), new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
            await rig.Engine.CreateTeamAsync("Alpha", ct: Ct);
            await rig.Engine.StartAsync(Ct);
            var terminals = new TerminalHub(new AppEvents());
            var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
            var dataDir = OperatingSystem.IsWindows() ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
            var install = OperatingSystem.IsWindows() ? @"C:\Program Files\Yawble" : "/opt/yawble";
            var ui = new UiRpc(rig.Engine, new PolicyStore(rig.Db.ConnectionString), new MemberLayouts(dataDir, install, home), new FakeModelCredentials(),
                new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, rig.Engine), terminals, 16_384);
            rig.Rpc = new RpcDispatcher(new Yawble.Core.Sandboxing.SandboxPlatformInfo(), () => [], ui: ui);
            rig.TeamId = (await rig.Engine.TeamsAsync(Ct)).Single().Id;
            await rig.Engine.SetTeamEnvAsync("Alpha", [new("REGISTRY_KEY", "s3cr3t-value"), new("API_BASE", "http://localhost:5000")], Ct);
            return rig;
        }

        private static JsonObject Entries(params (string Name, string Value)[] entries) =>
            new() { ["entries"] = new JsonArray(entries.Select(e => (JsonNode?)new JsonObject { ["name"] = e.Name, ["value"] = e.Value }).ToArray()) };

        public async Task<JsonObject> Env(Actor? who)
        {
            using var acting = who is null ? null : Actor.Use(who);
            return (await Rpc.DispatchAsync("teams.env", new JsonObject { ["teamId"] = TeamId }, Ct))!.AsObject();
        }

        public async Task<JsonObject> Save(Actor? who, params (string Name, string Value)[] entries)
        {
            var p = Entries(entries);
            p["teamId"] = TeamId;
            using var acting = who is null ? null : Actor.Use(who);
            return (await Rpc.DispatchAsync("teams.setEnv", p, Ct))!.AsObject();
        }

        public Task<IReadOnlyDictionary<string, string>> Stored() => Engine.TeamEnvAsync("Alpha", Ct);

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    private static readonly Actor Visitor = Actor.CloudViewer("visitor@example.com", "read");
    private static readonly Actor Member = Actor.CloudViewer("member@example.com", "full");

    [Fact]
    public async Task ACloudViewerOfEitherAccessGetsTheNames_WithEveryValueHidden()
    {
        await using var rig = await Rig.StartAsync();
        foreach (var who in new[] { Visitor, Member })
        {
            var env = await rig.Env(who);
            Assert.Equal(["API_BASE", "REGISTRY_KEY"], env.Select(e => e.Key).Order());
            Assert.All(env, e => Assert.Equal(TeamEnv.HiddenValue, e.Value!.GetValue<string>()));
            Assert.DoesNotContain("s3cr3t", env.ToJsonString());
        }
    }

    [Fact]
    public async Task TheWindowGetsTheValues()
    {
        await using var rig = await Rig.StartAsync();
        Assert.Equal("s3cr3t-value", (await rig.Env(Actor.Window))["REGISTRY_KEY"]!.GetValue<string>());
        Assert.Equal("s3cr3t-value", (await rig.Env(null))["REGISTRY_KEY"]!.GetValue<string>());
    }

    [Fact]
    public async Task ACloudSaveKeepsAHiddenValue_TakesANewOne_AndItsAnswerHidesThemAgain()
    {
        await using var rig = await Rig.StartAsync();
        var answer = await rig.Save(Member, ("REGISTRY_KEY", TeamEnv.HiddenValue), ("API_BASE", "http://localhost:6000"), ("NEW_ONE", "n"));
        Assert.All(answer, e => Assert.Equal(TeamEnv.HiddenValue, e.Value!.GetValue<string>()));
        var stored = await rig.Stored();
        Assert.Equal("s3cr3t-value", stored["REGISTRY_KEY"]);
        Assert.Equal("http://localhost:6000", stored["API_BASE"]);
        Assert.Equal("n", stored["NEW_ONE"]);

        // A variable left out is removed, as in the window.
        await rig.Save(Member, ("REGISTRY_KEY", TeamEnv.HiddenValue));
        Assert.Equal(["REGISTRY_KEY"], (await rig.Stored()).Keys);
        Assert.Equal("s3cr3t-value", (await rig.Stored())["REGISTRY_KEY"]);
    }

    [Fact]
    public async Task ACloudSaveNeverStoresTheHiddenText_NorCopiesAHiddenValueToAnotherName()
    {
        await using var rig = await Rig.StartAsync();
        var refused = await Assert.ThrowsAsync<RpcException>(() => rig.Save(Member, ("RENAMED_KEY", TeamEnv.HiddenValue), ("API_BASE", TeamEnv.HiddenValue)));
        Assert.Contains("nothing was saved", refused.Message);
        Assert.Contains("'RENAMED_KEY'", refused.Message);
        var stored = await rig.Stored();
        Assert.Equal(2, stored.Count);
        Assert.Equal("s3cr3t-value", stored["REGISTRY_KEY"]);
        Assert.DoesNotContain(TeamEnv.HiddenValue, stored.Values);
    }

    [Fact]
    public async Task TheWindowSavesValuesAsTheyAre()
    {
        await using var rig = await Rig.StartAsync();
        var answer = await rig.Save(Actor.Window, ("REGISTRY_KEY", "changed"));
        Assert.Equal("changed", answer["REGISTRY_KEY"]!.GetValue<string>());
        Assert.Equal("changed", (await rig.Stored())["REGISTRY_KEY"]);
    }
}
