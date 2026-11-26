using System.Diagnostics;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Link;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// The contract the shell builds against: git.webLink {teamId, team, repo?, kind, ...} -> {url},
/// teams.repos' provider fields (provider = the override) and teams.setRepo, through the window's dispatcher.
/// </summary>
public sealed class RepoProviderRpcTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempDb _db = new();
    private readonly string _work = Path.Combine(Path.GetTempPath(), "yrepo" + Guid.NewGuid().ToString("N")[..6]);
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
        foreach (var dir in new[] { _work, _work + "lib", _work + "bb" }.Where(Directory.Exists))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
        _db.Dispose();
    }

    private async Task<JsonObject> Call(string method, JsonObject p) => (await _rpc.DispatchAsync(method, p, Ct))!.AsObject();

    [Fact]
    public async Task TheShellsContract_LinksRepositoryFieldsAndTheOverride()
    {
        Git("init", "-q", "-b", "main");
        Git("commit", "-q", "--allow-empty", "-m", "first");
        var sha = Git("rev-parse", "HEAD");
        Git("remote", "add", "origin", "git@gitlab.com:acme/web/app.git");
        Git("update-ref", "refs/remotes/origin/main", sha);
        var teamId = await _bus.CreateTeamAsync("web", "Manager", "claude", _work, Ct);
        await _bus.Repos!.EnsureTeamRepoAsync(teamId, "web", Ct);

        // teams.repos: the main repository's provider fields. provider is the OVERRIDE (null: detecting).
        var main = (await Call("teams.repos", new JsonObject { ["teamId"] = teamId }))["main"]!.AsObject();
        Assert.True(main.ContainsKey("provider"));
        Assert.Null(main["provider"]);
        Assert.Equal("gitlab", main["providerDetected"]!.GetValue<string>());
        Assert.False(main.ContainsKey("providerOverride"));
        Assert.True(main.ContainsKey("linkPattern"));
        Assert.Null(main["linkPattern"]);
        Assert.True(main.ContainsKey("prSupported"));
        Assert.True(main.ContainsKey("prUnavailable"));

        // git.webLink by teamId + team name (TidyPanel's pair), by team name, and by team id; repo omitted is main.
        var link = await Call("git.webLink", new JsonObject { ["teamId"] = teamId, ["team"] = "web", ["kind"] = "commit", ["commit"] = sha });
        Assert.Equal("https://gitlab.com/acme/web/app/-/commit/" + sha, link["url"]!.GetValue<string>());
        Assert.Equal(link["url"]!.GetValue<string>(), (await Call("git.webLink", new JsonObject { ["team"] = "web", ["kind"] = "commit", ["commit"] = sha }))["url"]!.GetValue<string>());
        var byId = await Call("git.webLink", new JsonObject { ["team"] = teamId, ["kind"] = "pr", ["pr"] = 3 });
        Assert.Equal("https://gitlab.com/acme/web/app/-/merge_requests/3", byId["url"]!.GetValue<string>());
        // An unpushed commit or a branch origin lacks has no link: { url: null }.
        var none = await Call("git.webLink", new JsonObject { ["teamId"] = teamId, ["team"] = "web", ["kind"] = "branch", ["branch"] = "members/web/ada" });
        Assert.True(none.ContainsKey("url"));
        Assert.Null(none["url"]);
        Git("commit", "-q", "--allow-empty", "-m", "second");
        Assert.Null((await Call("git.webLink", new JsonObject { ["teamId"] = teamId, ["kind"] = "commit", ["commit"] = Git("rev-parse", "HEAD") }))["url"]);
        var bad = await Assert.ThrowsAsync<RpcException>(() => _rpc.DispatchAsync("git.webLink", new JsonObject { ["team"] = "web", ["kind"] = "tag" }, Ct));
        Assert.Equal("bad_params", bad.Code);
        var unknownRepo = await Assert.ThrowsAsync<RpcException>(() => _rpc.DispatchAsync("git.webLink", new JsonObject { ["teamId"] = teamId, ["repo"] = "nope", ["kind"] = "pr", ["pr"] = 1 }, Ct));
        Assert.Equal("invalid", unknownRepo.Code);

        // teams.setRepo (name omitted: main): the override and pattern, saved; it answers the entry as teams.repos shows it.
        var set = await Call("teams.setRepo", new JsonObject { ["teamId"] = teamId, ["provider"] = "none", ["linkPattern"] = "{base}/c/{commit}" });
        Assert.Equal(("none", "gitlab", false), (set["provider"]!.GetValue<string>(), set["providerDetected"]!.GetValue<string>(), set["prSupported"]!.GetValue<bool>()));
        Assert.Contains("not supported", set["prUnavailable"]!.GetValue<string>());
        var listed = (await Call("teams.repos", new JsonObject { ["teamId"] = teamId }))["main"]!.AsObject();
        Assert.Equal(listed.ToJsonString(), set.ToJsonString());
        Assert.Equal("{base}/c/{commit}", listed["linkPattern"]!.GetValue<string>());
        // {base} is the repository's web address; {path} its owner/name path.
        Assert.Equal("https://gitlab.com/acme/web/app/c/" + sha,
            (await Call("git.webLink", new JsonObject { ["teamId"] = teamId, ["team"] = "web", ["kind"] = "commit", ["commit"] = sha }))["url"]!.GetValue<string>());
        await Call("teams.setRepo", new JsonObject { ["teamId"] = teamId, ["name"] = "main", ["provider"] = "none", ["linkPattern"] = "https://mirror.example.com/{path}/c/{commit}" });
        Assert.Equal("https://mirror.example.com/acme/web/app/c/" + sha,
            (await Call("git.webLink", new JsonObject { ["team"] = "web", ["kind"] = "commit", ["commit"] = sha }))["url"]!.GetValue<string>());
        var refused = await Assert.ThrowsAsync<RpcException>(() => _rpc.DispatchAsync("teams.setRepo", new JsonObject { ["teamId"] = teamId, ["provider"] = "sourceforge" }, Ct));
        Assert.Equal("invalid", refused.Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<RpcException>(() => _rpc.DispatchAsync("teams.setRepo",
            new JsonObject { ["teamId"] = teamId, ["linkPattern"] = "https://x.example/{nope}" }, Ct))).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<RpcException>(() => _rpc.DispatchAsync("teams.setRepo",
            new JsonObject { ["teamId"] = teamId, ["name"] = "nope", ["provider"] = "github" }, Ct))).Code);
        // provider null and linkPattern null clear the override: detection again.
        var cleared = await Call("teams.setRepo", new JsonObject { ["teamId"] = teamId, ["name"] = null, ["provider"] = null, ["linkPattern"] = null });
        Assert.Null(cleared["provider"]);
        Assert.Null(cleared["linkPattern"]);
        Assert.Equal("gitlab", cleared["providerDetected"]!.GetValue<string>());
        Assert.Equal("https://gitlab.com/acme/web/app/-/commit/" + sha,
            (await Call("git.webLink", new JsonObject { ["teamId"] = teamId, ["team"] = "web", ["kind"] = "commit", ["commit"] = sha }))["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task TeamsSetRepo_OnAnExtra_AnswersItsEntry_AndGitWebLinkReachesIt()
    {
        var teamId = await _bus.CreateTeamAsync("web", "Manager", "claude", _work, Ct);
        var extra = _work + "lib";
        Directory.CreateDirectory(extra);
        GitIn(extra, "init", "-q", "-b", "main");
        GitIn(extra, "commit", "-q", "--allow-empty", "-m", "first");
        var sha = GitIn(extra, "rev-parse", "HEAD");
        GitIn(extra, "remote", "add", "origin", "https://github.com/owner/name.git");
        GitIn(extra, "update-ref", "refs/remotes/origin/main", sha);
        await Call("teams.addRepo", new JsonObject { ["teamId"] = teamId, ["path"] = extra, ["name"] = "lib" });

        var entry = (await Call("teams.repos", new JsonObject { ["teamId"] = teamId }))["extras"]!.AsArray().Single()!.AsObject();
        Assert.Equal("lib", entry["name"]!.GetValue<string>());
        Assert.Null(entry["provider"]);
        Assert.Equal("github", entry["providerDetected"]!.GetValue<string>());
        Assert.True(entry.ContainsKey("linkPattern") && entry.ContainsKey("prSupported") && entry.ContainsKey("prUnavailable"));

        Assert.Equal("https://github.com/owner/name/commit/" + sha,
            (await Call("git.webLink", new JsonObject { ["teamId"] = teamId, ["team"] = "web", ["repo"] = "lib", ["kind"] = "commit", ["commit"] = sha }))["url"]!.GetValue<string>());

        var set = await Call("teams.setRepo", new JsonObject { ["teamId"] = teamId, ["name"] = "lib", ["provider"] = "gitea", ["linkPattern"] = "{base}/x/{commit}?p={path}" });
        Assert.Equal("lib", set["name"]!.GetValue<string>());
        Assert.Equal("gitea", set["provider"]!.GetValue<string>());
        Assert.Equal("github", set["providerDetected"]!.GetValue<string>());
        Assert.Equal((await Call("teams.repos", new JsonObject { ["teamId"] = teamId }))["extras"]![0]!.ToJsonString(), set.ToJsonString());
        Assert.Equal($"https://github.com/owner/name/x/{sha}?p=owner/name",
            (await Call("git.webLink", new JsonObject { ["teamId"] = teamId, ["repo"] = "lib", ["kind"] = "commit", ["commit"] = sha }))["url"]!.GetValue<string>());
        // The main repository's override is untouched.
        Assert.Null(repoOverride((await Call("teams.repos", new JsonObject { ["teamId"] = teamId }))["main"]));
        static JsonNode? repoOverride(JsonNode? main) => main?["provider"];
    }

    [Fact]
    public async Task SetRepoClearsWithEmptyStrings_MainIsMainInAnyCase_ABitbucketExtraHasNoPullRequests_AndAnUnpushedCommitHasNoLink()
    {
        Git("init", "-q", "-b", "main");
        Git("commit", "-q", "--allow-empty", "-m", "first");
        var pushed = Git("rev-parse", "HEAD");
        Git("remote", "add", "origin", "https://oauth2:s3cr3tTOKEN@gitlab.corp.example:8443/grp/sub/app.git");
        Git("update-ref", "refs/remotes/origin/main", pushed);
        var teamId = await _bus.CreateTeamAsync("web", "Manager", "claude", _work, Ct);
        await _bus.Repos!.EnsureTeamRepoAsync(teamId, "web", Ct);
        Git("commit", "-q", "--allow-empty", "-m", "local only");
        var unpushed = Git("rev-parse", "HEAD");

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
        GitIn(bb, "init", "-q", "-b", "main");
        GitIn(bb, "commit", "-q", "--allow-empty", "-m", "first");
        var bsha = GitIn(bb, "rev-parse", "HEAD");
        GitIn(bb, "remote", "add", "origin", "git@bitbucket.org:acme/tools.git");
        GitIn(bb, "update-ref", "refs/remotes/origin/main", bsha);
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

    [Fact]
    public void ALinkIsARead_ForAVisitorAndTheAudit_TheOverrideAChange()
    {
        Assert.Equal("git.webLink", UiRpc.WebLinkMethod);
        Assert.True(UiRpc.Handles("git.webLink"));
        Assert.False(UiRpc.Handles("git_web_link"));
        Assert.True(AuditLog.IsReadOnly("git.webLink"));
        Assert.True(AuditLog.IsReadOnly("git.webLink", new JsonObject { ["teamId"] = 1, ["kind"] = "pr", ["pr"] = 2 }));
        Assert.False(AuditLog.IsReadOnly("teams.setRepo"));
        Assert.Equal(RemoteAccess.Kind.Reads, RemoteAccess.Kinds["git.webLink"]);
        Assert.Equal(RemoteAccess.Kind.Changes, RemoteAccess.Kinds["teams.setRepo"]);
        Assert.False(RemoteAccess.Kinds.ContainsKey("teams.setRepoProvider"));
        Assert.False(RemoteAccess.Kinds.ContainsKey("git_web_link"));
    }

    private string Git(params string[] args) => GitIn(_work, args);

    private string GitIn(string dir, params string[] args)
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
