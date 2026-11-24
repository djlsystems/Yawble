using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Integrations;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Teams;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>
/// The agents' pr_open / pr_status follow the repository's provider (the person's override, else its
/// origin): gh for GitHub, glab for GitLab, az repos for Azure DevOps; Bitbucket, Gitea and no provider say so and run
/// nothing. Fake gh / glab / az scripts that print their arguments; origins are addresses only (nothing is fetched).
/// </summary>
public class PrToolsProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IAsyncDisposable
    {
        public readonly TempDb Db = new();
        public BusEngine Engine = null!;
        public IntegrationService Integrations = null!;
        public TeamRepos Repos = null!;
        public long Alpha;
        public string MainDir = null!;
        private readonly ConcurrentDictionary<int, (string Tool, object Args, TaskCompletionSource<string> Done)> _calls = new();
        private readonly ConcurrentQueue<string[]> _lists = new();
        private int _next;

        /// <summary>Team Alpha (member Ada) whose main repository has <paramref name="origin"/> (null: none), with the fake CLIs named enabled.</summary>
        public static async Task<Rig> Make(string? origin, params string[] clis)
        {
            var r = new Rig();
            r.Repos = new TeamRepos(Path.Combine(r.Db.Dir, "app"), r.Db.ConnectionString);
            var runner = new ScriptedRunner { Default = r.Handle };
            r.Engine = new BusEngine(r.Db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false, Repos = r.Repos });
            r.Integrations = new IntegrationService(r.Db.ConnectionString, r.Engine);
            r.Engine.Integrations = r.Integrations;
            r.Alpha = await r.Engine.CreateTeamAsync("Alpha", ct: Ct);
            await r.Engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            r.MainDir = r.GitDir("main-repo", origin);
            await r.Repos.UseExistingRepoAsync(r.Alpha, "Alpha", r.MainDir, Ct);
            foreach (var cli in clis) await r.AddFakeCli(cli);
            await r.Engine.StartAsync(Ct);
            return r;
        }

        public string GitDir(string name, string? origin)
        {
            var d = Path.Combine(Db.Dir, name);
            Directory.CreateDirectory(d);
            AgentGit.Host(d, "init", "-q", "-b", "main");
            AgentGit.Host(d, "-c", "user.name=p", "-c", "user.email=p@x", "commit", "-q", "--allow-empty", "-m", "first");
            if (origin is not null) AgentGit.Host(d, "remote", "add", "origin", origin);
            return d;
        }

        private async Task AddFakeCli(string name)
        {
            string path;
            if (OperatingSystem.IsWindows())
            {
                path = Path.Combine(Db.Dir, name + ".cmd");
                File.WriteAllText(path, $"@echo off\r\necho {name} ran %*\r\n");
            }
            else
            {
                path = Path.Combine(Db.Dir, name);
                File.WriteAllText(path, $"#!/bin/sh\necho {name} ran \"$@\"\n");
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection(Db.ConnectionString))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "INSERT INTO integrations (kind, name, path, created_at, updated_at) VALUES ('cli', $n, $p, '2026-01-01', '2026-01-01');";
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$p", path);
                cmd.ExecuteNonQuery();
            }
            await Integrations.DispatchAsync("integrations.enable", new JsonObject { ["name"] = name, ["teamId"] = Alpha, ["on"] = true }, Ct);
        }

        private async Task<RunResult> Handle(RunContext run, CancellationToken ct)
        {
            foreach (var (id, call) in _calls)
            {
                if (!run.Items.Any(m => m.Payload.Contains($"call#{id}#", StringComparison.Ordinal))) continue;
                _calls.TryRemove(id, out _);
                try
                {
                    if (call.Tool == "#tools")
                    {
                        var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""), Ct);
                        var names = JsonNode.Parse(reply.Json!)!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>());
                        call.Done.TrySetResult(string.Join("\n", names));
                    }
                    else call.Done.TrySetResult(await CallTool(run, call.Tool, call.Args));
                }
                catch (Exception e) { call.Done.TrySetException(e); }
            }
            return RunResult.Ok;
        }

        private static async Task<string> CallTool(RunContext run, string tool, object args)
        {
            var body = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = System.Text.Json.JsonSerializer.SerializeToNode(args) },
            };
            var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct);
            return JsonNode.Parse(reply.Json!)!["result"]!["content"]![0]!["text"]!.GetValue<string>();
        }

        public async Task<string> Call(string tool, object args)
        {
            var id = Interlocked.Increment(ref _next);
            var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls[id] = (tool, args, done);
            await Engine.PersonTellAsync("Alpha", "Ada", $"call#{id}#", ct: Ct);
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }

        public async Task<string[]> Tools() => (await Call("#tools", new { })).Split('\n');

        public Task<string> PrOpen(string? repo = null) => Call("pr_open", repo is null
            ? new { title = "Greeting", body = "Says hi.", reason = "the work is done" }
            : (object)new { title = "Greeting", body = "Says hi.", reason = "the work is done", repo });

        public Task<string> PrStatus(string? repo = null) => Call("pr_status", repo is null ? new { } : (object)new { repo });

        public IReadOnlyList<AskRow> Pending => Integrations.Asks(Alpha, pendingOnly: true);

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    [Fact]
    public async Task GitLabOrigin_WithOnlyGlab_OffersBoth_StatusRunsGlab_OpenAsksForGlabMrCreate()
    {
        await using var rig = await Rig.Make("git@gitlab.com:acme/app.git", "glab");

        var tools = await rig.Tools();
        Assert.Contains("pr_open", tools);
        Assert.Contains("pr_status", tools);

        var status = await rig.PrStatus();
        Assert.Contains("glab ran mr list --source-branch team/alpha --all --per-page 5 --output json", status);

        var opened = await rig.PrOpen();
        Assert.StartsWith("asked:", opened);
        var ask = Assert.Single(rig.Pending);
        Assert.Equal("glab", ask.Integration);
        Assert.Equal(["mr", "create", "--source-branch", "team/alpha", "--target-branch", "main", "--title", "Greeting", "--description", "Says hi.", "--yes"], ask.Args);

        // glab's checklist has the built-in rows, as gh's has
        var list = rig.Integrations.Discover.GetChecklist("glab", null);
        Assert.Contains(list.Nodes, n => n.Path == "pr_open" && n.Origin == RowOrigin.Builtin);
        Assert.Contains(list.Nodes, n => n.Path == "pr_status" && n.Origin == RowOrigin.Builtin);
    }

    [Fact]
    public async Task GitLabOrigin_WithOnlyGh_OffersNothing_AndACallNeverRunsGh()
    {
        await using var rig = await Rig.Make("https://gitlab.example.com/acme/app.git", "gh");

        var tools = await rig.Tools();
        Assert.DoesNotContain("pr_open", tools);
        Assert.DoesNotContain("pr_status", tools);

        foreach (var said in new[] { await rig.PrOpen(), await rig.PrStatus() })
        {
            Assert.DoesNotContain("gh ran", said);
            Assert.Contains("made from here with glab, which is not enabled for your team", said);
        }
        Assert.Empty(rig.Pending);
    }

    [Fact]
    public async Task GitLabOrigin_WithGhAndGlab_RunsGlab_NotGh()
    {
        await using var rig = await Rig.Make("git@gitlab.com:acme/app.git", "gh", "glab");
        var status = await rig.PrStatus();
        Assert.Contains("glab ran mr list", status);
        Assert.DoesNotContain("gh ran", status);
        Assert.StartsWith("asked:", await rig.PrOpen());
        Assert.Equal("glab", Assert.Single(rig.Pending).Integration);
    }

    [Fact]
    public async Task AzureDevOpsOrigin_WithOnlyAz_OffersBoth_AndRunsAzRepos()
    {
        await using var rig = await Rig.Make("https://dev.azure.com/acme/proj/_git/app", "az");

        var tools = await rig.Tools();
        Assert.Contains("pr_open", tools);
        Assert.Contains("pr_status", tools);

        Assert.Contains("az ran repos pr list --source-branch team/alpha --status all --top 5 --detect true --output json", await rig.PrStatus());
        Assert.StartsWith("asked:", await rig.PrOpen());
        var ask = Assert.Single(rig.Pending);
        Assert.Equal("az", ask.Integration);
        Assert.Equal(["repos", "pr", "create", "--source-branch", "team/alpha", "--target-branch", "main", "--title", "Greeting", "--description", "Says hi.",
            "--detect", "true", "--output", "json"], ask.Args);
    }

    [Fact]
    public async Task GitHubOrigin_RunsGh_AsBefore()
    {
        await using var rig = await Rig.Make("https://github.com/acme/app.git", "gh", "glab", "az");
        Assert.Contains("gh ran pr list --head team/alpha --state all", await rig.PrStatus());
        Assert.StartsWith("asked:", await rig.PrOpen());
        var ask = Assert.Single(rig.Pending);
        Assert.Equal("gh", ask.Integration);
        Assert.Equal(["pr", "create", "--head", "team/alpha", "--base", "main", "--title", "Greeting", "--body", "Says hi."], ask.Args);
    }

    [Fact]
    public async Task GitHubOrigin_WithOnlyGlabOrAz_OffersNothing()
    {
        await using var rig = await Rig.Make("https://github.com/acme/app.git", "glab", "az");
        var tools = await rig.Tools();
        Assert.DoesNotContain("pr_open", tools);
        Assert.DoesNotContain("pr_status", tools);
        Assert.Contains("made from here with gh, which is not enabled", await rig.PrStatus());
    }

    [Theory]
    [InlineData("git@bitbucket.org:acme/app.git", null, "not supported for Bitbucket from here")]
    [InlineData("https://codeberg.org/acme/app.git", null, "not supported for Gitea from here")]
    [InlineData("https://git.example.org/acme/app.git", null, "not supported for git.example.org from here")]
    [InlineData("https://github.com/acme/app.git", RepoProviderKind.Gitea, "not supported for Gitea from here")]
    [InlineData("https://github.com/acme/app.git", RepoProviderKind.Bitbucket, "not supported for Bitbucket from here")]
    [InlineData("https://github.com/acme/app.git", RepoProviderKind.None, "not supported for github.com from here")]
    [InlineData(null, null, "has no remote (no origin)")]
    public async Task NoPullRequestProvider_WithGhEnabled_OffersBoth_ThatSaySoAndRunNothing(string? origin, RepoProviderKind? overrideKind, string expected)
    {
        await using var rig = await Rig.Make(origin, "gh");
        if (overrideKind is { } k) rig.Repos.SetHostSetting(rig.Alpha, null, k, null);

        var tools = await rig.Tools();
        Assert.Contains("pr_open", tools);
        Assert.Contains("pr_status", tools);
        foreach (var said in new[] { await rig.PrOpen(), await rig.PrStatus() })
        {
            Assert.Contains(expected, said);
            Assert.DoesNotContain("gh ran", said);
        }
        Assert.Empty(rig.Pending);
    }

    [Fact]
    public async Task TheOverrideWins_GitLabOriginSetToGitHub_RunsGh()
    {
        await using var rig = await Rig.Make("git@gitlab.com:acme/app.git", "gh", "glab");
        rig.Repos.SetHostSetting(rig.Alpha, null, RepoProviderKind.GitHub, null);
        Assert.Contains("gh ran pr list --head team/alpha", await rig.PrStatus());

        rig.Repos.SetHostSetting(rig.Alpha, null, RepoProviderKind.AzureDevOps, null);
        Assert.Contains("made from here with az, which is not enabled", await rig.PrStatus());
    }

    [Fact]
    public async Task AnotherRepository_FollowsItsOwnProvider()
    {
        await using var rig = await Rig.Make("https://github.com/acme/app.git", "glab");
        var cloud = rig.GitDir("cloud-repo", "git@gitlab.com:acme/cloud.git");
        await rig.Repos.AddExtraAsync(rig.Alpha, "Alpha", "cloud", cloud, null, writable: true, Ct);

        Assert.Contains("pr_open", await rig.Tools()); // offered for cloud, on GitLab
        Assert.Contains("glab ran mr list --source-branch team/alpha", await rig.PrStatus("cloud"));
        Assert.Contains("made from here with gh, which is not enabled", await rig.PrStatus());
    }

    [Fact]
    public async Task GlabAndAz_ShipTheBuiltInsPreset_StatusRunsAndOpenAsks()
    {
        foreach (var entry in new[] { CliCatalog.Generic("glab"), CliCatalog.Of("az")! })
        {
            Assert.Equal(Decisions.Allow, CliRules.Decide(["pr_status"], entry, [], []).Decision);
            Assert.Equal(Decisions.Ask, CliRules.Decide(["pr_open"], entry, [], []).Decision);
        }
        Assert.DoesNotContain(CliCatalog.Generic("fakecli").Preset, p => p.Pattern.StartsWith("pr_", StringComparison.Ordinal));
        Assert.Empty(CliCatalog.BuiltinRows("git"));
        Assert.Equal(2, CliCatalog.BuiltinRows("glab").Count);
    }
}
