using System.Diagnostics;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Policy;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// The tester's check of repository providers: gaps left by RepoProviderTests. Links for self-hosted GitLab (port, nested groups),
/// visualstudio.com and its ssh form, an origin carrying a credential, Azure compare by commit, the link pattern's rules
/// as the contract and docs state them, and the UI RPC contract (teams.setRepo clears with "" too, "MAIN", an extra on
/// Bitbucket, an unpushed commit, unknown repository) through the window's dispatcher.
/// </summary>
public sealed class RepoProviderVerifyTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private readonly TempDb _db = new();
    private readonly string _work = Path.Combine(Path.GetTempPath(), "y448" + Guid.NewGuid().ToString("N")[..6]);
    private BusEngine _bus = null!;
    private RpcDispatcher _rpc = null!;
    private string _cfg = null!;

    public async ValueTask InitializeAsync()
    {
        var appRoot = Path.Combine(_db.Dir, "app");
        Directory.CreateDirectory(appRoot);
        var terminals = new TerminalHub(new AppEvents());
        BusEngine? busRef = null;
        var runner = new FakePtyRunner(terminals, id => busRef!.MemberAsync(id).GetAwaiter().GetResult()?.Cli ?? "claude", ticks: 1, tick: TimeSpan.FromMilliseconds(10));
        _bus = await BusHost.StartAsync(_db.ConnectionString, _ => null, null, launcher: null, runner, () => 4, appDataRoot: appRoot);
        busRef = _bus;
        var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
        var ui = new UiRpc(_bus, new PolicyStore(_db.ConnectionString), new MemberLayouts(Path.Combine(appRoot, "data"), _db.Dir, home), new FakeModelCredentials(),
            new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, _bus), terminals, 16_384);
        _rpc = new RpcDispatcher(new Sandboxing.SandboxPlatformInfo(), () => [], ui: ui);
        _cfg = Path.Combine(_db.Dir, "empty.gitconfig");
        File.WriteAllText(_cfg, "");
        Directory.CreateDirectory(_work);
    }

    public async ValueTask DisposeAsync()
    {
        await _bus.DisposeAsync();
        foreach (var dir in new[] { _work, _work + "bb" }.Where(Directory.Exists))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
        _db.Dispose();
    }

    private static string? L(string origin, WebLinkRequest req, RepoHostSetting? s = null) => RepoProviders.Link(origin, s, req);

    // ------------------------------------------------------------------ (2) origins and links per provider, the gaps

    [Fact]
    public void SelfHostedGitLab_WithAPortAndNestedGroups()
    {
        const string https = "https://gitlab.corp.example:8443/platform/payments/backend/api.git";
        Assert.Equal(RepoProviderKind.GitLab, RepoProviders.Detect(https));
        Assert.Equal("https://gitlab.corp.example:8443/platform/payments/backend/api/-/commit/" + Sha, L(https, new(WebLinkKind.Commit, Commit: Sha)));
        Assert.Equal("https://gitlab.corp.example:8443/platform/payments/backend/api/-/merge_requests/12", L(https, new(WebLinkKind.Pr, Pr: 12)));
        // ssh with its own port: the site is https on the default port; the groups are kept.
        const string ssh = "ssh://git@gitlab.corp.example:2222/platform/payments/backend/api.git";
        Assert.Equal("https://gitlab.corp.example/platform/payments/backend/api/-/tree/members/t/ada", L(ssh, new(WebLinkKind.Branch, Branch: "members/t/ada")));
        // scp form, nested groups
        Assert.Equal("https://gitlab.corp.example/platform/payments/api/-/blob/" + Sha + "/src/x.cs",
            L("git@gitlab.corp.example:platform/payments/api.git", new(WebLinkKind.File, Commit: Sha, File: "src/x.cs")));
    }

    [Fact]
    public void AzureDevOps_VisualStudioCom_AndTheSshForms()
    {
        Assert.Equal("https://acme.visualstudio.com/DefaultCollection/Proj/_git/app/commit/" + Sha,
            L("https://acme.visualstudio.com/DefaultCollection/Proj/_git/app", new(WebLinkKind.Commit, Commit: Sha)));
        Assert.Equal("https://acme.visualstudio.com/Proj/_git/app/pullrequest/7",
            L("acme@vs-ssh.visualstudio.com:v3/acme/Proj/app", new(WebLinkKind.Pr, Pr: 7)));
        Assert.Equal("https://dev.azure.com/acme/Proj/_git/app?version=GBmain",
            L("git@ssh.dev.azure.com:v3/acme/Proj/app", new(WebLinkKind.Branch, Branch: "main")));
        // compare by commit: GC, by branch: GB
        Assert.Equal($"https://dev.azure.com/acme/Proj/_git/app/branchCompare?baseVersion=GC{Sha}&targetVersion=GBteam%2Fx",
            L("https://dev.azure.com/acme/Proj/_git/app", new(WebLinkKind.Compare, From: Sha, To: "team/x")));
    }

    [Fact]
    public void ACredentialInTheOrigin_NeverReachesALink()
    {
        foreach (var origin in new[] { "https://pat:s3cr3tTOKEN@github.com/acme/app.git", "https://oauth2:s3cr3tTOKEN@gitlab.com/acme/app.git", "https://acme:s3cr3tTOKEN@dev.azure.com/acme/P/_git/app" })
        {
            var url = L(origin, new(WebLinkKind.Commit, Commit: Sha));
            Assert.NotNull(url);
            Assert.DoesNotContain("s3cr3tTOKEN", url);
            Assert.DoesNotContain("@", url);
            var patterned = L(origin, new(WebLinkKind.Commit, Commit: Sha), new RepoHostSetting(null, "{base}/c/{commit}"));
            Assert.DoesNotContain("s3cr3tTOKEN", patterned);
        }
    }

    [Fact]
    public void AnUnknownHost_HasNoLink_AndNoneOnAKnownHostTurnsLinksOff()
    {
        Assert.Null(L("https://git.example.com/acme/app.git", new(WebLinkKind.Commit, Commit: Sha)));
        Assert.Null(L("https://github.com/acme/app.git", new(WebLinkKind.Commit, Commit: Sha), new RepoHostSetting(RepoProviderKind.None, null)));
        // The override reads an unknown host its own way.
        Assert.Equal("https://git.example.com/acme/app/-/commit/" + Sha,
            L("git@git.example.com:acme/app.git", new(WebLinkKind.Commit, Commit: Sha), new RepoHostSetting(RepoProviderKind.GitLab, null)));
    }

    // ------------------------------------------------------------------ (3) the link pattern, as the card and docs state it

    [Fact]
    public void LinkPattern_BaseIsTheRepositorysWebAddress_PathIsOwnerName_EachTemplateByItsExactPlaceholders()
    {
        const string origin = "git@git.example.com:acme/app.git"; // no provider
        var p = new RepoHostSetting(null,
            "{base}/c/{commit}  https://git.example.com/{path}/b/{branch}\nhttps://x.example/{path}/f/{commit}/{file} {base}/cmp/{from}..{to} {base}/pr/{pr}");
        Assert.Equal("https://git.example.com/acme/app/c/" + Sha, L(origin, new(WebLinkKind.Commit, Commit: Sha), p));
        Assert.Equal("https://git.example.com/acme/app/b/members/t/ada", L(origin, new(WebLinkKind.Branch, Branch: "members/t/ada"), p));
        Assert.Equal($"https://x.example/acme/app/f/{Sha}/src/a%20b.cs", L(origin, new(WebLinkKind.File, Commit: Sha, File: "src/a b.cs"), p));
        Assert.Equal("https://git.example.com/acme/app/cmp/main..team/x", L(origin, new(WebLinkKind.Compare, From: "main", To: "team/x"), p));
        Assert.Equal("https://git.example.com/acme/app/pr/9", L(origin, new(WebLinkKind.Pr, Pr: 9), p));

        // The docs' example: commit and branch links only, the other kinds none (no provider).
        var doc = new RepoHostSetting(null, "https://git.example.com/{path}/commit/{commit} https://git.example.com/{path}/src/{branch}");
        Assert.Equal("https://git.example.com/acme/app/commit/" + Sha, L(origin, new(WebLinkKind.Commit, Commit: Sha), doc));
        Assert.Equal("https://git.example.com/acme/app/src/main", L(origin, new(WebLinkKind.Branch, Branch: "main"), doc));
        Assert.Null(L(origin, new(WebLinkKind.File, Commit: Sha, File: "a"), doc));
        Assert.Null(L(origin, new(WebLinkKind.Compare, From: "main", To: "x"), doc));
        Assert.Null(L(origin, new(WebLinkKind.Pr, Pr: 1), doc));

        // A pattern on a provider's host: kinds it does not cover keep the provider's own layout.
        var gh = new RepoHostSetting(null, "https://mirror.example/{path}/c/{commit}");
        Assert.Equal("https://mirror.example/acme/app/c/" + Sha, L("https://github.com/acme/app.git", new(WebLinkKind.Commit, Commit: Sha), gh));
        Assert.Equal("https://github.com/acme/app/pull/3", L("https://github.com/acme/app.git", new(WebLinkKind.Pr, Pr: 3), gh));

        // {commit} alone is commit; {commit}+{file} is file, never commit.
        var fileOnly = new RepoHostSetting(null, "{base}/f/{commit}/{file}");
        Assert.Null(L(origin, new(WebLinkKind.Commit, Commit: Sha), fileOnly));
    }

    [Theory]
    [InlineData("{base}/c/{commit}", null)]
    [InlineData("https://h.example/{path}/{branch}", null)]
    [InlineData("{base}/{path}/f/{commit}/{file}", null)]
    [InlineData("{base}/compare/{from}...{to}", null)]
    [InlineData("{base}/pr/{pr}", null)]
    [InlineData("{base}/c/{from}", "fits no link")]          // {from} without {to}
    [InlineData("{base}/{file}", "fits no link")]            // {file} without {commit}
    [InlineData("{base}/pr/{pr}/{commit}", "fits no link")]
    [InlineData("ftp://h.example/{commit}", "does not start with")]
    [InlineData("{base}/{Commit}", "unknown placeholder")]
    public void APatternIsCheckedWhenSaved(string pattern, string? problem)
    {
        var why = RepoProviders.PatternProblem(pattern);
        if (problem is null) Assert.Null(why); else Assert.Contains(problem, why);
    }

    // ------------------------------------------------------------------ (1) the UI RPC contract end to end

    [Fact]
    public async Task Contract_SetRepoClearsWithEmptyStrings_MainByAnyName_BitbucketExtra_UnpushedHasNoLink()
    {
        Git(_work, "init", "-q", "-b", "main");
        Git(_work, "commit", "-q", "--allow-empty", "-m", "first");
        var pushed = Git(_work, "rev-parse", "HEAD");
        Git(_work, "remote", "add", "origin", "https://oauth2:s3cr3tTOKEN@gitlab.corp.example:8443/grp/sub/app.git");
        Git(_work, "update-ref", "refs/remotes/origin/main", pushed);
        var teamId = await _bus.CreateTeamAsync("web", "Manager", "claude", _work, Ct);
        await _bus.Repos!.EnsureTeamRepoAsync(teamId, "web", Ct);
        Git(_work, "commit", "-q", "--allow-empty", "-m", "local only");
        var unpushed = Git(_work, "rev-parse", "HEAD");

        async Task<string?> Link(JsonObject p) => (await Call("git.webLink", p))["url"]?.GetValue<string>();

        // Links by team name only and by id; the credential never shows; an unpushed commit has none.
        Assert.Equal("https://gitlab.corp.example:8443/grp/sub/app/-/commit/" + pushed, await Link(new() { ["team"] = "web", ["kind"] = "commit", ["commit"] = pushed }));
        Assert.Null(await Link(new() { ["teamId"] = teamId, ["team"] = "web", ["kind"] = "commit", ["commit"] = unpushed }));
        Assert.Null(await Link(new() { ["teamId"] = teamId, ["kind"] = "file", ["commit"] = unpushed, ["file"] = "a.cs" }));
        Assert.Equal("https://gitlab.corp.example:8443/grp/sub/app/-/compare/main...main",
            await Link(new() { ["teamId"] = teamId, ["kind"] = "compare", ["from"] = "main", ["to"] = "main" }));
        Assert.Equal("https://gitlab.corp.example:8443/grp/sub/app/-/merge_requests/4", await Link(new() { ["teamId"] = teamId, ["kind"] = "pr", ["pr"] = 4 }));

        // teams.repos main: the five fields; provider null while detecting.
        var main = (await Call("teams.repos", new JsonObject { ["teamId"] = teamId }))["main"]!.AsObject();
        foreach (var k in new[] { "provider", "providerDetected", "linkPattern", "prSupported", "prUnavailable" }) Assert.True(main.ContainsKey(k), k);
        Assert.Null(main["provider"]);
        Assert.Equal("gitlab", main["providerDetected"]!.GetValue<string>());

        // name "MAIN" is main; the pattern saved is used; "" provider and "" pattern clear it.
        var set = await Call("teams.setRepo", new JsonObject { ["teamId"] = teamId, ["name"] = "MAIN", ["provider"] = "none", ["linkPattern"] = "{base}/c/{commit}" });
        Assert.Equal(("none", "{base}/c/{commit}"), (set["provider"]!.GetValue<string>(), set["linkPattern"]!.GetValue<string>()));
        Assert.Equal("https://gitlab.corp.example:8443/grp/sub/app/c/" + pushed, await Link(new() { ["teamId"] = teamId, ["kind"] = "commit", ["commit"] = pushed }));
        Assert.Null(await Link(new() { ["teamId"] = teamId, ["kind"] = "commit", ["commit"] = unpushed })); // a pattern does not make an unpushed commit linkable
        Assert.Null(await Link(new() { ["teamId"] = teamId, ["kind"] = "pr", ["pr"] = 4 }));            // none + no {pr} template
        var cleared = await Call("teams.setRepo", new JsonObject { ["teamId"] = teamId, ["provider"] = "", ["linkPattern"] = "" });
        Assert.Null(cleared["provider"]);
        Assert.Null(cleared["linkPattern"]);
        Assert.Equal("https://gitlab.corp.example:8443/grp/sub/app/-/commit/" + pushed, await Link(new() { ["teamId"] = teamId, ["kind"] = "commit", ["commit"] = pushed }));

        // An extra on Bitbucket: its entry says pull requests are not supported; its links are Bitbucket's.
        var bb = _work + "bb";
        Directory.CreateDirectory(bb);
        Git(bb, "init", "-q", "-b", "main");
        Git(bb, "commit", "-q", "--allow-empty", "-m", "first");
        var bsha = Git(bb, "rev-parse", "HEAD");
        Git(bb, "remote", "add", "origin", "git@bitbucket.org:acme/tools.git");
        Git(bb, "update-ref", "refs/remotes/origin/main", bsha);
        await Call("teams.addRepo", new JsonObject { ["teamId"] = teamId, ["path"] = bb, ["name"] = "tools" });
        var extra = (await Call("teams.repos", new JsonObject { ["teamId"] = teamId }))["extras"]!.AsArray().Single()!.AsObject();
        foreach (var k in new[] { "provider", "providerDetected", "linkPattern", "prSupported", "prUnavailable" }) Assert.True(extra.ContainsKey(k), k);
        Assert.Equal(("bitbucket", false), (extra["providerDetected"]!.GetValue<string>(), extra["prSupported"]!.GetValue<bool>()));
        Assert.Contains("not supported for Bitbucket", extra["prUnavailable"]!.GetValue<string>());
        Assert.Equal("https://bitbucket.org/acme/tools/commits/" + bsha, await Link(new() { ["teamId"] = teamId, ["repo"] = "tools", ["kind"] = "commit", ["commit"] = bsha }));
        // The extra's override leaves main alone; an unknown repository is refused.
        await Call("teams.setRepo", new JsonObject { ["teamId"] = teamId, ["name"] = "tools", ["provider"] = "gitea", ["linkPattern"] = null });
        Assert.Equal("https://bitbucket.org/acme/tools/commit/" + bsha, await Link(new() { ["teamId"] = teamId, ["repo"] = "tools", ["kind"] = "commit", ["commit"] = bsha }));
        Assert.Null((await Call("teams.repos", new JsonObject { ["teamId"] = teamId }))["main"]!["provider"]);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<RpcException>(() => _rpc.DispatchAsync("teams.setRepo", new JsonObject { ["teamId"] = teamId, ["name"] = "nope", ["provider"] = "github" }, Ct))).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<RpcException>(() => _rpc.DispatchAsync("git.webLink", new JsonObject { ["teamId"] = teamId, ["repo"] = "nope", ["kind"] = "pr", ["pr"] = 1 }, Ct))).Code);
    }

    private async Task<JsonObject> Call(string method, JsonObject p) => (await _rpc.DispatchAsync(method, p, Ct))!.AsObject();

    private string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir };
        psi.Environment["GIT_CONFIG_GLOBAL"] = _cfg;
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        foreach (var a in new[] { "-c", "user.name=T", "-c", "user.email=t@example.test", "-c", "commit.gpgsign=false" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {e}");
        return o.Trim();
    }
}
