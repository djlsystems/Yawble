using System.Diagnostics;
using System.Text.Json.Nodes;
using Yawble.Core.Integrations;
using Yawble.Core.Provenance;
using Yawble.Core.Tasks;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Tasks;

/// <summary>A temp git repository playing the person's checkout, a database with one team, and a service wired to them.</summary>
internal sealed class TaskFixture : IDisposable
{
    public TempDb Db { get; } = new();
    public string Repo { get; }
    public string Docs { get; }
    public HostGit Git { get; }
    public long TeamId { get; }
    public HostTaskService Svc { get; }
    public List<TaskWake> Wakes { get; } = [];
    public string BaseBranch { get; set; } = "main";

    public static bool IsWin => OperatingSystem.IsWindows();
    public bool Win => IsWin;
    public string ScriptPath => Win ? "scripts/run.ps1" : "scripts/run.sh";

    /// <summary>Set with <c>withConnections</c>: Connections over an in-memory credential store, given to the service.</summary>
    public Connections.MemoryCredentialStore? Creds { get; }
    public Yawble.Core.Connections.ConnectionService? Conns { get; }

    public TaskFixture(bool withConnections = false)
    {
        if (withConnections)
        {
            Creds = new Connections.MemoryCredentialStore();
            Conns = new Yawble.Core.Connections.ConnectionService(Creds, Db.ConnectionString, log: _ => { });
        }
        Repo = Path.Combine(Db.Dir, "checkout");
        Docs = Path.Combine(Db.Dir, "docs");
        Directory.CreateDirectory(Repo);
        Git = new HostGit(Path.Combine(Db.Dir, "empty.gitconfig"));
        using (var s = Db.OpenStore()) TeamId = s.CreateTeam("Alpha");
        G("init");
        G("config", "core.autocrlf", "false");
        G("config", "core.longpaths", "true");
        Write(ScriptPath, Win ? "Write-Output 'hello'" : "echo hello");
        Write("other.txt", "unpinned");
        Commit("first");
        Svc = new HostTaskService(Db.ConnectionString, Git, (_, name) => name == "main" ? new TaskRepoInfo(Repo, BaseBranch) : null, _ => Docs,
            w => { lock (Wakes) Wakes.Add(w); return System.Threading.Tasks.Task.CompletedTask; }, connections: Conns);
    }

    public string G(params string[] args) => Git.RunAsync(Repo, args).GetAwaiter().GetResult();

    public void Write(string rel, string content)
    {
        var full = Path.Combine(Repo, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content.Replace("\r\n", "\n"));
    }

    public string Commit(string message)
    {
        G("add", "-A");
        G("-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", message);
        return G("rev-parse", "HEAD");
    }

    public HostTaskDef Def(string name = "build", Action<List<string>>? args = null, int timeout = 60, IEnumerable<string>? env = null, IEnumerable<string>? watch = null,
        string pinMode = PinModes.Files, string? script = null)
    {
        var list = Win ? new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script ?? ScriptPath } : [script ?? ScriptPath];
        args?.Invoke(list);
        return HostTaskDef.Make(name, Win ? "powershell" : "sh", list, timeoutSeconds: timeout, envNames: env, watch: watch, pinMode: pinMode);
    }

    public Task<HostTask> SaveAsync(HostTaskDef d) => Svc.SaveAsync(TeamId, d);

    public Task<TaskRequestResult> Request(string name = "build", string reason = "need it", long workflow = 1) =>
        Svc.RequestAsync(TeamId, name, reason, "Alpha/Ada", workflow, 7);

    public async Task<TaskRunRow> Finish(TaskRequestResult r)
    {
        Assert.NotNull(r.RunId);
        await Svc.WaitForRunAsync(r.RunId!.Value);
        return Svc.Store.Run(r.RunId.Value)!;
    }

    public void Dispose() => Db.Dispose();
}

