using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Integrations;
using Yawble.Core.Ipc;
using Yawble.Core.Provenance;
using Yawble.Core.Storage;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Xunit;
using MemberRef = Yawble.Core.Bus.MemberRef;

namespace Yawble.Core.Tests.Integrations;

/// <summary>
/// B017: on Linux, Install for a tool ask runs exactly "pkexec &lt;package manager at its fixed path&gt; ..." with the
/// catalog's packages, when pkexec, a graphical session and a recognised distribution are there; pkexec's 126 and 127
/// keep the ask open in plain words; the program is looked for again afterwards, and Check again looks for it after a
/// manual install. Fakes only: the fake tool host records what would run; no test runs pkexec, a package manager or any
/// other program.
/// </summary>
public sealed class LinuxToolInstallTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Pkexec = "/usr/bin/pkexec";

    private readonly TempDb _db = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeLinuxHost _host = new();
    private readonly List<(string Team, string Member, string Text, long? Causation)> _told = [];
    private readonly List<JsonObject> _progress = [];
    private readonly ToolAskService _asks;
    private readonly long _alpha, _beta, _priya, _wren;

    public LinuxToolInstallTests()
    {
        _asks = new ToolAskService(_db.ConnectionString, _host, (team, member, text, causation, _) =>
        {
            lock (_told) _told.Add((team, member, text, causation));
            return Task.CompletedTask;
        }, _clock)
        {
            Progress = e => { lock (_progress) _progress.Add(e); },
        };
        _alpha = Insert("INSERT INTO teams (name, created_at) VALUES ('Alpha', 'x') RETURNING id;");
        _beta = Insert("INSERT INTO teams (name, created_at) VALUES ('Beta', 'x') RETURNING id;");
        Insert($"INSERT INTO members (team_id, name, role, created_at) VALUES ({_alpha}, 'Manager', 'manager', 'x') RETURNING id;");
        _priya = Insert($"INSERT INTO members (team_id, name, role, created_at) VALUES ({_alpha}, 'Priya', 'member', 'x') RETURNING id;");
        Insert($"INSERT INTO members (team_id, name, role, created_at) VALUES ({_beta}, 'Manager', 'manager', 'x') RETURNING id;");
        _wren = Insert($"INSERT INTO members (team_id, name, role, created_at) VALUES ({_beta}, 'Wren', 'member', 'x') RETURNING id;");
    }

    public void Dispose() => _db.Dispose();

    private long Insert(string sql)
    {
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private async Task<long> Asked(string tool, MemberRef? m = null, long causation = 5)
    {
        await _asks.RequestFromAgentAsync(m ?? new MemberRef(_priya, _alpha, "Alpha", "Priya", MemberRole.Member), tool, "build it", 1, causation, Ct);
        return _asks.Asks().Single(a => a.Tool == tool && a.TeamId == (m?.TeamId ?? _alpha)).Id;
    }

    private static async Task<T> As<T>(Actor who, Func<Task<T>> f)
    {
        using (Actor.Use(who)) return await f();
    }

    private Task<JsonObject> Install(long id) => As(Actor.Window, () => _asks.InstallAsync(id, Ct));

    private Task<JsonObject> CheckAgain(long id, Actor? who = null) =>
        As(who ?? Actor.Window, async () => (await _asks.DispatchAsync("tools.checkAgain", new JsonObject { ["id"] = id }, Ct))!.AsObject());

    private async Task<JsonObject> Item() => (await _asks.DispatchAsync("tools.asks", null, Ct))!.AsArray().Single()!.AsObject();

    private void Offer(LinuxPackager packager)
    {
        _host.Packager = packager;
        _host.Installer = new LinuxInstaller(Pkexec, packager, LinuxPackagers.ProgramPath(packager));
    }

    // ------------------------------------------------------------------ the exact argv

    [Theory]
    [InlineData(LinuxPackager.Dnf, "dotnet-sdk-10", new[] { "/usr/bin/dnf", "install", "-y", "dotnet-sdk-10.0" })]
    [InlineData(LinuxPackager.Apt, "dotnet-sdk-10", new[] { "/usr/bin/apt-get", "install", "-y", "dotnet-sdk-10.0" })]
    [InlineData(LinuxPackager.Pacman, "dotnet-sdk-10", new[] { "/usr/bin/pacman", "-S", "--needed", "--noconfirm", "dotnet-sdk" })]
    [InlineData(LinuxPackager.Apt, "python-3", new[] { "/usr/bin/apt-get", "install", "-y", "python3", "python3-pip" })]
    [InlineData(LinuxPackager.Dnf, "nodejs-lts", new[] { "/usr/bin/dnf", "install", "-y", "nodejs", "npm" })]
    [InlineData(LinuxPackager.Pacman, "python-3", new[] { "/usr/bin/pacman", "-S", "--needed", "--noconfirm", "python", "python-pip" })]
    public async Task Install_RunsExactlyPkexecWithThePackageManagersFixedPath(LinuxPackager packager, string tool, string[] args)
    {
        Offer(packager);
        var id = await Asked(tool);
        _host.InstallsOnRun = tool;

        var r = await Install(id);

        var run = Assert.Single(_host.Runs);
        Assert.Equal(Pkexec, run.Exe);
        Assert.Equal(args, run.Args);
        Assert.Equal("installed", r["result"]!.GetValue<string>());
        Assert.Equal("pkexec " + string.Join(' ', args), _asks.Events(id).Single(e => e.Kind == "install").Detail);
    }

    [Fact]
    public void ThePackageManager_ComesFromTheFixedList_AndTheDistributionMustBeKnown()
    {
        Assert.Equal("/usr/bin/dnf", LinuxPackagers.ProgramPath(LinuxPackager.Dnf));
        Assert.Equal("/usr/bin/apt-get", LinuxPackagers.ProgramPath(LinuxPackager.Apt));
        Assert.Equal("/usr/bin/pacman", LinuxPackagers.ProgramPath(LinuxPackager.Pacman));
        Assert.Equal(LinuxPackager.Dnf, LinuxPackagers.Recognise("ID=\"rocky\"\nID_LIKE=\"rhel centos fedora\"\n"));
        Assert.Null(LinuxPackagers.Recognise("ID=opensuse-tumbleweed\nID_LIKE=\"opensuse suse\"\n"));
        Assert.Null(LinuxPackagers.Recognise(null));
        // The manual command stays apt for an unknown one.
        Assert.Equal(LinuxPackager.Apt, LinuxPackagers.FromOsRelease("ID=gentoo\n"));
        // Every package pkexec is given is the catalog's own.
        foreach (var t in ToolCatalog.All)
        foreach (var p in Enum.GetValues<LinuxPackager>())
        {
            var li = new LinuxInstaller(Pkexec, p, LinuxPackagers.ProgramPath(p));
            var catalog = (p switch { LinuxPackager.Dnf => t.DnfPackages, LinuxPackager.Pacman => t.PacmanPackages, _ => t.AptPackages }).Split(' ');
            Assert.Equal(catalog, ToolCatalog.PkexecArgs(t, li).Skip(p == LinuxPackager.Pacman ? 4 : 3));
        }
    }

    [Fact]
    public async Task TheAsk_CarriesTheInstallFlag_ThePasswordText_AndTheExactCommand()
    {
        Offer(LinuxPackager.Dnf);
        var text = await _asks.RequestFromAgentAsync(new MemberRef(_priya, _alpha, "Alpha", "Priya", MemberRole.Member), "dotnet-sdk-10", "build it", 1, 5, Ct);
        Assert.Contains("asked:", text);
        var item = await Item();
        Assert.True(item["canInstall"]!.GetValue<bool>());
        Assert.Equal("pkexec", item["installer"]!.GetValue<string>());
        Assert.Equal("Your system asks for your password", item["passwordPrompt"]!.GetValue<string>());
        Assert.Equal("pkexec /usr/bin/dnf install -y dotnet-sdk-10.0", item["command"]!.GetValue<string>());
        Assert.Equal("sudo dnf install -y dotnet-sdk-10.0", item["manualCommand"]!.GetValue<string>());
        Assert.Null(item["homeOnly"]);
        Assert.Equal(["install", "checkAgain", "notNow"], item["choices"]!.AsArray().Select(n => n!.GetValue<string>()));
        var catalog = (await _asks.DispatchAsync("tools.catalog", null, Ct))!.AsArray().Select(n => n!.AsObject()).Single(c => c["name"]!.GetValue<string>() == "dotnet-sdk-10");
        Assert.Equal("pkexec /usr/bin/dnf install -y dotnet-sdk-10.0", catalog["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task WithoutPkexecADisplayOrAKnownDistribution_TheAskIsUnchanged_TheManualCommandAndNotNow()
    {
        _host.Installer = null; // what SystemToolHost.LinuxInstall gives for each of the three
        _host.Packager = LinuxPackager.Dnf;
        var id = await Asked("nodejs-lts");
        var item = await Item();
        Assert.False(item["canInstall"]!.GetValue<bool>());
        Assert.Null(item["installer"]);
        Assert.Null(item["passwordPrompt"]);
        Assert.Equal("sudo dnf install -y nodejs npm", item["command"]!.GetValue<string>());
        Assert.Equal(["checkAgain", "notNow"], item["choices"]!.AsArray().Select(n => n!.GetValue<string>()));
        var r = await Install(id);
        Assert.Equal("manual", r["result"]!.GetValue<string>());
        Assert.Contains("sudo dnf install -y nodejs npm", r["message"]!.GetValue<string>());
        Assert.Empty(_host.Runs);
        Assert.Equal("pending", Assert.Single(_asks.Asks()).State);
    }

    // ------------------------------------------------------------------ who may

    [Fact]
    public async Task Install_IsTheWindowsAlone_TheCloudLinkAndTheCompanionAreRefused()
    {
        Offer(LinuxPackager.Apt);
        var id = await Asked("python-3");
        foreach (var actor in new[] { Actor.CloudViewer("person@example.com"), Actor.ExternalClient("claude-code"), new Actor(Actor.Concierge, "x"),
                     new Actor(Actor.Agent, "Alpha/Priya"), new Actor(Actor.Trigger, null) })
        {
            var e = await Assert.ThrowsAsync<RpcException>(() => As(actor, async () => await _asks.DispatchAsync("tools.install", new JsonObject { ["id"] = id }, Ct)));
            Assert.Equal("forbidden", e.Code);
        }
        Assert.Empty(_host.Runs);
        Assert.Equal(ToolAskService.InstallRefusal, Yawble.Core.Link.RemoteAccess.Refusal("tools.install", new JsonObject { ["id"] = id }, allowHostTerminals: true));
    }

    [Fact]
    public async Task CheckAgain_IsTheWindowsAndTheCloudLinks_NeverAnAgentTheConciergeATriggerOrTheCompanion()
    {
        var id = await Asked("python-3");
        Assert.Null(Yawble.Core.Link.RemoteAccess.Refusal("tools.checkAgain", new JsonObject { ["id"] = id }, allowHostTerminals: false));
        foreach (var actor in new[] { Actor.ExternalClient("claude-code"), new Actor(Actor.Concierge, "x"), new Actor(Actor.Agent, "Alpha/Priya"), new Actor(Actor.Trigger, null) })
            Assert.Equal("forbidden", (await Assert.ThrowsAsync<RpcException>(() => CheckAgain(id, actor))).Code);
        Assert.Equal("forbidden", (await Assert.ThrowsAsync<RpcException>(() => _asks.CheckAgainAsync(id, Ct))).Code);
        Assert.Equal("notFound", (await CheckAgain(id, Actor.CloudViewer("person@example.com")))["result"]!.GetValue<string>());
        Assert.Empty(_host.Runs);
        Assert.False(AuditLog.IsReadOnly("tools.checkAgain"));
    }

    // ------------------------------------------------------------------ 126 and 127

    [Theory]
    [InlineData(126, "cancelled", "closed the password window")]
    [InlineData(127, "notAuthorized", "did not allow the install")]
    public async Task PkexecsCancelAndNotAuthorized_KeepTheAskOpenInPlainWords_NotAPackageFailure(int exit, string code, string words)
    {
        Offer(LinuxPackager.Apt);
        var id = await Asked("nodejs-lts");
        _host.Result = new GitResult(exit, "", "Error executing command as another user: Request dismissed");

        var r = await Install(id);

        Assert.False(r["done"]!.GetValue<bool>());
        Assert.Equal(code, r["result"]!.GetValue<string>());
        Assert.Contains(words, r["message"]!.GetValue<string>());
        Assert.DoesNotContain("did not finish", r["message"]!.GetValue<string>());
        var ask = Assert.Single(_asks.Asks());
        Assert.Equal("pending", ask.State);
        Assert.Contains(words, ask.Error);
        Assert.Equal(code, _progress.Last()["phase"]!.GetValue<string>());
        Assert.Contains($"pkexec exit code {exit}", _asks.Events(id).Last().Detail);
        Assert.Equal(0, _host.Checks); // nothing was installed: not looked for again
        Assert.Empty(_told);
        // Try again runs the same command.
        _host.Result = new GitResult(0, "", "");
        _host.InstallsOnRun = "nodejs-lts";
        Assert.Equal("installed", (await Install(id))["result"]!.GetValue<string>());
        Assert.Equal(2, _host.Runs.Count);
    }

    // ------------------------------------------------------------------ looking again

    [Fact]
    public async Task AfterAnInstall_ItIsFound_EveryOpenAskClosesAndEachManagerIsTold()
    {
        Offer(LinuxPackager.Dnf);
        var id = await Asked("dotnet-sdk-10", causation: 41);
        await Asked("dotnet-sdk-lts", new MemberRef(_wren, _beta, "Beta", "Wren", MemberRole.Member), causation: 77);
        _host.InstallsOnRun = "dotnet-sdk-10";
        _host.InstalledAlso = ["dotnet-sdk-lts"];

        var r = await Install(id);

        Assert.True(r["done"]!.GetValue<bool>());
        Assert.Equal(["installing", "checking", "installed"], _progress.Select(p => p["phase"]!.GetValue<string>()));
        Assert.All(_progress, p => Assert.Equal("pkexec /usr/bin/dnf install -y dotnet-sdk-10.0", p["command"]!.GetValue<string>()));
        Assert.Contains("Your system asks for your password", _progress[0]["message"]!.GetValue<string>());
        Assert.Empty(_asks.Asks());
        Assert.Contains(_told, t => t is { Team: "Alpha", Member: "Manager", Causation: 41 } && t.Text.StartsWith(".NET SDK 10 is installed; retry what was blocked"));
        Assert.Contains(_told, t => t is { Team: "Beta", Member: "Manager" });
    }

    [Fact]
    public async Task NotFoundAfterAnInstall_TheAskStaysOpenWithTheOutput_TryAgain()
    {
        Offer(LinuxPackager.Apt);
        var id = await Asked("nodejs-lts");
        _host.Result = new GitResult(100, "", "E: Unable to locate package nodejs");

        var r = await Install(id);

        Assert.Equal("failed", r["result"]!.GetValue<string>());
        Assert.Contains("exit code 100: E: Unable to locate package nodejs", r["message"]!.GetValue<string>());
        var ask = Assert.Single(_asks.Asks());
        Assert.Equal(("pending", 1), (ask.State, ask.Attempts));
        Assert.Contains("Unable to locate package", ask.Error);
        Assert.Equal(["install", "checkAgain", "notNow"], (await Item())["choices"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Empty(_told);
    }

    [Fact]
    public async Task AProgramOnlyUnderTheHomeFolder_TheAskSaysTheSandboxCannotSeeIt()
    {
        Offer(LinuxPackager.Apt);
        var id = await Asked("dotnet-sdk-10");
        _host.HomeOnly = "/home/alice/.dotnet/dotnet";

        var item = await Item();
        Assert.Contains("/home/alice/.dotnet/dotnet", item["homeOnly"]!.GetValue<string>());
        Assert.Contains("sandbox cannot see it", item["homeOnly"]!.GetValue<string>());

        var check = await CheckAgain(id);
        Assert.Equal("homeOnly", check["result"]!.GetValue<string>());
        Assert.Contains("sandbox cannot see it", check["message"]!.GetValue<string>());

        var r = await Install(id); // exit 0, yet still not where a run looks
        Assert.Equal("homeOnly", r["result"]!.GetValue<string>());
        Assert.Contains("sandbox cannot see it", Assert.Single(_asks.Asks()).Error);
    }

    [Fact]
    public async Task CheckAgain_AfterAManualInstall_ClosesTheAsksAndTellsTheManager_RunningNothing()
    {
        _host.Installer = null;
        var id = await Asked("python-3", causation: 9);
        await Asked("python-3", new MemberRef(_wren, _beta, "Beta", "Wren", MemberRole.Member));

        var first = await CheckAgain(id);
        Assert.Equal(("notFound", false), (first["result"]!.GetValue<string>(), first["done"]!.GetValue<bool>()));
        Assert.Equal(2, _asks.Asks().Count);
        Assert.Contains("not found yet", _asks.Asks().First(a => a.Id == id).Error);
        Assert.Empty(_told);

        _host.Installed.Add("python-3"); // the person ran the command themselves
        var r = await CheckAgain(id);

        Assert.Equal("installed", r["result"]!.GetValue<string>());
        Assert.Equal(2, r["closed"]!.AsArray().Count);
        Assert.Empty(_asks.Asks());
        Assert.Equal(["Alpha/Manager", "Beta/Manager"], r["told"]!.AsArray().Select(n => n!.GetValue<string>()).Order());
        Assert.Contains(_told, t => t is { Team: "Alpha", Member: "Manager", Causation: 9 } && t.Text.StartsWith("Python 3 is installed; retry what was blocked"));
        Assert.Equal("found by Check again", _asks.Events(id).Last().Detail);
        Assert.Empty(_host.Runs);
        // A closed ask is not waiting any more.
        Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() => CheckAgain(id + 100))).Code);
    }

    // ------------------------------------------------------------------ B017 gap tests (card 84, TesterWren)

    [Fact]
    public async Task Gap_AnApprovedCompanionCall_IsStillRefusedInstallAndCheckAgain()
    {
        Offer(LinuxPackager.Dnf);
        var id = await Asked("python-3");
        var approved = Actor.ExternalClient("claude-code") with { ApprovedBy = "person" };
        Assert.Equal("forbidden", (await Assert.ThrowsAsync<RpcException>(() => CheckAgain(id, approved))).Code);
        Assert.Equal("forbidden", (await Assert.ThrowsAsync<RpcException>(() =>
            As(approved, async () => await _asks.DispatchAsync("tools.install", new JsonObject { ["id"] = id }, Ct)))).Code);
        Assert.Empty(_host.Runs);
        Assert.Equal("pending", Assert.Single(_asks.Asks()).State);
    }

    [Fact]
    public async Task Gap_TheCloudLinkMayNotInstallOnLinux_EvenWithPkexec()
    {
        Offer(LinuxPackager.Dnf);
        var id = await Asked("nodejs-lts");
        Assert.NotNull(Yawble.Core.Link.RemoteAccess.Refusal("tools.install", new JsonObject { ["id"] = id }, allowHostTerminals: false));
        Assert.Equal("forbidden", (await Assert.ThrowsAsync<RpcException>(() =>
            As(Actor.CloudViewer("person@example.com"), () => _asks.InstallAsync(id, Ct)))).Code);
        Assert.Empty(_host.Runs);
    }

    [Fact]
    public async Task Gap_WhilePkexecWaitsForThePassword_CheckAgainIsBusy_AndASecondInstallIsBusy()
    {
        Offer(LinuxPackager.Dnf);
        var id = await Asked("dotnet-sdk-10");
        var other = await Asked("nodejs-lts");
        _host.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.InstallsOnRun = "dotnet-sdk-10";

        var running = Install(id);
        await _host.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal("installing", _asks.Asks().Single(a => a.Id == id).State);
        Assert.Equal("busy", (await CheckAgain(id))["result"]!.GetValue<string>());
        Assert.Equal("busy", (await Install(id))["result"]!.GetValue<string>());
        Assert.Equal("busy", (await Install(other))["result"]!.GetValue<string>());
        Assert.Single(_host.Runs); // one install at a time

        _host.Gate.SetResult();
        Assert.Equal("installed", (await running)["result"]!.GetValue<string>());
        Assert.Equal("nodejs-lts", Assert.Single(_asks.Asks()).Tool);
    }

    [Fact]
    public async Task Gap_CancelledThenNotAuthorized_KeepTheAttemptsCount_AndNoManagerIsTold()
    {
        Offer(LinuxPackager.Pacman);
        var id = await Asked("python-3");
        _host.Result = new GitResult(126, "", "");
        Assert.Equal("cancelled", (await Install(id))["result"]!.GetValue<string>());
        _host.Result = new GitResult(127, "", "");
        Assert.Equal("notAuthorized", (await Install(id))["result"]!.GetValue<string>());
        var ask = Assert.Single(_asks.Asks());
        Assert.Equal(("pending", 2), (ask.State, ask.Attempts));
        Assert.Equal(ToolAskService.NotAuthorizedMessage, ask.Error);
        Assert.Equal(["install", "checkAgain", "notNow"], (await Item())["choices"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Empty(_told);
        Assert.All(_host.Runs, r => Assert.Equal(["/usr/bin/pacman", "-S", "--needed", "--noconfirm", "python", "python-pip"], r.Args));
    }

    [Fact]
    public async Task Gap_ExitCode126WithoutPkexec_IsNotPossible_TheManualPathRunsNothing()
    {
        _host.Installer = null;
        var id = await Asked("dotnet-sdk-10");
        _host.Result = new GitResult(126, "", "");
        Assert.Equal("manual", (await Install(id))["result"]!.GetValue<string>());
        Assert.Empty(_host.Runs);
    }

    [Fact]
    public async Task Gap_HomeOnly_AfterAFailedInstall_TheErrorCarriesTheOutputAndTheHomeNote()
    {
        Offer(LinuxPackager.Dnf);
        var id = await Asked("dotnet-sdk-10");
        _host.HomeOnly = "/home/alice/.dotnet/dotnet";
        _host.Result = new GitResult(1, "", "No match for argument: dotnet-sdk-10.0");

        var r = await Install(id);

        Assert.Equal("homeOnly", r["result"]!.GetValue<string>());
        var message = r["message"]!.GetValue<string>();
        Assert.Contains("did not finish (exit code 1: No match for argument", message);
        var note = (await Item())["homeOnly"]!.GetValue<string>();
        Assert.EndsWith(note, message); // the item's note is a suffix of the error, not equal to it (see the shell gap test)
        Assert.NotEqual(note, Assert.Single(_asks.Asks()).Error);
    }

    [Fact]
    public async Task Gap_HomeOnly_IsNotOfferedOnceTheProgramIsWhereARunLooks()
    {
        _host.Installer = null;
        var id = await Asked("dotnet-sdk-10");
        _host.HomeOnly = "/home/alice/.dotnet/dotnet";
        Assert.NotNull((await Item())["homeOnly"]);
        _host.Installed.Add("dotnet-sdk-10");
        Assert.Equal("installed", (await CheckAgain(id))["result"]!.GetValue<string>());
        Assert.Empty(_asks.Asks());
    }

    // ------------------------------------------------------------------ lastResult (card 85)

    [Fact]
    public async Task TheAsk_KeepsTheLastResultCode_SoAFreshCardKnowsACancelFromAFailure()
    {
        Offer(LinuxPackager.Dnf);
        var id = await Asked("dotnet-sdk-10");
        string? Last(JsonObject item) => item["lastResult"]?.GetValue<string>();
        var first = await Item();
        Assert.True(first.ContainsKey("lastResult"));
        Assert.Null(Last(first)); // nothing tried yet

        _host.Result = new GitResult(126, "", "");
        await Install(id);
        Assert.Equal("cancelled", Last(await Item()));
        Assert.Equal(ToolAskService.CancelledMessage, (await Item())["error"]!.GetValue<string>());

        _host.Result = new GitResult(127, "", "");
        await Install(id);
        Assert.Equal("notAuthorized", Last(await Item()));

        _host.Result = new GitResult(1, "", "No match for argument: dotnet-sdk-10.0");
        await Install(id);
        Assert.Equal("failed", Last(await Item()));

        Assert.Equal("notFound", (await CheckAgain(id))["result"]!.GetValue<string>());
        Assert.Equal("notFound", Last(await Item()));

        _host.HomeOnly = "/home/alice/.dotnet/dotnet";
        await Install(id);
        Assert.Equal("homeOnly", Last(await Item()));

        // An install that is running has no result yet.
        _host.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.HomeOnly = null;
        _host.Result = new GitResult(0, "", "");
        _host.InstallsOnRun = "dotnet-sdk-10";
        var running = Install(id);
        await _host.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Null(_asks.Asks().Single(a => a.Id == id).LastResult);
        _host.Gate.SetResult();
        Assert.Equal("installed", (await running)["result"]!.GetValue<string>());
        Assert.Null(_asks.Asks(openOnly: false).Single(a => a.Id == id).LastResult);
    }

    // ------------------------------------------------------------------ lastResult gap tests (card 84 re-check, TesterWren)

    private long Scalar(string sql)
    {
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void Gap_0042_OnAFreshDatabase_AddsANullableLastResultColumn()
    {
        Assert.Contains("0042_tool_ask_last_result", SchemaMigrator.Migrate(_db.ConnectionString));
        Assert.Equal(1, Scalar("SELECT count(*) FROM pragma_table_info('tool_asks') WHERE name = 'last_result' AND \"notnull\" = 0 AND type = 'TEXT';"));
    }

    [Fact]
    public async Task Gap_0042_OnAnUpgradedDatabase_KeepsTheAsks_WithNoLastResult_AndRunsOnce()
    {
        Offer(LinuxPackager.Dnf);
        var id = await Asked("dotnet-sdk-10");
        _host.Result = new GitResult(126, "", "");
        await Install(id); // an ask with an error from before 0042
        // Wind the database back to before 0042, as an existing install is.
        Insert("ALTER TABLE tool_asks DROP COLUMN last_result; DELETE FROM schema_migrations WHERE name = '0042_tool_ask_last_result'; SELECT 1;");
        Assert.Equal(0, Scalar("SELECT count(*) FROM pragma_table_info('tool_asks') WHERE name = 'last_result';"));

        var applied = SchemaMigrator.Migrate(_db.ConnectionString);
        Assert.Contains("0042_tool_ask_last_result", applied);
        Assert.Equal(applied, SchemaMigrator.Migrate(_db.ConnectionString)); // twice: nothing happens
        Assert.Equal(1, Scalar("SELECT count(*) FROM schema_migrations WHERE name = '0042_tool_ask_last_result';"));

        var ask = Assert.Single(_asks.Asks());
        Assert.Equal((id, "pending", 1, ToolAskService.CancelledMessage), (ask.Id, ask.State, ask.Attempts, ask.Error));
        Assert.Null(ask.LastResult); // an ask from before 0042 has no code: the card shows its error as before
        var item = await Item();
        Assert.True(item.ContainsKey("lastResult"));
        Assert.Null(item["lastResult"]);

        // and the next attempt writes it again
        _host.Result = new GitResult(127, "", "");
        await Install(id);
        Assert.Equal("notAuthorized", Assert.Single(_asks.Asks()).LastResult);
    }

    [Fact]
    public async Task Gap_LastResult_ClearsOnInstallStart_EvenAfterACancel_AndOnCheckAgainFound()
    {
        Offer(LinuxPackager.Apt);
        var id = await Asked("python-3");
        await Asked("python-3", new MemberRef(_wren, _beta, "Beta", "Wren", MemberRole.Member));
        _host.Result = new GitResult(126, "", "");
        await Install(id);
        Assert.Equal("cancelled", _asks.Asks().Single(a => a.Id == id).LastResult);

        _host.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Result = new GitResult(100, "", "E: Unable to locate package python3");
        var running = Install(id);
        await _host.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var during = await As(Actor.Window, async () => (await _asks.DispatchAsync("tools.asks", null, Ct))!.AsArray());
        var mine = during.Single(n => n!["id"]!.GetValue<long>() == id)!;
        Assert.Equal("installing", mine["state"]!.GetValue<string>());
        Assert.Null(mine["lastResult"]);
        Assert.Null(mine["error"]);
        _host.Gate.SetResult();
        await running;
        Assert.Equal("failed", _asks.Asks().Single(a => a.Id == id).LastResult);

        _host.Installed.Add("python-3"); // the person installed it themselves
        Assert.Equal("installed", (await CheckAgain(id))["result"]!.GetValue<string>());
        Assert.All(_asks.Asks(openOnly: false), a => Assert.Equal(("installed", null, null), (a.State, a.Error, a.LastResult)));
    }

    [Fact]
    public void Gap_AnInstallTheAppClosedDuring_IsFailed_NotACancel()
    {
        Insert($"INSERT INTO tool_asks (at, team_id, tool, member, count, last_seen, state, attempts, last_result) " +
               $"VALUES ('x', {_alpha}, 'nodejs-lts', 'Alpha/Priya', 1, 'x', 'installing', 1, 'cancelled') RETURNING id;");

        var fresh = new ToolAskService(_db.ConnectionString, _host, (_, _, _, _, _) => Task.CompletedTask, _clock);

        var ask = Assert.Single(fresh.Asks());
        Assert.Equal(("pending", "failed"), (ask.State, ask.LastResult));
        Assert.Contains("app closed while it was installing", ask.Error);
    }

    [Fact]
    public async Task Gap_TheRemoteView_ReadsTheSameLastResult()
    {
        Offer(LinuxPackager.Dnf);
        var id = await Asked("dotnet-sdk-10");
        _host.Result = new GitResult(126, "", "");
        await Install(id);
        var remote = await As(Actor.CloudViewer("alice@example.com"), async () => (await _asks.DispatchAsync("tools.asks", null, Ct))!.AsArray().Single()!.AsObject());
        Assert.Equal("cancelled", remote["lastResult"]!.GetValue<string>());
        Assert.Equal(ToolAskService.CancelledMessage, remote["error"]!.GetValue<string>());
    }

    [Fact]
    public void CheckAgain_IsAnsweredOffTheReadLoop() =>
        Assert.Contains("tools.checkAgain", IpcServer.LongRunning);

    /// <summary>A Linux host: records what would run, never starts a process.</summary>
    private sealed class FakeLinuxHost : IToolHost
    {
        public bool IsWindows => false;

        public bool IsMac => false;

        public LinuxPackager Packager { get; set; } = LinuxPackager.Apt;

        public LinuxInstaller? Installer { get; set; }

        public LinuxInstaller? LinuxInstall => Installer;

        public string? HomeOnly { get; set; }

        public string? FoundOnlyInHome(ToolEntry tool) => Installed.Contains(tool.Name) ? null : HomeOnly;

        public HashSet<string> Installed { get; } = [];

        public string? InstallsOnRun { get; set; }

        public string[] InstalledAlso { get; set; } = [];

        public GitResult Result { get; set; } = new(0, "", "");

        public List<(string Exe, IReadOnlyList<string> Args)> Runs { get; } = [];

        public int Checks { get; private set; }

        /// <summary>When set, RunAsync waits for it (the password window still open); Started is set when it begins.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? FindWinget() => throw new InvalidOperationException("winget is never looked for on Linux");

        public Task<bool> IsInstalledAsync(ToolEntry tool, CancellationToken ct)
        {
            if (Runs.Count > 0) Checks++;
            return Task.FromResult(Installed.Contains(tool.Name));
        }

        public async Task<GitResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan limit, CancellationToken ct)
        {
            Runs.Add((exe, args.ToList()));
            Checks = 0;
            Started.TrySetResult();
            if (Gate is { } g) await g.Task.WaitAsync(ct);
            if (Result.ExitCode == 0 && InstallsOnRun is { } t)
            {
                Installed.Add(t);
                foreach (var also in InstalledAlso) Installed.Add(also);
            }
            return Result;
        }
    }
}
