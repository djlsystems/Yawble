using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Companion;
using Yawble.Core.Concierge;
using Yawble.Core.Gateway;
using Yawble.Core.Integrations;
using Yawble.Core.Ipc;
using Yawble.Core.Logs;
using Yawble.Core.Provenance;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Xunit;
using MemberRef = Yawble.Core.Bus.MemberRef;

namespace Yawble.Core.Tests.Integrations;

/// <summary>
/// Host tool asks: request_tool names a tool from the app's fixed catalog; the person is asked once per
/// (team, tool); Install runs exactly the catalog's winget command for the person at the window; the program is looked
/// for again afterwards and the Manager is told to retry. Nothing outside the catalog is installable, one install runs
/// at a time, and one package under two names is one tool. Fakes only: a fake tool host records what would run; no test
/// runs winget, an installer or any other program.
/// </summary>
public sealed class ToolAskTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDb _db = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeToolHost _host = new();
    private readonly List<(string Team, string Member, string Text, long? Causation)> _told = [];
    private readonly List<JsonObject> _progress = [];
    private readonly ToolAskService _asks;
    private int _changed;
    private readonly long _alpha, _beta, _priya, _bo, _wren;

    public ToolAskTests()
    {
        _asks = NewService();
        _alpha = Team("Alpha");
        _beta = Team("Beta");
        Member(_alpha, "Manager", "manager");
        _priya = Member(_alpha, "Priya", "member");
        _bo = Member(_alpha, "Bo", "member");
        Member(_beta, "Manager", "manager");
        _wren = Member(_beta, "Wren", "member");
    }

    public void Dispose() => _db.Dispose();

    private ToolAskService NewService() =>
        new(_db.ConnectionString, _host, (team, member, text, causation, _) =>
        {
            lock (_told) _told.Add((team, member, text, causation));
            return Task.CompletedTask;
        }, _clock)
        {
            Changed = () => Interlocked.Increment(ref _changed),
            Progress = e => { lock (_progress) _progress.Add(e); },
        };

    private MemberRef Priya => new(_priya, _alpha, "Alpha", "Priya", MemberRole.Member);
    private MemberRef Bo => new(_bo, _alpha, "Alpha", "Bo", MemberRole.Member);
    private MemberRef Wren => new(_wren, _beta, "Beta", "Wren", MemberRole.Member);

    private long Team(string name) => Insert("INSERT INTO teams (name, created_at) VALUES ($n, '2026-11-13T00:00:00Z') RETURNING id;", ("$n", name));

    private long Member(long team, string name, string role) =>
        Insert("INSERT INTO members (team_id, name, role, created_at) VALUES ($t, $n, $r, '2026-11-13T00:00:00Z') RETURNING id;", ("$t", team), ("$n", name), ("$r", role));

    private long Insert(string sql, params (string, object)[] args)
    {
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return (long)cmd.ExecuteScalar()!;
    }

    private Task<string> Request(MemberRef m, string tool, string reason = "build the console app", long deliverable = 1, long causation = 2) =>
        _asks.RequestFromAgentAsync(m, tool, reason, deliverable, causation, Ct);

    private async Task<T> AsWindow<T>(Func<Task<T>> f)
    {
        using (Actor.Use(Actor.Window)) return await f();
    }

    // ------------------------------------------------------------------ the catalog

    [Fact]
    public void TheCatalog_IsFixed_FourTools()
    {
        Assert.Equal(["dotnet-sdk-10", "dotnet-sdk-lts", "nodejs-lts", "python-3"], ToolCatalog.Names);
        Assert.Equal("Microsoft.DotNet.SDK.10", ToolCatalog.Find("dotnet-sdk-10")!.WingetId);
        Assert.Equal($"Microsoft.DotNet.SDK.{ToolCatalog.DotNetLtsMajor}", ToolCatalog.Find("dotnet-sdk-lts")!.WingetId);
        Assert.Equal("OpenJS.NodeJS.LTS", ToolCatalog.Find("nodejs-lts")!.WingetId);
        Assert.StartsWith("Python.Python.3.", ToolCatalog.Find("python-3")!.WingetId);
        foreach (var t in ToolCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Publisher));
            Assert.Contains(t.Program, (string[])["dotnet", "node", "python", "python3"]);
        }
    }

    [Theory]
    [InlineData("Node", "nodejs-lts")]
    [InlineData("python3", "python-3")]
    [InlineData(" DOTNET-SDK-10 ", "dotnet-sdk-10")]
    [InlineData("dotnet", "dotnet-sdk-lts")]
    public void ANameOrAlias_FindsItsEntry(string asked, string name) => Assert.Equal(name, ToolCatalog.Find(asked)!.Name);

    [Theory]
    [InlineData("dotnet-sdk-10", "Microsoft.DotNet.SDK.10", false)]
    [InlineData("nodejs-lts", "OpenJS.NodeJS.LTS", false)]
    [InlineData("python-3", null, true)]
    public void TheWingetCommand_IsExactlyTheCatalogs(string name, string? id, bool userScope)
    {
        var t = ToolCatalog.Find(name)!;
        string[] expected =
        [
            "install", "--id", id ?? t.WingetId, "--exact", "--source", "winget", "--silent", "--accept-package-agreements",
            "--accept-source-agreements", "--disable-interactivity", .. userScope ? (string[])["--scope", "user"] : [],
        ];
        Assert.Equal(expected, ToolCatalog.WingetArgs(t));
        Assert.Equal(userScope, t.UserScope);
    }

    [Fact]
    public void TheSdkVersion_IsReadFromListSdks()
    {
        const string list = "8.0.404 [C:\\Program Files\\dotnet\\sdk]\r\n10.0.100 [C:\\Program Files\\dotnet\\sdk]\r\n";
        Assert.True(ToolAskService.ListsSdk(list, 10));
        Assert.True(ToolAskService.ListsSdk(list, 8));
        Assert.False(ToolAskService.ListsSdk(list, 9));
        Assert.False(ToolAskService.ListsSdk("1.0.0 [x]\n", 10));
    }

    // ------------------------------------------------------------------ request_tool

    [Fact]
    public async Task AnUnknownName_ListsTheCatalog_AndAsksNothing()
    {
        foreach (var asked in new[] { "rust", "Microsoft.DotNet.SDK.10", "winget install --id Git.Git", "" })
        {
            var text = await Request(Priya, asked);
            Assert.StartsWith("refused:", text);
            foreach (var n in ToolCatalog.Names) Assert.Contains(n, text);
        }
        Assert.Empty(_asks.Asks(openOnly: false));
        Assert.Equal(0, _changed);
    }

    [Fact]
    public async Task AlreadyInstalled_SaysSo_AndAsksNothing()
    {
        _host.Installed.Add("nodejs-lts");
        var text = await Request(Priya, "node");
        Assert.StartsWith("Node.js LTS is already installed", text);
        Assert.Contains("next run", text);
        Assert.Empty(_asks.Asks(openOnly: false));
    }

    [Fact]
    public async Task AMissingReason_IsRefused() =>
        await Assert.ThrowsAsync<ArgumentException>(() => Request(Priya, "python-3", " "));

    [Fact]
    public async Task OneAskPerTeamAndTool_RepeatsCountUp()
    {
        var first = await Request(Priya, "dotnet-sdk-10", "build the .NET 10 app", deliverable: 40, causation: 41);
        Assert.StartsWith("asked: the person is asked to install .NET SDK 10", first);
        Assert.Contains("blocked on .NET SDK 10", first);
        var again = await Request(Bo, "dotnet-sdk-10", "run the tests", deliverable: 50, causation: 51);
        Assert.Contains("already waiting", again);
        await Request(Wren, "dotnet-sdk-10");   // another team: its own ask
        await Request(Priya, "python-3");        // another tool: its own ask

        var open = _asks.Asks();
        Assert.Equal(3, open.Count);
        var alpha = Assert.Single(open, a => a.TeamId == _alpha && a.Tool == "dotnet-sdk-10");
        Assert.Equal((2, "Alpha/Bo", "run the tests", 51L), (alpha.Count, alpha.Member, alpha.Reason, alpha.CausationSeq));
        Assert.Equal(3, _asks.PendingCount());
        Assert.Equal(["asked", "asked again"], _asks.Events(alpha.Id).Select(e => e.Kind));
        Assert.Contains(_asks.PendingLines(), l => l.Contains("Alpha/Bo asks for .NET SDK 10") && l.Contains("2 time(s)"));
        Assert.Empty(_host.Runs); // asking never runs anything
    }

    [Fact]
    public async Task NotNow_QuietsTheToolForTheTeam_For24Hours()
    {
        await Request(Priya, "python-3", causation: 9);
        var id = _asks.Asks().Single().Id;
        await AsWindow(() => _asks.NotNowAsync(id, "later this week", Ct));

        var told = Assert.Single(_told);
        Assert.Equal(("Alpha", "Priya", 9L), (told.Team, told.Member, told.Causation));
        Assert.Contains("Not now", told.Text);
        Assert.Empty(_asks.Asks());
        Assert.StartsWith("refused: the person chose Not now", await Request(Bo, "python"));
        Assert.StartsWith("asked:", await Request(Wren, "python-3")); // another team is asked
        _clock.Advance(TimeSpan.FromHours(23));
        Assert.StartsWith("refused:", await Request(Priya, "python-3"));
        _clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        Assert.StartsWith("asked:", await Request(Priya, "python-3"));
        Assert.Equal(["asked", "not now"], _asks.Events(id).Select(e => e.Kind));
    }

    [Fact]
    public async Task NotNow_IsThePersons_InTheWindowOrOverTheCloudLink()
    {
        await Request(Priya, "python-3");
        var id = _asks.Asks().Single().Id;
        foreach (var via in new[] { Actor.Concierge, Actor.Agent, Actor.External, Actor.Trigger })
            using (Actor.Use(new Actor(via, "someone")))
                await Assert.ThrowsAsync<ArgumentException>(() => _asks.NotNowAsync(id, null, Ct));
        using (Actor.Use(Actor.CloudViewer("person@example.com")))
            await _asks.NotNowAsync(id, null, Ct);
        var row = Assert.Single(_asks.Asks(openOnly: false));
        Assert.Equal(("dismissed", "person@example.com", Actor.Cloud), (row.State, row.DecidedBy, row.DecidedVia));
    }

    // ------------------------------------------------------------------ Install

    [Fact]
    public async Task Install_RunsExactlyTheCatalogsWingetCommand_ThenTellsTheManagerToRetry()
    {
        await Request(Priya, "dotnet-sdk-10", causation: 41);
        await Request(Wren, "dotnet-sdk-10", causation: 77);
        await Request(Bo, "python-3");
        var id = _asks.Asks().First(a => a.TeamId == _alpha && a.Tool == "dotnet-sdk-10").Id;
        _host.InstallsOnRun = "dotnet-sdk-10";

        var r = await AsWindow(() => _asks.InstallAsync(id, Ct));

        Assert.True(r["done"]!.GetValue<bool>());
        var run = Assert.Single(_host.Runs);
        Assert.Equal(FakeToolHost.Winget, run.Exe);
        Assert.Equal(ToolCatalog.WingetArgs(ToolCatalog.Find("dotnet-sdk-10")!), run.Args);
        Assert.Equal(["installing", "checking", "installed"], _progress.Select(p => p["phase"]!.GetValue<string>()));
        // Every open ask for the tool closes; each team's Manager is told, in the ask's deliverable.
        Assert.Equal(2, r["closed"]!.AsArray().Count);
        Assert.Equal(["Alpha/Manager", "Beta/Manager"], r["told"]!.AsArray().Select(n => n!.GetValue<string>()).Order());
        Assert.Contains(_told, t => t is { Team: "Alpha", Member: "Manager", Causation: 41 } && t.Text.StartsWith(".NET SDK 10 is installed; retry what was blocked"));
        Assert.Contains(_told, t => t is { Team: "Beta", Member: "Manager", Causation: 77 });
        Assert.Equal("python-3", Assert.Single(_asks.Asks()).Tool);
        Assert.Equal(["asked", "install", "installed"], _asks.Events(id).Select(e => e.Kind));
        Assert.Contains("winget install --id Microsoft.DotNet.SDK.10 --exact", _asks.Events(id)[1].Detail);
        // A new request finds it installed.
        Assert.Contains("already installed", await Request(Priya, "dotnet-sdk-10"));
    }

    [Fact]
    public async Task AFailedInstall_KeepsTheAskOpenWithWingetsMessage_AndTryAgainWorks()
    {
        await Request(Priya, "nodejs-lts");
        var id = _asks.Asks().Single().Id;
        _host.Result = new GitResult(-1978335212, "  -\r\n  \\\r\nNo package found matching input criteria.\r\n", "");

        var r = await AsWindow(() => _asks.InstallAsync(id, Ct));

        Assert.False(r["done"]!.GetValue<bool>());
        Assert.Contains("No package found matching input criteria.", r["message"]!.GetValue<string>());
        var ask = Assert.Single(_asks.Asks());
        Assert.Equal(("pending", 1), (ask.State, ask.Attempts));
        Assert.Contains("No package found", ask.Error);
        Assert.Equal("failed", _progress.Last()["phase"]!.GetValue<string>());
        Assert.Empty(_told);
        var json = (await _asks.DispatchAsync("tools.asks", null, Ct))!.AsArray().Single()!.AsObject();
        Assert.Contains("No package found", json["error"]!.GetValue<string>());

        // Try again: the same command, and this time the program is found.
        _host.Result = new GitResult(0, "Successfully installed", "");
        _host.InstallsOnRun = "nodejs-lts";
        Assert.True((await AsWindow(() => _asks.InstallAsync(id, Ct)))["done"]!.GetValue<bool>());
        Assert.Equal(2, _host.Runs.Count);
        Assert.Equal(_host.Runs[0].Args, _host.Runs[1].Args);
        Assert.Equal(["asked", "install", "failed", "install", "installed"], _asks.Events(id).Select(e => e.Kind));
        Assert.Single(_told);
    }

    [Fact]
    public async Task WingetMissing_KeepsTheAskOpen_RunsNothing()
    {
        await Request(Priya, "nodejs-lts");
        var id = _asks.Asks().Single().Id;
        _host.WingetPath = null;
        var r = await AsWindow(() => _asks.InstallAsync(id, Ct));
        Assert.False(r["done"]!.GetValue<bool>());
        Assert.Contains("winget is not available", r["message"]!.GetValue<string>());
        Assert.Empty(_host.Runs);
        Assert.Contains("winget is not available", Assert.Single(_asks.Asks()).Error);
    }

    [Fact]
    public async Task Install_IsTheWindowsAlone_NeverTheCloudLinkTheCompanionOrAnAgent()
    {
        await Request(Priya, "python-3");
        var id = _asks.Asks().Single().Id;
        foreach (var actor in new[] { Actor.CloudViewer("person@example.com"), Actor.ExternalClient("claude-code"), new Actor(Actor.Concierge, "x"),
                     new Actor(Actor.Agent, "Alpha/Priya"), new Actor(Actor.Trigger, null) })
        {
            using (Actor.Use(actor))
            {
                var e = await Assert.ThrowsAsync<RpcException>(() => _asks.DispatchAsync("tools.install", new JsonObject { ["id"] = id }, Ct));
                Assert.Equal("forbidden", e.Code);
            }
        }
        // No actor at all (a call that did not come through the window) is refused too.
        Assert.Equal("forbidden", (await Assert.ThrowsAsync<RpcException>(() => _asks.InstallAsync(id, Ct))).Code);
        Assert.Empty(_host.Runs);

        Assert.Equal(ToolAskService.InstallRefusal, Yawble.Core.Link.RemoteAccess.Refusal("tools.install", new JsonObject { ["id"] = id }, allowHostTerminals: true));
        Assert.Null(Yawble.Core.Link.RemoteAccess.Refusal("tools.asks", null, allowHostTerminals: false));
        Assert.Null(Yawble.Core.Link.RemoteAccess.Refusal("tools.catalog", null, allowHostTerminals: false));
        Assert.Null(Yawble.Core.Link.RemoteAccess.Refusal("tools.notNow", new JsonObject { ["id"] = id }, allowHostTerminals: false));
        Assert.True(AuditLog.IsReadOnly("tools.asks"));
        Assert.True(AuditLog.IsReadOnly("tools.catalog"));
        Assert.False(AuditLog.IsReadOnly("tools.install"));
        Assert.False(AuditLog.IsReadOnly("tools.notNow"));
    }

    [Fact]
    public async Task OnFedora_TheAskCarriesTheDnfCommand()
    {
        _host.IsWindows = false;
        _host.Packager = LinuxPackager.Dnf;
        var text = await Request(Priya, "dotnet-sdk-10");
        Assert.Contains("sudo dnf install -y dotnet-sdk-10.0", text);
        var item = (await _asks.DispatchAsync("tools.asks", null, Ct))!.AsArray().Single()!.AsObject();
        Assert.Equal("sudo dnf install -y dotnet-sdk-10.0", item["command"]!.GetValue<string>());
        Assert.Empty(_host.Runs);
    }

    [Fact]
    public async Task OffWindows_TheAskCarriesNoWingetIdAndNoWindowsScope()
    {
        // The card on a Mac showed "Microsoft.DotNet.SDK.10 · installs for the whole computer: Windows may ask for permission".
        _host.IsWindows = false;
        await Request(Priya, "dotnet-sdk-lts");
        var item = (await _asks.DispatchAsync("tools.asks", null, Ct))!.AsArray().Single()!.AsObject();
        Assert.Null(item["wingetId"]);
        Assert.Null(item["scope"]);
        var entry = (await _asks.DispatchAsync("tools.catalog", null, Ct))!.AsArray().First()!.AsObject();
        Assert.Null(entry["wingetId"]);
        Assert.Null(entry["scope"]);
    }

    [Theory]
    [InlineData("NAME=\"Fedora Linux\"\nID=fedora\nVERSION_ID=44\n", LinuxPackager.Dnf)]
    [InlineData("ID=\"rocky\"\nID_LIKE=\"rhel centos fedora\"\n", LinuxPackager.Dnf)]
    [InlineData("ID=ubuntu\nID_LIKE=debian\n", LinuxPackager.Apt)]
    [InlineData("ID=debian\n", LinuxPackager.Apt)]
    [InlineData("ID=manjaro\nID_LIKE=arch\n", LinuxPackager.Pacman)]
    [InlineData("ID=arch\n", LinuxPackager.Pacman)]
    [InlineData("ID=opensuse-tumbleweed\nID_LIKE=\"opensuse suse\"\n", LinuxPackager.Apt)]
    [InlineData("", LinuxPackager.Apt)]
    [InlineData(null, LinuxPackager.Apt)]
    public void ThePackageManagerComesFromOsRelease(string? osRelease, LinuxPackager expected) =>
        Assert.Equal(expected, LinuxPackagers.FromOsRelease(osRelease));

    [Fact]
    public void EveryToolHasACommandForEachPackageManager()
    {
        foreach (var t in ToolCatalog.All)
        {
            Assert.StartsWith("sudo apt-get install -y ", t.ManualCommand(false, LinuxPackager.Apt));
            Assert.StartsWith("sudo dnf install -y ", t.ManualCommand(false, LinuxPackager.Dnf));
            Assert.StartsWith("sudo pacman -S --needed ", t.ManualCommand(false, LinuxPackager.Pacman));
            Assert.StartsWith("brew ", t.ManualCommand(true));
        }
    }

    [Fact]
    public async Task OnLinuxAndMacOS_TheAskCarriesTheCommand_AndNothingIsInstalled()
    {
        _host.IsWindows = false;
        var text = await Request(Priya, "python-3");
        Assert.Contains("sudo apt-get install -y python3", text);
        var id = _asks.Asks().Single().Id;
        var item = (await _asks.DispatchAsync("tools.asks", null, Ct))!.AsArray().Single()!.AsObject();
        Assert.False(item["canInstall"]!.GetValue<bool>());
        Assert.Equal("sudo apt-get install -y python3 python3-pip", item["command"]!.GetValue<string>());
        // No pkexec here (the fake has none): no Install, only Check again after the person ran the command, and Not now.
        Assert.Equal(["checkAgain", "notNow"], item["choices"]!.AsArray().Select(n => n!.GetValue<string>()));
        var r = await AsWindow(() => _asks.InstallAsync(id, Ct));
        Assert.True(r["manual"]!.GetValue<bool>());
        Assert.Equal("manual", r["result"]!.GetValue<string>());
        Assert.Empty(_host.Runs);
        Assert.Equal("pending", Assert.Single(_asks.Asks()).State);
    }

    [Fact]
    public async Task AnInstallTheAppClosedDuring_WaitsAgain()
    {
        await Request(Priya, "python-3");
        var id = _asks.Asks().Single().Id;
        Insert("UPDATE tool_asks SET state = 'installing' WHERE id = $id RETURNING id;", ("$id", id));
        NewService();
        var ask = Assert.Single(_asks.Asks());
        Assert.Equal("pending", ask.State);
        Assert.Contains("try again", ask.Error);
    }

    [Fact]
    public async Task AnInstallTheAppClosedDuring_WritesOneInterruptedRow_InTheApprovalsLog()
    {
        await Request(Priya, "python-3");
        var id = _asks.Asks().Single().Id;
        await AsWindow(() => _asks.NotNowAsync(id, null, Ct));
        await Request(Priya, "nodejs-lts");
        var other = _asks.Asks().Single(a => a.Tool == "nodejs-lts").Id;
        Insert("UPDATE tool_asks SET state = 'installing' WHERE id = $id RETURNING id;", ("$id", other));
        Insert("INSERT INTO tool_ask_events (ask_id, at, kind, detail) VALUES ($id, '2026-01-01T00:00:00Z', 'install', 'winget install') RETURNING id;", ("$id", other));

        NewService();
        NewService(); // a second start finds nothing installing and writes nothing more

        var events = _asks.Events(other);
        Assert.Equal(["asked", "install", "failed"], events.Select(e => e.Kind));
        Assert.Single(events, e => e.Detail == ToolAskService.InterruptedDetail);
        Assert.DoesNotContain(_asks.Events(id), e => e.Detail == ToolAskService.InterruptedDetail);

        var logs = new LogService(_db.Dir, _db.ConnectionString, _clock);
        var rows = logs.Query(new LogQuery("approvals"))["rows"]!.AsArray().Select(r => r!.AsObject())
            .Where(r => r["integration"]!.GetValue<string>() == "host tool" && r["args"]!.GetValue<string>().StartsWith("nodejs-lts")).ToList();
        var row = Assert.Single(rows, r => r["args"]!.GetValue<string>().Contains("install interrupted: the app closed"));
        Assert.Equal("failed", row["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task AFailedInstall_ShowsWingetsExitCodeInHex()
    {
        Assert.Equal("winget exit code -1978335212 (0x8A150014)", ToolAskService.WingetExitCode(-1978335212));
        Assert.Equal("winget exit code 0 (0x00000000)", ToolAskService.WingetExitCode(0));
        await Request(Priya, "nodejs-lts");
        var id = _asks.Asks().Single().Id;
        _host.Result = new GitResult(-1978335212, "No package found matching input criteria.", "");
        var r = await AsWindow(() => _asks.InstallAsync(id, Ct));
        Assert.Contains("winget exit code -1978335212 (0x8A150014)", r["message"]!.GetValue<string>());
        Assert.Contains("0x8A150014", _asks.Events(id).Last().Detail);
    }

    // ------------------------------------------------------------------ the RPCs, the bus tool, the logs

    [Fact]
    public async Task TheRpcs_AsTheWindowCallsThem()
    {
        var catalog = (await _asks.DispatchAsync("tools.catalog", null, Ct))!.AsArray().Select(n => n!.AsObject()).ToList();
        Assert.Equal(ToolCatalog.Names, catalog.Select(c => c["name"]!.GetValue<string>()));
        Assert.Equal("user", catalog.Single(c => c["name"]!.GetValue<string>() == "python-3")["scope"]!.GetValue<string>());

        await Request(Priya, "nodejs-lts", "npm test");
        var item = (await _asks.DispatchAsync("tools.asks", null, Ct))!.AsArray().Single()!.AsObject();
        Assert.Equal(("Alpha/Priya", "Priya", "Alpha"), (item["member"]!.GetValue<string>(), item["memberName"]!.GetValue<string>(), item["team"]!.GetValue<string>()));
        Assert.Equal(("nodejs-lts", "Node.js LTS", "OpenJS Foundation"), (item["tool"]!.GetValue<string>(), item["displayName"]!.GetValue<string>(), item["publisher"]!.GetValue<string>()));
        Assert.Equal("machine", item["scope"]!.GetValue<string>());
        Assert.Equal("npm test", item["reason"]!.GetValue<string>());
        Assert.Equal("pending", item["state"]!.GetValue<string>());
        Assert.True(item["canInstall"]!.GetValue<bool>());
        Assert.StartsWith("winget install --id OpenJS.NodeJS.LTS --exact --source winget", item["command"]!.GetValue<string>());
        Assert.Equal(["install", "notNow"], item["choices"]!.AsArray().Select(n => n!.GetValue<string>()));

        var bad = await Assert.ThrowsAsync<RpcException>(() => _asks.DispatchAsync("tools.notNow", new JsonObject(), Ct));
        Assert.Equal("bad_params", bad.Code);
        using (Actor.Use(Actor.Window))
            await _asks.DispatchAsync("tools.notNow", new JsonObject { ["id"] = item["id"]!.GetValue<long>() }, Ct);
        Assert.Empty((await _asks.DispatchAsync("tools.asks", null, Ct))!.AsArray());
        Assert.Single((await _asks.DispatchAsync("tools.asks", new JsonObject { ["all"] = true }, Ct))!.AsArray());
    }

    [Fact]
    public void TheBusOffersRequestTool_WithToolAndReason()
    {
        Assert.Contains("request_tool", McpBusServer.ToolNames);
        var tool = McpBusServer.ToolList().Select(n => n!.AsObject()).Single(o => o["name"]!.GetValue<string>() == "request_tool");
        Assert.Equal(["reason", "tool"], tool["inputSchema"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()).Order());
        var description = tool["description"]!.GetValue<string>();
        foreach (var n in ToolCatalog.Names) Assert.Contains(n, description);
        Assert.Contains("never install it yourself", description);
    }

    [Fact]
    public async Task EachAskAnswerAndResult_IsInTheApprovalsLog()
    {
        await Request(Priya, "nodejs-lts", "npm test");
        var id = _asks.Asks().Single().Id;
        _host.Result = new GitResult(1, "", "Installer failed with exit code: 1603");
        await AsWindow(() => _asks.InstallAsync(id, Ct));
        await AsWindow(() => _asks.NotNowAsync(id, null, Ct));

        var logs = new LogService(_db.Dir, _db.ConnectionString, _clock);
        var rows = logs.Query(new LogQuery("approvals"))["rows"]!.AsArray().Select(r => r!.AsObject()).Where(r => r["integration"]!.GetValue<string>() == "host tool").ToList();
        Assert.Equal(["asked", "failed", "install", "not now"], rows.Select(r => r["state"]!.GetValue<string>()).Order());
        Assert.All(rows, r => Assert.Equal("Alpha", r["team"]!.GetValue<string>()));
        Assert.Contains(rows, r => r["state"]!.GetValue<string>() == "asked" && r["args"]!.GetValue<string>().Contains("npm test"));
        Assert.Contains(rows, r => r["state"]!.GetValue<string>() == "failed" && r["args"]!.GetValue<string>().Contains("1603"));
        Assert.Contains(rows, r => r["state"]!.GetValue<string>() == "not now" && r["by"]!.GetValue<string>().Contains(Actor.LocalUser));

        var activity = logs.Query(new LogQuery(LogSources.Activity))["rows"]!.AsArray().Select(r => r!.AsObject()).ToList();
        Assert.Contains(activity, r => r["source"]!.GetValue<string>() == "Approvals" && r["event"]!.GetValue<string>() == "host tool nodejs-lts install");
    }

    [Fact]
    public async Task WhileAnInstallRuns_TheWindowsOtherCallsAreAnswered()
    {
        await Request(Priya, "nodejs-lts");
        var id = _asks.Asks().Single().Id;
        _host.InstallsOnRun = "nodejs-lts";
        _host.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const string token = "0123456789abcdef0123456789abcdef-test";
        await using var server = IpcServer.Start(IpcEndpoint.Create(IpcEndpoint.NewAddress(Path.GetTempPath())), token,
            new RpcDispatcher(new Yawble.Core.Sandboxing.SandboxPlatformInfo(), () => ["0001_baseline"], toolAsks: _asks));
        await using var client = await TestIpcClient.ConnectAsync(server.Address, Ct);
        await client.AuthAsync(token, Ct);

        var install = await client.SendAsync("tools.install", new JsonObject { ["id"] = id }, Ct);
        var info = await client.ReadReplyAsync(await client.SendAsync("core.getInfo", null, Ct), Ct);
        Assert.NotNull(info!["result"]); // answered while winget (the fake) is still running
        Assert.False(_host.Gate.Task.IsCompleted);

        _host.Gate.SetResult();
        var done = await client.ReadReplyAsync(install, Ct);
        Assert.True(done!["result"]!["done"]!.GetValue<bool>());
    }

    /// <summary>Records what would run; a run "installs" <see cref="InstallsOnRun"/> when set. Never starts a process.</summary>
    private sealed class FakeToolHost : IToolHost
    {
        public const string Winget = @"C:\Users\p\AppData\Local\Microsoft\WindowsApps\winget.exe";

        public bool IsWindows { get; set; } = true;

        public bool IsMac => false;

        public LinuxPackager Packager { get; set; } = LinuxPackager.Apt;

        public string? WingetPath { get; set; } = Winget;

        public HashSet<string> Installed { get; } = [];

        public string? InstallsOnRun { get; set; }

        public GitResult Result { get; set; } = new(0, "Successfully installed", "");

        public List<(string Exe, IReadOnlyList<string> Args)> Runs { get; } = [];

        public string? FindWinget() => WingetPath;

        public Task<bool> IsInstalledAsync(ToolEntry tool, CancellationToken ct) => Task.FromResult(Installed.Contains(tool.Name));

        /// <summary>When set, a run waits for it (an install that takes a while).</summary>
        public TaskCompletionSource? Gate { get; set; }

        /// <summary>Set when the first run starts.</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<GitResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan limit, CancellationToken ct)
        {
            Runs.Add((exe, args.ToList()));
            Started.TrySetResult();
            if (Gate is { } gate) await gate.Task.WaitAsync(ct);
            if (InstallsOnRun is { } t) Installed.Add(t);
            return Result;
        }
    }

    // ------------------------------------------------------------------ only the catalog, one install at a time, one tool per package

    private Task<string> Request(string tool) => Request(Priya, tool);

    private async Task<long> Asked(string tool)
    {
        Assert.StartsWith("asked:", await Request(tool));
        return _asks.Asks().Single(a => a.Tool == ToolCatalog.Find(tool)!.Name).Id;
    }

    private Task<JsonObject> Install(long id) => AsWindow(() => _asks.InstallAsync(id, Ct));

    [Fact]
    public async Task InstallingPython_RunsExactlyTheCatalogLine_WithScopeUser()
    {
        var id = await Asked("python");
        _host.InstallsOnRun = "python-3";
        Assert.True((await Install(id))["done"]!.GetValue<bool>());
        var run = Assert.Single(_host.Runs);
        Assert.Equal(FakeToolHost.Winget, run.Exe);
        var wingetId = ToolCatalog.Find("python-3")!.WingetId;
        Assert.Equal(
            ["install", "--id", wingetId, "--exact", "--source", "winget", "--silent", "--accept-package-agreements",
             "--accept-source-agreements", "--disable-interactivity", "--scope", "user"], run.Args);
        Assert.Equal($"winget install --id {wingetId} --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity --scope user",
            ToolCatalog.CommandLine(ToolCatalog.Find("python-3")!));
        // The machine-wide entries never carry a scope.
        foreach (var t in ToolCatalog.All.Where(t => t.Name != "python-3"))
            Assert.DoesNotContain("--scope", ToolCatalog.WingetArgs(t));
    }

    [Theory]
    [InlineData("python-3 --override /quiet")]
    [InlineData("python-3; winget install Evil.Pkg")]
    [InlineData("Python.Python.3.14")]
    [InlineData("OpenJS.NodeJS.LTS")]
    [InlineData("git")]
    [InlineData("--id Microsoft.DotNet.SDK.10")]
    public async Task ANameCarryingAnArgumentOrAPackageId_IsRefused_AndNothingIsAsked(string asked)
    {
        Assert.StartsWith("refused:", await Request(asked));
        Assert.Empty(_asks.Asks(openOnly: false));
    }

    [Fact]
    public async Task ARowWhoseToolIsNotInTheCatalog_CannotBeInstalled()
    {
        var id = await Asked("nodejs-lts");
        // Only the database could hold such a row (request_tool refuses the name): Install still runs nothing.
        Insert("UPDATE tool_asks SET tool = 'Evil.Package' WHERE id = $id RETURNING id;", ("$id", id));
        var e = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            using (Actor.Use(Actor.Window)) await _asks.DispatchAsync("tools.install", new JsonObject { ["id"] = id }, Ct);
        });
        Assert.Equal("bad_params", e.Code);
        Assert.Empty(_host.Runs);
        var item = (await _asks.DispatchAsync("tools.asks", null, Ct))!.AsArray().Single()!.AsObject();
        Assert.False(item["canInstall"]!.GetValue<bool>());
        Assert.Equal(["notNow"], item["choices"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task AnAskClosedByNotNowOrAnInstall_CannotBeInstalledAgain()
    {
        var dismissed = await Asked("python-3");
        using (Actor.Use(Actor.Window)) await _asks.NotNowAsync(dismissed, null, Ct);
        await Assert.ThrowsAsync<RpcException>(async () =>
        {
            using (Actor.Use(Actor.Window)) await _asks.DispatchAsync("tools.install", new JsonObject { ["id"] = dismissed }, Ct);
        });
        Assert.Empty(_host.Runs);

        var node = await Asked("nodejs-lts");
        _host.InstallsOnRun = "nodejs-lts";
        await Install(node);
        _host.Installed.Remove("nodejs-lts"); // even if the program went away again
        await Assert.ThrowsAsync<ArgumentException>(() => Install(node));
        Assert.Single(_host.Runs);
        // And Not now on an ask that is no longer waiting is refused.
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            using (Actor.Use(Actor.Window)) await _asks.NotNowAsync(node, null, Ct);
        });
    }

    [Fact]
    public async Task OneInstallAtATime_ASecondWaits_AndTheFirstFinishes()
    {
        var node = await Asked("nodejs-lts");
        var python = await Asked("python-3");
        _host.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = Install(node);
        await _host.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("installing", _asks.Asks().Single(a => a.Id == node).State);
        // The same ask again: already being installed. Another ask: another tool is being installed. Nothing more runs.
        Assert.Contains("being installed already", (await Install(node))["message"]!.GetValue<string>());
        Assert.Contains("Another tool is being installed", (await Install(python))["message"]!.GetValue<string>());
        Assert.Single(_host.Runs);

        _host.Installed.Add("nodejs-lts");
        _host.Gate.SetResult();
        Assert.True((await first)["done"]!.GetValue<bool>());
        Assert.Equal("pending", Assert.Single(_asks.Asks()).State); // python still waits
    }

    [Fact]
    public async Task NotNow_HoldsAcrossARestartOfTheApp()
    {
        var id = await Asked("nodejs-lts");
        using (Actor.Use(Actor.Window)) await _asks.NotNowAsync(id, null, Ct);
        var restarted = NewService();
        Assert.StartsWith("refused: the person chose Not now", await restarted.RequestFromAgentAsync(Priya, "node", "again", 1, 2, Ct));
        _clock.Advance(ToolAskService.NotNowQuiet);
        Assert.StartsWith("asked:", await restarted.RequestFromAgentAsync(Priya, "node", "again", 1, 2, Ct));
    }

    [Fact]
    public async Task AToolFoundWhenInstallIsClicked_ClosesTheAsk_RunsNothing_AndTellsTheManager()
    {
        var id = await Asked("dotnet-sdk-10");
        _host.Installed.Add("dotnet-sdk-10"); // the person installed it some other way meanwhile
        var r = await Install(id);
        Assert.True(r["done"]!.GetValue<bool>());
        Assert.Empty(_host.Runs);
        Assert.Empty(_asks.Asks());
        var told = Assert.Single(_told);
        Assert.Equal(("Alpha", "Manager"), (told.Team, told.Member));
        Assert.StartsWith(".NET SDK 10 is installed; retry what was blocked", told.Text);
    }

    [Fact]
    public async Task ASuccessfulInstall_IsInTheApprovalsLogAndAllActivity()
    {
        var id = await Asked("nodejs-lts");
        await Request("nodejs-lts"); // asked again
        _host.InstallsOnRun = "nodejs-lts";
        await Install(id);

        var logs = new LogService(_db.Dir, _db.ConnectionString, _clock);
        var rows = logs.Query(new LogQuery("approvals"))["rows"]!.AsArray().Select(r => r!.AsObject())
            .Where(r => r["integration"]!.GetValue<string>() == "host tool").ToList();
        Assert.Equal(["asked", "asked again", "install", "installed"], rows.Select(r => r["state"]!.GetValue<string>()).Order());
        Assert.Contains(rows, r => r["state"]!.GetValue<string>() == "install" && r["args"]!.GetValue<string>().Contains("winget install --id OpenJS.NodeJS.LTS"));
        var activity = logs.Query(new LogQuery(LogSources.Activity))["rows"]!.AsArray().Select(r => r!.AsObject())
            .Where(r => r["source"]!.GetValue<string>() == "Approvals").Select(r => r["event"]!.GetValue<string>()).ToList();
        foreach (var kind in new[] { "asked", "asked again", "install", "installed" })
            Assert.Contains($"host tool nodejs-lts {kind}", activity);
    }

    [Fact]
    public void TheConciergeAndTheCompanion_OfferNoWayToInstallATool()
    {
        var names = ConciergeTools.ToolNames.Concat(ConciergeActions.ToolNames).Concat(ConciergeReadTools.Names)
            .Concat(CompanionTools.Read).Concat(CompanionTools.Act).Concat(CompanionTools.Destructive).Concat(CompanionTools.Own).ToList();
        Assert.DoesNotContain(names, n => n.Contains("install", StringComparison.OrdinalIgnoreCase) || n.StartsWith("tools", StringComparison.Ordinal)
                                          || n.Contains("request_tool", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSamePackageUnderTwoNames_IsOneTool_ForNotNowAndTheAsk()
    {
        Assert.Equal(ToolCatalog.Find("dotnet-sdk-10")!.WingetId, ToolCatalog.Find("dotnet")!.WingetId);
        var id = await Asked("dotnet-sdk-10");
        using (Actor.Use(Actor.Window)) await _asks.NotNowAsync(id, null, Ct);
        Assert.StartsWith("refused:", await Request("dotnet"));
        // and the hold is read from the stored row, so it holds across a restart under the other name too
        Assert.StartsWith("refused:", await NewService().RequestFromAgentAsync(Priya, "dotnet-sdk-lts", "again", 1, 2, Ct));
    }

    [Fact]
    public async Task AnOpenAskUnderOneName_IsTheAskForTheOtherName()
    {
        var id = await Asked("dotnet-sdk-10");
        var again = await Request("dotnet");
        Assert.DoesNotContain("refused", again);
        var ask = Assert.Single(_asks.Asks()); // one Needs you item, not two
        Assert.Equal(id, ask.Id);
        Assert.Equal("dotnet-sdk-10", ask.Tool);
        Assert.Equal(2, ask.Count);
    }

    [Fact]
    public async Task AskingDotnetSdkLts_ThenDotnetSdk10_IsOnePendingAsk_WithCountTwo()
    {
        var id = await Asked("dotnet-sdk-lts");
        var again = await Request("dotnet-sdk-10");
        Assert.StartsWith("asked:", again);
        Assert.Contains($"tool request {id}", again);
        Assert.Contains("already waiting", again);
        var ask = Assert.Single(_asks.Asks());
        Assert.Equal((id, "dotnet-sdk-lts", 2, "pending"), (ask.Id, ask.Tool, ask.Count, ask.State));
        Assert.Equal(1, _asks.PendingCount());
    }

    [Fact]
    public async Task AFinishedInstall_ClosesTheAskFiledUnderTheOtherName_InEveryTeam_AndTellsEachManager()
    {
        // Beta asked under the other name; and Alpha has a second open row under the other name.
        var id = await Asked("dotnet-sdk-lts");
        Insert(
            "INSERT INTO tool_asks (at, team_id, tool, member, reason, count, last_seen, deliverable, causation_seq) " +
            "VALUES ('2026-11-13T11:00:00Z', $t, 'dotnet-sdk-10', 'Alpha/Priya', 'old ask', 1, '2026-11-13T11:00:00Z', 1, 2) RETURNING id;", ("$t", _alpha));
        Assert.StartsWith("asked:", await Request(Wren, "dotnet-sdk-10"));
        Assert.Equal(3, _asks.Asks().Count);

        _host.InstallsOnRun = "dotnet-sdk-lts";
        var r = await Install(id);
        Assert.True(r["done"]!.GetValue<bool>());
        Assert.Single(_host.Runs); // one install, not one per name
        Assert.Empty(_asks.Asks());
        Assert.Equal(0, _asks.PendingCount());
        Assert.Equal(3, r["closed"]!.AsArray().Count);
        Assert.Equal(["Alpha", "Beta"], _told.Select(t => t.Team).Order());
        Assert.All(_told, t => Assert.Equal("Manager", t.Member));
    }

    [Fact]
    public async Task NodeAndPython_StaySeparateTools_ForTheAsk_NotNow_AndTheInstall()
    {
        var node = await Asked("nodejs-lts");
        var python = await Asked("python-3");
        Assert.NotEqual(node, python);
        Assert.All(_asks.Asks(), a => Assert.Equal(1, a.Count));
        Assert.Equal(["nodejs-lts"], ToolCatalog.SamePackage(ToolCatalog.Find("node")!));
        Assert.Equal(["python-3"], ToolCatalog.SamePackage(ToolCatalog.Find("python")!));

        using (Actor.Use(Actor.Window)) await _asks.NotNowAsync(node, null, Ct);
        Assert.StartsWith("refused:", await Request("npm"));
        var again = await Request("pip"); // Not now on Node.js holds nothing for Python
        Assert.StartsWith("asked:", again);
        Assert.Equal(2, Assert.Single(_asks.Asks()).Count);

        _host.InstallsOnRun = "python-3";
        var r = await Install(python);
        Assert.True(r["done"]!.GetValue<bool>());
        Assert.Equal([python], r["closed"]!.AsArray().Select(n => n!.GetValue<long>()));
    }

    // ------------------------------------------------------------------ pruning the approvals log

    private long NotNowAt(string tool)
    {
        var id = _asks.Asks().Single(a => a.Tool == tool).Id;
        AsWindow(() => _asks.NotNowAsync(id, null, Ct)).GetAwaiter().GetResult();
        return id;
    }

    private long PruneAll() => new LogService(_db.Dir, _db.ConnectionString, _clock).Prune("approvals", "9999-12-31");

    private long Count(string sql) => Insert(sql);

    [Fact]
    public async Task ANotNowForDotnetSdkLts_HoldsDotnetSdk10_ThroughPruningAll_UntilIts24HoursEnd()
    {
        // A Not now on dotnet-sdk-10 25 hours ago (its hold is over), then a Not now on dotnet-sdk-lts (the same package).
        await Request(Priya, "dotnet-sdk-10");
        NotNowAt("dotnet-sdk-10");
        _clock.Advance(TimeSpan.FromHours(25));
        Assert.DoesNotContain("refused", await Request(Priya, "dotnet-sdk-lts"));
        NotNowAt("dotnet-sdk-lts");

        // Pruning all an hour later: the old dotnet-sdk-10 Not now goes, the dotnet-sdk-lts one stays; dotnet-sdk-10 is still refused.
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.True(PruneAll() > 0);
        Assert.Equal(0, Count("SELECT count(*) FROM tool_asks WHERE tool = 'dotnet-sdk-10';"));
        Assert.Equal(1, Count("SELECT count(*) FROM tool_asks WHERE tool = 'dotnet-sdk-lts' AND state = 'dismissed';"));
        Assert.StartsWith("refused: the person chose Not now", await Request(Priya, "dotnet-sdk-10"));
        Assert.StartsWith("refused: the person chose Not now", await Request(Bo, "dotnet"));

        // 23h59 after the Not now: pruning all keeps it, still refused.
        _clock.Advance(TimeSpan.FromHours(22) + TimeSpan.FromMinutes(59));
        PruneAll();
        Assert.Equal(1, Count("SELECT count(*) FROM tool_asks WHERE tool = 'dotnet-sdk-lts' AND state = 'dismissed';"));
        Assert.StartsWith("refused", await Request(Priya, "dotnet-sdk-10"));

        // 24h01 after: the hold is over and pruning all removes the Not now with its events; a new ask is raised.
        _clock.Advance(TimeSpan.FromMinutes(2));
        PruneAll();
        Assert.Equal(0, Count("SELECT count(*) FROM tool_asks;"));
        Assert.Equal(0, Count("SELECT count(*) FROM tool_ask_events;"));
        Assert.DoesNotContain("refused", await Request(Priya, "dotnet-sdk-10"));
        Assert.Equal("pending", Assert.Single(_asks.Asks()).State);
        // Another team was never held.
        Assert.DoesNotContain("refused", await Request(Wren, "dotnet-sdk-lts"));
    }

    [Fact]
    public async Task TheInterruptedRow_ReadsInTheApprovalsLog_AndSurvivesPruningAll()
    {
        await Request(Priya, "nodejs-lts");
        var id = _asks.Asks().Single().Id;
        _host.Gate = new TaskCompletionSource();
        var install = AsWindow(() => _asks.InstallAsync(id, Ct)); // the real path to 'installing', with its install event
        for (var i = 0; i < 200 && _asks.Asks().Single().State != "installing"; i++) await Task.Delay(10, Ct);
        Assert.Equal("installing", _asks.Asks().Single().State);

        NewService(); // the app starts again; the earlier install never finished

        var logs = new LogService(_db.Dir, _db.ConnectionString, _clock);
        var rows = logs.Query(new LogQuery("approvals"))["rows"]!.AsArray().Select(r => r!.AsObject())
            .Where(r => r["integration"]!.GetValue<string>() == "host tool").ToList();
        var interrupted = Assert.Single(rows, r => r["args"]!.GetValue<string>().Contains(ToolAskService.InterruptedDetail));
        Assert.Equal("failed", interrupted["state"]!.GetValue<string>());
        Assert.Single(rows, r => r["state"]!.GetValue<string>() == "install");
        Assert.Equal("pending", _asks.Asks().Single().State);

        // The ask waits again, so pruning all keeps it and its interrupted row.
        PruneAll();
        Assert.Contains(_asks.Events(id), e => e.Detail == ToolAskService.InterruptedDetail);
        _host.Gate.TrySetResult();
        await install;
    }
}