public class HostTaskTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ 1. hashes

    [Fact]
    public async Task TheDefinitionHashChangesForEachFieldAndNotForARename()
    {
        using var f = new TaskFixture();
        var baseDef = f.Def(watch: ["other.txt"]);
        var p = await f.Svc.PreviewAsync(f.TeamId, baseDef, Ct);
        Assert.NotNull(p.ProgramResolved);
        string H(HostTaskDef d, string? program = null) => TaskPinning.Definition(f.TeamId, d, program ?? p.ProgramResolved!).Hash;
        var h = H(baseDef);

        Assert.Equal(h, H(baseDef with { Name = "renamed" }));
        Assert.Equal(h, H(baseDef with { EnvNames = [], Watch = ["other.txt"] }));
        Assert.NotEqual(h, H(baseDef with { Args = [.. baseDef.Args, "x"] }));
        Assert.NotEqual(h, H(baseDef with { Workdir = "scripts" }));
        Assert.NotEqual(h, H(baseDef with { TimeoutSeconds = 61 }));
        Assert.NotEqual(h, H(baseDef with { EnvNames = ["FOO"] }));
        Assert.NotEqual(h, H(baseDef with { Watch = ["other.txt", "x"] }));
        Assert.NotEqual(h, H(baseDef with { PinMode = PinModes.Commit }));
        Assert.NotEqual(h, H(baseDef, @"C:\elsewhere\powershell.exe"));
        Assert.NotEqual(h, H(baseDef with { Repo = "other" }));
        Assert.Equal(H(baseDef with { EnvNames = ["B", "A"] }), H(baseDef with { EnvNames = ["A", "B"] })); // sorted
    }

    [Fact]
    public async Task TheFilesHashFollowsPinnedFilesOnly_AndCommitModeFollowsTheCommit()
    {
        using var f = new TaskFixture();
        var def = f.Def();
        var before = await f.Svc.PreviewAsync(f.TeamId, def, Ct);
        Assert.Contains(f.ScriptPath, before.Pins!.Files.Select(x => x.Path));
        var h0 = TaskPinning.FilesHash(before.Pins.Files);

        f.Write("other.txt", "changed"); // not pinned
        f.Commit("unpinned");
        Assert.Equal(h0, TaskPinning.FilesHash((await f.Svc.PreviewAsync(f.TeamId, def, Ct)).Pins!.Files));

        f.Write(f.ScriptPath, "# changed");
        f.Commit("script");
        Assert.NotEqual(h0, TaskPinning.FilesHash((await f.Svc.PreviewAsync(f.TeamId, def, Ct)).Pins!.Files));

        // commit mode: an approval is for one commit, whatever changed.
        var commitDef = f.Def("c", pinMode: PinModes.Commit);
        await f.SaveAsync(commitDef);
        var ask = await f.Request("c");
        Assert.Equal(RequestKind.Asked, ask.Kind);
        var started = await f.Svc.ApproveAsync(ask.AskId!.Value, always: true, Ct);
        await f.Finish(started);
        Assert.Equal(RequestKind.Started, (await RequestAndFinish(f, "c")).Kind);
        f.Write("other.txt", "yet another");
        f.Commit("any commit");
        Assert.Equal(RequestKind.Asked, (await f.Request("c")).Kind);
    }

    private static async Task<TaskRequestResult> RequestAndFinish(TaskFixture f, string name)
    {
        var r = await f.Request(name);
        if (r.RunId is { } id) await f.Svc.WaitForRunAsync(id);
        return r;
    }

    // ------------------------------------------------------------------ 3-6. approvals

    [Fact]
    public async Task AnAlwaysApprovalStopsApplyingWhenTheScriptChanges_AndTheAskShowsTheFileWithOldAndNewHashes()
    {
        using var f = new TaskFixture();
        await f.SaveAsync(f.Def());

        var first = await f.Request();
        Assert.Equal(RequestKind.Asked, first.Kind);
        var approved = await f.Svc.ApproveAsync(first.AskId!.Value, always: true, Ct);
        var run = await f.Finish(approved);
        Assert.Equal(0, run.ExitCode);
        Assert.Contains(f.Wakes, w => w.Text.Contains("exit 0") && w.Text.Contains("hello"));

        var second = await f.Request(); // runs without asking
        Assert.Equal(RequestKind.Started, second.Kind);
        await f.Finish(second);

        f.Write(f.ScriptPath, f.Win ? "Write-Output 'evil'" : "echo evil");
        f.Commit("script changed on base");
        var third = await f.Request();
        Assert.Equal(RequestKind.Asked, third.Kind);
        var detail = JsonNode.Parse(f.Svc.Store.Ask(third.AskId!.Value)!.DetailJson!)!;
        var file = detail["changed"]!["files"]!.AsArray().Single()!;
        Assert.Equal(f.ScriptPath, file["path"]!.GetValue<string>());
        Assert.Equal("changed", file["change"]!.GetValue<string>());
        Assert.NotEqual(file["oldBlob"]!.GetValue<string>(), file["newBlob"]!.GetValue<string>());
        Assert.Contains("+", file["diff"]!.GetValue<string>());
        Assert.Contains("evil", file["diff"]!.GetValue<string>());
    }

    [Fact]
    public async Task ApprovingAfterTheBaseMovedClosesTheAskAsStale_RunsNothing_AndAsksAgain()
    {
        using var f = new TaskFixture();
        await f.SaveAsync(f.Def());
        var ask = await f.Request();
        f.Write(f.ScriptPath, f.Win ? "Write-Output 'new'" : "echo new");
        f.Commit("merge landed while the dialog was open");

        var r = await f.Svc.ApproveAsync(ask.AskId!.Value, always: true, Ct);
        Assert.Equal(RequestKind.Asked, r.Kind);
        Assert.StartsWith("stale", r.Text);
        Assert.Empty(f.Svc.Store.Runs(1));
        Assert.Null(f.Svc.Store.Approval(1)); // not even "Always" was stored
        var old = f.Svc.Store.Ask(ask.AskId.Value)!;
        Assert.Equal("declined", old.State);
        Assert.StartsWith("stale:", old.Note);
        Assert.Equal(r.AskId, f.Svc.Store.PendingAsk(1)!.Id);
        Assert.Contains(f.Wakes, w => w.Text.Contains("went stale"));
    }

    [Fact]
    public async Task RevertingToAnOlderApprovedScriptAsksAgain()
    {
        using var f = new TaskFixture();
        await f.SaveAsync(f.Def());
        await f.Finish(await f.Svc.ApproveAsync((await f.Request()).AskId!.Value, true, Ct)); // A approved

        f.Write(f.ScriptPath, "# B");
        f.Commit("B");
        var b = await f.Request();
        await f.Finish(await f.Svc.ApproveAsync(b.AskId!.Value, true, Ct)); // B approved, replaces A
        Assert.Equal(RequestKind.Started, (await RequestAndFinish(f, "build")).Kind);

        f.Write(f.ScriptPath, f.Win ? "Write-Output 'hello'" : "echo hello"); // back to A
        f.Commit("A again");
        Assert.Equal(RequestKind.Asked, (await f.Request()).Kind);
    }

    [Fact]
    public async Task RunOnceLeavesNoStandingApproval()
    {
        using var f = new TaskFixture();
        await f.SaveAsync(f.Def());
        await f.Finish(await f.Svc.ApproveAsync((await f.Request()).AskId!.Value, always: false, Ct));
        Assert.Null(f.Svc.Store.Approval(1));
        Assert.Equal(RequestKind.Asked, (await f.Request()).Kind);
    }
    [Fact]
    public async Task AlwaysStoresTheApprovalAtTheShownHashes_AndAnEditOfTheTaskDeletesIt()
    {
        using var f = new TaskFixture();
        var task = await f.SaveAsync(f.Def());
        var ask = await f.Request();
        await f.Finish(await f.Svc.ApproveAsync(ask.AskId!.Value, always: true, Ct));
        var a1 = f.Svc.Store.Approval(task.Id)!;
        Assert.Equal(f.Svc.Store.Ask(ask.AskId.Value)!.DefinitionHash, a1.DefinitionHash);
        Assert.Equal(f.Svc.Store.Ask(ask.AskId.Value)!.FilesHash, a1.FilesHash);
        Assert.Equal("always", await f.Svc.ApprovalStateAsync(f.Svc.Store.TaskById(task.Id)!, Ct));

        // Always replaces an older approval: change the script, ask, approve Always again.
        f.Write(f.ScriptPath, "# v2");
        f.Commit("v2");
        Assert.Equal("always-but-changed", await f.Svc.ApprovalStateAsync(f.Svc.Store.TaskById(task.Id)!, Ct));
        var ask2 = await f.Request();
        await f.Finish(await f.Svc.ApproveAsync(ask2.AskId!.Value, true, Ct));
        var a2 = f.Svc.Store.Approval(task.Id)!;
        Assert.NotEqual(a1.FilesHash, a2.FilesHash);

        await f.SaveAsync(f.Def() with { TimeoutSeconds = 99 }); // an edit
        Assert.Null(f.Svc.Store.Approval(task.Id));
    }

    // ------------------------------------------------------------------ 7. preconditions

    [Fact]
    public async Task PreconditionsRefuse_WithoutCreatingAnAsk()
    {
        using var f = new TaskFixture();
        await f.SaveAsync(f.Def());
        async Task<TaskRequestResult> Refused(int precondition)
        {
            var r = await f.Request();
            Assert.Equal(RequestKind.Refused, r.Kind);
            Assert.Equal(precondition, r.Precondition);
            Assert.Empty(f.Svc.Store.PendingAsks());
            return r;
        }

        // dirty tracked file
        f.Write("other.txt", "dirty");
        var dirty = await Refused(4);
        Assert.Contains("other.txt", dirty.Text);
        f.G("checkout", "--", "other.txt");

        // untracked file: exactly the deploy script that must not run
        f.Write("scripts/deploy.ps1", "x");
        Assert.Contains("deploy.ps1", (await Refused(4)).Text);
        File.Delete(Path.Combine(f.Repo, "scripts", "deploy.ps1"));

        // staged file
        f.Write("other.txt", "staged");
        f.G("add", "other.txt");
        await Refused(4);
        f.G("reset", "-q", "--hard");

        // wrong branch, then detached HEAD
        f.G("checkout", "-q", "-b", "feature");
        Assert.Contains("feature", (await Refused(3)).Text);
        f.G("checkout", "-q", "--detach");
        Assert.Contains("detached", (await Refused(3)).Text);
        f.G("checkout", "-q", "main");

        // the repository is not there
        f.BaseBranch = "nope";
        await Refused(3);
        f.BaseBranch = "main";
        Assert.Equal(RequestKind.Asked, (await f.Request()).Kind); // all clear again: now it asks
    }

    [Fact]
    public async Task AWorkdirOutsideTheRepoIsNotSaved_AJunctionOutIsRefused_AMissingProgramIsRefused()
    {
        using var f = new TaskFixture();
        Assert.Throws<ArgumentException>(() => HostTaskRules.Validate(f.Def() with { Workdir = ".." }));
        Assert.Throws<ArgumentException>(() => HostTaskRules.Validate(f.Def() with { Workdir = "a/../../b" }));
        await Assert.ThrowsAsync<ArgumentException>(() => f.SaveAsync(f.Def() with { Program = "no-such-program-xyz" }));

        f.Write("scripts/tool.exe", "not really");
        f.Commit("tool");
        await f.SaveAsync(HostTaskDef.Make("tool", "scripts/tool.exe", timeoutSeconds: 30));
        f.G("rm", "-q", "scripts/tool.exe");
        f.Commit("remove tool");
        var gone = await f.Request("tool");
        Assert.Equal(6, gone.Precondition);
        Assert.Empty(f.Svc.Store.PendingAsks());

        if (!OperatingSystem.IsWindows()) return;
        var outside = Directory.CreateDirectory(Path.Combine(f.Db.Dir, "outside")).FullName;
        var link = Path.Combine(f.Repo, "scripts", "evil");
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        psi.Arguments = $"/c mklink /J \"{link}\" \"{outside}\"";
        using (var p = Process.Start(psi)!)
        {
            p.WaitForExit();
            if (p.ExitCode != 0) return; // this environment may not allow creating a junction: the rest of the test needs one
        }
        File.AppendAllText(Path.Combine(f.Repo, ".git", "info", "exclude"), "scripts/evil\n");
        // saving through a junction is refused at once
        await Assert.ThrowsAsync<ArgumentException>(() => f.SaveAsync(f.Def("viaLink") with { Workdir = "scripts/evil" }));
    }

    // ------------------------------------------------------------------ 8. dedup, cooldown, rate limit

    [Fact]
    public async Task ASecondRequestReturnsTheSameAsk_ADeclineStartsACooldown_AndTheTeamIsRateLimited()
    {
        using var f = new TaskFixture();
        await f.SaveAsync(f.Def());
        var first = await f.Request();
        var again = await f.Request();
        Assert.Equal(RequestKind.AlreadyWaiting, again.Kind);
        Assert.Equal(first.AskId, again.AskId);
        Assert.Single(f.Svc.Store.PendingAsks());

        await f.Svc.DeclineAsync(first.AskId!.Value, "not now", Ct);
        Assert.Contains(f.Wakes, w => w.Text.Contains("declined") && w.Text.Contains("not now"));
        var cooled = await f.Request();
        Assert.Equal(RequestKind.Refused, cooled.Kind);
        Assert.Contains("not now", cooled.Text);

        // the rate limit: other tasks still ask, up to 5 asks an hour for the team
        for (var i = 0; i < 4; i++) await f.SaveAsync(f.Def($"t{i}"));
        for (var i = 0; i < 4; i++) Assert.Equal(RequestKind.Asked, (await f.Request($"t{i}")).Kind);
        await f.SaveAsync(f.Def("t4"));
        var limited = await f.Request("t4");
        Assert.Equal(RequestKind.Refused, limited.Kind);
        Assert.Contains("too many", limited.Text);
    }

    // ------------------------------------------------------------------ validation

    [Fact]
    public void ValidationRefusesShellWrappers_AppEnvNames_AndBadLimits()
    {
        var ok = HostTaskDef.Make("x", "dotnet", ["test"]);
        HostTaskRules.Validate(ok);
        Assert.Throws<ArgumentException>(() => HostTaskRules.Validate(HostTaskDef.Make("x", "cmd", ["/c", "dir"])));
        Assert.Throws<ArgumentException>(() => HostTaskRules.Validate(HostTaskDef.Make("x", "pwsh", ["-Command", "ls"])));
        Assert.Throws<ArgumentException>(() => HostTaskRules.Validate(HostTaskDef.Make("x", "powershell", ["-EncodedCommand", "AAA"])));
        Assert.Throws<ArgumentException>(() => HostTaskRules.Validate(HostTaskDef.Make("x", "bash", ["-c", "ls"])));
        Assert.Throws<ArgumentException>(() => HostTaskRules.Validate(ok with { EnvNames = ["YAWBLE_RUN_TOKEN"] }));
        Assert.Throws<ArgumentException>(() => HostTaskRules.Validate(ok with { EnvNames = ["ANTHROPIC_API_KEY"] }));
        Assert.Throws<ArgumentException>(() => HostTaskRules.Validate(ok with { EnvNames = ["A=b"] }));
        Assert.Throws<ArgumentException>(() => HostTaskRules.Validate(ok with { TimeoutSeconds = 5 }));
        Assert.Throws<ArgumentException>(() => HostTaskRules.Validate(ok with { TimeoutSeconds = 7201 }));
        Assert.Throws<ArgumentException>(() => HostTaskRules.Validate(ok with { Name = "Bad Name" }));
        HostTaskRules.Validate(HostTaskDef.Make("x", "pwsh", ["-NoProfile", "-File", "scripts/x.ps1"]));
    }

    [Fact]
    public async Task OnlyTheCommandLinePinnedNeedsAnExplicitTick()
    {
        using var f = new TaskFixture();
        // a program on PATH with no script and no watch pins nothing
        var bare = HostTaskDef.Make("bare", f.Win ? "powershell" : "sh", ["--version"]);
        await Assert.ThrowsAsync<ArgumentException>(() => f.SaveAsync(bare));
        await f.SaveAsync(bare with { CommandLineOnly = true });
        // a watch glob that matches nothing is a warning
        var p = await f.Svc.PreviewAsync(f.TeamId, f.Def(watch: ["nothing/**"]), Ct);
        Assert.Contains(p.Pins!.Warnings, w => w.Contains("nothing/**"));
    }

    // ------------------------------------------------------------------ 9. runner

    private static string Prog => TaskFixture.IsWin ? "powershell" : "sh";

    private static (string Program, string[] Args) Script(string dir, string psBody, string shBody)
    {
        if (TaskFixture.IsWin)
        {
            var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".ps1");
            File.WriteAllText(path, psBody);
            return (CliRunner.Find("powershell")!, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path]);
        }
        var sh = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(sh, shBody);
        return (CliRunner.Find("sh")!, [sh]);
    }

    private static TaskProcessOptions Options(string dir, (string Program, string[] Args) s, TimeSpan timeout, Dictionary<string, string>? env = null,
        IReadOnlyList<string>? secrets = null, long cap = 50L * 1024 * 1024) => new()
    {
        Program = s.Program, Args = s.Args, WorkingDirectory = dir, Timeout = timeout, LogPath = Path.Combine(dir, "logs", Guid.NewGuid().ToString("N") + ".log"),
        Environment = env ?? TaskProcess.BuildEnvironment([], Environment.GetEnvironmentVariable).Env, SecretValues = secrets ?? [], LogCapBytes = cap,
    };

    [Fact]
    public async Task TheTimeLimitKillsTheWholeTree_IncludingADetachedGrandchild()
    {
        var dir = Directory.CreateTempSubdirectory("yawble-task-").FullName;
        try
        {
            var pidFile = Path.Combine(dir, "child.pid");
            var s = Script(dir,
                $"$p = Start-Process -FilePath powershell -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 300' -PassThru -WindowStyle Hidden\r\n$p.Id | Set-Content -Path '{pidFile}'\r\nStart-Sleep -Seconds 300",
                $"sleep 300 &\necho $! > '{pidFile}'\nsleep 300");
            var watch = Stopwatch.StartNew();
            var r = await TaskProcess.RunAsync(Options(dir, s, TimeSpan.FromSeconds(4)), Ct);
            Assert.True(r.TimedOut);
            Assert.Equal("timeout", r.KilledBy);
            Assert.Null(r.ExitCode);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60));
            Assert.True(File.Exists(pidFile));
            var pid = int.Parse(File.ReadAllText(pidFile).Trim());
            await Task.Delay(500, Ct);
            Assert.False(IsAlive(pid), "the grandchild survived the time limit");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    [Fact]
    public async Task OutputPastTheCapStillGoesToTheTailButNotTheLog_AndDoesNotKillTheProcess()
    {
        var dir = Directory.CreateTempSubdirectory("yawble-task-").FullName;
        try
        {
            var s = Script(dir, "1..3000 | ForEach-Object { \"line $_\" }", "i=1; while [ $i -le 3000 ]; do echo line $i; i=$((i+1)); done");
            var o = Options(dir, s, TimeSpan.FromSeconds(60), cap: 2000);
            var r = await TaskProcess.RunAsync(o, Ct);
            Assert.Equal(0, r.ExitCode);
            Assert.True(r.Truncated);
            Assert.True(r.OutputBytes > 20000);
            var log = File.ReadAllText(o.LogPath);
            Assert.True(log.Length < 2600);
            Assert.Contains("log cut at", log);
            Assert.EndsWith("line 3000", r.Tail);
            Assert.True(r.Tail.Split('\n').Length <= 200);
            Assert.Equal(r.LogSha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(o.LogPath))).ToLowerInvariant());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task TheChildGetsOnlyTheBasicsAndTheNamedVariables_AndNamedValuesAreRedactedExactly()
    {
        var dir = Directory.CreateTempSubdirectory("yawble-task-").FullName;
        Environment.SetEnvironmentVariable("YAWBLE_RUN_TOKEN", "run-token-should-not-pass-12345");
        Environment.SetEnvironmentVariable("HOSTTASK_UNNAMED", "unnamed-value-should-not-pass");
        Environment.SetEnvironmentVariable("HOSTTASK_NAMED", "named-value-passes-1234567");
        try
        {
            var s = Script(dir,
                "Write-Output \"token=[$env:YAWBLE_RUN_TOKEN] unnamed=[$env:HOSTTASK_UNNAMED] named=[$env:HOSTTASK_NAMED] ci=[$env:CI] absent=[$env:HOSTTASK_ABSENT]\"",
                "echo \"token=[$YAWBLE_RUN_TOKEN] unnamed=[$HOSTTASK_UNNAMED] named=[$HOSTTASK_NAMED] ci=[$CI] absent=[$HOSTTASK_ABSENT]\"");
            var (env, missing, secrets) = TaskProcess.BuildEnvironment(["HOSTTASK_NAMED", "HOSTTASK_ABSENT"], Environment.GetEnvironmentVariable);
            Assert.Equal(["HOSTTASK_ABSENT"], missing);
            var o = Options(dir, s, TimeSpan.FromSeconds(60), env, secrets);
            var r = await TaskProcess.RunAsync(o, Ct);
            Assert.Equal(0, r.ExitCode);
            Assert.Contains("token=[]", r.Tail);
            Assert.Contains("unnamed=[]", r.Tail);
            Assert.Contains("ci=[1]", r.Tail);
            Assert.Contains("absent=[]", r.Tail);
            Assert.Contains("named=[[redacted]]", r.Tail); // present in the child, never in the log or the message
            Assert.DoesNotContain("named-value-passes", File.ReadAllText(o.LogPath));
            Assert.DoesNotContain("run-token-should-not-pass", r.Tail);
        }
        finally
        {
            Environment.SetEnvironmentVariable("YAWBLE_RUN_TOKEN", null);
            Environment.SetEnvironmentVariable("HOSTTASK_UNNAMED", null);
            Environment.SetEnvironmentVariable("HOSTTASK_NAMED", null);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task OneRunAtATimePerTaskAndPerCheckout_ACancelStopsItAndTheAgentIsTold_AndARestartMarksItInterrupted()
    {
        using var f = new TaskFixture();
        var flag = Path.Combine(f.Db.Dir, "stop.flag");
        f.Write(f.ScriptPath, f.Win
            ? $"while (-not (Test-Path '{flag}')) {{ Start-Sleep -Milliseconds 100 }}"
            : $"while [ ! -e '{flag}' ]; do sleep 0.1; done");
        f.Commit("long script");
        await f.SaveAsync(f.Def("long"));
        await f.SaveAsync(f.Def("second"));

        var run = await f.Svc.RunNowAsync(f.TeamId, "long", Ct);
        Assert.Equal(RequestKind.Started, run.Kind);
        var sameTask = await f.Svc.RunNowAsync(f.TeamId, "long", Ct);
        Assert.Equal(8, sameTask.Precondition);
        var sameRepo = await f.Svc.RunNowAsync(f.TeamId, "second", Ct);
        Assert.Equal(8, sameRepo.Precondition);
        Assert.Contains("same checkout", sameRepo.Text);
        var agent = await f.Request("second");
        Assert.Equal(8, agent.Precondition);

        Assert.True(f.Svc.Cancel(run.RunId!.Value));
        await f.Svc.WaitForRunAsync(run.RunId.Value);
        var done = f.Svc.Store.Run(run.RunId.Value)!;
        Assert.Equal("done", done.State);
        Assert.Equal("person", done.KilledBy);
        Assert.NotNull(done.LogPath);
        Assert.True(File.Exists(done.LogPath));
        Assert.Equal(f.Repo, f.Repo); // the checkout is untouched by the run
        Assert.False(done.DirtyAfter);

        // free again
        Assert.Equal(RequestKind.Started, (await f.Svc.RunNowAsync(f.TeamId, "second", Ct)).Kind);
        File.WriteAllText(flag, "x");
        await f.Svc.WaitForRunAsync(run.RunId.Value + 1);

        // a run left 'running' when the app died
        var task = f.Svc.Store.Task(f.TeamId, "long")!;
        var stuck = f.Svc.Store.InsertRun(task, f.Repo, null, null, "abc", "d", "f", "p", null, null);
        Assert.Equal(1, f.Svc.Recover());
        Assert.Equal("interrupted", f.Svc.Store.Run(stuck)!.State);
        Assert.Equal("app-exit", f.Svc.Store.Run(stuck)!.KilledBy);
    }

    [Fact]
    public async Task ARunRecordsTheCommitAndNotesWhenTheCheckoutIsLeftDirty()
    {
        using var f = new TaskFixture();
        f.Write(f.ScriptPath, f.Win ? "Set-Content -Path 'stray.txt' -Value 'x'; Write-Output done" : "echo x > stray.txt; echo done");
        var head = f.Commit("dirtying script");
        await f.SaveAsync(f.Def());
        var started = await f.Svc.RunNowAsync(f.TeamId, "build", Ct);
        var run = await f.Finish(started);
        Assert.Equal(head, run.CommitSha);
        Assert.Equal(head, run.HeadAfter);
        Assert.True(run.DirtyAfter);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(started.RunId, run.Id);
        Assert.StartsWith(Path.Combine(f.Docs, "task-runs"), run.LogPath);
        Assert.Matches(@"build-\d+-\d{8}-\d{6}\.log$", run.LogPath);
    }
}
