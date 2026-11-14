using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
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
/// Host tool asks (B013): request_tool names a tool from the app's fixed catalog; the person is asked once per
/// (team, tool); Install runs exactly the catalog's winget command for the person at the window; the program is looked
/// for again afterwards and the Manager is told to retry. Fakes only: a fake tool host records what would run; no test
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

    private Task<string> Request(MemberRef m, string tool, string reason = "build the console app", long workflow = 1, long causation = 2) =>
        _asks.RequestFromAgentAsync(m, tool, reason, workflow, causation, Ct);

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
        var first = await Request(Priya, "dotnet-sdk-10", "build the .NET 10 app", workflow: 40, causation: 41);
        Assert.StartsWith("asked: the person is asked to install .NET SDK 10", first);
        Assert.Contains("blocked on .NET SDK 10", first);
        var again = await Request(Bo, "dotnet-sdk-10", "run the tests", workflow: 50, causation: 51);
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
        // Every open ask for the tool closes; each team's Manager is told, in the ask's workflow.
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
        Assert.Equal(["notNow"], item["choices"]!.AsArray().Select(n => n!.GetValue<string>()));
        var r = await AsWindow(() => _asks.InstallAsync(id, Ct));
        Assert.True(r["manual"]!.GetValue<bool>());
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

        public async Task<GitResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan limit, CancellationToken ct)
        {
            Runs.Add((exe, args.ToList()));
            if (Gate is { } gate) await gate.Task.WaitAsync(ct);
            if (InstallsOnRun is { } t) Installed.Add(t);
            return Result;
        }
    }

    // ------------------------------------------------------------------ B015 tester probes (card 70)

    private long ProbeNotNowAt(string tool)
    {
        var id = _asks.Asks().Single(a => a.Tool == tool).Id;
        AsWindow(() => _asks.NotNowAsync(id, null, Ct)).GetAwaiter().GetResult();
        return id;
    }

    private long ProbePruneAll() => new LogService(_db.Dir, _db.ConnectionString, _clock).Prune("approvals", "9999-12-31");

    private long ProbeCount(string sql) => Insert(sql);

    [Fact]
    public async Task Probe_B015_ANotNowForDotnetSdkLts_HoldsDotnetSdk10_ThroughPruningAll_UntilIts24HoursEnd()
    {
        // A Not now on dotnet-sdk-10 25 hours ago (its hold is over), then a Not now on dotnet-sdk-lts (the same package).
        await Request(Priya, "dotnet-sdk-10");
        ProbeNotNowAt("dotnet-sdk-10");
        _clock.Advance(TimeSpan.FromHours(25));
        Assert.DoesNotContain("refused", await Request(Priya, "dotnet-sdk-lts"));
        ProbeNotNowAt("dotnet-sdk-lts");

        // Pruning all an hour later: the old dotnet-sdk-10 Not now goes, the dotnet-sdk-lts one stays; dotnet-sdk-10 is still refused.
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.True(ProbePruneAll() > 0);
        Assert.Equal(0, ProbeCount("SELECT count(*) FROM tool_asks WHERE tool = 'dotnet-sdk-10';"));
        Assert.Equal(1, ProbeCount("SELECT count(*) FROM tool_asks WHERE tool = 'dotnet-sdk-lts' AND state = 'dismissed';"));
        Assert.StartsWith("refused: the person chose Not now", await Request(Priya, "dotnet-sdk-10"));
        Assert.StartsWith("refused: the person chose Not now", await Request(Bo, "dotnet"));

        // 23h59 after the Not now: pruning all keeps it, still refused.
        _clock.Advance(TimeSpan.FromHours(22) + TimeSpan.FromMinutes(59));
        ProbePruneAll();
        Assert.Equal(1, ProbeCount("SELECT count(*) FROM tool_asks WHERE tool = 'dotnet-sdk-lts' AND state = 'dismissed';"));
        Assert.StartsWith("refused", await Request(Priya, "dotnet-sdk-10"));

        // 24h01 after: the hold is over and pruning all removes the Not now with its events; a new ask is raised.
        _clock.Advance(TimeSpan.FromMinutes(2));
        ProbePruneAll();
        Assert.Equal(0, ProbeCount("SELECT count(*) FROM tool_asks;"));
        Assert.Equal(0, ProbeCount("SELECT count(*) FROM tool_ask_events;"));
        Assert.DoesNotContain("refused", await Request(Priya, "dotnet-sdk-10"));
        Assert.Equal("pending", Assert.Single(_asks.Asks()).State);
        // Another team was never held.
        Assert.DoesNotContain("refused", await Request(Wren, "dotnet-sdk-lts"));
    }

    [Fact]
    public async Task Probe_B015_TheInterruptedRow_ReadsInTheApprovalsLog_AndSurvivesPruningAll()
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
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "b015-approvals-rows.json"),
            new JsonArray(rows.Select(r => (JsonNode)r.DeepClone()).ToArray()).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        var interrupted = Assert.Single(rows, r => r["args"]!.GetValue<string>().Contains(ToolAskService.InterruptedDetail));
        Assert.Equal("failed", interrupted["state"]!.GetValue<string>());
        Assert.Single(rows, r => r["state"]!.GetValue<string>() == "install");
        Assert.Equal("pending", _asks.Asks().Single().State);

        // The ask waits again, so pruning all keeps it and its interrupted row.
        ProbePruneAll();
        Assert.Contains(_asks.Events(id), e => e.Detail == ToolAskService.InterruptedDetail);
        _host.Gate.TrySetResult();
        await install;
    }
}
