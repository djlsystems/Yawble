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
/// Card 457 (B075 re-check, tester): card 448's F1 repros as tests, from angles card 455's own tests do not take: only glab
/// or only az enabled against providers with no pull requests here and against no origin; Azure DevOps on visualstudio.com;
/// self-hosted GitLab with a port and nested groups; no pull request CLI at all; a credential in the origin never reaches
/// the text; a person's rule allowing gh's pr_open does not make gh run on a GitLab origin. Rig copied from card 455's.
/// </summary>
public class PrToolsProviderRecheckTests
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

    private static readonly string[] Clis = ["gh", "glab", "az"];

    private static void RanNothing(string said)
    {
        foreach (var cli in Clis) Assert.DoesNotContain(cli + " ran", said);
    }

    [Fact]
    public async Task OnlyGlab_GitLabOrigin_Offered_StatusRunsGlab_OpenAsksGlab()
    {
        await using var rig = await Rig.Make("https://gitlab.com/acme/app.git", "glab");
        var tools = await rig.Tools();
        Assert.Contains("pr_open", tools);
        Assert.Contains("pr_status", tools);
        Assert.Contains("glab ran mr list --source-branch team/alpha", await rig.PrStatus());
        Assert.StartsWith("asked:", await rig.PrOpen());
        var ask = Assert.Single(rig.Pending);
        Assert.Equal("glab", ask.Integration);
        Assert.Equal("mr", ask.Args[0]);
        Assert.Equal("create", ask.Args[1]);
    }

    [Theory]
    [InlineData("https://gitlab.example.com:8443/acme/platform/app.git")]
    [InlineData("ssh://git@gitlab.example.com:2222/acme/platform/app.git")]
    public async Task OnlyGlab_SelfHostedGitLabWithPortAndNestedGroups_RunsGlab(string origin)
    {
        await using var rig = await Rig.Make(origin, "glab");
        // an unknown host is detected as None; the person sets GitLab (Settings > Repositories), then glab runs
        rig.Repos.SetHostSetting(rig.Alpha, null, RepoProviderKind.GitLab, null);
        Assert.Contains("pr_status", await rig.Tools());
        Assert.Contains("glab ran mr list --source-branch team/alpha", await rig.PrStatus());
    }

    [Fact]
    public async Task OnlyGh_GitLabOrigin_ErrorNamesGlab_GhPrCreateNeverRuns_EvenWhenGhPrOpenIsAllowed()
    {
        await using var rig = await Rig.Make("git@gitlab.com:acme/app.git", "gh");
        Assert.DoesNotContain("pr_open", await rig.Tools());
        var opened = await rig.PrOpen();
        Assert.Contains("glab", opened);
        Assert.Contains("not enabled for your team", opened);
        Assert.Contains("glab auth login", opened);
        RanNothing(opened);
        Assert.Empty(rig.Pending);
        // nothing asked and nothing run: gh's audit has no pr create
        Assert.DoesNotContain(rig.Integrations.Asks(rig.Alpha, pendingOnly: false), a => a.Integration == "gh");
    }

    [Theory]
    [InlineData("https://dev.azure.com/acme/Proj/_git/app")]
    [InlineData("https://acme.visualstudio.com/Proj/_git/app")]
    [InlineData("git@ssh.dev.azure.com:v3/acme/Proj/app")]
    [InlineData("acme@vs-ssh.visualstudio.com:v3/acme/Proj/app")]
    public async Task OnlyAz_AzureDevOpsOrigins_Offered_RunAzRepos(string origin)
    {
        await using var rig = await Rig.Make(origin, "az");
        Assert.Contains("pr_status", await rig.Tools());
        Assert.Contains("az ran repos pr list --source-branch team/alpha --status all --top 5 --detect true --output json", await rig.PrStatus());
        Assert.StartsWith("asked:", await rig.PrOpen());
        var ask = Assert.Single(rig.Pending);
        Assert.Equal("az", ask.Integration);
        Assert.Equal(["repos", "pr", "create", "--source-branch", "team/alpha", "--target-branch", "main", "--title", "Greeting",
            "--description", "Says hi.", "--detect", "true", "--output", "json"], ask.Args);
    }

    [Theory]
    [InlineData("glab", "git@bitbucket.org:acme/app.git", null, "not supported for Bitbucket from here")]
    [InlineData("az", "git@bitbucket.org:acme/app.git", null, "not supported for Bitbucket from here")]
    [InlineData("glab", "https://gitea.com/acme/app.git", null, "not supported for Gitea from here")]
    [InlineData("az", "https://codeberg.org/acme/app.git", null, "not supported for Gitea from here")]
    [InlineData("glab", "https://git.example.org/acme/app.git", null, "not supported for git.example.org from here")]
    [InlineData("az", "https://gitlab.com/acme/app.git", RepoProviderKind.None, "not supported for gitlab.com from here")]
    [InlineData("glab", "https://gitlab.com/acme/app.git", RepoProviderKind.Bitbucket, "not supported for Bitbucket from here")]
    [InlineData("glab", null, null, "has no remote (no origin)")]
    [InlineData("az", null, null, "has no remote (no origin)")]
    public async Task OnlyGlabOrAz_NoPullRequestProvider_Offered_SaysSo_NothingRuns(string cli, string? origin, RepoProviderKind? set, string expected)
    {
        await using var rig = await Rig.Make(origin, cli);
        if (set is { } k) rig.Repos.SetHostSetting(rig.Alpha, null, k, null);
        var tools = await rig.Tools();
        Assert.Contains("pr_open", tools);
        Assert.Contains("pr_status", tools);
        foreach (var said in new[] { await rig.PrOpen(), await rig.PrStatus() })
        {
            Assert.Contains(expected, said);
            RanNothing(said);
        }
        Assert.Empty(rig.Pending);
    }

    [Fact]
    public async Task NoOrigin_TextNamesTheRepository()
    {
        await using var rig = await Rig.Make(null, "gh", "glab", "az");
        Assert.Contains("main has no remote (no origin)", await rig.PrStatus());
    }

    [Theory]
    [InlineData("https://github.com/acme/app.git")]
    [InlineData("git@gitlab.com:acme/app.git")]
    [InlineData(null)]
    public async Task NoPullRequestCliEnabled_NotOffered(string? origin)
    {
        await using var rig = await Rig.Make(origin);
        var tools = await rig.Tools();
        Assert.DoesNotContain("pr_open", tools);
        Assert.DoesNotContain("pr_status", tools);
    }

    [Fact]
    public async Task ACredentialInTheOrigin_NeverReachesTheText()
    {
        await using var rig = await Rig.Make("https://wren:s3cr3tT0ken@bitbucket.org/acme/app.git", "glab");
        var said = await rig.PrStatus();
        Assert.Contains("not supported for Bitbucket from here", said);
        Assert.DoesNotContain("s3cr3tT0ken", said);
        Assert.DoesNotContain("wren:", said);
        RanNothing(said);
    }

    [Fact]
    public async Task UnknownRepoName_RefusedBeforeAnythingRuns()
    {
        await using var rig = await Rig.Make("https://github.com/acme/app.git", "gh");
        var said = await rig.PrStatus("nope");
        RanNothing(said);
        Assert.Empty(rig.Pending);
    }

    [Fact]
    public async Task ExtraWithNoOrigin_MainOnGitHubWithOnlyGlab_OfferedForTheExtra_MainNamesGh()
    {
        await using var rig = await Rig.Make("https://github.com/acme/app.git", "glab");
        var local = rig.GitDir("local-repo", null);
        await rig.Repos.AddExtraAsync(rig.Alpha, "Alpha", "local", local, null, writable: true, Ct);
        Assert.Contains("pr_status", await rig.Tools());
        Assert.Contains("local has no remote (no origin)", await rig.PrStatus("local"));
        var main = await rig.PrStatus();
        Assert.Contains("made from here with gh, which is not enabled", main);
        RanNothing(main);
    }
}
