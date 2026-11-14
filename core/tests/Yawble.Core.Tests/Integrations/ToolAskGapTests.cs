using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Companion;
using Yawble.Core.Concierge;
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
/// B013 end-to-end checks the developers' ToolAskTests leave open (card 65): Python's Install line with --scope user,
/// nothing outside the catalog installable (argument smuggling, a row whose tool is not in the catalog, a closed ask),
/// one install at a time, Not now across a restart and only on a waiting ask, a tool found at Install time, the Concierge
/// and the companion offering no install, the installed result in the logs, and the same package under two names.
/// Fakes only: a temporary database, a fake clock, a recording tell, a fake tool host; no test starts a process.
/// </summary>
public sealed class ToolAskGapTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDb _db = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeToolHost _host = new();
    private readonly List<(string Team, string Member, string Text)> _told = [];
    private readonly ToolAskService _asks;
    private readonly long _alpha, _priya;

    public ToolAskGapTests()
    {
        _asks = NewService();
        _alpha = Insert("INSERT INTO teams (name, created_at) VALUES ('Alpha', '2026-11-13T00:00:00Z') RETURNING id;");
        Insert("INSERT INTO members (team_id, name, role, created_at) VALUES ($t, 'Manager', 'manager', '2026-11-13T00:00:00Z') RETURNING id;", ("$t", _alpha));
        _priya = Insert("INSERT INTO members (team_id, name, role, created_at) VALUES ($t, 'Priya', 'member', '2026-11-13T00:00:00Z') RETURNING id;", ("$t", _alpha));
    }

    public void Dispose() => _db.Dispose();

    private ToolAskService NewService() =>
        new(_db.ConnectionString, _host, (team, member, text, _, _) =>
        {
            lock (_told) _told.Add((team, member, text));
            return Task.CompletedTask;
        }, _clock);

    private MemberRef Priya => new(_priya, _alpha, "Alpha", "Priya", MemberRole.Member);

    private long Insert(string sql, params (string, object)[] args)
    {
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return (long)cmd.ExecuteScalar()!;
    }

    private Task<string> Request(string tool) => _asks.RequestFromAgentAsync(Priya, tool, "build the app", 1, 2, Ct);

    private async Task<long> Asked(string tool)
    {
        Assert.StartsWith("asked:", await Request(tool));
        return _asks.Asks().Single(a => a.Tool == ToolCatalog.Find(tool)!.Name).Id;
    }

    private async Task<JsonObject> Install(long id)
    {
        using (Actor.Use(Actor.Window)) return await _asks.InstallAsync(id, Ct);
    }

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
        var gate = new TaskCompletionSource<GitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Pending = gate.Task;

        var first = Install(node);
        await _host.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("installing", _asks.Asks().Single(a => a.Id == node).State);
        // The same ask again: already being installed. Another ask: another tool is being installed. Nothing more runs.
        Assert.Contains("being installed already", (await Install(node))["message"]!.GetValue<string>());
        Assert.Contains("Another tool is being installed", (await Install(python))["message"]!.GetValue<string>());
        Assert.Single(_host.Runs);

        _host.Installed.Add("nodejs-lts");
        gate.SetResult(new GitResult(0, "Successfully installed", ""));
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

    // ---- card 65 re-check of the B013 fix (ToolCatalog.SamePackage)

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
        // Beta asked under the other name; and Alpha has an older open row under the other name (filed before the fix).
        var beta = Insert("INSERT INTO teams (name, created_at) VALUES ('Beta', '2026-11-13T00:00:00Z') RETURNING id;");
        Insert("INSERT INTO members (team_id, name, role, created_at) VALUES ($t, 'Manager', 'manager', '2026-11-13T00:00:00Z') RETURNING id;", ("$t", beta));
        var ines = Insert("INSERT INTO members (team_id, name, role, created_at) VALUES ($t, 'Ines', 'member', '2026-11-13T00:00:00Z') RETURNING id;", ("$t", beta));
        var id = await Asked("dotnet-sdk-lts");
        Insert(
            "INSERT INTO tool_asks (at, team_id, tool, member, reason, count, last_seen, workflow, causation_seq) " +
            "VALUES ('2026-11-13T11:00:00Z', $t, 'dotnet-sdk-10', 'Alpha/Priya', 'old ask', 1, '2026-11-13T11:00:00Z', 1, 2) RETURNING id;", ("$t", _alpha));
        Assert.StartsWith("asked:", await _asks.RequestFromAgentAsync(new MemberRef(ines, beta, "Beta", "Ines", MemberRole.Member), "dotnet-sdk-10", "build", 1, 2, Ct));
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

    /// <summary>Records what would run; <see cref="Pending"/> holds a run until the test lets it finish. Never starts a process.</summary>
    private sealed class FakeToolHost : IToolHost
    {
        public const string Winget = @"C:\Users\p\AppData\Local\Microsoft\WindowsApps\winget.exe";

        public bool IsWindows => true;

        public bool IsMac => false;

        public HashSet<string> Installed { get; } = [];

        public string? InstallsOnRun { get; set; }

        public Task<GitResult>? Pending { get; set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<(string Exe, IReadOnlyList<string> Args)> Runs { get; } = [];

        public string? FindWinget() => Winget;

        public Task<bool> IsInstalledAsync(ToolEntry tool, CancellationToken ct) => Task.FromResult(Installed.Contains(tool.Name));

        public Task<GitResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan limit, CancellationToken ct)
        {
            lock (Runs) Runs.Add((exe, args.ToList()));
            if (InstallsOnRun is { } t) Installed.Add(t);
            Started.TrySetResult();
            return Pending ?? Task.FromResult(new GitResult(0, "Successfully installed", ""));
        }
    }
}
