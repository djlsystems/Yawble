using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Companion;
using Yawble.Core.Concierge;
using Yawble.Core.Ipc;
using Yawble.Core.Provenance;
using Yawble.Core.Storage;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Companion;

/// <summary>
/// Companion task 3: the CompanionService. Levels over the Concierge's own tools, approval requests the window decides, and
/// the executor that runs exactly what was approved. Fakes for the app's calls (ui/route, board, gh); git runs for real but
/// only on temp repositories with an empty global config and no system config. No other program is ever run.
/// </summary>
public class CompanionServiceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("yawble-csvc-").FullName;
    private readonly TempDb _db = new();
    private string TestGitConfig => Path.Combine(_root, "test.gitconfig");
    private string Origin => Path.Combine(_root, "origin.git");
    private string Work => Path.Combine(_root, "work");

    public CompanionServiceTests() => File.WriteAllText(TestGitConfig, "");

    public void Dispose()
    {
        _db.Dispose();
        try { TestFiles.DeleteTree(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // ---------------------------------------------------------------- rig

    private sealed class Rig
    {
        public required BusEngine Bus;
        public required ConciergeStore Store;
        public required CompanionClientStore Clients;
        public required AuditLog Audit;
        public required AppEvents Events;
        public required ManualClock Clock;
        public required ConciergeGit Git;
        public ActionRepo? Repo;
        public CompanionService Service = null!;
        public List<(string Method, JsonObject? P, Actor? Actor)> Ui { get; } = [];
        public List<(string Method, JsonObject? P)> Board { get; } = [];
        public JsonObject Env = new() { ["KEEP"] = "s3cret-old" };
        public JsonObject GitStatus = JsonNode.Parse("""
            {"repoPath":"x","managed":true,"baseBranch":"main","teamBranch":"team/alpha","teamSha":"abc","teamAheadOfBase":2,"teamBehindBase":0,
             "merge":"fast-forward","conflictFiles":[],"origin":null}
            """)!.AsObject();
        /// <summary>When set, what git.merge answers (else a fast-forward).</summary>
        public JsonObject? GitMerge;
        public readonly Dictionary<string, string> Keys = [];
        /// <summary>Runs first on every routed call (a test throws or blocks here).</summary>
        public Func<string, JsonObject?, Task>? OnRoute;
        /// <summary>Runs each time the service makes its Concierge actions (a plan, or the start of an approved run).</summary>
        public Action? OnActions;
        /// <summary>The token the last routed call was given.</summary>
        public CancellationToken LastRouteToken;

        public IEnumerable<string> Methods => Ui.Select(u => u.Method);

        public async Task<JsonNode?> RouteAsync(string method, JsonObject? p, CancellationToken ct)
        {
            lock (Ui) Ui.Add((method, p?.DeepClone().AsObject(), Actor.Current));
            LastRouteToken = ct;
            if (OnRoute is { } hook) await hook(method, p);
            switch (method)
            {
                case "bus.tellManager":
                {
                    var m = await Bus.PersonTellAsync("alpha", "Manager", p!["text"]!.GetValue<string>(), null, ct);
                    return new JsonObject { ["seq"] = m.Seq, ["workflow"] = m.CorrelationId };
                }
                case "teams.env": return Env.DeepClone();
                case "docs.read": return new JsonObject { ["kind"] = "text", ["text"] = "Release notes: commit 0123456789abcdef shipped the login form." };
                case "link.status": return new JsonObject { ["state"] = "connected", ["account"] = "person@example.test" };
                case "git.status": return GitStatus.DeepClone();
                case "git.merge": return GitMerge?.DeepClone() ?? new JsonObject { ["outcome"] = "fast-forward", ["message"] = "Fast-forwarded main to team/alpha.", ["changed"] = true, ["sha"] = "abc" };
                case "ops.backups":
                    return JsonNode.Parse("""[{"name":"daily-20261110.db","size":4096,"reason":"daily","time":"2026-11-10T03:00:00Z"}]""");
                case "ops.restore":
                    // As BackupRpc: only the person, or an external call the person approved.
                    if (BackupRpc.RestoreRefusal(Actor.Current) is { } refusal) throw new RpcException("forbidden", refusal);
                    return new JsonObject { ["staged"] = new JsonObject { ["name"] = p!["backup"]!.GetValue<string>() }, ["restarting"] = true };
                default: return new JsonObject();
            }
        }

        public ConciergeActions Actions(ConciergeTurn turn, ConfirmationBook book)
        {
            OnActions?.Invoke();
            return MakeActions(turn, book);
        }

        private ConciergeActions MakeActions(ConciergeTurn turn, ConfirmationBook book) => new(Bus, RouteAsync,
            (m, p, _) => { lock (Board) Board.Add((m, p?.DeepClone().AsObject())); return Task.FromResult<JsonNode?>(new JsonObject { ["label"] = "B006" }); },
            Git, (_, _) => Task.FromResult<IReadOnlyList<ActionRepo>>(Repo is null ? [] : [Repo]), book,
            (_, a, _) => Task.FromResult(new GitResult(0, a[1] == "list" ? "[]" : "https://example.test/pr/7", "")), Audit, turn);

        public CompanionClient Client(string name, CompanionLevel level)
        {
            var c = Clients.Add(name, level, "tester", out var key);
            Keys[name] = key;
            return c;
        }

        /// <summary>The window's decision as it answers (an approval answers Running at once; the run goes on by itself).</summary>
        public async Task<ActionStatus> Decide(string id, bool approve = true)
        {
            using (Actor.Use(Actor.Window)) return await Service.DecideActionAsync(id, approve, Ct);
        }

        /// <summary>The window's decision and, when it started a run, how that run ended.</summary>
        public async Task<ActionStatus> Approve(string id, bool approve = true)
        {
            var s = await Decide(id, approve);
            return s.State == ActionState.Running && Service.RunOf(id) is { } run ? await run.WaitAsync(TimeSpan.FromSeconds(30), Ct) : s;
        }
    }

    private async Task<Rig> MakeRigAsync(bool git = false, TeamRepos? repos = null)
    {
        var bus = new BusEngine(_db.OpenStore(), new UnavailableRunner(), new BusOptions { IdleOffer = false, Repos = repos });
        await bus.StartAsync(Ct);
        await bus.CreateTeamAsync("alpha", "Manager", "claude", null, Ct);
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero));
        var rig = new Rig
        {
            Bus = bus,
            Store = new ConciergeStore(_db.ConnectionString),
            Clients = new CompanionClientStore(_db.ConnectionString, clock),
            Audit = new AuditLog(_db.ConnectionString, clock),
            Events = new AppEvents(),
            Clock = clock,
            Git = new ConciergeGit(new HostGit(Path.Combine(_root, "empty.gitconfig")), new PersonGitPush(globalConfig: TestGitConfig)),
        };
        if (git)
        {
            Run(_root, "init", "--bare", "-b", "main", Origin);
            Run(_root, "clone", Origin, Work);
            Config(Work);
            Commit(Work, "base.txt", "base");
            Run(Work, "push", "origin", "HEAD:main");
            rig.Repo = new ActionRepo("main", Work, "main", []);
        }
        rig.Service = new CompanionService(rig.Bus, rig.Store, rig.RouteAsync, rig.Actions, rig.Clients, rig.Audit, rig.Events, clock);
        return rig;
    }

    private static string RequestPrefix(string id) => $"NOT DONE: waiting for the person's approval in the Yawble window (request {id}): ";

    private static readonly System.Text.RegularExpressions.Regex AnyToken = new("confirm=|\\b[0-9a-f]{16}\\b");

    private static List<AuditLog.Row> AuditRows(Rig rig) => [.. rig.Audit.Recent(500)];

    /// <summary>Every text value of every row of every table, for a "never stored anywhere" check.</summary>
    private List<string> EveryStoredText()
    {
        var all = new List<string>();
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        var tables = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
            using var r = cmd.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));
        }
        foreach (var t in tables)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{t}\";";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                for (var i = 0; i < r.FieldCount; i++)
                    if (!r.IsDBNull(i) && r.GetValue(i) is string s) all.Add($"{t}: {s}");
        }
        return all;
    }

    // ---------------------------------------------------------------- git helpers (temp repos only, hermetic)

    private string Run(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList()) psi.Environment.Remove(key);
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = TestGitConfig;
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {e}");
        return o.Trim();
    }

    private void Config(string dir) { Run(dir, "config", "user.name", "T"); Run(dir, "config", "user.email", "t@example.test"); Run(dir, "config", "commit.gpgsign", "false"); }

    private string Commit(string dir, string file, string text)
    {
        File.WriteAllText(Path.Combine(dir, file), text);
        Run(dir, "add", "-A");
        Run(dir, "commit", "-m", text);
        return Run(dir, "rev-parse", "HEAD");
    }

    private string Branch(string name, bool merged)
    {
        Run(Work, "checkout", "-q", "-b", name, "main");
        var sha = Commit(Work, name.Replace('/', '-') + ".txt", name);
        Run(Work, "checkout", "-q", "main");
        if (merged) Run(Work, "merge", "--no-ff", "-q", "-m", "merge " + name, name);
        return sha;
    }

    private bool Has(string branch) => Run(Work, "branch", "--list", branch).Length > 0;

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(d.Replace(from, to));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(file, file.Replace(from, to), true);
    }

    // ---------------------------------------------------------------- levels and definitions

    private static HashSet<string> Names(JsonArray defs) => [.. defs.Select(d => d!["name"]!.GetValue<string>())];

    [Fact]
    public void DefinitionsPerLevel_ReadHasOnlyReads_ActAddsActions_DestructiveAddsTheRest_NoConfirmPropertyAnywhere_NoNotesOrWatches()
    {
        var read = CompanionTools.Definitions(CompanionLevel.Read);
        var act = CompanionTools.Definitions(CompanionLevel.Act);
        var destructive = CompanionTools.Definitions(CompanionLevel.Destructive);

        Assert.Equal(CompanionTools.Read.Order().ToList(), Names(read).Order().ToList());
        Assert.Equal(CompanionTools.Read.Concat(CompanionTools.Act).Order().ToList(), Names(act).Order().ToList());
        Assert.Equal(CompanionTools.Read.Concat(CompanionTools.Act).Concat(CompanionTools.Destructive).Order().ToList(), Names(destructive).Order().ToList());
        Assert.Contains("team_git", Names(read));
        Assert.Contains("backups_list", Names(read));
        Assert.Contains("request_status", Names(read));
        Assert.DoesNotContain("board_move", Names(read));
        Assert.Contains("board_move", Names(act));
        Assert.DoesNotContain("git_prune", Names(act));
        Assert.Contains("git_merge_team", Names(destructive));
        Assert.Contains("backup_restore", Names(destructive));
        Assert.Contains("backup_now", Names(destructive));
        Assert.Contains("team_create", Names(destructive));

        foreach (var excluded in new[] { "notes_read", "notes_write", "watch", "watch_list", "unwatch", "team_current" })
        {
            Assert.DoesNotContain(excluded, Names(destructive));
            Assert.Null(CompanionTools.LevelOf(excluded, []));
        }
        Assert.Null(CompanionTools.LevelOf("no_such_tool", []));

        foreach (var def in destructive)
        {
            var schema = def!["inputSchema"]!.AsObject();
            Assert.False(schema["properties"]!.AsObject().ContainsKey("confirm"), def["name"] + " offers confirm");
            Assert.DoesNotContain(schema["required"]!.AsArray(), r => r!.GetValue<string>() == "confirm");
            var name = def["name"]!.GetValue<string>();
            if (CompanionTools.Destructive.Contains(name))
                Assert.Contains("needs the person's approval in the Yawble window", def["description"]!.GetValue<string>());
        }

        // backlog_dispatch is act for an existing team; with new_team it makes a team, so it is destructive.
        Assert.Equal(CompanionLevel.Act, CompanionTools.LevelOf("backlog_dispatch", new JsonObject { ["item"] = "B006", ["team"] = "alpha" }));
        Assert.Equal(CompanionLevel.Destructive, CompanionTools.LevelOf("backlog_dispatch", new JsonObject { ["item"] = "B006", ["new_team"] = "beta" }));
        var actDispatch = act.Single(d => d!["name"]!.GetValue<string>() == "backlog_dispatch")!["inputSchema"]!["properties"]!.AsObject();
        Assert.False(actDispatch.ContainsKey("new_team"));
        var fullDispatch = destructive.Single(d => d!["name"]!.GetValue<string>() == "backlog_dispatch")!["inputSchema"]!["properties"]!.AsObject();
        Assert.True(fullDispatch.ContainsKey("new_team"));

        Assert.Equal(CompanionLevel.Read, CompanionTools.LevelOf("team_list", []));
        Assert.Equal(CompanionLevel.Act, CompanionTools.LevelOf("tell", []));
        Assert.Equal(CompanionLevel.Destructive, CompanionTools.LevelOf("git_merge_team", []));
        Assert.Equal(["git_branches_delete", "git_prune", "git_pr_merge", "team_archive", "team_delete"], CompanionTools.SelfConfirming);
    }

    [Fact]
    public async Task AReadClientIsRefusedAnActTool_AndNothingRuns()
    {
        var rig = await MakeRigAsync();
        var reader = rig.Client("reader", CompanionLevel.Read);

        var refused = await rig.Service.CallAsync(reader, "board_move", new JsonObject { ["card"] = 38, ["lane"] = "done" }, Ct);
        Assert.True(refused.IsError);
        Assert.Equal("refused: board_move needs the act level; this client was paired for read", refused.Text);
        Assert.Null(refused.RequestId);
        Assert.Empty(rig.Board);

        var prune = await rig.Service.CallAsync(reader, "git_prune", new JsonObject { ["team"] = "alpha" }, Ct);
        Assert.Equal("refused: git_prune needs the destructive level; this client was paired for read", prune.Text);
        Assert.True((await rig.Service.CallAsync(reader, "notes_write", new JsonObject { ["text"] = "x" }, Ct)).IsError);
        Assert.Equal("", rig.Store.Notes() ?? "");

        // An act client may dispatch to an existing team, but not make a new one.
        var actor = rig.Client("actor", CompanionLevel.Act);
        var fresh = await rig.Service.CallAsync(actor, "backlog_dispatch", new JsonObject { ["item"] = "B006", ["new_team"] = "beta" }, Ct);
        Assert.Equal("refused: backlog_dispatch needs the destructive level; this client was paired for act", fresh.Text);
        Assert.Empty(rig.Board);
        Assert.Equal(0, rig.Service.PendingCount);

        var row = Assert.Single(AuditRows(rig), r => r.Method == "companion.board_move");
        Assert.Equal(Actor.External, row.Via);
        Assert.Equal("reader", row.Client);
        Assert.StartsWith("refused", row.Outcome);
    }

    [Fact]
    public async Task ActCallsRunAsTheExternalClient()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Act);

        var told = await rig.Service.CallAsync(cc, "tell", new JsonObject { ["team"] = "alpha", ["instruction"] = "Add a README" }, Ct);
        Assert.False(told.IsError, told.Text);
        Assert.StartsWith("ok: told the Manager of alpha", told.Text);
        var message = Assert.Single(await rig.Bus.RecentTeamMessagesAsync("alpha", 10, Ct), m => m.Source == MessageTypes.PersonSource);
        var provenance = _db.OpenStore().ProvenanceOf(message.Seq)!;
        Assert.Equal(Actor.External, provenance.Via);
        Assert.Equal(Actor.LocalUser, provenance.User);
        var call = Assert.Single(rig.Ui, u => u.Method == "bus.tellManager");
        Assert.Equal(Actor.External, call.Actor!.Via);
        Assert.Equal("cc", call.Actor.Client);
        Assert.Null(call.Actor.ApprovedBy);
        // No Concierge watch is made for an external client (it has no chat to post updates in).
        Assert.Empty(rig.Store.ActiveWatches());

        var moved = await rig.Service.CallAsync(cc, "board_move", new JsonObject { ["card"] = 38, ["lane"] = "done" }, Ct);
        Assert.False(moved.IsError, moved.Text);
        Assert.Equal("board.move", Assert.Single(rig.Board).Method);
        var row = Assert.Single(AuditRows(rig), r => r.Method == "concierge.board_move");
        Assert.Equal(Actor.External, row.Via);
        Assert.Equal("cc", row.Client);

        // Act tools never wait for approval.
        Assert.Null(moved.RequestId);
        Assert.Equal(0, rig.Service.PendingCount);
    }

    [Fact]
    public async Task ExternalReadsKeepToCloudLinkParity()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Destructive);

        var status = await rig.Service.CallAsync(cc, "system_status", [], Ct);
        Assert.False(status.IsError, status.Text);
        Assert.Contains("Cloud Link: only on the computer itself", status.Text);
        Assert.DoesNotContain("person@example.test", status.Text);
        Assert.DoesNotContain(rig.Methods, m => m == "link.status");

        var teams = await rig.Service.CallAsync(cc, "team_list", [], Ct);
        Assert.Contains("alpha", teams.Text);
    }

    [Fact]
    public async Task TeamGitThroughTheCompanionStillNeverRunsGitInAClone()
    {
        var appRoot = Path.Combine(_root, "app");
        var hostConfig = Path.Combine(appRoot, "data", "git", "empty.gitconfig");
        Directory.CreateDirectory(Path.GetDirectoryName(hostConfig)!);
        // The temp folders may belong to another account in this sandbox; the host git trusts none by default.
        File.WriteAllText(hostConfig, "[safe]\n\tdirectory = *\n[core]\n\tlongpaths = true\n");
        var repos = new TeamRepos(appRoot, _db.ConnectionString);
        var rig = await MakeRigAsync(repos: repos);
        var team = (await rig.Bus.TeamsAsync(Ct)).Single();
        var repo = await repos.EnsureTeamRepoAsync(team.Id, team.Name, Ct);

        var clone = Path.Combine(repos.MemberRoot("alpha", "Manager"), "repo");
        CopyTree(repo.Path, clone);
        Config(clone);
        Run(clone, "checkout", "-q", "-b", "team/alpha");
        Commit(clone, "c.txt", "manager's work");
        static string Sh(string p) => p.Replace('\\', '/');
        var marker = Path.Combine(_root, "clone-program-ran");
        Run(clone, "config", "core.fsmonitor", $"echo ran >> '{Sh(marker)}'; false");
        Run(clone, "config", "filter.evil.clean", $"echo ran >> '{Sh(marker)}'; cat");
        File.WriteAllText(Path.Combine(clone, ".git", "info", "attributes"), "* filter=evil\n");
        var hooks = Directory.CreateDirectory(Path.Combine(_root, "evil-hooks")).FullName;
        foreach (var hook in new[] { "reference-transaction", "post-checkout", "post-index-change" })
            File.WriteAllText(Path.Combine(hooks, hook), $"#!/bin/sh\necho ran >> '{Sh(marker)}'\n");
        Run(clone, "config", "core.hooksPath", Sh(hooks));

        var cc = rig.Client("cc", CompanionLevel.Read);
        var text = await rig.Service.CallAsync(cc, "team_git", new JsonObject { ["team"] = "alpha" }, Ct);

        Assert.False(File.Exists(marker), "a program the clone's config names ran on the host");
        Assert.False(text.IsError, text.Text);
        Assert.Contains("Manager: on team/alpha", text.Text);
        Assert.Contains("uncommitted changes: not checked", text.Text);
    }

    // ---------------------------------------------------------------- destructive: approval requests

    [Fact]
    public async Task ADestructiveCallReturnsTheExactListAndRunsNothing()
    {
        var rig = await MakeRigAsync(git: true);
        var shaOld = Branch("members/old", merged: true);
        Branch("members/live", merged: false);
        var cc = rig.Client("cc", CompanionLevel.Destructive);

        var r = await rig.Service.CallAsync(cc, "git_branches_delete", new JsonObject { ["team"] = "alpha" }, Ct);
        Assert.False(r.IsError, r.Text);
        Assert.NotNull(r.RequestId);
        Assert.StartsWith(RequestPrefix(r.RequestId), r.Text);
        Assert.Contains($"delete members/old at {shaOld}", r.Text);
        Assert.Contains("KEPT, not merged", r.Text);
        Assert.DoesNotMatch(AnyToken, r.Text);
        Assert.True(Has("members/old")); // asking deletes nothing

        var listed = rig.Service.ListForWindow();
        var action = Assert.Single(listed["actions"]!.AsArray())!;
        Assert.Equal(r.RequestId, action["id"]!.GetValue<string>());
        Assert.Equal("cc", action["client"]!.GetValue<string>());
        Assert.Equal("git_branches_delete", action["tool"]!.GetValue<string>());
        Assert.Contains($"delete members/old at {shaOld}", action["plan"]!.GetValue<string>());
        Assert.Equal(r.Text[RequestPrefix(r.RequestId).Length..], action["plan"]!.GetValue<string>());
        Assert.DoesNotMatch(AnyToken, listed.ToJsonString());

        var status = rig.Service.Status(cc, r.RequestId)!;
        Assert.Equal(ActionState.Pending, status.State);
        Assert.Equal(action["plan"]!.GetValue<string>(), status.Plan);
        Assert.Null(status.Result);
        Assert.Equal(1, rig.Service.PendingCount);

        // Another client cannot see this client's request.
        Assert.Null(rig.Service.Status(rig.Client("other", CompanionLevel.Destructive), r.RequestId));

        // Nothing to delete: said at once, no request.
        Run(Work, "branch", "-D", "members/old");
        var none = await rig.Service.CallAsync(cc, "git_prune", new JsonObject { ["team"] = "alpha" }, Ct);
        Assert.StartsWith("nothing to do", none.Text);
        Assert.Null(none.RequestId);
    }

    [Fact]
    public async Task ApprovalRunsExactlyTheStoredArgsOnce()
    {
        var rig = await MakeRigAsync(git: true);
        Branch("members/old", merged: true);
        Branch("members/also", merged: true);
        var cc = rig.Client("cc", CompanionLevel.Destructive);

        var r = await rig.Service.CallAsync(cc, "git_branches_delete", new JsonObject { ["team"] = "alpha", ["branches"] = "members/old" }, Ct);
        Assert.NotNull(r.RequestId);
        var done = await rig.Approve(r.RequestId);
        Assert.Equal(ActionState.Done, done.State);
        Assert.StartsWith("ok:", done.Result);
        Assert.False(Has("members/old"));
        Assert.True(Has("members/also")); // only what was asked for and shown

        // team_delete: approved twice, run once, as the client, approved by the person.
        var del = await rig.Service.CallAsync(cc, "team_delete", new JsonObject { ["team"] = "alpha" }, Ct);
        Assert.StartsWith(RequestPrefix(del.RequestId!), del.Text);
        Assert.Contains("DELETE team alpha", del.Text);
        Assert.DoesNotContain(rig.Methods, m => m == "teams.delete");
        var first = await rig.Approve(del.RequestId!);
        var second = await rig.Approve(del.RequestId!);
        Assert.Equal(ActionState.Done, first.State);
        Assert.Equal(ActionState.Done, second.State);
        var call = Assert.Single(rig.Ui, u => u.Method == "teams.delete");
        Assert.Equal("alpha", call.P!["confirm"]!.GetValue<string>());
        Assert.Equal(Actor.External, call.Actor!.Via);
        Assert.Equal("cc", call.Actor.Client);
        Assert.Equal(Actor.LocalUser, call.Actor.ApprovedBy);

        var row = AuditRows(rig).First(x => x.Method == "companion.team_delete" && x.Outcome.StartsWith("approved", StringComparison.Ordinal));
        Assert.Equal("cc", row.Client);
        Assert.Contains("approved by " + Actor.LocalUser, row.Outcome);
        Assert.Equal(0, rig.Service.PendingCount);
        Assert.Empty(rig.Service.ListForWindow()["actions"]!.AsArray());
    }

    [Fact]
    public async Task AClientCannotSelfConfirm()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Destructive);

        var r = await rig.Service.CallAsync(cc, "team_archive", new JsonObject { ["team"] = "alpha", ["confirm"] = "0123456789abcdef" }, Ct);
        Assert.NotNull(r.RequestId);
        Assert.DoesNotContain(rig.Methods, m => m == "teams.archive");
        var token = rig.Service.TokenOf(r.RequestId)!;
        Assert.Matches("^[0-9a-f]{16}$", token);
        Assert.DoesNotContain(token, r.Text);
        Assert.DoesNotContain(token, rig.Service.ListForWindow().ToJsonString());
        Assert.DoesNotContain(token, rig.Service.Status(cc, r.RequestId)!.ToString());

        // Even with the real token, every path the client has makes a new request or is refused; nothing runs.
        var again = await rig.Service.CallAsync(cc, "team_archive", new JsonObject { ["team"] = "alpha", ["confirm"] = token }, Ct);
        Assert.NotNull(again.RequestId);
        Assert.NotEqual(r.RequestId, again.RequestId);
        var status = await rig.Service.CallAsync(cc, "request_status", new JsonObject { ["id"] = r.RequestId, ["confirm"] = token }, Ct);
        Assert.Contains("pending", status.Text);
        Assert.DoesNotContain(rig.Methods, m => m == "teams.archive");
        await Assert.ThrowsAsync<RpcException>(async () =>
        {
            using (Actor.Use(Actor.ExternalClient("cc"))) await rig.Service.DecideActionAsync(r.RequestId, true, Ct);
        });
        Assert.DoesNotContain(rig.Methods, m => m == "teams.archive");
        Assert.Equal(ActionState.Pending, rig.Service.Status(cc, r.RequestId)!.State);
        // The stored args never kept the client's confirm.
        Assert.DoesNotContain("0123456789abcdef", rig.Service.ListForWindow().ToJsonString());
    }

    [Fact]
    public async Task AListThatChangedBeforeApprovalMakesANewRequest()
    {
        var rig = await MakeRigAsync(git: true);
        Branch("members/a", merged: true);
        var cc = rig.Client("cc", CompanionLevel.Destructive);
        var r = await rig.Service.CallAsync(cc, "git_branches_delete", new JsonObject { ["team"] = "alpha" }, Ct);
        Branch("members/b", merged: true); // another merged branch appears after the list was shown

        var changed = await rig.Approve(r.RequestId!);
        Assert.Equal(ActionState.Changed, changed.State);
        Assert.NotNull(changed.NextId);
        Assert.True(Has("members/a") && Has("members/b"));
        Assert.Equal($"the list changed before approval; request {changed.NextId} shows the new list", changed.Result);

        var next = rig.Service.Status(cc, changed.NextId)!;
        Assert.Equal(ActionState.Pending, next.State);
        Assert.Contains("members/b", next.Plan);
        Assert.Equal(1, rig.Service.PendingCount);

        // No text a client or the window sees holds a confirm token or the Concierge's "call again with confirm" instruction.
        var visible = new List<string>
        {
            r.Text, changed.ToString(), next.ToString(), rig.Service.Status(cc, r.RequestId!)!.ToString(), rig.Service.ListForWindow().ToJsonString(),
            (await rig.Service.CallAsync(cc, "request_status", new JsonObject { ["id"] = r.RequestId }, Ct)).Text,
            (await rig.Service.CallAsync(cc, "request_status", new JsonObject { ["id"] = changed.NextId }, Ct)).Text,
            (await rig.Service.WaitAsync(cc, r.RequestId!, TimeSpan.Zero, Ct))!.ToString(),
        };
        var done = await rig.Approve(changed.NextId);
        Assert.Equal(ActionState.Done, done.State);
        Assert.False(Has("members/a") || Has("members/b"));
        visible.Add(done.ToString());
        visible.Add((await rig.Service.CallAsync(cc, "request_status", new JsonObject { ["id"] = changed.NextId }, Ct)).Text);
        foreach (var text in visible)
        {
            Assert.DoesNotMatch(AnyToken, text);
            Assert.DoesNotContain("say yes in chat", text);
        }
    }

    [Fact]
    public async Task DenyRunsNothing()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Destructive);
        var r = await rig.Service.CallAsync(cc, "team_delete", new JsonObject { ["team"] = "alpha" }, Ct);

        var denied = await rig.Approve(r.RequestId!, approve: false);
        Assert.Equal(ActionState.Denied, denied.State);
        Assert.Equal(ActionState.Denied, (await rig.Approve(r.RequestId!)).State);
        Assert.DoesNotContain(rig.Methods, m => m == "teams.delete");
        Assert.Equal(0, rig.Service.PendingCount);
        Assert.Contains(AuditRows(rig), x => x.Method == "companion.team_delete" && x.Outcome.StartsWith("denied", StringComparison.Ordinal) && x.Client == "cc");
    }

    [Fact]
    public async Task AnExpiredRequestRunsNothing()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Destructive);
        var r = await rig.Service.CallAsync(cc, "team_delete", new JsonObject { ["team"] = "alpha" }, Ct);
        Assert.Equal(1, rig.Service.PendingCount);

        rig.Clock.Now += CompanionService.ActionLife + TimeSpan.FromSeconds(1);
        Assert.Equal(0, rig.Service.PendingCount);
        Assert.Empty(rig.Service.ListForWindow()["actions"]!.AsArray());
        Assert.Equal(ActionState.Expired, rig.Service.Status(cc, r.RequestId!)!.State);
        Assert.Equal(ActionState.Expired, (await rig.Approve(r.RequestId!)).State);
        Assert.DoesNotContain(rig.Methods, m => m == "teams.delete");
        Assert.Equal(ConfirmationBook.Life - TimeSpan.FromSeconds(5), CompanionService.ActionLife);
    }

    [Fact]
    public async Task OnlyTheWindowCanApprove()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Destructive);
        var r = await rig.Service.CallAsync(cc, "team_delete", new JsonObject { ["team"] = "alpha" }, Ct);

        foreach (var who in new Actor?[] { null, Actor.ExternalClient("cc"), Actor.CloudViewer("someone@example.test"), new(Actor.Concierge, Actor.LocalUser),
                     Actor.ExternalClient("cc") with { ApprovedBy = Actor.LocalUser } })
        {
            var e = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                using (who is null ? null : Actor.Use(who)) await rig.Service.DecideActionAsync(r.RequestId!, true, Ct);
            });
            Assert.Equal("forbidden", e.Code);
        }
        Assert.DoesNotContain(rig.Methods, m => m == "teams.delete");
        Assert.Equal(ActionState.Pending, rig.Service.Status(cc, r.RequestId!)!.State);

        Assert.Equal(ActionState.Done, (await rig.Approve(r.RequestId!)).State);
        Assert.Single(rig.Ui, u => u.Method == "teams.delete");
    }

    [Fact]
    public async Task PushSyncAndMergeTeamWaitForApproval()
    {
        var rig = await MakeRigAsync(git: true);
        var sha = Branch("members/ines", merged: false);
        var cc = rig.Client("cc", CompanionLevel.Destructive);

        var push = await rig.Service.CallAsync(cc, "git_push_branch", new JsonObject { ["team"] = "alpha", ["branch"] = "members/ines" }, Ct);
        Assert.NotNull(push.RequestId);
        Assert.Contains("members/ines", push.Text);
        Assert.Equal("", Run(Origin, "branch", "--list", "members/ines")); // not pushed by asking

        var sync = await rig.Service.CallAsync(cc, "git_sync_branch", new JsonObject { ["team"] = "alpha", ["branch"] = "members/ines" }, Ct);
        Assert.NotNull(sync.RequestId);
        Assert.Equal(sha, Run(Work, "rev-parse", "members/ines")); // not synced by asking

        var merge = await rig.Service.CallAsync(cc, "git_merge_team", new JsonObject { ["team"] = "alpha" }, Ct);
        Assert.NotNull(merge.RequestId);
        Assert.Contains("team/alpha", merge.Text);
        Assert.Contains("main", merge.Text);
        Assert.Contains(rig.Methods, m => m == "git.status");
        Assert.DoesNotContain(rig.Methods, m => m == "git.merge");
        Assert.Equal(3, rig.Service.PendingCount);

        var pushed = await rig.Approve(push.RequestId);
        Assert.Equal(ActionState.Done, pushed.State);
        Assert.Equal(sha, Run(Origin, "rev-parse", "members/ines"));

        var merged = await rig.Approve(merge.RequestId);
        Assert.Equal(ActionState.Done, merged.State);
        var call = Assert.Single(rig.Ui, u => u.Method == "git.merge");
        Assert.Equal((await rig.Bus.TeamsAsync(Ct)).Single().Id, call.P!["teamId"]!.GetValue<long>());
        Assert.Equal(Actor.LocalUser, call.Actor!.ApprovedBy);
        Assert.Contains("Fast-forwarded", merged.Result);

        // Nothing to merge: said at once, no request.
        rig.GitStatus["merge"] = "up-to-date";
        var none = await rig.Service.CallAsync(cc, "git_merge_team", new JsonObject { ["team"] = "alpha" }, Ct);
        Assert.Null(none.RequestId);
        Assert.StartsWith("nothing to do", none.Text);
    }

    /// <summary>
    /// The field names the service reads from git.status and git.merge are the window's own: the real TeamGitStatus and
    /// TeamGitResult records, serialized exactly as UiRpc answers them (RpcDispatcher.Json).
    /// </summary>
    [Fact]
    public async Task MergeAndPushPlansReadTheRealGitSerializers()
    {
        var rig = await MakeRigAsync();
        var status = new TeamGitStatus("x", true, "main", "main", false, "https://example.test/origin.git",
            "1111111", "1111111", 0, 0, "team/alpha", "2222222", 3, 1, "merge", [],
            [new TeamGitBranch("members/ines", "3333333", 1, false)]);
        rig.GitStatus = System.Text.Json.JsonSerializer.SerializeToNode(status, RpcDispatcher.Json)!.AsObject();
        rig.GitMerge = System.Text.Json.JsonSerializer.SerializeToNode(
            new TeamGitResult("merged", "Merged team/alpha into main.", true, "4444444"), RpcDispatcher.Json)!.AsObject();
        var cc = rig.Client("cc", CompanionLevel.Destructive);

        var merge = await rig.Service.CallAsync(cc, "git_merge_team", new JsonObject { ["team"] = "alpha" }, Ct);
        Assert.NotNull(merge.RequestId);
        Assert.Contains("Now: team/alpha at 2222222 is 3 commit(s) ahead of main and 1 behind; it lands as a merge commit and is pushed to origin.", merge.Text);

        var push = await rig.Service.CallAsync(cc, "git_push_branch", new JsonObject { ["team"] = "alpha", ["branch"] = "members/ines" }, Ct);
        Assert.Contains("Its tip now: 3333333.", push.Text);
        var pushTeam = await rig.Service.CallAsync(cc, "git_push_branch", new JsonObject { ["team"] = "alpha", ["branch"] = "team/alpha" }, Ct);
        Assert.Contains("Its tip now: 2222222.", pushTeam.Text);

        var merged = await rig.Approve(merge.RequestId);
        Assert.Equal(ActionState.Done, merged.State);
        Assert.Equal("ok: Merged team/alpha into main.", merged.Result);

        rig.GitStatus = System.Text.Json.JsonSerializer.SerializeToNode(status with { Merge = "conflict", ConflictFiles = ["a.txt", "b.txt"] }, RpcDispatcher.Json)!.AsObject();
        var conflict = await rig.Service.CallAsync(cc, "git_merge_team", new JsonObject { ["team"] = "alpha" }, Ct);
        Assert.Null(conflict.RequestId);
        Assert.Contains("conflicts in a.txt, b.txt", conflict.Text);

        rig.GitStatus = System.Text.Json.JsonSerializer.SerializeToNode(status with { Origin = null, Merge = "fast-forward" }, RpcDispatcher.Json)!.AsObject();
        var local = await rig.Service.CallAsync(cc, "git_merge_team", new JsonObject { ["team"] = "alpha" }, Ct);
        Assert.Contains("it lands as a fast-forward.", local.Text);
    }

    [Fact]
    public async Task RestoreIsStagedOnlyAfterApproval()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Destructive);

        var unknown = await rig.Service.CallAsync(cc, "backup_restore", new JsonObject { ["backup"] = "nope.db" }, Ct);
        Assert.True(unknown.IsError);
        Assert.Null(unknown.RequestId);

        var list = await rig.Service.CallAsync(rig.Client("reader", CompanionLevel.Read), "backups_list", [], Ct);
        Assert.Contains("daily-20261110.db", list.Text);

        var r = await rig.Service.CallAsync(cc, "backup_restore", new JsonObject { ["backup"] = "daily-20261110.db" }, Ct);
        Assert.NotNull(r.RequestId);
        Assert.Contains("daily-20261110.db", r.Text);
        Assert.DoesNotContain(rig.Methods, m => m == "ops.restore");

        var done = await rig.Approve(r.RequestId);
        Assert.Equal(ActionState.Done, done.State);
        var call = Assert.Single(rig.Ui, u => u.Method == "ops.restore");
        Assert.True(call.P!["confirm"]!.GetValue<bool>());
        Assert.Equal("daily-20261110.db", call.P["backup"]!.GetValue<string>());
        Assert.Equal(Actor.External, call.Actor!.Via);
        Assert.Equal(Actor.LocalUser, call.Actor.ApprovedBy);
    }

    [Fact]
    public async Task EnvValueNeverInThePlanOrAudit()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Destructive);
        var seen = new List<string>();

        var r = await rig.Service.CallAsync(cc, "team_env_set", new JsonObject { ["team"] = "alpha", ["name"] = "KEEP", ["value"] = "s3cret-new" }, Ct);
        seen.Add(r.Text);
        Assert.NotNull(r.RequestId);
        Assert.Contains("KEEP", r.Text);
        seen.Add(rig.Service.ListForWindow().ToJsonString());
        Assert.DoesNotContain(rig.Methods, m => m == "teams.setEnv");

        // Replacing an existing variable needs the Concierge's own second yes: approving shows that list as a new request.
        var first = await rig.Approve(r.RequestId);
        seen.Add(first.ToString());
        Assert.Equal(ActionState.Changed, first.State);
        Assert.Equal($"needs another approval; request {first.NextId} shows exactly what it will do", first.Result);
        Assert.NotNull(first.NextId);
        Assert.DoesNotContain(rig.Methods, m => m == "teams.setEnv");
        var next = rig.Service.Status(cc, first.NextId)!;
        seen.Add(next.Plan);
        Assert.Contains("Replace the value of environment variable KEEP", next.Plan);
        seen.Add(rig.Service.ListForWindow().ToJsonString());

        var done = await rig.Approve(first.NextId);
        seen.Add(done.ToString());
        Assert.Equal(ActionState.Done, done.State);
        var set = Assert.Single(rig.Ui, u => u.Method == "teams.setEnv").P!["entries"]!.AsArray();
        Assert.Contains(set, e => e!["name"]!.GetValue<string>() == "KEEP" && e["value"]!.GetValue<string>() == "s3cret-new");

        seen.Add((await rig.Service.CallAsync(cc, "request_status", new JsonObject { ["id"] = r.RequestId }, Ct)).Text);
        seen.Add((await rig.Service.CallAsync(cc, "request_status", new JsonObject { ["id"] = first.NextId }, Ct)).Text);
        seen.Add(rig.Service.Status(cc, r.RequestId)!.ToString());
        foreach (var text in seen)
        {
            Assert.DoesNotContain("s3cret", text);
            Assert.DoesNotMatch(AnyToken, text);
            Assert.DoesNotContain("say yes in chat", text);
        }
        foreach (var row in AuditRows(rig)) Assert.DoesNotContain("s3cret", row.ToString());
        Assert.DoesNotContain(EveryStoredText(), s => s.Contains("s3cret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoAuditRowResultOrListHoldsAClientKeyOrItsHash()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Destructive);
        var key = rig.Keys["cc"];
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        var texts = new List<string>();

        texts.Add((await rig.Service.CallAsync(cc, "team_list", [], Ct)).Text);
        texts.Add((await rig.Service.CallAsync(cc, "board_move", new JsonObject { ["card"] = 1, ["lane"] = "done" }, Ct)).Text);
        var r = await rig.Service.CallAsync(cc, "team_archive", new JsonObject { ["team"] = "alpha" }, Ct);
        texts.Add(r.Text);
        texts.Add((await rig.Approve(r.RequestId!)).ToString());
        texts.Add((await rig.Service.CallAsync(cc, "request_status", new JsonObject { ["id"] = r.RequestId }, Ct)).Text);

        var ticket = rig.Service.BeginPairing("second", CompanionLevel.Read);
        texts.Add(rig.Service.ListForWindow().ToJsonString());
        var wait = rig.Service.WaitPairingAsync(ticket.Id, Ct);
        using (Actor.Use(Actor.Window)) rig.Service.DecidePairing(ticket.Id, true, null);
        var outcome = await wait;
        var secondHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(outcome.Key!)));
        texts.Add(rig.Service.ListForWindow().ToJsonString());
        Assert.True(rig.Service.Revoke("second"));
        texts.Add(rig.Service.ListForWindow().ToJsonString());

        foreach (var text in texts.Concat(AuditRows(rig).Select(x => x.ToString())))
        {
            Assert.DoesNotContain(key, text);
            Assert.DoesNotContain(hash, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(outcome.Key!, text);
            Assert.DoesNotContain(secondHash, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ywb_", text);
        }
        // Outside companion_clients.key_hash, the database holds neither.
        Assert.DoesNotContain(EveryStoredText().Where(s => !s.StartsWith("companion_clients:", StringComparison.Ordinal)),
            s => s.Contains(key, StringComparison.Ordinal) || s.Contains(hash, StringComparison.OrdinalIgnoreCase) || s.Contains(secondHash, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RevokeVoidsThatClientsPendingRequests()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Destructive);
        var other = rig.Client("other", CompanionLevel.Destructive);
        var mine = await rig.Service.CallAsync(cc, "team_archive", new JsonObject { ["team"] = "alpha" }, Ct);
        var theirs = await rig.Service.CallAsync(other, "team_archive", new JsonObject { ["team"] = "alpha" }, Ct);
        var revoked = new List<string>();
        rig.Service.Revoked += revoked.Add;

        Assert.True(rig.Service.Revoke("cc"));
        Assert.Equal(["cc"], revoked);
        Assert.Equal(ActionState.Denied, rig.Service.Status(cc, mine.RequestId!)!.State);
        Assert.Equal(ActionState.Denied, (await rig.Approve(mine.RequestId!)).State);
        Assert.DoesNotContain(rig.Methods, m => m == "teams.archive");
        Assert.Equal(ActionState.Pending, rig.Service.Status(other, theirs.RequestId!)!.State);
        Assert.Equal(1, rig.Service.PendingCount);

        // A revoked client's next call is refused, even on a connection that is still open.
        var after = await rig.Service.CallAsync(cc, "team_list", [], Ct);
        Assert.True(after.IsError);
        Assert.Contains("revoked", after.Text);
        Assert.False(rig.Service.Revoke("cc"));
    }

    // ---------------------------------------------------------------- pairing

    [Fact]
    public async Task PairingApprovedHandsTheKeyOnce_DeniedHandsNone_LevelCanBeLoweredNotRaised_AtMostThreePending_ExpiresAfterFiveMinutes()
    {
        var rig = await MakeRigAsync();
        Assert.Equal(TimeSpan.FromMinutes(5), CompanionService.PairingLife);

        // Approved, lowered from act to read: the key once, at the lowered level.
        var t1 = rig.Service.BeginPairing("claude-code", CompanionLevel.Act);
        Assert.Matches("^[A-Z0-9]{6}$", t1.Code);
        Assert.Equal(rig.Clock.Now + CompanionService.PairingLife, t1.Expires);
        var pairing = Assert.Single(rig.Service.ListForWindow()["pairings"]!.AsArray())!;
        Assert.Equal("claude-code", pairing["name"]!.GetValue<string>());
        Assert.Equal("act", pairing["level"]!.GetValue<string>());
        Assert.Equal(t1.Code, pairing["code"]!.GetValue<string>());
        var wait1 = rig.Service.WaitPairingAsync(t1.Id, Ct);
        Assert.False(wait1.IsCompleted);
        Assert.Throws<RpcException>(() => rig.Service.DecidePairing(t1.Id, true, null)); // not from the window
        using (Actor.Use(Actor.Window))
        {
            Assert.Equal("bad_params", Assert.Throws<RpcException>(() => rig.Service.DecidePairing(t1.Id, true, CompanionLevel.Destructive)).Code);
            rig.Service.DecidePairing(t1.Id, true, CompanionLevel.Read);
        }
        var o1 = await wait1;
        Assert.True(o1.Approved);
        Assert.StartsWith("ywb_", o1.Key);
        Assert.Equal(CompanionLevel.Read, o1.Level);
        Assert.Equal(CompanionLevel.Read, rig.Clients.Authenticate("claude-code", o1.Key!)!.Level);
        var o1Again = await rig.Service.WaitPairingAsync(t1.Id, Ct);
        Assert.Null(o1Again.Key);
        Assert.False(o1Again.Approved);
        Assert.Empty(rig.Service.ListForWindow()["pairings"]!.AsArray());

        // A paired name cannot pair again; a bad name is refused.
        Assert.Equal("exists", Assert.Throws<RpcException>(() => rig.Service.BeginPairing("claude-code", CompanionLevel.Read)).Code);
        Assert.Equal("bad_params", Assert.Throws<RpcException>(() => rig.Service.BeginPairing("Claude Code", CompanionLevel.Read)).Code);

        // Denied: no key, no client.
        var t2 = rig.Service.BeginPairing("denied-one", CompanionLevel.Read);
        var wait2 = rig.Service.WaitPairingAsync(t2.Id, Ct);
        using (Actor.Use(Actor.Window)) rig.Service.DecidePairing(t2.Id, false, null);
        var o2 = await wait2;
        Assert.False(o2.Approved);
        Assert.Null(o2.Key);
        Assert.DoesNotContain(rig.Clients.List(), c => c.Name == "denied-one");

        // At most three pending; a fourth is busy; a pending name cannot be asked for twice.
        var a = rig.Service.BeginPairing("a", CompanionLevel.Read);
        rig.Service.BeginPairing("b", CompanionLevel.Read);
        Assert.Equal("exists", Assert.Throws<RpcException>(() => rig.Service.BeginPairing("b", CompanionLevel.Read)).Code);
        rig.Service.BeginPairing("c", CompanionLevel.Read);
        Assert.Equal("busy", Assert.Throws<RpcException>(() => rig.Service.BeginPairing("d", CompanionLevel.Read)).Code);

        // Five minutes later they have expired: a waiting client hears so, and new pairings can start.
        var waitA = rig.Service.WaitPairingAsync(a.Id, Ct);
        rig.Clock.Now += CompanionService.PairingLife + TimeSpan.FromSeconds(1);
        var oa = await waitA.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(oa.Approved);
        Assert.Null(oa.Key);
        Assert.Contains("expired", oa.Reason);
        using (Actor.Use(Actor.Window))
            Assert.Equal("not_found", Assert.Throws<RpcException>(() => rig.Service.DecidePairing(a.Id, true, null)).Code);
        Assert.DoesNotContain(rig.Clients.List(), c => c.Name == "a");
        Assert.Empty(rig.Service.ListForWindow()["pairings"]!.AsArray());
        rig.Service.BeginPairing("d", CompanionLevel.Read);

        // Revoked, the name can pair again.
        Assert.True(rig.Service.Revoke("claude-code"));
        rig.Service.BeginPairing("claude-code", CompanionLevel.Read);
    }

    [Fact]
    public async Task PendingCountCountsPairingsAndActions_ChangedEventHasNoData()
    {
        var rig = await MakeRigAsync();
        var seen = new ConcurrentQueue<JsonObject>();
        using var sub = rig.Events.Subscribe(m => { seen.Enqueue(m); return Task.CompletedTask; });
        var cc = rig.Client("cc", CompanionLevel.Destructive);

        Assert.Equal(0, rig.Service.PendingCount);
        var ticket = rig.Service.BeginPairing("pairing", CompanionLevel.Read);
        Assert.Equal(1, rig.Service.PendingCount);
        var r = await rig.Service.CallAsync(cc, "team_archive", new JsonObject { ["team"] = "alpha" }, Ct);
        Assert.Equal(2, rig.Service.PendingCount);
        using (Actor.Use(Actor.Window)) rig.Service.DecidePairing(ticket.Id, false, null);
        Assert.Equal(1, rig.Service.PendingCount);
        await rig.Approve(r.RequestId!);
        Assert.Equal(0, rig.Service.PendingCount);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (seen.Count < 4 && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
        Assert.True(seen.Count >= 4, $"only {seen.Count} events");
        foreach (var e in seen)
        {
            Assert.Equal(CompanionService.ChangedEvent, e["event"]!.GetValue<string>());
            Assert.Null(e["data"]);
        }
        Assert.Equal("companion.changed", CompanionService.ChangedEvent);
    }

    // ---------------------------------------------------------------- fix round 1

    [Fact]
    public async Task ExecutorFailuresFinishTheRequest_AndItIsNeverLeftRunning()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Destructive);
        rig.OnRoute = (method, _) => method switch
        {
            "git.merge" => throw new GitException("git exploded"),
            "ops.restore" => throw new IOException("the disk is gone"),
            "teams.delete" => throw new OperationCanceledException(),
            _ => Task.CompletedTask,
        };

        var merge = await rig.Service.CallAsync(cc, "git_merge_team", new JsonObject { ["team"] = "alpha" }, Ct);
        var restore = await rig.Service.CallAsync(cc, "backup_restore", new JsonObject { ["backup"] = "daily-20261110.db" }, Ct);
        var delete = await rig.Service.CallAsync(cc, "team_delete", new JsonObject { ["team"] = "alpha" }, Ct);
        Assert.Equal(3, rig.Service.PendingCount);

        var m = await rig.Approve(merge.RequestId!);
        Assert.Equal(ActionState.Failed, m.State);
        Assert.Contains("git exploded", m.Result);
        var s = await rig.Approve(restore.RequestId!);
        Assert.Equal(ActionState.Failed, s.State);
        Assert.Contains("the disk is gone", s.Result);
        // A cancellation inside the run (not the window's call) also finishes it: Failed, never left running.
        var d = await rig.Approve(delete.RequestId!);
        Assert.Equal(ActionState.Failed, d.State);
        Assert.Equal("failed: cancelled before it finished", d.Result);
        Assert.Equal(ActionState.Failed, rig.Service.Status(cc, delete.RequestId!)!.State);
        Assert.Equal(0, rig.Service.PendingCount);

        // Decided once: a later Approve runs nothing again.
        Assert.Equal(ActionState.Failed, (await rig.Approve(merge.RequestId!)).State);
        Assert.Equal(ActionState.Failed, (await rig.Approve(delete.RequestId!)).State);
        Assert.Single(rig.Ui, u => u.Method == "git.merge");
        Assert.Single(rig.Ui, u => u.Method == "teams.delete");
        foreach (var tool in new[] { "git_merge_team", "backup_restore", "team_delete" })
            Assert.Contains(AuditRows(rig), x => x.Method == "companion." + tool && x.Client == "cc" && x.Outcome.StartsWith("approved by " + Actor.LocalUser, StringComparison.Ordinal) && x.Outcome.Contains("failed"));
    }

    [Fact]
    public async Task EveryActCallIsAudited_AsTheExternalClient_OnceEach()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Act);

        Assert.False((await rig.Service.CallAsync(cc, "tell", new JsonObject { ["team"] = "alpha", ["instruction"] = "Add a README" }, Ct)).IsError);
        Assert.False((await rig.Service.CallAsync(cc, "team_pause", new JsonObject { ["team"] = "alpha" }, Ct)).IsError);
        Assert.True((await rig.Service.CallAsync(cc, "team_resume", new JsonObject { ["team"] = "nobody" }, Ct)).IsError);
        Assert.False((await rig.Service.CallAsync(cc, "board_move", new JsonObject { ["card"] = 38, ["lane"] = "done" }, Ct)).IsError);
        Assert.False((await rig.Service.CallAsync(cc, "team_list", [], Ct)).IsError);
        Assert.True((await rig.Service.CallAsync(cc, "notes_read", [], Ct)).IsError);

        var rows = AuditRows(rig);
        var tell = Assert.Single(rows, x => x.Method == "companion.tell");
        Assert.Equal(Actor.External, tell.Via);
        Assert.Equal(Actor.LocalUser, tell.User);
        Assert.Equal("cc", tell.Client);
        Assert.Equal("ok", tell.Outcome);
        Assert.Contains("team=alpha", tell.Target);
        Assert.DoesNotContain("README", tell.ToString()); // never the instruction itself
        var pause = Assert.Single(rows, x => x.Method == "companion.team_pause");
        Assert.Equal("cc", pause.Client);
        Assert.Equal("ok", pause.Outcome);
        Assert.StartsWith("refused", Assert.Single(rows, x => x.Method == "companion.team_resume").Outcome);
        Assert.StartsWith("refused", Assert.Single(rows, x => x.Method == "companion.notes_read").Outcome);
        // board_move is audited by the Concierge's actions already: one row, not two.
        Assert.Single(rows, x => x.Method.EndsWith(".board_move", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, x => x.Method.EndsWith("team_list", StringComparison.Ordinal)); // reads are not audited
    }

    [Fact]
    public async Task MergePushAndSyncAreBoundToTheShasShown()
    {
        var rig = await MakeRigAsync(git: true);
        var cc = rig.Client("cc", CompanionLevel.Destructive);
        var sha1 = Branch("members/ines", merged: false);
        rig.GitStatus["members"] = new JsonArray(new JsonObject { ["branch"] = "members/ines", ["sha"] = sha1, ["aheadOfBase"] = 1, ["inTeamBranch"] = false });
        rig.GitStatus["teamSha"] = "1111111111111111111111111111111111111111";

        var push = await rig.Service.CallAsync(cc, "git_push_branch", new JsonObject { ["team"] = "alpha", ["branch"] = "members/ines" }, Ct);
        var sync = await rig.Service.CallAsync(cc, "git_sync_branch", new JsonObject { ["team"] = "alpha", ["branch"] = "members/ines" }, Ct);
        var merge = await rig.Service.CallAsync(cc, "git_merge_team", new JsonObject { ["team"] = "alpha" }, Ct);
        Assert.Contains(sha1, push.Text);
        Assert.Contains(sha1, sync.Text);
        Assert.Contains("1111111111111111111111111111111111111111", merge.Text);

        // The branch moves after the plan was shown: the approval of the old plan does not push the new tip.
        Run(Work, "checkout", "-q", "members/ines");
        var sha2 = Commit(Work, "more.txt", "more");
        Run(Work, "checkout", "-q", "main");
        rig.GitStatus["members"]![0]!["sha"] = sha2;
        rig.GitStatus["teamSha"] = "2222222222222222222222222222222222222222";

        var pushed = await rig.Approve(push.RequestId!);
        Assert.Equal(ActionState.Changed, pushed.State);
        Assert.Equal("", Run(Origin, "branch", "--list", "members/ines"));
        Assert.Contains(sha2, rig.Service.Status(cc, pushed.NextId!)!.Plan);
        var synced = await rig.Approve(sync.RequestId!);
        Assert.Equal(ActionState.Changed, synced.State);
        Assert.Equal(sha2, Run(Work, "rev-parse", "members/ines"));
        var merged = await rig.Approve(merge.RequestId!);
        Assert.Equal(ActionState.Changed, merged.State);
        Assert.DoesNotContain(rig.Methods, x => x == "git.merge");
        Assert.Contains("2222222222222222222222222222222222222222", rig.Service.Status(cc, merged.NextId!)!.Plan);

        // The new requests, approved while nothing moved, run.
        Assert.Equal(ActionState.Done, (await rig.Approve(pushed.NextId!)).State);
        Assert.Equal(sha2, Run(Origin, "rev-parse", "members/ines"));
        Assert.Equal(ActionState.Done, (await rig.Approve(merged.NextId!)).State);
        Assert.Single(rig.Ui, u => u.Method == "git.merge");
    }

    [Fact]
    public async Task ARevokedClientsRequestsCannotBeApproved_EvenWhenRevokedBehindTheServicesBack()
    {
        var rig = await MakeRigAsync();

        // Revoked in the store while a request waits (a race with the service's Revoke): approving runs nothing.
        var cc = rig.Client("cc", CompanionLevel.Destructive);
        var r = await rig.Service.CallAsync(cc, "team_delete", new JsonObject { ["team"] = "alpha" }, Ct);
        Assert.True(rig.Clients.Revoke("cc"));
        Assert.Equal(ActionState.Denied, (await rig.Approve(r.RequestId!)).State);
        Assert.DoesNotContain(rig.Methods, m => m == "teams.delete");

        // Revoked while its request's plan is being made: no request is stored.
        var c2 = rig.Client("c2", CompanionLevel.Destructive);
        rig.OnRoute = (method, _) => { if (method == "git.status") rig.Clients.Revoke("c2"); return Task.CompletedTask; };
        var racing = await rig.Service.CallAsync(c2, "git_merge_team", new JsonObject { ["team"] = "alpha" }, Ct);
        Assert.True(racing.IsError);
        Assert.Contains("revoked", racing.Text);
        Assert.Null(racing.RequestId);
        Assert.Equal(0, rig.Service.PendingCount);

        // Revoked while its approved run makes a second list: the new request is not stored.
        var c3 = rig.Client("c3", CompanionLevel.Destructive);
        var env = await rig.Service.CallAsync(c3, "team_env_set", new JsonObject { ["team"] = "alpha", ["name"] = "KEEP", ["value"] = "x" }, Ct);
        rig.OnRoute = (method, _) => { if (method == "teams.env") rig.Clients.Revoke("c3"); return Task.CompletedTask; };
        var decided = await rig.Approve(env.RequestId!);
        Assert.NotEqual(ActionState.Done, decided.State);
        Assert.Null(decided.NextId);
        Assert.Equal(0, rig.Service.PendingCount);
        Assert.DoesNotContain(rig.Methods, m => m == "teams.setEnv");
    }

    [Fact]
    public async Task AnApprovedPairingNobodyCollectsExpiresAndFreesTheName_AndAHandedKeyIsForgotten()
    {
        var rig = await MakeRigAsync();

        var handed = rig.Service.BeginPairing("handed", CompanionLevel.Read);
        var wait = rig.Service.WaitPairingAsync(handed.Id, Ct);
        using (Actor.Use(new Actor(Actor.App, "person-at-window"))) rig.Service.DecidePairing(handed.Id, true, null);
        Assert.NotNull((await wait).Key);
        Assert.False(rig.Service.HoldsKey(handed.Id));
        // The decision is audited as whoever decided it at the window.
        var row = Assert.Single(AuditRows(rig), x => x.Method == "companion.pair");
        Assert.Equal(Actor.App, row.Via);
        Assert.Equal("person-at-window", row.User);

        var late = rig.Service.BeginPairing("late", CompanionLevel.Read);
        using (Actor.Use(Actor.Window)) rig.Service.DecidePairing(late.Id, true, null);
        Assert.True(rig.Service.HoldsKey(late.Id));
        rig.Clock.Now += CompanionService.PairingLife + TimeSpan.FromMinutes(2);
        Assert.False(rig.Service.HoldsKey(late.Id));
        var outcome = await rig.Service.WaitPairingAsync(late.Id, Ct);
        Assert.False(outcome.Approved);
        Assert.Null(outcome.Key);
        Assert.NotNull(rig.Clients.List().Single(c => c.Name == "late").RevokedAt);
        rig.Service.BeginPairing("late", CompanionLevel.Read); // the name is free again
    }

    [Fact]
    public async Task PrOpenPlanShowsTheFullTitleAndBody_AndAnOverlongArgumentIsRefused()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Destructive);
        var title = "T" + new string('t', 300);
        var body = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line {i} of what the pull request does"));

        var r = await rig.Service.CallAsync(cc, "git_pr_open", new JsonObject { ["team"] = "alpha", ["branch"] = "members/ines", ["title"] = title, ["body"] = body }, Ct);
        Assert.NotNull(r.RequestId);
        Assert.Contains(title, r.Text);
        Assert.Contains(body, r.Text);
        Assert.DoesNotContain("…", r.Text);

        var tooLong = await rig.Service.CallAsync(cc, "git_pr_open", new JsonObject { ["team"] = "alpha", ["branch"] = "members/ines", ["title"] = "x", ["body"] = new string('b', 9000) }, Ct);
        Assert.True(tooLong.IsError);
        Assert.Contains("8 KB", tooLong.Text);
        Assert.Null(tooLong.RequestId);
        Assert.Equal(1, rig.Service.PendingCount);
    }

    [Fact]
    public void NoDestructiveToolDefinitionMentionsATokenOrConfirm()
    {
        foreach (var def in CompanionTools.Definitions(CompanionLevel.Destructive))
        {
            var name = def!["name"]!.GetValue<string>();
            if (!CompanionTools.Destructive.Contains(name) && name != "backlog_dispatch") continue;
            var text = def.ToJsonString();
            Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("confirm", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("yes in", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ASecondApproveDuringARunSaysItIsRunning()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Destructive);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.OnRoute = async (method, _) =>
        {
            if (method != "git.merge") return;
            started.TrySetResult();
            await release.Task;
        };
        var r = await rig.Service.CallAsync(cc, "git_merge_team", new JsonObject { ["team"] = "alpha" }, Ct);

        var first = rig.Approve(r.RequestId!);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var second = await rig.Decide(r.RequestId!);
        Assert.Equal(ActionState.Running, second.State);
        Assert.Equal(ActionState.Running, rig.Service.Status(cc, r.RequestId!)!.State);
        var shown = Assert.Single(rig.Service.ListForWindow()["actions"]!.AsArray())!;
        Assert.Equal("running", shown["state"]!.GetValue<string>());
        Assert.Equal(0, rig.Service.PendingCount); // nothing waits for the person now

        release.SetResult();
        Assert.Equal(ActionState.Done, (await first).State);
        Assert.Single(rig.Ui, u => u.Method == "git.merge");
        Assert.Empty(rig.Service.ListForWindow()["actions"]!.AsArray());
    }

    // ---------------------------------------------------------------- fix round 2

    [Fact]
    public async Task APushOrSyncWhoseTipCannotBePinnedSaysSo()
    {
        var rig = await MakeRigAsync(); // git.status lists no member branches
        var cc = rig.Client("cc", CompanionLevel.Destructive);

        var push = await rig.Service.CallAsync(cc, "git_push_branch", new JsonObject { ["team"] = "alpha", ["branch"] = "feature/x" }, Ct);
        Assert.Contains("Its tip is not pinned: whatever it holds when you approve is pushed.", push.Text);
        var sync = await rig.Service.CallAsync(cc, "git_sync_branch", new JsonObject { ["team"] = "alpha", ["branch"] = "feature/x" }, Ct);
        Assert.Contains("Its tip is not pinned: whatever it holds when you approve is synced.", sync.Text);

        // A listed branch is pinned, and the plan does not say otherwise.
        rig.GitStatus["members"] = new JsonArray(new JsonObject { ["branch"] = "members/ines", ["sha"] = "3333333333333333333333333333333333333333" });
        var pinned = await rig.Service.CallAsync(cc, "git_push_branch", new JsonObject { ["team"] = "alpha", ["branch"] = "members/ines" }, Ct);
        Assert.Contains("Its tip now: 3333333333333333333333333333333333333333.", pinned.Text);
        Assert.DoesNotContain("not pinned", pinned.Text);
    }

    [Fact]
    public async Task ReadResultsKeepTheirText_RequestTextsAreMasked()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Destructive);

        var read = await rig.Service.CallAsync(cc, "documents_read", new JsonObject { ["team"] = "alpha", ["path"] = "notes.md" }, Ct);
        Assert.False(read.IsError, read.Text);
        Assert.Contains("commit 0123456789abcdef shipped", read.Text);

        var r = await rig.Service.CallAsync(cc, "git_pr_open", new JsonObject { ["team"] = "alpha", ["branch"] = "members/ines", ["title"] = "Fix 0123456789abcdef" }, Ct);
        Assert.NotNull(r.RequestId);
        Assert.DoesNotMatch(AnyToken, r.Text);
        Assert.Contains("[withheld]", r.Text);
        Assert.DoesNotMatch(AnyToken, rig.Service.ListForWindow().ToJsonString());
        Assert.DoesNotMatch(AnyToken, rig.Service.Status(cc, r.RequestId)!.ToString());
    }

    [Fact]
    public async Task AnUncollectedPairingIsUndone_ByItsOwnClientOnly_AuditedAndAnnounced()
    {
        var rig = await MakeRigAsync();
        var revoked = new ConcurrentQueue<string>();
        rig.Service.Revoked += revoked.Enqueue;

        // A: approved, never collected. The person revokes "x"; B pairs "x" again and collects its key.
        var a = rig.Service.BeginPairing("x", CompanionLevel.Read);
        using (Actor.Use(Actor.Window)) rig.Service.DecidePairing(a.Id, true, null);
        Assert.True(rig.Service.Revoke("x"));
        var b = rig.Service.BeginPairing("x", CompanionLevel.Read);
        var waitB = rig.Service.WaitPairingAsync(b.Id, Ct);
        using (Actor.Use(Actor.Window)) rig.Service.DecidePairing(b.Id, true, null);
        var keyB = (await waitB).Key!;
        while (revoked.TryDequeue(out _)) { }

        // A's pairing is undone later: B's client is not touched.
        rig.Clock.Now += CompanionService.PairingLife + TimeSpan.FromMinutes(2);
        Assert.False(rig.Service.HoldsKey(a.Id));
        Assert.NotNull(rig.Clients.Authenticate("x", keyB));
        await Task.Delay(300, Ct);
        Assert.Empty(revoked);

        // Plain case: the uncollected client is revoked, audited and announced.
        var c = rig.Service.BeginPairing("lonely", CompanionLevel.Read);
        using (Actor.Use(Actor.Window)) rig.Service.DecidePairing(c.Id, true, null);
        rig.Clock.Now += CompanionService.PairingLife + TimeSpan.FromMinutes(2);
        Assert.False(rig.Service.HoldsKey(c.Id));
        Assert.NotNull(rig.Clients.List().Single(x => x.Name == "lonely").RevokedAt);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (revoked.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
        Assert.Equal(["lonely"], revoked.ToArray());
        var row = Assert.Single(AuditRows(rig), x => x.Method == "companion.revoke" && x.Target == "name=lonely");
        Assert.Contains("not collected", row.Outcome);
    }

    // ---------------------------------------------------------------- final fix round

    [Fact]
    public async Task AnApprovalAnswersRunningAtOnce_TheRunFinishesByItselfAndIsAnnounced()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Destructive);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.OnRoute = async (method, _) =>
        {
            if (method != "git.merge") return;
            started.TrySetResult();
            await release.Task;
        };
        var r = await rig.Service.CallAsync(cc, "git_merge_team", new JsonObject { ["team"] = "alpha" }, Ct);
        var id = r.RequestId!;
        var events = new ConcurrentQueue<string>();
        using var subscription = rig.Events.Subscribe(e => { events.Enqueue(e["event"]!.GetValue<string>()); return Task.CompletedTask; });
        try
        {
            // Answered while the run is still blocked inside git.merge.
            var answered = await rig.Decide(id).WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(ActionState.Running, answered.State);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(ActionState.Running, rig.Service.Status(cc, id)!.State);
            Assert.Equal(ActionState.Running, (await rig.Decide(id)).State); // already running: nothing more runs
            Assert.Equal("running", Assert.Single(rig.Service.ListForWindow()["actions"]!.AsArray())!["state"]!.GetValue<string>());
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (events.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
            events.Clear();
        }
        finally { release.TrySetResult(); }

        var done = await rig.Service.WaitAsync(cc, id, TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(ActionState.Done, done!.State);
        await rig.Service.RunOf(id)!.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!events.Contains(CompanionService.ChangedEvent) && DateTime.UtcNow < until) await Task.Delay(20, Ct);
        Assert.Contains(CompanionService.ChangedEvent, events);
        Assert.Single(rig.Ui, u => u.Method == "git.merge");
        Assert.Contains(AuditRows(rig), x => x.Method == "companion.git_merge_team" && x.Outcome.StartsWith("approved by", StringComparison.Ordinal) && x.Outcome.EndsWith("done", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposingTheServiceCancelsARunningApproval_ItEndsFailed()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Destructive);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.OnRoute = async (method, _) =>
        {
            if (method != "ops.restore") return;
            started.TrySetResult();
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(30));
        };
        var r = await rig.Service.CallAsync(cc, "backup_restore", new JsonObject { ["backup"] = "daily-20261110.db" }, Ct);
        Assert.Equal(ActionState.Running, (await rig.Decide(r.RequestId!)).State);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // The run's token is the service's own: disposing cancels it, and the run ends Failed (never left running).
        var routeToken = rig.LastRouteToken;
        Assert.False(routeToken.IsCancellationRequested);
        routeToken.Register(() => stopping.TrySetException(new OperationCanceledException(routeToken)));
        await rig.Service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(routeToken.IsCancellationRequested);
        var s = rig.Service.Status(cc, r.RequestId!)!;
        Assert.Equal(ActionState.Failed, s.State);
        Assert.Equal("failed: cancelled while running (it may have partly run; check before asking again)", s.Result);
        Assert.Contains(AuditRows(rig), x => x.Method == "companion.backup_restore" && x.Outcome.Contains("failed: cancelled while running"));
    }

    [Fact]
    public async Task ARePairedNameIsAnotherClient_ItReadsNoOldResultAndInheritsNothing()
    {
        var rig = await MakeRigAsync();
        var a = rig.Client("cc", CompanionLevel.Destructive);
        var done = await rig.Service.CallAsync(a, "backup_restore", new JsonObject { ["backup"] = "daily-20261110.db" }, Ct);
        Assert.Equal(ActionState.Done, (await rig.Approve(done.RequestId!)).State);
        var waiting = await rig.Service.CallAsync(a, "team_delete", new JsonObject { ["team"] = "alpha" }, Ct);
        for (var i = 0; i < CompanionService.MaxPendingPerClient - 1; i++)
            Assert.NotNull((await rig.Service.CallAsync(a, "backup_restore", new JsonObject { ["backup"] = "daily-20261110.db" }, Ct)).RequestId);

        // Revoked behind the service's back (its requests still pending) and paired again under the same name.
        Assert.True(rig.Clients.Revoke("cc"));
        var b = rig.Client("cc", CompanionLevel.Destructive);
        Assert.NotEqual(a.Id, b.Id); // a re-pairing is never the same row id

        foreach (var id in new[] { done.RequestId!, waiting.RequestId! })
        {
            Assert.Null(rig.Service.Status(b, id));
            Assert.Null(await rig.Service.WaitAsync(b, id, TimeSpan.Zero, Ct));
            Assert.True((await rig.Service.CallAsync(b, "request_status", new JsonObject { ["id"] = id }, Ct)).IsError);
        }
        // The old client's waiting requests do not count against the new one's cap...
        var fresh = await rig.Service.CallAsync(b, "backup_restore", new JsonObject { ["backup"] = "daily-20261110.db" }, Ct);
        Assert.NotNull(fresh.RequestId);
        // ...and approving the old client's request runs nothing: it belonged to a revoked client.
        Assert.Equal(ActionState.Denied, (await rig.Approve(waiting.RequestId!)).State);
        Assert.DoesNotContain(rig.Methods, m => m == "teams.delete");

        // Revoking the new client voids its own requests.
        Assert.True(rig.Service.Revoke("cc"));
        Assert.Equal(ActionState.Denied, rig.Service.Status(b, fresh.RequestId!)!.State);
    }

    [Fact]
    public async Task AFollowUpRequestIsNotAttachedWhenTheNameWasPairedAgainDuringTheRun()
    {
        var rig = await MakeRigAsync();
        var a = rig.Client("c4", CompanionLevel.Destructive);
        var env = await rig.Service.CallAsync(a, "team_env_set", new JsonObject { ["team"] = "alpha", ["name"] = "KEEP", ["value"] = "x" }, Ct);
        CompanionClient? b = null;
        rig.OnRoute = (method, _) =>
        {
            if (method == "teams.env" && b is null) { rig.Clients.Revoke("c4"); b = rig.Client("c4", CompanionLevel.Destructive); }
            return Task.CompletedTask;
        };
        var decided = await rig.Approve(env.RequestId!);
        Assert.NotNull(b);
        Assert.Equal(ActionState.Denied, decided.State);
        Assert.Null(decided.NextId);
        Assert.Equal(0, rig.Service.PendingCount);
        Assert.Empty(rig.Service.ListForWindow()["actions"]!.AsArray());
    }

    [Fact]
    public async Task TheAuditNamesOnlyAWellFormedTool()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Destructive);
        foreach (var tool in new[] { "Bad Tool\nforged=1", "team.delete", new string('a', 65), "" })
            Assert.True((await rig.Service.CallAsync(cc, tool, [], Ct)).IsError);
        Assert.True((await rig.Service.CallAsync(cc, "no_such_tool", [], Ct)).IsError);
        var methods = AuditRows(rig).Select(x => x.Method).ToList();
        Assert.Equal(4, methods.Count(m => m == "companion.(unknown)"));
        Assert.Single(methods, m => m == "companion.no_such_tool");
        Assert.DoesNotContain(AuditRows(rig), x => x.Outcome.Contains("forged") || x.Outcome.Contains("team.delete") || x.Outcome.Contains("aaaa"));
        Assert.All(methods, m => Assert.Matches(@"^companion\.([a-z_]{1,64}|\(unknown\))\z", m));
    }

    [Fact]
    public async Task ConcurrentCallsNeverPassTheCapOfPendingRequests()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Destructive);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        rig.OnRoute = async (method, _) =>
        {
            if (method != "ops.backups") return;
            Interlocked.Increment(ref entered);
            await gate.Task;
        };
        var calls = Enumerable.Range(0, 15)
            .Select(_ => Task.Run(() => rig.Service.CallAsync(cc, "backup_restore", new JsonObject { ["backup"] = "daily-20261110.db" }, Ct))).ToList();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Volatile.Read(ref entered) < 15 && DateTime.UtcNow < deadline) await Task.Delay(10, Ct);
        Assert.Equal(15, Volatile.Read(ref entered)); // every call passed the first check while none was stored
        gate.SetResult();
        var results = await Task.WhenAll(calls);
        Assert.Equal(CompanionService.MaxPendingPerClient, results.Count(x => x.RequestId is not null));
        Assert.Equal(5, results.Count(x => x.IsError && x.Text.Contains("already has 10 requests waiting")));
        Assert.Equal(CompanionService.MaxPendingPerClient, rig.Service.PendingCount);
    }

    [Fact]
    public async Task ARequestExpiresBeforeItsToken_SoALateApprovalSaysExpiredNotChanged()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Destructive);
        Assert.Equal(ConfirmationBook.Life - TimeSpan.FromSeconds(5), CompanionService.ActionLife);

        // At its last moment the request is still pending, and an approval then still redeems its token (Done, not Changed).
        var edge = await rig.Service.CallAsync(cc, "team_archive", new JsonObject { ["team"] = "alpha" }, Ct);
        rig.Clock.Now += CompanionService.ActionLife - TimeSpan.FromMilliseconds(1);
        Assert.Equal(ActionState.Pending, rig.Service.Status(cc, edge.RequestId!)!.State);
        rig.OnActions = () => rig.Clock.Now += TimeSpan.FromSeconds(3); // the run starts: a slow one, before it redeems the token
        var ran = await rig.Approve(edge.RequestId!);
        rig.OnActions = null;
        Assert.Equal(ActionState.Done, ran.State);
        Assert.Null(ran.NextId);
        Assert.Single(rig.Ui, u => u.Method == "teams.archive");

        // Inside the token's life but past the request's: the request has expired; nothing runs, no new list is made.
        var late = await rig.Service.CallAsync(cc, "team_delete", new JsonObject { ["team"] = "alpha" }, Ct);
        rig.Clock.Now += ConfirmationBook.Life - TimeSpan.FromSeconds(2);
        Assert.Equal(ActionState.Expired, rig.Service.Status(cc, late.RequestId!)!.State);
        var s = await rig.Approve(late.RequestId!);
        Assert.Equal(ActionState.Expired, s.State);
        Assert.Null(s.NextId);
        Assert.DoesNotContain(rig.Methods, m => m == "teams.delete");
    }

    [Fact]
    public void TellAndAnswerOfferNoImagesOrFollowUp()
    {
        foreach (var level in new[] { CompanionLevel.Act, CompanionLevel.Destructive })
            foreach (var def in CompanionTools.Definitions(level).Where(d => d!["name"]!.GetValue<string>() is "tell" or "answer"))
            {
                var properties = def!["inputSchema"]!["properties"]!.AsObject();
                Assert.False(properties.ContainsKey("images"), def["name"] + " offers images");
                Assert.False(properties.ContainsKey("follow_up"), def["name"] + " offers follow_up");
                Assert.True(properties.ContainsKey("team"));
            }
        Assert.Equal(2, CompanionTools.Definitions(CompanionLevel.Act).Count(d => d!["name"]!.GetValue<string>() is "tell" or "answer"));
    }

    [Fact]
    public async Task ADatabaseErrorAfterTheRunStillFinishesTheRequest_AndDisposeDoesNotFault()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Destructive);
        var env = await rig.Service.CallAsync(cc, "team_env_set", new JsonObject { ["team"] = "alpha", ["name"] = "KEEP", ["value"] = "x" }, Ct);
        Assert.NotNull(env.RequestId);
        // During the run the client store breaks: checking whether the client is still paired (to attach the follow-up) throws.
        rig.OnRoute = (method, _) =>
        {
            if (method != "teams.env") return Task.CompletedTask;
            using var c = new SqliteConnection(_db.ConnectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP TABLE companion_clients;";
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        };
        var decided = await rig.Approve(env.RequestId!);
        Assert.Equal(ActionState.Denied, decided.State);
        Assert.StartsWith("voided:", decided.Result);
        Assert.Null(decided.NextId);
        Assert.Equal(ActionState.Denied, rig.Service.Status(cc, env.RequestId!)!.State);
        Assert.Equal(0, rig.Service.PendingCount); // nothing left waiting or running (the client list itself is broken here)
        await rig.Service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task TheWindowsActionStatusReadsARequestAndDecidesNothing()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc", CompanionLevel.Destructive);
        var r = await rig.Service.CallAsync(cc, "team_delete", new JsonObject { ["team"] = "alpha" }, Ct);
        ActionStatus? s;
        using (Actor.Use(Actor.Window)) s = rig.Service.StatusForWindow(r.RequestId!);
        Assert.Equal(ActionState.Pending, s!.State);
        Assert.Equal(ActionState.Pending, rig.Service.Status(cc, r.RequestId!)!.State);
        Assert.Equal(1, rig.Service.PendingCount);
        Assert.DoesNotContain(rig.Methods, m => m == "teams.delete");
        using (Actor.Use(Actor.Window)) Assert.Null(rig.Service.StatusForWindow("nope"));
        foreach (var who in new Actor?[] { null, Actor.ExternalClient("cc"), Actor.CloudViewer("v@example.test"), new(Actor.Concierge, Actor.LocalUser) })
            Assert.Equal("forbidden", Assert.Throws<RpcException>(() => { using (who is null ? null : Actor.Use(who)) rig.Service.StatusForWindow(r.RequestId!); }).Code);
    }
}
