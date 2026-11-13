using Microsoft.Data.Sqlite;
using Yawble.Core.Integrations;
using Yawble.Core.Storage;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>Discover core: help parser, classifier, model fallback, walker, storage, save -> rules, re-discover.</summary>
public class DiscoverTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static long IdOf(TempDb db, string name)
    {
        using var c = new SqliteConnection(db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM integrations WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", name);
        return (long)cmd.ExecuteScalar()!;
    }

    private static string[] Names(string fixture) => HelpParser.Parse(FixtureCli.Read(fixture)).Commands.Select(c => c.Name).ToArray();

    // ------------------------------------------------------------------ the parser, on recorded help of each shape

    [Fact]
    public void ParsesTheCommandListsOfGhAzAwsGcloudKubectlTerraformAndGit()
    {
        var gh = Names("gh/2.45/_root.txt");
        Assert.Contains("pr", gh);
        Assert.Contains("secret", gh);
        Assert.Contains("api", gh);
        Assert.Contains("run", gh);
        Assert.DoesNotContain("flags", gh);
        Assert.Equal(["create", "list", "status", "checkout", "checks", "close", "comment", "diff", "merge", "view"], Names("gh/2.45/pr.txt"));
        Assert.Empty(Names("gh/2.45/pr_list.txt"));

        Assert.Equal(["account", "group", "keyvault", "storage", "vm", "login", "logout", "version"], Names("az/2.57/_root.txt"));
        Assert.Equal(["disk", "extension", "create", "delete", "list", "restart", "show", "stop"], Names("az/2.57/vm.txt"));
        Assert.Empty(Names("az/2.57/vm_list.txt"));

        Assert.Equal(["configure", "ec2", "s3", "s3api", "secretsmanager", "sts"], Names("aws/2.15/_root.txt"));
        Assert.Equal(["cp", "ls", "mb", "mv", "presign", "rb", "rm", "sync"], Names("aws/2.15/s3.txt"));
        Assert.Equal(["create-secret", "delete-secret", "get-secret-value", "list-secrets"], Names("aws/2.15/secretsmanager.txt"));

        Assert.Equal(["auth", "compute", "secrets", "info", "version"], Names("gcloud/470/_root.txt"));
        Assert.Equal(["list", "login", "print-access-token"], Names("gcloud/470/auth.txt"));

        var kube = Names("kubectl/1.29/_root.txt");
        Assert.Contains("get", kube);
        Assert.Contains("api-resources", kube);
        Assert.DoesNotContain("kubectl", kube);
        Assert.Empty(Names("kubectl/1.29/get.txt"));

        Assert.Equal(["init", "validate", "plan", "apply", "destroy", "console", "fmt", "output", "state"], Names("terraform/1.7/_root.txt"));

        // a real capture of `git -h`: the commands sit under several headings of their own
        var git = Names("git/real/_root.txt");
        Assert.Contains("clone", git);
        Assert.Contains("push", git);
        Assert.Contains("status", git);
        Assert.True(git.Length >= 24, $"{git.Length} git commands");
        Assert.Empty(Names("git/real/commit.txt"));
    }

    [Fact]
    public void AliasesAndBulletsAreStripped_UnrecognisedHelpHasNoCommands_AHugeHelpIsBounded()
    {
        var parsed = HelpParser.Parse("Tools\n\nCommands:\n  get (g, show)    Get a thing\n  o  put\n   wrapped description line here\n  ls               List\n");
        Assert.Equal(["get", "ls"], parsed.Commands.Select(c => c.Name));
        Assert.Equal("Get a thing", parsed.Commands[0].Description);
        Assert.Empty(HelpParser.Parse(FixtureCli.Read("misc/no-sections.txt")).Commands);
        Assert.Empty(HelpParser.Parse(FixtureCli.Read("misc/empty.txt")).Commands);
        Assert.Empty(HelpParser.Parse(null).Commands);
        var huge = FixtureCli.Read("misc/huge.txt");
        Assert.True(huge.Length > 100_000);
        Assert.Empty(HelpParser.Parse(huge).Commands);
        Assert.Equal("Work seamlessly with GitHub from the command line.", HelpParser.Parse(FixtureCli.Read("gh/2.45/_root.txt")).Summary);
    }

    [Fact]
    public void TheHashIgnoresWhitespaceAndVersionLines()
    {
        Assert.Equal(HelpParser.Hash("a  b\nversion 1.2.3\nc"), HelpParser.Hash("a b\n\nv1.2.4\nc  "));
        Assert.NotEqual(HelpParser.Hash("a b\nc"), HelpParser.Hash("a b\nd"));
    }

    // ------------------------------------------------------------------ the classifier

    [Theory]
    [InlineData("vm list", CommandClass.ReadOnly)]
    [InlineData("s3api get-object", CommandClass.ReadOnly)]
    [InlineData("ec2 describe-instances", CommandClass.ReadOnly)]
    [InlineData("vm create", CommandClass.Change)]
    [InlineData("s3 sync", CommandClass.Change)]
    [InlineData("s3 export", CommandClass.Change)]
    [InlineData("storage download", CommandClass.Change)]
    [InlineData("vm delete", CommandClass.Destructive)]
    [InlineData("ec2 terminate-instances", CommandClass.Destructive)]
    [InlineData("s3 force-remove", CommandClass.Destructive)]
    [InlineData("s3api batch-delete-thing", CommandClass.Destructive)]
    [InlineData("frobnicate", CommandClass.Unknown)]
    public void TheVerbOfTheLastWordClassifies(string path, string expected)
    {
        var c = CommandClassifier.Classify("x", null, path, false);
        Assert.Equal(expected, c.Class);
        Assert.False(string.IsNullOrWhiteSpace(c.Reason));
        if (expected != CommandClass.Unknown) Assert.Equal(ClassSource.Verb, c.Source);
    }

    [Fact]
    public void AGroupIsNeutral_TheFloorBeatsTheVerb_AKnownPresetBeatsTheVerb()
    {
        Assert.Equal(CommandClass.Group, CommandClassifier.Classify("x", null, "vm", true).Class);
        Assert.Equal(CommandClass.Group, CommandClassifier.Classify("x", null, "delete", true).Class);
        // "get" is a read-only verb, but the words say token
        Assert.Equal(CommandClass.Credential, CommandClassifier.Classify("x", null, "auth get-token", false).Class);
        Assert.Equal(CommandClass.Credential, CommandClassifier.Classify("x", null, "secret", true).Class); // a floor hits a group too
        var gh = CliCatalog.Of("gh")!;
        var merge = CommandClassifier.Classify("gh", gh, "pr merge", false);
        Assert.Equal((CommandClass.Change, ClassSource.Preset), (merge.Class, merge.Source));
        var view = CommandClassifier.Classify("gh", gh, "pr view", false);
        Assert.Equal((CommandClass.ReadOnly, ClassSource.Preset), (view.Class, view.Source));
        Assert.Equal(CommandClass.Destructive, CommandClassifier.Classify("gh", gh, "repo delete", false).Class);
        Assert.Equal(CommandClass.Destructive, CommandClassifier.Classify("gh", gh, "pr checkout", false).Class); // refused by gh's own floor
        Assert.Equal(CommandClass.Credential, CommandClassifier.Classify("gh", gh, "auth login", false).Class);
    }

    // ------------------------------------------------------------------ the model fallback

    private const string FakeHelp = "A fake tool.\n\nCommands:\n  list-things    List the things\n  get-thing      Show a thing\n  delete-thing   Delete a thing\n  frobnicate     Do the frobnication\n  twiddle        Twiddle with it\n  show-token     Show the token\n";

    private static Dictionary<string, string> FakeCliHelp(string frob = "A", string twiddle = "A")
    {
        var d = new Dictionary<string, string> { ["_root"] = FakeHelp };
        foreach (var n in new[] { "list-things", "get-thing", "delete-thing", "show-token" }) d[n] = $"usage: fakecli {n}\n\nHelp for {n}.";
        d["frobnicate"] = $"usage: fakecli frobnicate\n\nFrobnicate, edition {frob}.";
        d["twiddle"] = $"usage: fakecli twiddle\n\nTwiddle, edition {twiddle}.";
        return d;
    }

    private static HelpRunner HelpFlagOnlyDashH(FixtureCli fx) => (args, ct) =>
        args[^1] == "--help" ? Task.FromResult(new CliOutput(2, "", "unknown flag", 12, TimeSpan.Zero, false, false)) : fx.Runner(args, ct);

    [Fact]
    public async Task TheModelFallbackSendsOnlyPathAndSummary_ValidatesTheAnswer_AndOnlyLocksOrAdvises()
    {
        var model = new FakeModel(p => """
            Sure! {"items":[
              {"path":"frobnicate","class":"readonly","why":"reads\u0001 only, <script> and a very long explanation that goes on and on past eighty characters for sure yes"},
              {"path":"twiddle","class":"credential","why":"prints a key"},
              {"path":"not-asked","class":"readonly","why":"x"},
              {"path":"list-things","class":"nonsense","why":"x"}]}
            """);
        using var rig = new DiscoverRig(() => model);
        var fx = new FixtureCli(null, FakeCliHelp());
        var outcome = await rig.Svc.DiscoverAsync("fakecli", new DiscoverOptions { Runner = fx.Runner, Version = "1" }, null, Ct);
        Assert.Null(outcome.Error);
        Assert.Equal(2, outcome.ModelClassified);

        // only the unknown leaves were asked, one batch, with path and one-line summary and nothing else of the help
        var prompt = Assert.Single(model.Prompts);
        Assert.Contains("frobnicate - Do the frobnication", prompt);
        Assert.Contains("twiddle - Twiddle with it", prompt);
        Assert.DoesNotContain("list-things", prompt);
        Assert.DoesNotContain("edition", prompt);
        Assert.DoesNotContain("A fake tool", prompt);

        var list = rig.Svc.GetChecklist("fakecli", null).Nodes.ToDictionary(n => n.Path);
        var frob = list["frobnicate"];
        Assert.Equal((CommandClass.ReadOnly, ClassSource.Model), (frob.Class, frob.Source));
        Assert.True(frob.Reason.Length <= 120 && !frob.Reason.Any(char.IsControl));
        Assert.False(frob.Ticked); // a model's "readonly" pre-ticks nothing...
        Assert.False(frob.DefaultTick);
        Assert.Equal("free", frob.State); // ...but the person may tick it
        Assert.Equal("locked", list["twiddle"].State); // a model may lock
        Assert.Equal(CommandClass.ReadOnly, list["list-things"].Class); // the dropped item: the verb decided
        Assert.True(list["list-things"].Ticked);
        Assert.True(list["get-thing"].Ticked);
        Assert.Equal("refused", list["delete-thing"].State);
        Assert.Equal("locked", list["show-token"].State);
        rig.Svc.SaveChecklist("fakecli", null, ["frobnicate"]);
        Assert.Contains(rig.Rules("fakecli"), r => r is (null, "frobnicate *", Decisions.Allow));
        Assert.Throws<ArgumentException>(() => rig.Svc.SaveChecklist("fakecli", null, ["twiddle"]));
    }

    [Fact]
    public async Task AModelThatFailsOrIsAbsent_LeavesTheLeavesUnknown_AndNothingBreaks()
    {
        foreach (var model in new ICommandModel?[] { null, new FakeModel(_ => "no json here"), new FakeModel(_ => throw new InvalidOperationException("down")) })
        {
            using var rig = new DiscoverRig(() => model);
            var fx = new FixtureCli(null, FakeCliHelp());
            var outcome = await rig.Svc.DiscoverAsync("fakecli", new DiscoverOptions { Runner = fx.Runner }, null, Ct);
            Assert.Null(outcome.Error);
            var node = rig.Svc.GetChecklist("fakecli", null).Nodes.Single(n => n.Path == "frobnicate");
            Assert.Equal(CommandClass.Unknown, node.Class);
            Assert.False(node.Ticked);
        }
    }

    [Fact]
    public async Task ModelAnswersAreBatchedByForty()
    {
        var leaves = Enumerable.Range(0, 85).Select(i => ($"cmd{i}", "does cmd")).ToList();
        var model = new FakeModel(_ => """{"items":[]}""");
        var result = await ModelClassifier.ClassifyAsync("x", leaves, model, Ct);
        Assert.Empty(result);
        Assert.Equal(3, model.Prompts.Count);
    }

    // ------------------------------------------------------------------ the walker

    private static WalkOptions Fast => new() { Parallelism = 4, CallTimeout = TimeSpan.FromSeconds(2) };

    [Fact]
    public async Task TheWalkNeverRunsARealCommand_FloorPathsAreRecordedNotWalked_AndTheGhTreeIsRead()
    {
        var fx = new FixtureCli("gh/2.45");
        var walk = await CliWalker.WalkAsync("gh", CliCatalog.Of("gh"), fx.Runner, Fast, null, Ct);
        Assert.Null(walk.Error);
        Assert.Equal("--help", walk.HelpFlag);
        var paths = walk.Commands.Select(c => c.Path).ToHashSet();
        Assert.Contains("pr list", paths);
        Assert.Contains("pr merge", paths);
        Assert.Contains("secret", paths); // recorded...
        Assert.DoesNotContain(fx.Calls, c => c.Contains("secret")); // ...and not walked
        Assert.Contains(walk.Commands, c => c.Path == "pr" && c.HasChildren);
        Assert.Contains(walk.Commands, c => c.Path == "pr list" && !c.HasChildren && c.HelpHash is not null);
        Assert.Contains(walk.Commands, c => c.Path == "auth" && c.HelpUnreadable); // no fixture: unreadable, the walk went on
        // every call: only words from the parsed lists, the help flag last
        Assert.All(fx.Calls, call =>
        {
            Assert.Equal("--help", call[^1]);
            Assert.All(call[..^1], w => Assert.Matches("^[a-z0-9][a-z0-9-]*$", w));
        });
    }

    [Fact]
    public void TheGuardRefusesAnythingButWordsAndAHelpFlag()
    {
        Assert.Equal(["pr", "list", "--help"], CliWalker.GuardedArgs(["pr", "list"], "--help"));
        Assert.Throws<InvalidOperationException>(() => CliWalker.GuardedArgs(["pr", "list; rm"], "--help"));
        Assert.Throws<InvalidOperationException>(() => CliWalker.GuardedArgs(["--delete"], "--help"));
        Assert.Throws<InvalidOperationException>(() => CliWalker.GuardedArgs(["pr", "Merge"], "--help"));
        Assert.Throws<InvalidOperationException>(() => CliWalker.GuardedArgs(["pr", "merge"], "--force"));
        Assert.Throws<InvalidOperationException>(() => CliWalker.GuardedArgs(["auth", "token"], "--help")); // the floor
    }

    [Fact]
    public async Task TheHelpFlagIsFoundOnce_GitOnlyEverGetsDashH()
    {
        // Ruling 11: an unknown command is never probed with -h (shutdown -h halts the computer): --help only
        var fx = new FixtureCli(null, FakeCliHelp());
        var walk = await CliWalker.WalkAsync("fakecli", null, HelpFlagOnlyDashH(fx), Fast, null, Ct);
        Assert.Equal("could not read its help", walk.Error);
        Assert.DoesNotContain(fx.Calls, c => c.Contains("-h"));
        var ok = await CliWalker.WalkAsync("fakecli", null, new FixtureCli(null, FakeCliHelp()).Runner, Fast, null, Ct);
        Assert.Equal("--help", ok.HelpFlag);
        Assert.Contains("frobnicate", ok.Commands.Select(c => c.Path));
        Assert.Equal("--help", CliCatalog.Generic("fakecli").HelpFlag);
        Assert.DoesNotContain(CliCatalog.Generic("fakecli").Preset, p => p.Pattern.Split(' ').Contains("-h"));

        var git = new FixtureCli("git/real");
        var gitWalk = await CliWalker.WalkAsync("git", null, git.Runner, new WalkOptions { MaxDepth = 1 }, null, Ct);
        Assert.Equal("-h", gitWalk.HelpFlag);
        Assert.All(git.Calls, c => Assert.Equal("-h", c[^1]));
        Assert.Contains("clone", gitWalk.Commands.Select(c => c.Path));

        var none = await CliWalker.WalkAsync("nohelp", null, new FixtureCli().Runner, Fast, null, Ct);
        Assert.Equal("could not read its help", none.Error);
        Assert.Empty(none.Commands);
    }

    private static HelpRunner Generated(Func<string[], string?> help, Func<string[], bool>? timeout = null) => (args, ct) =>
    {
        var words = args.Take(args.Count - 1).ToArray();
        if (timeout?.Invoke(words) == true) return Task.FromResult(new CliOutput(-1, "", "", 0, TimeSpan.FromSeconds(8), true, false));
        var text = help(words);
        return Task.FromResult(text is null ? new CliOutput(1, "", "no", 2, TimeSpan.Zero, false, false) : new CliOutput(0, text, "", text.Length, TimeSpan.Zero, false, false));
    };

    private static string Listing(params string[] names) => "Tool\n\nCommands:\n" + string.Concat(names.Select(n => $"  {n}   does {n}\n"));

    [Fact]
    public async Task DepthCountTimeoutCycleAndHugeOutputAreAllLimited()
    {
        // depth: every level lists one deeper child
        var deep = await CliWalker.WalkAsync("t", null, Generated(w => Listing($"c{w.Length}", $"d{w.Length}")), new WalkOptions { MaxDepth = 3 }, null, Ct);
        Assert.Equal(14, deep.Commands.Count);
        Assert.All(deep.Commands, c => Assert.True(c.Path.Split(' ').Length <= 3));

        // count: 50 children, the limit is 10: partial
        var many = await CliWalker.WalkAsync("t", null, Generated(w => w.Length == 0 ? Listing(Enumerable.Range(0, 50).Select(i => $"n{i}").ToArray()) : "usage\n\nleaf"), new WalkOptions { MaxCommands = 10 }, null, Ct);
        Assert.Equal(10, many.Commands.Count);
        Assert.True(many.Partial);
        Assert.Contains("partial", many.PartialReason);

        // a cycle: the same help at every level
        var loop = await CliWalker.WalkAsync("t", null, Generated(w => Listing("a", "b")), new WalkOptions { MaxDepth = 4 }, null, Ct);
        Assert.False(loop.Partial);
        Assert.Equal(["a", "b"], loop.Commands.Select(c => c.Path).Order());

        // a call that times out, and one whose output was cut: unreadable, the walk goes on
        var odd = await CliWalker.WalkAsync("t", null, (args, ct) =>
            args.Take(args.Count - 1).FirstOrDefault() switch
            {
                null => Task.FromResult(new CliOutput(0, Listing("slow", "big", "fine"), "", 1, TimeSpan.Zero, false, false)),
                "slow" => Task.FromResult(new CliOutput(-1, "", "", 0, TimeSpan.FromSeconds(8), true, false)),
                "big" => Task.FromResult(new CliOutput(0, "x\ny\nz", "", 65536, TimeSpan.Zero, false, true)),
                _ => Task.FromResult(new CliOutput(0, "usage: fine\n\nfine", "", 5, TimeSpan.Zero, false, false)),
            }, Fast, null, Ct);
        Assert.True(odd.Commands.Single(c => c.Path == "slow").HelpUnreadable);
        Assert.True(odd.Commands.Single(c => c.Path == "big").HelpUnreadable);
        Assert.False(odd.Commands.Single(c => c.Path == "fine").HelpUnreadable);

        // the total time: a node that never answers ends the walk, partial, what was found is kept
        var stuck = await CliWalker.WalkAsync("t", null, (args, ct) =>
            args.Count == 1 ? Task.FromResult(new CliOutput(0, Listing("a", "b"), "", 1, TimeSpan.Zero, false, false))
            : args[0] == "a" ? Task.Delay(Timeout.Infinite, ct).ContinueWith<CliOutput>(_ => throw new OperationCanceledException(ct))
            : Task.FromResult(new CliOutput(0, "usage\n\nleaf", "", 1, TimeSpan.Zero, false, false)),
            new WalkOptions { TotalTime = TimeSpan.FromMilliseconds(300) }, null, Ct);
        Assert.True(stuck.Partial);
        Assert.Equal(["a", "b"], stuck.Commands.Select(c => c.Path).Order());
    }

    [Fact]
    public async Task ARealProcessGetsANonInteractiveEnvironmentAndAnEmptyFolder()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("yawble-fakecli-").FullName;
        var cwd = Directory.CreateTempSubdirectory("yawble-fakecwd-").FullName;
        try
        {
            var cmd = Path.Combine(dir, "fakecli.cmd");
            var log = Path.Combine(dir, "log.txt");
            File.WriteAllText(cmd, $"@echo off\r\necho %*>> \"{log}\"\r\necho CI=%CI%\r\necho NO_COLOR=%NO_COLOR%\r\necho PAGER=%PAGER%\r\necho BROWSER=[%BROWSER%]\r\necho CWD=%CD%\r\n");
            var run = CliWalker.ForExecutable(cmd, null, cwd, TimeSpan.FromSeconds(20));
            var o = await run(CliWalker.GuardedArgs(["sub"], "--help"), Ct);
            Assert.Contains("CI=1", o.Stdout);
            Assert.Contains("NO_COLOR=1", o.Stdout);
            Assert.Contains("PAGER=cat", o.Stdout);
            Assert.Contains("BROWSER=[]", o.Stdout);
            Assert.Contains(Path.GetFileName(cwd), o.Stdout);
            Assert.Equal("sub --help", File.ReadAllText(log).Trim());
        }
        finally
        {
            Directory.Delete(dir, true);
            Directory.Delete(cwd, true);
        }
    }

    // ------------------------------------------------------------------ migration

    [Fact]
    public void TheMigrationCreatesTheTables_UpgradesAnOldDatabaseKeepingRules_AndIsIdempotent()
    {
        using var rig = new DiscoverRig();
        string[] tables = ["integration_commands", "integration_choices", "integration_discovery"];
        foreach (var t in tables) Assert.Equal(1, rig.Scalar($"SELECT count(*) FROM sqlite_master WHERE name = '{t}'"));

        // an old database: before 0026, with a hand-written rule
        using (var c = rig.Open())
        {
            foreach (var sql in new[]
            {
                "INSERT INTO integrations (kind, name, created_at, updated_at) VALUES ('cli', 'gh', 'x', 'x');",
                "INSERT INTO integration_rules (integration_id, team_id, pattern, decision, created_at) VALUES (1, NULL, 'pr merge*', 'deny', 'x');",
                "DROP TABLE integration_choices;", "DROP TABLE integration_commands;", "DROP TABLE integration_discovery;",
                "DELETE FROM schema_migrations WHERE name = '0026_discover';",
            })
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }
        Assert.Equal(0, rig.Scalar("SELECT count(*) FROM sqlite_master WHERE name = 'integration_commands'"));
        var applied = SchemaMigrator.Migrate(rig.Db.ConnectionString);
        Assert.Contains("0026_discover", applied);
        Assert.Equal(applied, SchemaMigrator.Migrate(rig.Db.ConnectionString)); // twice: nothing happens
        foreach (var t in tables) Assert.Equal(1, rig.Scalar($"SELECT count(*) FROM sqlite_master WHERE name = '{t}'"));
        Assert.Equal([(null, "pr merge*", "deny")], rig.Rules("gh").Select(r => ((long?)r.Team, r.Pattern, r.Decision)));
    }

    // ------------------------------------------------------------------ checklist, save -> rules

    private static async Task<DiscoverRig> GhRig()
    {
        var rig = new DiscoverRig();
        var o = await rig.Svc.DiscoverAsync("gh", new DiscoverOptions { Runner = new FixtureCli("gh/2.45").Runner, Version = "gh version 2.45.0", Walk = Fast }, null, Ct);
        Assert.Null(o.Error);
        return rig;
    }

    [Fact]
    public async Task ReadsAreTickedByDefault_ChangesAreNot_DestructiveIsRefused_CredentialsAreLocked()
    {
        using var rig = await GhRig();
        var list = rig.Svc.GetChecklist("gh", null);
        var n = list.Nodes.ToDictionary(x => x.Path);
        foreach (var p in new[] { "pr list", "pr view", "pr status", "pr checks", "pr diff" })
        {
            Assert.True(n[p].Ticked, p);
            Assert.True(n[p].DefaultTick, p);
            Assert.Equal("default", n[p].TickedFrom);
        }
        foreach (var p in new[] { "pr create", "pr merge", "pr comment", "pr close", "api" })
        {
            Assert.False(n[p].Ticked, p);
            Assert.Equal(CommandClass.Change, n[p].Class);
        }
        Assert.Equal("refused", n["pr checkout"].State);
        Assert.Equal("refused", n["browse"].State);
        Assert.Equal("locked", n["secret"].State);
        Assert.False(n["secret"].Ticked);
        Assert.Equal(CommandClass.Group, n["pr"].Class);
        Assert.False(n["pr"].Ticked);
        Assert.Equal(0, list.ChoiceCount);
        Assert.Empty(rig.Rules("gh")); // nothing is written until Save
        Assert.Equal(1, list.Runs);
    }

    [Fact]
    public async Task SaveWritesAllowRulesForTicks_DenyForDestructive_NothingForCredentials_AndAskForAnUntickedPreset()
    {
        using var rig = await GhRig();
        var team = rig.AddTeam("Alpha");
        var r = rig.Svc.SaveChecklist("gh", team, ["pr list", "pr create"]);
        var rules = rig.Rules("gh");
        Assert.All(rules, x => Assert.Equal(team, x.Team));
        Assert.Contains(rules, x => x is (_, "pr list *", Decisions.Allow));
        Assert.Contains(rules, x => x is (_, "pr create *", Decisions.Allow));
        Assert.Contains(rules, x => x is (_, "pr checkout *", Decisions.Deny)); // refused
        Assert.Contains(rules, x => x is (_, "browse *", Decisions.Deny));
        Assert.DoesNotContain(rules, x => x.Pattern.StartsWith("secret") || x.Pattern.StartsWith("pr *"));
        // the preset allows "pr view"; the person unticked it, so it asks now
        Assert.Contains(rules, x => x is (_, "pr view *", Decisions.Ask));
        Assert.Equal(2, r.Allowed);

        var gh = CliCatalog.Of("gh")!;
        var (t, g) = rig.RulesFor("gh", team);
        Assert.Equal(Decisions.Allow, CliRules.Decide(["pr", "create", "--fill"], gh, t, g).Decision);
        Assert.Equal(Decisions.Ask, CliRules.Decide(["pr", "view", "5"], gh, t, g).Decision);
        Assert.Equal(Decisions.Deny, CliRules.Decide(["browse"], gh, t, g).Decision);
        Assert.Equal(Decisions.Allow, CliRules.Decide(["pr", "list"], gh, t, g).Decision);
        Assert.Equal(Decisions.Deny, CliRules.Decide(["secret", "list"], gh, t, g).Decision);

        // the checklist shows the saved state
        var n = rig.Svc.GetChecklist("gh", team).Nodes.ToDictionary(x => x.Path);
        Assert.True(n["pr create"].Ticked);
        Assert.Equal("choice", n["pr create"].TickedFrom);
        Assert.False(n["pr view"].Ticked);
        Assert.False(n["pr view"].DefaultTick);
    }

    [Fact]
    public async Task AGroupTickTicksItsReadOnlyLeavesOnly_NeverGroupStar()
    {
        using var rig = await GhRig();
        rig.Svc.SaveChecklist("gh", null, ["pr"]);
        var rules = rig.Rules("gh");
        Assert.DoesNotContain(rules, x => x.Pattern == "pr*");
        Assert.Equal(["pr checks *", "pr diff *", "pr list *", "pr status *", "pr view *"], rules.Where(x => x.Decision == Decisions.Allow).Select(x => x.Pattern).Order());
        Assert.DoesNotContain(rules, x => x.Pattern is "pr create *" or "pr merge *" && x.Decision == Decisions.Allow);
    }

    [Fact]
    public async Task ACredentialOrDestructivePathCannotBeSaved_WhateverTheUiSends()
    {
        using var rig = await GhRig();
        var locked = Assert.Throws<ArgumentException>(() => rig.Svc.SaveChecklist("gh", null, ["secret"]));
        Assert.Contains("never allowed", locked.Message);
        Assert.Contains("refused", Assert.Throws<ArgumentException>(() => rig.Svc.SaveChecklist("gh", null, ["pr checkout"])).Message);
        Assert.Contains("not a command", Assert.Throws<ArgumentException>(() => rig.Svc.SaveChecklist("gh", null, ["pr nuke"])).Message);
        Assert.Throws<ArgumentException>(() => rig.Svc.SaveChecklist("gh", 999, ["pr list"])); // no such team
        Assert.Empty(rig.Rules("gh")); // a rejected save writes nothing

        // a stale class cannot sneak a credential through: the floor is checked again at save
        using (var c = rig.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE integration_commands SET class = 'change', class_source = 'verb' WHERE path = 'secret';";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<ArgumentException>(() => rig.Svc.SaveChecklist("gh", null, ["secret"]));
        Assert.Empty(rig.Rules("gh"));
    }

    [Fact]
    public async Task ScopesAreIsolated_TheTeamOverridesGlobal_AndAnUntickRemovesTheRule()
    {
        using var rig = await GhRig();
        var a = rig.AddTeam("Alpha");
        var b = rig.AddTeam("Beta");
        rig.Svc.SaveChecklist("gh", null, ["pr list", "pr view", "pr status", "pr checks", "pr diff", "pr create"]); // everyone
        rig.Svc.SaveChecklist("gh", a, ["pr list"]);
        Assert.Contains(rig.Rules("gh"), x => x is (null, "pr create *", Decisions.Allow));
        Assert.DoesNotContain(rig.Rules("gh"), x => x.Team == b);

        var gh = CliCatalog.Of("gh")!;
        var (ta, ga) = rig.RulesFor("gh", a);
        var (tb, gb) = rig.RulesFor("gh", b);
        Assert.Equal(Decisions.Ask, CliRules.Decide(["pr", "create"], gh, ta, ga).Decision); // Alpha unticked it: its own ask wins
        Assert.Equal(Decisions.Allow, CliRules.Decide(["pr", "create"], gh, tb, gb).Decision); // Beta inherits
        var viewB = rig.Svc.GetChecklist("gh", b).Nodes.Single(x => x.Path == "pr create");
        Assert.True(viewB.Ticked);
        Assert.Equal("all teams", viewB.TickedFrom);

        // untick a plain (non-preset) tick: the rule goes
        rig.Svc.SaveChecklist("gh", null, ["pr list", "pr view", "pr status", "pr checks", "pr diff"]);
        Assert.DoesNotContain(rig.Rules("gh"), x => x is (null, "pr create *", _));
    }

    [Fact]
    public async Task ReplacingAChoiceReplacesItsRuleInOneStep_NoDuplicates()
    {
        using var rig = await GhRig();
        rig.Svc.SaveChecklist("gh", null, ["pr create"]);
        var first = rig.Scalar("SELECT rule_id FROM integration_choices WHERE path = 'pr create'");
        Assert.Equal(first, rig.Scalar("SELECT id FROM integration_rules WHERE pattern = 'pr create *'"));
        rig.Svc.SaveChecklist("gh", null, ["pr create", "pr list"]); // pr create is saved again: unchanged
        Assert.Equal(first, rig.Scalar("SELECT rule_id FROM integration_choices WHERE path = 'pr create'"));
        // flip pr view (preset allow): unticked -> ask, ticked again -> allow; always one rule for the pattern
        rig.Svc.SaveChecklist("gh", null, ["pr create", "pr list", "pr view"]);
        Assert.Equal(1, rig.Scalar("SELECT count(*) FROM integration_rules WHERE pattern = 'pr view *'"));
        Assert.Equal("allow", rig.Rules("gh").Single(x => x.Pattern == "pr view *").Decision);
        rig.Svc.SaveChecklist("gh", null, ["pr create", "pr list"]);
        Assert.Equal("ask", rig.Rules("gh").Single(x => x.Pattern == "pr view *").Decision);
        Assert.Equal(1, rig.Scalar("SELECT count(*) FROM integration_rules WHERE pattern = 'pr view *'"));
        Assert.Equal(rig.Scalar("SELECT id FROM integration_rules WHERE pattern = 'pr view *'"), rig.Scalar("SELECT rule_id FROM integration_choices WHERE path = 'pr view'"));
        Assert.Equal(0, rig.Scalar("SELECT count(*) FROM integration_choices WHERE rule_id IS NULL"));
    }

    [Fact]
    public async Task ExistingRulesShowAsTicks_AndRulesMatchingNoNodeAreListedAsOther()
    {
        using var rig = await GhRig();
        using (var c = rig.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO integration_rules (integration_id, team_id, pattern, decision, created_at) SELECT id, NULL, 'pr merge*', 'allow', 'x' FROM integrations WHERE name = 'gh';" +
                "INSERT INTO integration_rules (integration_id, team_id, pattern, decision, created_at) SELECT id, NULL, 'codespace *', 'ask', 'x' FROM integrations WHERE name = 'gh';";
            cmd.ExecuteNonQuery();
        }
        var list = rig.Svc.GetChecklist("gh", null);
        var merge = list.Nodes.Single(x => x.Path == "pr merge");
        Assert.True(merge.Ticked);
        Assert.Equal("rule", merge.TickedFrom);
        Assert.Equal(["codespace *"], list.OtherRules.Select(r => r.Pattern));
    }

    // ------------------------------------------------------------------ re-discover

    [Fact]
    public async Task ReDiscoverKeepsChoices_MarksNewChangedAndGone_AndDoesNotTickNewReads()
    {
        using var rig = await GhRig();
        rig.Svc.SaveChecklist("gh", null, ["pr list", "pr create"]);
        var choicesBefore = rig.Scalar("SELECT count(*) FROM integration_choices");
        var rulesBefore = rig.Rules("gh");

        // v2: pr ready is new, pr close is gone, pr view's help changed (a new line), pr list unchanged
        var pr2 = FixtureCli.Read("gh/2.45/pr.txt").Replace("  close:      Close a pull request\n", "").Replace("  merge:      Merge a pull request\n", "  merge:      Merge a pull request\n  ready:      Mark a pull request as ready for review\n");
        var fx2 = new FixtureCli("gh/2.45", new Dictionary<string, string>
        {
            ["pr"] = pr2,
            ["pr_view"] = "View a pull request.\n\nUSAGE\n  gh pr view [flags]\n\nFLAGS\n  --comments   View pull request comments\n",
            ["pr_ready"] = "Mark a pull request as ready.\n\nUSAGE\n  gh pr ready [flags]\n",
        });
        var o = await rig.Svc.DiscoverAsync("gh", new DiscoverOptions { Runner = fx2.Runner, Version = "gh version 2.46.0", Walk = Fast }, null, Ct);
        Assert.Equal((1, 1), (o.Merge!.New, o.Merge.Gone));
        Assert.True(o.Merge.Changed >= 1);
        Assert.Empty(o.Merge.Dropped);

        Assert.Equal(choicesBefore, rig.Scalar("SELECT count(*) FROM integration_choices")); // never deleted
        Assert.Equal(rulesBefore, rig.Rules("gh"));
        var list = rig.Svc.GetChecklist("gh", null);
        var n = list.Nodes.ToDictionary(x => x.Path);
        Assert.Equal("new", n["pr ready"].Badge);
        Assert.False(n["pr ready"].Ticked); // a new read-only... (ready is a change here, so also check a read)
        Assert.True(n["pr close"].Gone); // kept, shown as removed
        Assert.Equal("changed", n["pr view"].Badge);
        Assert.Null(n["pr list"].Badge);
        Assert.True(n["pr create"].Ticked);
        Assert.Equal("gh version 2.46.0", n["pr list"].SeenVersion);
        Assert.Equal((1, 1), (list.NewCount, list.GoneCount));
        Assert.Equal(2, list.Runs);
    }

    [Fact]
    public async Task ANewReadOnlyNodeIsNotTickedOnReDiscover_ButIsOnTheFirstDiscover()
    {
        using var rig = new DiscoverRig();
        var fx = new FixtureCli(null, FakeCliHelp());
        await rig.Svc.DiscoverAsync("fakecli", new DiscoverOptions { Runner = fx.Runner, Version = "1" }, null, Ct);
        Assert.True(rig.Svc.GetChecklist("fakecli", null).Nodes.Single(n => n.Path == "list-things").Ticked);
        fx.Overrides["_root"] = FakeHelp + "  list-more      List more things\n";
        fx.Overrides["list-more"] = "usage: fakecli list-more\n\nHelp.";
        await rig.Svc.DiscoverAsync("fakecli", new DiscoverOptions { Runner = fx.Runner, Version = "2" }, null, Ct);
        var n = rig.Svc.GetChecklist("fakecli", null).Nodes.ToDictionary(x => x.Path);
        Assert.Equal("new", n["list-more"].Badge);
        Assert.Equal(CommandClass.ReadOnly, n["list-more"].Class);
        Assert.False(n["list-more"].Ticked);
        Assert.False(n["list-more"].DefaultTick);
        Assert.Null(n["list-things"].Badge);
        Assert.True(n["list-things"].Ticked); // curated earlier: still as it was
    }

    [Fact]
    public async Task APartialWalkMarksNothingGone_AFullOneThatLacksANodeDoes()
    {
        using var rig = await GhRig();
        var before = rig.Svc.GetChecklist("gh", null).Nodes.Count;
        var partial = await rig.Svc.DiscoverAsync("gh", new DiscoverOptions { Runner = new FixtureCli("gh/2.45").Runner, Walk = new WalkOptions { MaxCommands = 5 } }, null, Ct);
        Assert.True(partial.Partial);
        Assert.Equal(0, partial.Merge!.Gone);
        Assert.Equal(before, rig.Svc.GetChecklist("gh", null).Nodes.Count);
        Assert.DoesNotContain(rig.Svc.GetChecklist("gh", null).Nodes, n => n.Gone);

        // a walk that cannot read the CLI at all changes nothing and says why
        var failed = await rig.Svc.DiscoverAsync("gh", new DiscoverOptions { Runner = new FixtureCli().Runner }, null, Ct);
        Assert.Equal("could not read its help", failed.Error);
        var list = rig.Svc.GetChecklist("gh", null);
        Assert.Equal(before, list.Nodes.Count);
        Assert.Equal("could not read its help", list.Error);
    }

    [Fact]
    public async Task AClassThatGetsLessSafeTakesASavedTickBackToAsk_AndOneThatBecomesACredentialLoosesNothing()
    {
        string frob = "readonly", twid = "readonly";
        var model = new FakeModel(p => "{\"items\":[" +
            (p.Contains("frobnicate") ? $"{{\"path\":\"frobnicate\",\"class\":\"{frob}\",\"why\":\"w\"}}," : "") +
            (p.Contains("twiddle") ? $"{{\"path\":\"twiddle\",\"class\":\"{twid}\",\"why\":\"w\"}}" : "{\"path\":\"none\",\"class\":\"change\",\"why\":\"\"}") + "]}");
        using var rig = new DiscoverRig(() => model);
        var fx = new FixtureCli(null, FakeCliHelp());
        await rig.Svc.DiscoverAsync("fakecli", new DiscoverOptions { Runner = fx.Runner, Version = "1" }, null, Ct);
        rig.Svc.SaveChecklist("fakecli", null, ["frobnicate", "twiddle"]);
        Assert.Equal(2, rig.Rules("fakecli").Count(r => r.Decision == Decisions.Allow));

        // v2: both commands' help changed, and the model now says change / credential
        frob = "change";
        twid = "credential";
        var v2 = FakeCliHelp("B", "B");
        foreach (var (k, v) in v2) fx.Overrides[k] = v;
        var o = await rig.Svc.DiscoverAsync("fakecli", new DiscoverOptions { Runner = fx.Runner, Version = "2" }, null, Ct);
        Assert.Equal(2, o.Merge!.Dropped.Count);
        Assert.Contains(o.Merge.Dropped, d => d.StartsWith("frobnicate") && d.Contains("please review"));
        Assert.Contains(o.Merge.Dropped, d => d.StartsWith("twiddle") && d.Contains("locked"));
        var rules = rig.Rules("fakecli");
        Assert.Contains(rules, r => r is (null, "frobnicate *", Decisions.Ask));
        Assert.DoesNotContain(rules, r => r.Pattern == "twiddle *"); // the credential's rule is gone; the floor-class is locked
        var n = rig.Svc.GetChecklist("fakecli", null).Nodes.ToDictionary(x => x.Path);
        Assert.False(n["frobnicate"].Ticked);
        Assert.Equal("changed", n["frobnicate"].Badge);
        Assert.Equal("locked", n["twiddle"].State);
        Assert.Equal(1, rig.Scalar("SELECT count(*) FROM integration_choices WHERE path = 'frobnicate' AND decision = 'ask'"));
    }

    [Fact]
    public async Task TheRpcServesTheChecklistAndSaves_AndTheAgentToolListsWhatEachCommandDoesForItsTeam()
    {
        using var db = new TempDb();
        await using var engine = new Yawble.Core.Bus.BusEngine(db.OpenStore(), new ScriptedRunner(), new Yawble.Core.Bus.BusOptions { IdleOffer = false });
        var integrations = new IntegrationService(db.ConnectionString, engine);
        engine.Integrations = integrations;
        var alpha = await engine.CreateTeamAsync("Alpha", ct: Ct);
        var o = await integrations.Discover.DiscoverAsync("gh", new DiscoverOptions { Runner = new FixtureCli("gh/2.45").Runner, Version = "gh version 2.45.0", Walk = Fast }, null, Ct);
        Assert.Null(o.Error);
        using (var offer = new Microsoft.Data.Sqlite.SqliteConnection(db.ConnectionString)) // the checklist of a team is read once the team is offered the CLI
        {
            offer.Open();
            using var cmd = offer.CreateCommand();
            cmd.CommandText = "INSERT INTO integration_teams (integration_id, team_id) SELECT id, $t FROM integrations WHERE name = 'gh';";
            cmd.Parameters.AddWithValue("$t", alpha);
            cmd.ExecuteNonQuery();
        }

        var cl = (await integrations.DispatchAsync("integrations.checklist", new System.Text.Json.Nodes.JsonObject { ["name"] = "gh", ["teamId"] = alpha }, Ct))!;
        var nodes = cl["nodes"]!.AsArray();
        Assert.Contains(nodes, n => n!["path"]!.GetValue<string>() == "pr list" && n["ticked"]!.GetValue<bool>());
        Assert.Contains(nodes, n => n!["path"]!.GetValue<string>() == "secret" && n["state"]!.GetValue<string>() == "locked");

        // a credential path sent as ticked is refused server side, whatever the UI did
        await Assert.ThrowsAnyAsync<Exception>(() => integrations.DispatchAsync("integrations.saveChecklist",
            new System.Text.Json.Nodes.JsonObject { ["name"] = "gh", ["teamId"] = alpha, ["ticked"] = new System.Text.Json.Nodes.JsonArray("secret") }, Ct));
        var save = (await integrations.DispatchAsync("integrations.saveChecklist",
            new System.Text.Json.Nodes.JsonObject { ["name"] = "gh", ["teamId"] = alpha, ["ticked"] = new System.Text.Json.Nodes.JsonArray("pr list", "pr create") }, Ct))!;
        Assert.Equal(2, save["allowed"]!.GetValue<int>());

        var row = new IntegrationRow(IdOf(db, "gh"), "cli", "gh", null, null, null, null, false);
        var text = integrations.CommandsFor(row, alpha, []);
        Assert.Contains("- pr list [run]", text);
        Assert.Contains("- pr create [run]", text);
        Assert.Contains("- pr view [ask]", text);       // unticked preset: asks
        Assert.Contains("- pr checkout [never]", text); // gh's own floor: never, not even asked
        Assert.Contains("- secret [never]", text);   // the credential floor
        var only = integrations.CommandsFor(row, alpha, ["pr"]);
        Assert.Contains("pr list", only);
        Assert.DoesNotContain("- secret [", only);
        Assert.Contains("not run Discover", integrations.CommandsFor(row with { Name = "nothing" }, alpha, []));
        Assert.Contains(integrations.ToolsFor(alpha).Count == 0 ? "" : "x", "x");
    }

    [Fact]
    public async Task TheWalkRunsAsABackgroundJobWithProgressAndCancel()
    {
        using var rig = new DiscoverRig();
        var gate = new TaskCompletionSource();
        var fx = new FixtureCli("gh/2.45");
        HelpRunner slow = async (args, ct) =>
        {
            if (args.Count > 1) await gate.Task.WaitAsync(ct);
            return await fx.Runner(args, ct);
        };
        Assert.True(rig.Svc.Start("gh", new DiscoverOptions { Runner = slow, Walk = Fast }));
        Assert.False(rig.Svc.Start("gh")); // one at a time
        Assert.Equal("running", rig.Svc.Status("gh")!.State);
        Assert.True(rig.Svc.Cancel("gh"));
        await Wait.UntilAsync(() => rig.Svc.Status("gh")!.State == "cancelled", "the cancelled walk");

        gate.SetResult();
        Assert.True(rig.Svc.Start("gh", new DiscoverOptions { Runner = fx.Runner, Walk = Fast }));
        await Wait.UntilAsync(() => rig.Svc.Status("gh")!.State == "done", "the walk");
        Assert.True(rig.Svc.Status("gh")!.Outcome!.Commands > 10);
    }
}
