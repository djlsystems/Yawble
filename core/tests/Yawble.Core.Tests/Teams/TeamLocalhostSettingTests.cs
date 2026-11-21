using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Link;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Sandboxing;
using Yawble.Core.Skills;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Launch;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// B119 Part B (card 534): the team setting "Allow localhost (macOS)" (teams.allow_localhost, 0054). Off by default, read and
/// set with teams.localhost over the real UiRpc dispatcher, a set is audited and refused for a read-only cloud viewer
/// (Visitor), and the launcher gives it to the run's request input (RunSpec.AllowLocalhost). Fakes only.
/// </summary>
public class TeamLocalhostSettingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public BusEngine Engine { get; private set; } = null!;
        public RpcDispatcher Rpc { get; private set; } = null!;
        public AuditLog Audit { get; private set; } = null!;
        public long TeamId { get; private set; }

        public static async Task<Rig> StartAsync()
        {
            var rig = new Rig();
            rig.Engine = new BusEngine(new BusStore(rig.Db.ConnectionString), new ScriptedRunner(),
                new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
            await rig.Engine.CreateTeamAsync("Alpha", ct: Ct);
            await rig.Engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            await rig.Engine.CreateTeamAsync("Beta", ct: Ct);
            await rig.Engine.AddMemberAsync("Beta", "Bo", ct: Ct);
            await rig.Engine.StartAsync(Ct);
            var terminals = new TerminalHub(new AppEvents());
            var policies = new PolicyStore(rig.Db.ConnectionString);
            var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
            var dataDir = OperatingSystem.IsWindows() ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
            var install = OperatingSystem.IsWindows() ? @"C:\Program Files\Yawble" : "/opt/yawble";
            var ui = new UiRpc(rig.Engine, policies, new MemberLayouts(dataDir, install, home), new FakeModelCredentials(),
                new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, rig.Engine), terminals, 16_384);
            rig.Audit = new AuditLog(rig.Db.ConnectionString);
            rig.Rpc = new RpcDispatcher(new SandboxPlatformInfo(), () => [], ui: ui, audit: rig.Audit);
            rig.TeamId = (await rig.Engine.TeamsAsync(Ct)).Single(t => t.Name == "Alpha").Id;
            return rig;
        }

        public async Task<JsonNode?> Call(string method, object? args = null, Actor? actor = null) =>
            await Rpc.DispatchAsAsync(actor ?? Actor.Window, method, args is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(args)!.AsObject(), Ct);

        public async Task<RpcException> Refused(string method, object? args = null) =>
            await Assert.ThrowsAsync<RpcException>(() => Call(method, args));

        public async Task<bool> On(long teamId) => (await Call("teams.localhost", new { teamId }))!["on"]!.GetValue<bool>();

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    [Fact]
    public async Task OffByDefault_SetAndReadBack_PerTeam_AndTheMemberDirectoryCarriesIt()
    {
        await using var rig = await Rig.StartAsync();
        var beta = (await rig.Engine.TeamsAsync(Ct)).Single(t => t.Name == "Beta").Id;
        var members = new SqliteMemberDirectory(rig.Db.ConnectionString);
        Assert.False(await rig.On(rig.TeamId));
        Assert.False(await rig.On(beta));
        Assert.False(members.Find("Alpha", "Ada")!.AllowLocalhost);

        var set = (await rig.Call("teams.localhost", new { teamId = rig.TeamId, on = true }))!.AsObject();
        Assert.Equal("""{"on":true}""", set.ToJsonString());
        Assert.True(await rig.On(rig.TeamId));
        Assert.False(await rig.On(beta)); // per team
        Assert.True(members.Find("Alpha", "Ada")!.AllowLocalhost);
        Assert.False(members.Find("Beta", "Bo")!.AllowLocalhost);

        Assert.False((await rig.Call("teams.localhost", new { teamId = rig.TeamId, on = false }))!["on"]!.GetValue<bool>());
        Assert.False(members.Find("Alpha", "Ada")!.AllowLocalhost);
    }

    [Fact]
    public async Task AnOnThatIsNotTrueOrFalse_IsRefused_AndChangesNothing()
    {
        await using var rig = await Rig.StartAsync();
        await rig.Call("teams.localhost", new { teamId = rig.TeamId, on = true });
        foreach (var bad in new object[] { "false", 0, 1, new[] { false }, new { on = false } })
            Assert.Equal("bad_request", (await rig.Refused("teams.localhost", new { teamId = rig.TeamId, on = bad })).Code);
        Assert.True(await rig.On(rig.TeamId));
    }

    [Fact]
    public async Task ASetIsAudited_AReadIsNot()
    {
        await using var rig = await Rig.StartAsync();
        await rig.On(rig.TeamId);
        Assert.DoesNotContain(rig.Audit.Recent(), r => r.Method == "teams.localhost");

        await rig.Call("teams.localhost", new { teamId = rig.TeamId, on = true });
        await rig.Call("teams.localhost", new { teamId = rig.TeamId, on = false }, Actor.CloudViewer("pat@example.com"));
        await rig.On(rig.TeamId);
        var rows = rig.Audit.Recent().Where(r => r.Method == "teams.localhost").Reverse().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal((Actor.App, "done"), (rows[0].Via, rows[0].Outcome));
        Assert.Equal((Actor.Cloud, "pat@example.com"), (rows[1].Via, rows[1].User));
        Assert.All(rows, r => Assert.Contains("team=Alpha", r.Target));

        Assert.True(AuditLog.IsReadOnly("teams.localhost", new JsonObject { ["teamId"] = 1 }));
        Assert.False(AuditLog.IsReadOnly("teams.localhost", new JsonObject { ["teamId"] = 1, ["on"] = true }));
        Assert.False(AuditLog.IsReadOnly("teams.localhost", new JsonObject { ["teamId"] = 1, ["on"] = false }));
    }

    [Fact]
    public void AVisitorMayReadIt_ButNotSetIt_AFullViewerMay()
    {
        Assert.True(RemoteAccess.OnAllowList("teams.localhost"));
        var read = new JsonObject { ["teamId"] = 1 };
        Assert.Null(RemoteAccess.Refusal("teams.localhost", read, allowHostTerminals: false, RemoteAccess.Read));
        foreach (var on in new[] { true, false })
        {
            var set = new JsonObject { ["teamId"] = 1, ["on"] = on };
            Assert.Equal(RemoteAccess.ReadOnlyRefusal, RemoteAccess.Refusal("teams.localhost", set, allowHostTerminals: false, RemoteAccess.Read));
            Assert.Null(RemoteAccess.Refusal("teams.localhost", set, allowHostTerminals: false, RemoteAccess.Full));
        }
    }

    [Fact]
    public async Task TheLauncherGivesTheTeamsSettingToTheRequestInput()
    {
        await using var kit = await LaunchKit.StartAsync();
        var specs = new List<RunSpec>();
        kit.Launcher.Compiling = s => { lock (specs) specs.Add(s); };
        kit.Backend.Script = (_, p) => { p.Exit(0); return Task.CompletedTask; };

        await kit.Bus.PersonTellAsync("alpha", "dev1", "first, with the setting off", ct: Ct);
        await kit.OutcomeAsync("dev1");
        Assert.False(Assert.Single(Of(specs, "dev1")).AllowLocalhost);

        Assert.True(await kit.Bus.TeamLocalhostAsync("alpha", true, Ct));
        await kit.Bus.PersonTellAsync("alpha", "dev2", "then with it on", ct: Ct);
        await kit.OutcomeAsync("dev2");
        Assert.True(Assert.Single(Of(specs, "dev2")).AllowLocalhost);
    }

    [Fact]
    public void TheDevToolsSkill_SteersMacRunsToXunitV3_AndSaysWhereTheSettingIs()
    {
        var body = SkillCatalog.BuiltIns.Single(s => s.Name == "dev-tools").Body;
        var text = string.Join(' ', body.Split((char[])['\n', ' '], StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("## macOS", body);
        Assert.Contains("\"Allow localhost (macOS)\" (Team settings > Network; off by default)", text);
        Assert.Contains("xUnit v3 on Microsoft.Testing.Platform", text);
        Assert.Contains("`SocketException (13): Permission denied` at `TcpListener.Start`", text);
        Assert.Contains("Tell your Manager in one line: \"dotnet test needs Allow localhost (macOS): Team settings > Network\". Do not guess", text);
        Assert.DoesNotContain("m" + "xc", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The run inputs whose clone is the member's (members/&lt;member&gt; in its path).</summary>
    private static List<RunSpec> Of(List<RunSpec> specs, string member)
    {
        lock (specs)
            return [.. specs.Where(s => s.Layout.ClonePath.Replace('\\', '/').Contains($"/members/{member}/", StringComparison.OrdinalIgnoreCase)
                || s.Layout.ClonePath.Replace('\\', '/').EndsWith($"/members/{member}", StringComparison.OrdinalIgnoreCase))];
    }
}
