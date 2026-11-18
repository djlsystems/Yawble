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
/// Card 446 (B075), the contract the shell builds against (card 447): git_web_link {team, repo, kind, ...} -> {url},
/// teams.repos' provider fields and teams.setRepoProvider, through the window's dispatcher.
/// </summary>
public sealed class Card446RepoProviderRpcTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempDb _db = new();
    private readonly string _work = Path.Combine(Path.GetTempPath(), "y446" + Guid.NewGuid().ToString("N")[..6]);
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
        foreach (var f in Directory.EnumerateFiles(_work, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        try { Directory.Delete(_work, true); } catch (IOException) { }
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

        // teams.repos: the main repository's provider fields.
        var main = (await Call("teams.repos", new JsonObject { ["teamId"] = teamId }))["main"]!.AsObject();
        Assert.Equal("gitlab", main["provider"]!.GetValue<string>());
        Assert.Equal("gitlab", main["providerDetected"]!.GetValue<string>());
        Assert.Null(main["providerOverride"]);
        Assert.Null(main["linkPattern"]);
        Assert.True(main.ContainsKey("prSupported"));
        Assert.True(main.ContainsKey("prUnavailable"));

        // git_web_link by team name and by id; an unpushed commit or a branch origin lacks has no link.
        var link = await Call("git_web_link", new JsonObject { ["team"] = "web", ["repo"] = "main", ["kind"] = "commit", ["commit"] = sha });
        Assert.Equal("https://gitlab.com/acme/web/app/-/commit/" + sha, link["url"]!.GetValue<string>());
        var byId = await Call("git_web_link", new JsonObject { ["team"] = teamId, ["kind"] = "pr", ["pr"] = 3 });
        Assert.Equal("https://gitlab.com/acme/web/app/-/merge_requests/3", byId["url"]!.GetValue<string>());
        Assert.Null((await Call("git_web_link", new JsonObject { ["team"] = "web", ["kind"] = "branch", ["branch"] = "members/web/ada" }))["url"]);
        Git("commit", "-q", "--allow-empty", "-m", "second");
        Assert.Null((await Call("git_web_link", new JsonObject { ["teamId"] = teamId, ["kind"] = "commit", ["commit"] = Git("rev-parse", "HEAD") }))["url"]);
        var bad = await Assert.ThrowsAsync<RpcException>(() => _rpc.DispatchAsync("git_web_link", new JsonObject { ["team"] = "web", ["kind"] = "tag" }, Ct));
        Assert.Equal("bad_params", bad.Code);

        // teams.setRepoProvider: the override and pattern, saved and shown; "auto" clears it.
        var set = await Call("teams.setRepoProvider", new JsonObject { ["teamId"] = teamId, ["repo"] = "main", ["provider"] = "none", ["linkPattern"] = "https://mirror.example.com/{path}/c/{commit}" });
        Assert.Equal(("none", "gitlab", "none", false), (set["provider"]!.GetValue<string>(), set["providerDetected"]!.GetValue<string>(), set["providerOverride"]!.GetValue<string>(), set["prSupported"]!.GetValue<bool>()));
        Assert.Contains("not supported", set["prUnavailable"]!.GetValue<string>());
        Assert.Equal("https://mirror.example.com/acme/web/app/c/" + sha,
            (await Call("git_web_link", new JsonObject { ["team"] = "web", ["kind"] = "commit", ["commit"] = sha }))["url"]!.GetValue<string>());
        Assert.Equal("https://mirror.example.com/{path}/c/{commit}", (await Call("teams.repos", new JsonObject { ["teamId"] = teamId }))["main"]!["linkPattern"]!.GetValue<string>());
        var refused = await Assert.ThrowsAsync<RpcException>(() => _rpc.DispatchAsync("teams.setRepoProvider", new JsonObject { ["teamId"] = teamId, ["provider"] = "sourceforge" }, Ct));
        Assert.Equal("invalid", refused.Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<RpcException>(() => _rpc.DispatchAsync("teams.setRepoProvider",
            new JsonObject { ["teamId"] = teamId, ["linkPattern"] = "https://x.example/{nope}" }, Ct))).Code);
        var cleared = await Call("teams.setRepoProvider", new JsonObject { ["teamId"] = teamId, ["provider"] = "auto", ["linkPattern"] = "" });
        Assert.Equal("gitlab", cleared["provider"]!.GetValue<string>());
        Assert.Null(cleared["providerOverride"]);
    }

    [Fact]
    public void ALinkIsARead_ForAVisitorAndTheAudit_TheOverrideAChange()
    {
        Assert.True(UiRpc.Handles("git_web_link"));
        Assert.True(AuditLog.IsReadOnly("git_web_link"));
        Assert.False(AuditLog.IsReadOnly("teams.setRepoProvider"));
        Assert.Equal(RemoteAccess.Kind.Reads, RemoteAccess.Kinds["git_web_link"]);
        Assert.Equal(RemoteAccess.Kind.Changes, RemoteAccess.Kinds["teams.setRepoProvider"]);
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = _work };
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
