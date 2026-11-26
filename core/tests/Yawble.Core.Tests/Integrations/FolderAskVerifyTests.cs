using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Integrations;
using Yawble.Core.Ipc;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Yawble.Core.Tests.Policy;
using Yawble.Core.Tests.Ui;
using Yawble.Core.Ui;
using Xunit;
using MemberRef = Yawble.Core.Bus.MemberRef;

namespace Yawble.Core.Tests.Integrations;

/// <summary>
/// The tester's checks (Marlo 79d45d7/afb7205 + Ines 74dfd22): folder asks through the real bus, UiRpc and RpcDispatcher
/// (the window's path), filling what FolderAskTests leaves open: request_folder answers at once with nobody answering;
/// a repeat pushes no second change; folder.* reaches FolderAskService through RpcDispatcher, not UiRpc.Prefixes;
/// app.overview counts the ask in needsYou; an approval is what Member settings > Policy (policy.getMember) shows and
/// policy.saveLoosen removes, both audited; a read-write grant of a folder already read-only moves it; a second answer is
/// refused; every refusal (Windows and Linux rules) raises no ask, no push and no count; the Concierge's lines and the
/// org summary's title never carry the full path. Fakes only: a temporary database, fake runners, fictional paths.
/// </summary>
public sealed class FolderAskVerifyTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly bool Win = OperatingSystem.IsWindows();
    private static readonly string Home = Win ? @"C:\Users\pat" : "/home/pat";
    private static readonly string DataDir = Win ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
    private static readonly string Install = Win ? @"C:\Program Files\Yawble" : "/opt/yawble";
    private static readonly string Sessions = Win ? @"Z:\smokesessions" : "/srv/smokesessions";

    private readonly TempDb _db = new();
    private readonly List<(string Team, string Member, string Text)> _told = [];
    private TerminalHub _terminals = null!;
    private BusEngine _bus = null!;
    private RpcDispatcher _rpc = null!;
    private PolicyStore _policies = null!;
    private MemberLayouts _layouts = null!;
    private FolderAskService _asks = null!;
    private int _changed;

    public async ValueTask InitializeAsync()
    {
        var hub = new AppEvents();
        _terminals = new TerminalHub(hub);
        _policies = new PolicyStore(_db.ConnectionString);
        BusEngine? busRef = null;
        var runner = new FakePtyRunner(_terminals, id => busRef!.MemberAsync(id).GetAwaiter().GetResult()?.Cli ?? "claude", ticks: 3, tick: TimeSpan.FromMilliseconds(40));
        _bus = await BusHost.StartAsync(_db.ConnectionString, _ => null, null, runner, () => _policies.GetGlobal().App.MaxConcurrentAgents);
        busRef = _bus;
        _layouts = new MemberLayouts(DataDir, Install, Home);
        var ui = new UiRpc(_bus, _policies, _layouts, new FakeModelCredentials(),
            new FakeHostTerminals(_terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(_terminals, _bus), _terminals, 16_384);
        // Wired as Program.cs wires it: the member's layout from the bus rows, the person's tell.
        _asks = new FolderAskService(_db.ConnectionString, _policies,
            async (memberId, ct) =>
            {
                var everyone = await _bus.AllMemberRowsAsync(ct);
                return _layouts.For(everyone.First(m => m.Id == memberId), everyone);
            },
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]),
            (team, member, text, _, _) =>
            {
                lock (_told) _told.Add((team, member, text));
                return Task.CompletedTask;
            },
            exists: p => string.Equals(p, Sessions, StringComparison.OrdinalIgnoreCase))
        { Changed = () => Interlocked.Increment(ref _changed) };
        _bus.FolderAsks = _asks;
        _rpc = new RpcDispatcher(new Yawble.Core.Sandboxing.SandboxPlatformInfo(), () => [], ui: ui, audit: new AuditLog(_db.ConnectionString), folderAsks: _asks);
    }

    public async ValueTask DisposeAsync()
    {
        await _bus.DisposeAsync();
        _db.Dispose();
    }

    private async Task<JsonNode?> Call(string method, object? args = null) =>
        await _rpc.DispatchAsAsync(Actor.Window, method, args is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(args)!.AsObject(), Ct);

    private async Task<(long TeamId, MemberRef Manager, MemberRef Ada, MemberRef Ben)> Team(string name = "payments")
    {
        var teamId = (await Call("teams.create", new { name, managerCli = "claude", members = Array.Empty<object>() }))!["teamId"]!.GetValue<long>();
        foreach (var m in new[] { "Ada", "Ben" }) await Call("members.add", new { teamId, name = m, cli = "claude" });
        var agents = (await Call("teams.agents", new { teamId }))!.AsArray().Select(a => a!["member"]!).ToList();
        MemberRef Ref(JsonNode m) => new(m["id"]!.GetValue<long>(), teamId, name, m["name"]!.GetValue<string>(),
            string.Equals(m["role"]!.GetValue<string>(), "manager", StringComparison.OrdinalIgnoreCase) ? MemberRole.Manager : MemberRole.Member);
        var manager = Ref(agents.Single(m => string.Equals(m["role"]!.GetValue<string>(), "manager", StringComparison.OrdinalIgnoreCase)));
        return (teamId, manager, Ref(agents.Single(m => m["name"]!.GetValue<string>() == "Ada")), Ref(agents.Single(m => m["name"]!.GetValue<string>() == "Ben")));
    }

    private Task<string> Ask(MemberRef who, string path, string access = "readwrite", string? member = null) =>
        _asks.RequestFromAgentAsync(who, path, access, "replay the recorded smoke sessions", member, deliverable: 9, causation: 10, Ct);

    private async Task<JsonObject> MemberPolicy(long memberId) => (await Call("policy.getMember", new { memberId }))!.AsObject();

    private static List<string> Strings(JsonNode? n) => n!.AsArray().Select(x => x!.GetValue<string>()).ToList();

    private async Task<(int FolderAsks, int NeedsYou)> Overview()
    {
        var o = (await Call("app.overview"))!;
        return (o["folderAsks"]!.GetValue<int>(), o["needsYou"]!.GetValue<int>());
    }

    // ------------------------------------------------------------------ P1/P2: at once, never blocks; a repeat reuses

    [Fact]
    public async Task P1_RequestFolder_AnswersAtOnce_WithNobodyAnswering_AndARepeatPushesNothing()
    {
        var (_, _, ada, _) = await Team();
        var baseline = await Overview();
        var ask = Ask(ada, Sessions);
        // Nobody answers: the tool must still return (well inside a second; 5 s to stay safe on a loaded machine).
        Assert.Same(ask, await Task.WhenAny(ask, Task.Delay(TimeSpan.FromSeconds(5), Ct)));
        Assert.StartsWith("asked (ask ", await ask);
        Assert.Equal(1, _changed);
        var after = await Overview();
        Assert.Equal((1, baseline.NeedsYou + 1), (after.FolderAsks, after.NeedsYou));

        var again = await Ask(ada, Sessions.ToUpperInvariant());
        Assert.Contains("already waiting", again);
        Assert.Equal(1, _changed); // no push for a repeat
        Assert.Single(_asks.Asks());
        Assert.Equal(after, await Overview());
    }

    // ------------------------------------------------------------------ routing

    [Fact]
    public async Task Routing_FolderMethods_GoThroughRpcDispatcherToFolderAskService_NotUiRpc()
    {
        Assert.DoesNotContain(UiRpc.Prefixes, p => "folder.asks".StartsWith(p, StringComparison.Ordinal));
        Assert.True(FolderAskService.Handles("folder.asks"));
        Assert.True(FolderAskService.Handles("folder.answer"));
        Assert.False(FolderAskService.Handles("folders.asks"));
        var (_, _, ada, _) = await Team();
        await Ask(ada, Sessions);
        Assert.Single((await Call("folder.asks"))!.AsArray());

        // A dispatcher with the window's UiRpc but no folder asks wired does not answer folder.* at all.
        var bare = new RpcDispatcher(new Yawble.Core.Sandboxing.SandboxPlatformInfo(), () => [], ui: new UiRpc(_bus, _policies, _layouts, new FakeModelCredentials(),
            new FakeHostTerminals(_terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(_terminals, _bus), _terminals, 16_384));
        await Assert.ThrowsAnyAsync<Exception>(() => bare.DispatchAsAsync(Actor.Window, "folder.asks", null, Ct));
    }

    // ------------------------------------------------------------------ P5: what Member settings > Policy shows, and Remove

    [Fact]
    public async Task P5_Approve_IsShownInMemberPolicy_AndRemovingItThere_TakesItAway_BothAudited()
    {
        var (_, _, ada, ben) = await Team();
        await Ask(ada, Sessions);
        var id = _asks.Asks().Single().Id;
        var answer = (await Call("folder.answer", new { id, choice = "readwrite" }))!;
        Assert.Equal("payments/" + ManagerName(), answer["told"]!.GetValue<string>());
        Assert.Equal(0, (await Overview()).FolderAsks);

        var shown = await MemberPolicy(ada.Id);
        Assert.Contains(Sessions, Strings(shown["loosen"]!["filesystem"]!["readwrite"]));
        Assert.Contains(Sessions, Strings(shown["effective"]!["readwrite"]));
        Assert.Contains(Strings(shown["loosenSummary"]), l => l.Contains("filesystem.readwrite + " + Sessions, StringComparison.Ordinal));
        Assert.False(string.IsNullOrEmpty(shown["loosenApprovedAt"]?.GetValue<string>()));
        Assert.DoesNotContain(Sessions, Strings((await MemberPolicy(ben.Id))["effective"]!["readwrite"]));

        // The Policy page's remove: save the loosen block without that line (a tightening confirms nothing).
        await Call("policy.saveLoosen", new { memberId = ada.Id, loosen = new { }, confirmed = Array.Empty<string>() });
        var removed = await MemberPolicy(ada.Id);
        Assert.DoesNotContain(Sessions, Strings(removed["effective"]!["readwrite"]));
        Assert.DoesNotContain(Sessions, Strings(removed["effective"]!["readonly"]));

        var audited = new AuditLog(_db.ConnectionString).Recent().Select(r => r.Method).ToList();
        Assert.Contains("folder.answer", audited);
        Assert.Contains("policy.saveLoosen", audited);

        // Asked again after the removal: a new ask (the old one is answered), not "already read-write".
        Assert.StartsWith("asked (ask ", await Ask(ada, Sessions));
        Assert.Equal(2, _asks.Asks(openOnly: false).Count);
    }

    private string ManagerName() => _bus.AllMemberRowsAsync(Ct).GetAwaiter().GetResult().Single(m => m.Role.Equals("manager", StringComparison.OrdinalIgnoreCase)).Name;

    // ------------------------------------------------------------------ P6: read narrows; read-write over read-only moves it

    [Fact]
    public async Task P6_ReadWriteGrant_OfAFolderAlreadyReadOnly_MovesItOffTheReadOnlyList()
    {
        var (_, _, ada, _) = await Team();
        await Ask(ada, Sessions, "read");
        await Call("folder.answer", new { id = _asks.Asks().Single().Id, choice = "read" });
        Assert.Contains(Sessions, Strings((await MemberPolicy(ada.Id))["effective"]!["readonly"]));

        // Readable already, so a read ask is not asked; a read-write one is.
        Assert.Contains("already readable", await Ask(ada, Sessions, "read"));
        Assert.StartsWith("asked", await Ask(ada, Sessions, "readwrite"));
        await Call("folder.answer", new { id = _asks.Asks().Single().Id, choice = "readwrite" });
        var p = await MemberPolicy(ada.Id);
        Assert.Contains(Sessions, Strings(p["effective"]!["readwrite"]));
        Assert.DoesNotContain(Sessions, Strings(p["effective"]!["readonly"]));
        Assert.Null(p["loosen"]!["filesystem"]!["readonly"]);
    }

    [Fact]
    public async Task ASecondAnswer_IsRefused_AndChangesNothing()
    {
        var (_, _, ada, _) = await Team();
        await Ask(ada, Sessions);
        var id = _asks.Asks().Single().Id;
        await Call("folder.answer", new { id, choice = "deny" });
        var e = await Assert.ThrowsAsync<RpcException>(() => Call("folder.answer", new { id, choice = "readwrite" }));
        Assert.Equal("bad_params", e.Code);
        Assert.Contains("not waiting", e.Message);
        Assert.DoesNotContain(Sessions, Strings((await MemberPolicy(ada.Id))["effective"]!["readwrite"]));
        Assert.Equal("denied", _asks.Asks(openOnly: false).Single().State);
        Assert.Single(_told); // the deny only
    }

    // ------------------------------------------------------------------ P3: who may ask for whom, through real teams

    [Fact]
    public async Task P3_AManagerAsksForItsOwnMember_NotAnotherTeams_AndTheDenyTellsTheManager()
    {
        var (_, manager, _, ben) = await Team();
        var (_, _, otherAda, _) = await Team("docs");
        Assert.StartsWith("asked", await Ask(manager, Sessions, member: "Ben"));
        var ask = _asks.Asks().Single();
        Assert.Equal(("payments/Ben", $"payments/{manager.Name}"), (ask.Member, ask.AskedBy));
        Assert.StartsWith("refused:", await Ask(manager, Sessions, member: "docs/Ada"));
        Assert.StartsWith("refused:", await Ask(manager, Sessions, member: "Nobody"));
        Assert.StartsWith("refused: only your Manager", await Ask(otherAda, Sessions, member: "Ben"));
        Assert.Single(_asks.Asks(openOnly: false));

        await Call("folder.answer", new { id = ask.Id, choice = "deny" });
        var told = Assert.Single(_told);
        Assert.Equal(("payments", manager.Name), (told.Team, told.Member));
        Assert.Contains("for Ben", told.Text);
        Assert.Null((await MemberPolicy(ben.Id))["loosenApprovedAt"]?.GetValue<string>());
    }

    // ------------------------------------------------------------------ P4: every refusal, no card anywhere

    // ~\.claude (the CLI's config) is not in this list: MemberLayouts here finds no CLI under the fake home, so its
    // install is a placeholder with no ConfigDenied. FolderAskTests covers it with a detected CLI (PolicyFixtures).
    public static TheoryData<string, string, string> WindowsRefusals => new()
    {
        { @"..\data", "read", "not an absolute path" },
        { @"Z:\", "read", "root" },
        { @"C:\Users\pat", "read", "user profile folder" },
        { @"C:\Users\pat\.ssh\keys", "read", "secrets" },
        { @"C:\Users\pat\AppData\Roaming\Microsoft\Credentials", "read", "secrets" },
        { @"C:\Users\pat\AppData\Local\Yawble\data", "read", "Yawble's own data" },
        { @"C:\Users\pat\AppData\Local\Yawble\teams\docs\documents", "read", "another team" },
        { @"C:\Users\pat\AppData\Local\Yawble", "read", "Yawble" },
        { @"C:\Users\pat\Downloads\x", "read", "policy denies" },
        { @"C:\$Recycle.Bin", "read", "Windows system folder" },
        { @"D:\Recovery", "read", "Windows system folder" },
        { @"C:\ProgramData\Tool", "readwrite", "never read-write" },
    };

    [Theory]
    [MemberData(nameof(WindowsRefusals))]
    public async Task P4_EachWindowsRefusal_HasAReason_AndNoAskCountOrPush(string path, string access, string why)
    {
        Assert.SkipUnless(Win, "the window's layout here is this machine's OS (Windows paths)");
        var (_, _, ada, _) = await Team();
        await Team("docs");
        var baseline = await Overview();
        var text = await Ask(ada, path, access);
        Assert.StartsWith("refused:", text);
        Assert.Contains(why, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The person is not asked", text);
        Assert.Empty(_asks.Asks(openOnly: false));
        Assert.Empty((await Call("folder.asks", new { all = true }))!.AsArray());
        Assert.Equal(0, _changed);
        Assert.Equal(baseline, await Overview());
        Assert.Empty(_told);
    }

    [Theory]
    [InlineData("data/sessions", "read", "not an absolute path")]
    [InlineData("/", "read", "root")]
    [InlineData("/home/alice", "read", "user profile folder")]
    [InlineData("/home", "read", "contains the person's user profile")]
    [InlineData("/home/alice/.ssh", "read", "secrets")]
    [InlineData("/home/alice/.kube/config.d", "read", "secrets")]
    [InlineData("/etc/ssl", "read", "system folder")]
    [InlineData("/proc/1", "read", "system folder")]
    [InlineData("/usr/local/data", "readwrite", "never read-write")]
    [InlineData("/var/data", "readwrite", "never read-write")]
    public async Task P4_EachLinuxRefusal_IsSaidAtOnce_WithNoAsk(string path, string access, string why)
    {
        var told = new List<string>();
        var changed = 0;
        var asks = new FolderAskService(_db.ConnectionString, _policies,
            (_, _) => Task.FromResult(PolicyFixtures.Layout(TargetOs.Linux, AgentCli.Claude)),
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]), (_, _, t, _, _) => { told.Add(t); return Task.CompletedTask; }, exists: _ => true)
        { Changed = () => changed++ };
        var (_, _, ada, _) = await Team();
        var text = await asks.RequestFromAgentAsync(ada, path, access, "need it", null, 1, 1, Ct);
        Assert.StartsWith("refused:", text);
        Assert.Contains(why, text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(asks.Asks(openOnly: false));
        Assert.Equal(0, changed);
        Assert.Empty(told);
    }

    [Theory]
    [InlineData("/usr/local/data")]
    [InlineData("/opt/datasets")]
    public async Task P4_LinuxProgramFolders_MayBeAskedReadOnly(string path)
    {
        var asks = new FolderAskService(_db.ConnectionString, _policies,
            (_, _) => Task.FromResult(PolicyFixtures.Layout(TargetOs.Linux, AgentCli.Claude)),
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]), (_, _, _, _, _) => Task.CompletedTask, exists: _ => true);
        var (_, _, ada, _) = await Team();
        Assert.StartsWith("asked", await asks.RequestFromAgentAsync(ada, path, "read", "need it", null, 1, 1, Ct));
    }

    // ------------------------------------------------------------------ elsewhere: Concierge lines, org summary title

    [Fact]
    public async Task TheConciergeLines_NameTheAsk_AndTheOrgTitle_IsTheFolderNameOnly()
    {
        var (_, manager, _, _) = await Team();
        var deep = Win ? @"Z:\people\pat\private\smoke" : "/srv/people/pat/private/smoke";
        await Ask(manager, deep, "read", member: "Ben");
        var line = Assert.Single(_asks.PendingLines());
        Assert.Contains("payments/Ben asks for read-only access to " + deep, line);
        Assert.Contains($"(asked by {manager.Name})", line);
        Assert.Contains($"(folder request {_asks.Asks().Single().Id})", line);

        Assert.Equal("smoke", Yawble.Core.Link.AppOrgSummarySource.FolderName(deep));
        Assert.Equal("smoke", Yawble.Core.Link.AppOrgSummarySource.FolderName(deep + (Win ? @"\" : "/")));
    }
}
