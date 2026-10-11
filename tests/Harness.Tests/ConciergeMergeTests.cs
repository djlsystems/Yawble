using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harness.Tests;

/// <summary>
/// A person may let the Concierge merge, through the platform's own Merge to main and Bring
/// current and merge. The setting <c>concierge.mayMerge</c> is off by default; with it on, the
/// Concierge holding <c>Merge</c> lands a team branch exactly as a person does, and the tenant row
/// names the person with <c>viaConcierge</c>. Nobody else gets through, whatever it holds. Every
/// origin is a local bare repository whose HEAD is <c>trunk</c>.
/// </summary>
public sealed class ConciergeMergeTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";
    private const string Person = "person@example.test";
    private const string Repo = "Widget";
    private const string Url = "https://github.com/owner/Widget.git";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-concierge-merge-").FullName;
    private readonly string _origin;
    private readonly string _seed;
    private readonly WebApplicationFactory<Program> _factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ConciergeMergeTests()
    {
        _origin = Path.Combine(_root, "origin.git");
        _seed = Path.Combine(_root, "seed");
        Git(_root, "init", "--bare", "-b", "trunk", _origin);
        Git(_root, "clone", _origin, _seed);
        Git(_seed, "checkout", "-b", "trunk");
        File.WriteAllText(Path.Combine(_seed, "README.md"), "widget\n");
        File.WriteAllText(Path.Combine(_seed, "shared.txt"), "one\n");
        Commit(_seed, "base");
        Git(_seed, "push", "origin", "trunk");

        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .UseSetting(InstanceGitIdentity.NameVariable, "Pat Person")
            .UseSetting(InstanceGitIdentity.EmailVariable, "pat@example.test")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());
                services.AddSingleton<IRepoClone>(new LocalOriginClone(_origin));
                services.Replace(ServiceDescriptor.Singleton<ITeamPublisher>(new NoPublisher()));
            }));
    }

    private IBacklogStore Backlog => _factory.Services.GetRequiredService<IBacklogStore>();
    private TenantSettings Settings => _factory.Services.GetRequiredService<TenantSettings>();
    private ITenantLog Log => _factory.Services.GetRequiredService<ITenantLog>();

    // ---- the setting off ----

    [Fact]
    public async Task With_the_setting_off_by_default_the_Concierge_is_refused_naming_it_and_a_persons_merge_is_unchanged()
    {
        var person = await PersonAsync();
        Assert.False(Settings.ConciergeMayMerge);
        Assert.Equal("off", Settings.Fallback(TenantSettings.ConciergeMayMergeName));
        Assert.Equal("builtIn", Settings.FallbackSource(TenantSettings.ConciergeMayMergeName));

        var team = await TeamAsync("Alpha");
        var item = await DispatchAsync(team);
        var sha = PushWork(team, "alpha work");
        var trunk = RevParse(_origin, "refs/heads/trunk");
        var concierge = await ConciergeAsync();

        foreach (var bringCurrent in new bool?[] { null, true })
        {
            var reply = await concierge.Repo(action: "merge", team: team, repo: Repo, bringCurrent: bringCurrent, cancellationToken: Ct);
            AssertRefusal(reply);
            Assert.Contains("is off", reply, StringComparison.Ordinal);
            Assert.Contains("a person turns it on", reply, StringComparison.Ordinal);
        }

        // Over HTTP too: the route's own sentence names the setting.
        using (var raw = KeyClient(concierge.Key))
        {
            var refused = await raw.PostAsync($"/api/teams/{team}/repos/{Repo}/merge-to-main", null, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            var error = await ErrorAsync(refused);
            Assert.Contains("concierge.mayMerge", error, StringComparison.Ordinal);
            Assert.Contains("a person turns it on", error, StringComparison.Ordinal);
        }

        Assert.Equal(trunk, RevParse(_origin, "refs/heads/trunk"));
        Assert.Equal(sha, RevParse(_origin, $"refs/heads/team/{team}"));
        Assert.Null((await CurrentDispatchAsync(item)).LandedAt);

        // A person's merge is exactly as before, and its row is the person's own.
        var merged = await ActAsync(person, team, "merge-to-main");
        Assert.True(merged.StatusCode == HttpStatusCode.OK, await merged.Content.ReadAsStringAsync(Ct));
        Assert.Equal(sha, RevParse(_origin, "refs/heads/trunk"));
        Assert.Equal(sha, (await CurrentDispatchAsync(item)).LandedSha);
        var row = await RowAsync(TenantActions.RepoMergeToMain, team);
        Assert.Equal(Person, row.ActorEmail);
        Assert.False(ViaConcierge(row));
    }

    // ---- the setting on: it lands as a person's merge does ----

    [Fact]
    public async Task With_the_setting_on_the_Concierge_fast_forwards_stores_landed_records_the_tip_and_names_the_person()
    {
        var person = await PersonAsync();
        await SetAsync(person, "on");
        var team = await TeamAsync("Beta");
        var item = await DispatchAsync(team);
        var sha = PushWork(team, "beta work");
        var concierge = await ConciergeAsync();

        var reply = await concierge.Repo(action: "merge", team: team, repo: Repo, cancellationToken: Ct);

        Assert.True(reply.StartsWith("HTTP 200", StringComparison.Ordinal), reply);
        Assert.Equal(sha, RevParse(_origin, "refs/heads/trunk"));

        // The clone's absolute host path goes to a person only.
        using (var answer = JsonDocument.Parse(reply[reply.IndexOf('{')..]))
        {
            Assert.Equal(JsonValueKind.Null, answer.RootElement.GetProperty("status").GetProperty("clonePath").ValueKind);
        }

        var stored = await CurrentDispatchAsync(item);
        Assert.NotNull(stored.LandedAt);
        Assert.Equal(sha, stored.LandedSha);
        Assert.Equal("trunk", stored.LandedBranch);
        Assert.Equal(sha, Assert.Single(await Backlog.TipsAsync(stored.Id, Ct)).Sha);

        var row = await RowAsync(TenantActions.RepoMergeToMain, team);
        Assert.Equal(Person, row.ActorEmail);
        Assert.True(ViaConcierge(row));
        Assert.True(Succeeded(row));
    }

    [Fact]
    public async Task With_the_setting_on_the_Concierge_joins_a_branch_behind_trunk_with_a_merge_commit()
    {
        var person = await PersonAsync();
        await SetAsync(person, "on");
        var team = await TeamAsync("Gamma");
        var item = await DispatchAsync(team);
        var sha = PushWork(team, "gamma work");
        var other = LandOnTrunk("other.txt", "other\n");
        var concierge = await ConciergeAsync();

        var reply = await concierge.Repo(action: "merge", team: team, repo: Repo, bringCurrent: false, cancellationToken: Ct);

        Assert.True(reply.StartsWith("HTTP 200", StringComparison.Ordinal), reply);
        var trunk = RevParse(_origin, "refs/heads/trunk");
        Assert.Equal(other, RevParse(_origin, "refs/heads/trunk^1"));
        Assert.Equal(sha, RevParse(_origin, "refs/heads/trunk^2"));
        Assert.Equal(sha, RevParse(_origin, $"refs/heads/team/{team}"));

        var stored = await CurrentDispatchAsync(item);
        Assert.Equal(sha, stored.LandedSha);
        Assert.Equal(sha, Assert.Single(await Backlog.TipsAsync(stored.Id, Ct)).Sha);

        var row = await RowAsync(TenantActions.RepoMergeToMain, team);
        Assert.True(ViaConcierge(row));
        Assert.Equal(Person, row.ActorEmail);
        using var detail = JsonDocument.Parse(row.Detail!);
        Assert.Equal(trunk, detail.RootElement.GetProperty("mergeCommit").GetString());
    }

    [Fact]
    public async Task With_the_setting_on_the_Concierge_brings_current_and_merges_and_both_rows_name_the_person()
    {
        var person = await PersonAsync();
        await SetAsync(person, "on");
        var team = await TeamAsync("Delta");
        var item = await DispatchAsync(team);
        var sha = PushWork(team, "delta work");
        var other = LandOnTrunk("other.txt", "other\n");
        var concierge = await ConciergeAsync();

        var reply = await concierge.Repo(action: "merge", team: team, repo: Repo, bringCurrent: true, cancellationToken: Ct);

        Assert.True(reply.StartsWith("HTTP 200", StringComparison.Ordinal), reply);
        var trunk = RevParse(_origin, "refs/heads/trunk");
        Assert.Equal(trunk, RevParse(_origin, $"refs/heads/team/{team}"));
        Assert.Equal(sha, RevParse(_origin, $"refs/heads/team/{team}^1"));
        Assert.True(IsAncestor(_origin, other, trunk));

        Assert.NotNull((await CurrentDispatchAsync(item)).LandedAt);
        foreach (var action in new[] { TenantActions.RepoBringCurrentAndMerge, TenantActions.RepoMergeToMain })
        {
            var row = await RowAsync(action, team);
            Assert.Equal(Person, row.ActorEmail);
            Assert.True(ViaConcierge(row), action);
            Assert.True(Succeeded(row), action);
        }
    }

    // ---- refused for the Concierge as for a person, changing nothing ----

    [Fact]
    public async Task A_conflict_is_refused_for_the_Concierge_and_nothing_changes()
    {
        var person = await PersonAsync();
        await SetAsync(person, "on");
        var team = await TeamAsync("Epsilon");
        var item = await DispatchAsync(team);
        PushWork(team, "team edits shared", file: "shared.txt");
        LandOnTrunk("shared.txt", "other\n");
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(person, team, "fetch")).StatusCode);
        var concierge = await ConciergeAsync();

        foreach (var bringCurrent in new bool?[] { null, true })
        {
            var before = Refs(_origin);
            var clone = Refs(Clone(team));

            var reply = await concierge.Repo(action: "merge", team: team, repo: Repo, bringCurrent: bringCurrent, cancellationToken: Ct);

            AssertRefusal(reply);
            Assert.Contains("conflict", reply, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, Refs(_origin));
            Assert.Equal(clone, Refs(Clone(team)));
            Assert.Equal(string.Empty, Run(Clone(team), "status", "--porcelain").Stdout);
        }

        Assert.Null((await CurrentDispatchAsync(item)).LandedAt);
    }

    [Fact]
    public async Task An_unknown_default_branch_is_refused_for_the_Concierge_and_nothing_changes()
    {
        var person = await PersonAsync();
        await SetAsync(person, "on");
        var team = await TeamAsync("Zeta");
        PushWork(team, "zeta work");
        Git(_origin, "symbolic-ref", "HEAD", "refs/heads/nowhere");
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(person, team, "fetch")).StatusCode);
        Assert.Null(_factory.Services.GetRequiredService<TeamRegistry>().DefaultBranchFor(team, Repo).Branch);
        var concierge = await ConciergeAsync();

        foreach (var bringCurrent in new bool?[] { null, true })
        {
            var before = Refs(_origin);
            var reply = await concierge.Repo(action: "merge", team: team, repo: Repo, bringCurrent: bringCurrent, cancellationToken: Ct);

            AssertRefusal(reply);
            Assert.Contains("default branch is not known", reply, StringComparison.Ordinal);
            Assert.Equal(before, Refs(_origin));
        }
    }

    [Fact]
    public async Task Contributor_mode_is_refused_for_the_Concierge_and_the_pull_request_stays_a_persons()
    {
        var person = await PersonAsync();
        await SetAsync(person, "on");
        var team = await TeamAsync("Eta");
        PushWork(team, "eta work");
        await _factory.Services.GetRequiredService<TeamRegistry>().SetContributorAsync(
            team, Repo, "https://github.com/upstream/Widget.git", "example", false, null, Ct);
        var concierge = await ConciergeAsync();

        foreach (var bringCurrent in new bool?[] { null, true })
        {
            var before = Refs(_origin);
            var reply = await concierge.Repo(action: "merge", team: team, repo: Repo, bringCurrent: bringCurrent, cancellationToken: Ct);

            AssertRefusal(reply);
            Assert.Contains("contributor mode", reply, StringComparison.Ordinal);
            Assert.Contains("409", reply, StringComparison.Ordinal);
            Assert.Equal(before, Refs(_origin));
        }

        using var raw = KeyClient(concierge.Key);
        foreach (var path in new[] { $"/api/teams/{team}/repos/{Repo}/pull-request", "/api/github/fork" })
        {
            var refused = await raw.PostAsJsonAsync(path, new { }, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(PermitGate.HumansOnlyMessage, await ErrorAsync(refused));
        }
    }

    // ---- nobody else, whatever it holds ----

    [Fact]
    public async Task A_manager_a_member_a_plugin_and_any_key_made_with_merge_are_refused_with_the_setting_on()
    {
        var person = await PersonAsync();
        await SetAsync(person, "on");
        var team = await TeamAsync("Theta");
        var sha = PushWork(team, "theta work");
        var owner = (await _factory.Services.GetRequiredService<IUserStore>().FindAsync(Person, Ct))!;
        var every = new HashSet<string>(Permits.All, StringComparer.Ordinal) { Permits.Merge };
        var principals = _factory.Services.GetRequiredService<IPrincipalStore>();

        var keys = new List<(string Who, string Key)>
        {
            ("Manager", await principals.MintAsync(
                new ContainerId(team, TeamRegistry.DefaultManagerName).ToString(), PrincipalKind.Container, team, every, ct: Ct)),
            ("member", await principals.MintAsync(
                new ContainerId(team, "Worker").ToString(), PrincipalKind.Container, team, every, ct: Ct)),
            ("plugin", await principals.MintAsync(
                new ContainerId(team, "plugin-widget").ToString(), PrincipalKind.Container, team, every, ct: Ct)),
            ("api key", await principals.MintAsync(
                "key-with-merge", PrincipalKind.ApiKey, null, every, ownerUserId: owner.Id, ct: Ct)),
            // The Concierge's kind and owner, under an id that is not the Concierge's.
            ("tenant concierge kind, other id", await principals.MintAsync(
                "not-the-concierge", PrincipalKind.TenantConcierge, null, every, ownerUserId: owner.Id, ct: Ct)),
            ("team concierge", await principals.MintAsync(
                "concierge-session", PrincipalKind.Concierge, team, every, ownerUserId: owner.Id, ct: Ct)),
        };

        foreach (var (who, key) in keys)
        {
            using var client = KeyClient(key);
            foreach (var action in new[] { "merge-to-main", "bring-current-and-merge" })
            {
                var refused = await client.PostAsync($"/api/teams/{team}/repos/{Repo}/{action}", null, Ct);
                Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{who} {action}: {refused.StatusCode}");
                Assert.Equal(PermitGate.HumansOnlyMessage, await ErrorAsync(refused));
            }

            var reply = await Tools(key, who == "Manager" || who == "member" || who == "plugin"
                ? PrincipalKind.Container : PrincipalKind.ApiKey).Repo(action: "merge", team: team, repo: Repo, cancellationToken: Ct);
            AssertRefusal(reply);
        }

        Assert.NotEqual(sha, RevParse(_origin, "refs/heads/trunk"));
    }

    [Fact]
    public void Merge_is_the_Concierges_permit_alone()
    {
        Assert.Contains(Permits.Merge, ConciergeLaunchFactory.ConciergePermits);
        Assert.DoesNotContain(Permits.Merge, TeamRegistry.ManagerPermits);
        Assert.DoesNotContain(Permits.Merge, Permits.All);

        var owner = "user-1";
        var concierge = new Principal(
            ConciergeLaunchFactory.PrincipalId(owner), PrincipalKind.TenantConcierge, ConciergeLaunchFactory.ConciergePermits, owner);
        Assert.True(ConciergeLaunchFactory.Strip(concierge).May(Permits.Merge));

        var every = new HashSet<string>(Permits.All, StringComparer.Ordinal) { Permits.Merge };
        foreach (var other in new[]
                 {
                     new Principal("alpha/Manager", PrincipalKind.Container, every),
                     new Principal("key", PrincipalKind.ApiKey, every, owner),
                     new Principal("other", PrincipalKind.TenantConcierge, every, owner),
                     new Principal(ConciergeLaunchFactory.PrincipalId(owner), PrincipalKind.TenantConcierge, every, "user-2"),
                     new Principal(ConciergeLaunchFactory.PrincipalId(owner), PrincipalKind.Concierge, every, owner),
                 })
        {
            var stripped = ConciergeLaunchFactory.Strip(other);
            Assert.False(stripped.May(Permits.Merge), other.Id);
            Assert.True(stripped.May(Permits.Read), other.Id);
        }
    }

    // ---- read at every call ----

    [Fact]
    public async Task Turning_the_setting_off_mid_session_refuses_the_next_merge_without_a_restart()
    {
        var person = await PersonAsync();
        await SetAsync(person, "on");
        var first = await TeamAsync("Iota");
        var second = await TeamAsync("Kappa");
        var firstSha = PushWork(first, "iota work");
        var secondSha = PushWork(second, "kappa work");
        var concierge = await ConciergeAsync();

        var landed = await concierge.Repo(action: "merge", team: first, repo: Repo, cancellationToken: Ct);
        Assert.True(landed.StartsWith("HTTP 200", StringComparison.Ordinal), landed);
        Assert.Equal(firstSha, RevParse(_origin, "refs/heads/trunk"));

        await SetAsync(person, "off");
        var refused = await concierge.Repo(action: "merge", team: second, repo: Repo, cancellationToken: Ct);
        AssertRefusal(refused);
        Assert.Contains("is off", refused, StringComparison.Ordinal);
        Assert.Equal(firstSha, RevParse(_origin, "refs/heads/trunk"));
        Assert.Equal(secondSha, RevParse(_origin, $"refs/heads/team/{second}"));

        // A reset is off again: the default.
        await SetAsync(person, "on");
        using (var content = new StringContent("""{"concierge.mayMerge": null}""", System.Text.Encoding.UTF8, "application/json"))
        {
            (await person.PutAsync("/api/tenant/settings", content, Ct)).EnsureSuccessStatusCode();
        }

        Assert.False(Settings.ConciergeMayMerge);
        AssertRefusal(await concierge.Repo(action: "merge", team: second, repo: Repo, cancellationToken: Ct));
    }

    /// <summary>
    /// A general "may you merge?" names no team. The `repo` tool's status with no team answers the
    /// Concierge the setting alone, read at the call, so a change shows on the next read; anyone
    /// else with no team is still told to name one.
    /// </summary>
    [Fact]
    public async Task The_repo_tools_status_with_no_team_answers_the_Concierge_the_setting_now()
    {
        var person = await PersonAsync();
        var concierge = await ConciergeAsync();

        static bool MayMerge(string reply)
        {
            Assert.True(reply.StartsWith("HTTP 200", StringComparison.Ordinal), reply);
            using var body = JsonDocument.Parse(reply.Split('\n', 2)[1]);
            return body.RootElement.GetProperty("conciergeMayMerge").GetBoolean();
        }

        Assert.False(MayMerge(await concierge.Repo(cancellationToken: Ct)));

        await SetAsync(person, "on");
        Assert.True(MayMerge(await concierge.Repo(cancellationToken: Ct)));
        Assert.True(MayMerge(await concierge.Repo(action: "status", cancellationToken: Ct)));

        await SetAsync(person, "off");
        Assert.False(MayMerge(await concierge.Repo(cancellationToken: Ct)));

        // A merge still needs a team, and nobody but the Concierge reads the setting this way.
        AssertRefusal(await concierge.Repo(action: "merge", repo: Repo, cancellationToken: Ct));
        var other = new KeyedTools(Tools(concierge.Key, PrincipalKind.ApiKey), concierge.Key);
        Assert.StartsWith("Refused: name a team", await other.Repo(cancellationToken: Ct), StringComparison.Ordinal);
    }

    // ---- the tool ----

    [Fact]
    public async Task The_repo_tool_says_merge_is_the_Concierges_and_refuses_a_missing_repository_naming_itself()
    {
        var description = typeof(PlatformMcpTools).GetMethod(nameof(PlatformMcpTools.Repo))!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;
        Assert.Contains("merge", description, StringComparison.Ordinal);
        Assert.Contains("Concierge", description, StringComparison.Ordinal);
        Assert.Contains("concierge.mayMerge", description, StringComparison.Ordinal);
        Assert.Contains("a person has turned", description, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/", description, StringComparison.Ordinal);

        await PersonAsync();
        var team = await TeamAsync("Lambda");
        var concierge = await ConciergeAsync();
        AssertRefusal(await concierge.Repo(action: "merge", team: team, cancellationToken: Ct));
        AssertRefusal(await concierge.Repo(action: "merge", team: team, repo: "Nothing", cancellationToken: Ct));
        Assert.StartsWith("Refused:", await concierge.Repo(action: "rebase", team: team, cancellationToken: Ct));
    }

    // ---- helpers ----

    private static void AssertRefusal(string reply)
    {
        Assert.StartsWith("Refused:", reply);
        Assert.Contains("`repo`", reply, StringComparison.Ordinal);
        Assert.Contains("concierge.mayMerge", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", reply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", reply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/api/", reply, StringComparison.Ordinal);
    }

    private async Task SetAsync(HttpClient person, string value)
    {
        var response = await person.PutAsJsonAsync(
            "/api/tenant/settings", new Dictionary<string, object> { [TenantSettings.ConciergeMayMergeName] = value }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(value == "on", Settings.ConciergeMayMerge);
    }

    private async Task<TenantEvent> RowAsync(string action, string team)
    {
        var row = await Log.FindLatestAsync(action, $"{team}/{Repo}", Ct);
        Assert.NotNull(row);
        return row!;
    }

    private static bool ViaConcierge(TenantEvent row)
    {
        using var detail = JsonDocument.Parse(row.Detail!);
        return detail.RootElement.TryGetProperty("viaConcierge", out var via) && via.ValueKind == JsonValueKind.True;
    }

    private static bool Succeeded(TenantEvent row)
    {
        using var detail = JsonDocument.Parse(row.Detail!);
        return detail.RootElement.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("error").GetString()!;
    }

    private HttpClient KeyClient(string key)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);
        return client;
    }

    private sealed record KeyedTools(PlatformMcpTools Tools, string Key)
    {
        public Task<string> Repo(
            string? action = null, string? team = null, string? repo = null, bool? bringCurrent = null,
            CancellationToken cancellationToken = default) =>
            Tools.Repo(team: team, action: action, repo: repo, bringCurrent: bringCurrent, cancellationToken: cancellationToken);
    }

    /// <summary>The person's tenant Concierge, minted as <see cref="ConciergeLaunchFactory"/> mints it.</summary>
    private async Task<KeyedTools> ConciergeAsync()
    {
        var user = (await _factory.Services.GetRequiredService<IUserStore>().FindAsync(Person, Ct))!;
        var key = await _factory.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            ConciergeLaunchFactory.PrincipalId(user.Id), PrincipalKind.TenantConcierge, null,
            ConciergeLaunchFactory.ConciergePermits, ownerUserId: user.Id, ct: Ct);
        return new KeyedTools(Tools(key, PrincipalKind.TenantConcierge), key);
    }

    private PlatformMcpTools Tools(string key, PrincipalKind kind)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = key;
        context.User = PrincipalClaims.ToClaimsPrincipal(new Principal("tools", kind, new HashSet<string>()), "test");

        return new PlatformMcpTools(
            new FixedAccessor(context),
            _factory.Services.GetRequiredService<IPrincipalStore>(),
            _factory.Services.GetRequiredService<AgentCatalog>(),
            new ServerClients(_factory.Server.CreateHandler()));
    }

    /// <summary>An action as a person presses it: again while the answer is 409 "still working".</summary>
    private static async Task<HttpResponseMessage> ActAsync(HttpClient person, string team, string action)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var response = await person.PostAsync($"/api/teams/{team}/repos/{Repo}/{action}", null, Ct);
            if (response.StatusCode != HttpStatusCode.Conflict || DateTime.UtcNow > deadline) return response;

            var text = await response.Content.ReadAsStringAsync(Ct);
            if (!text.Contains("still working", StringComparison.Ordinal)) return response;
            await Task.Delay(100, Ct);
        }
    }

    private async Task<BacklogDispatch> CurrentDispatchAsync(long item) =>
        (await Backlog.DispatchesAsync(item, Ct))[^1];

    private async Task<long> DispatchAsync(string team)
    {
        var item = await Backlog.CreateAsync(null, $"Item for {team}", "body", Person, Ct);
        var row = await _factory.Services.GetRequiredService<IMessageLog>().AppendAsync(
            new NewMessage("test.dispatched", "{}", "test", null), Ct);
        var dispatch = await Backlog.AddDispatchAsync(item.Id, team, team, row.Seq, Person, Ct);
        await _factory.Services.GetRequiredService<BacklogTipRecorder>().RecordBaseAsync(dispatch, Ct);
        return item.Id;
    }

    /// <summary>Commits on top of trunk from a scratch clone and pushes them as team/{id}.</summary>
    private string PushWork(string team, string message, string? file = null)
    {
        var work = Path.Combine(_root, $"work-{team}");
        Git(_root, "clone", "-b", "trunk", _origin, work);
        File.WriteAllText(Path.Combine(work, file ?? $"{team}.txt"), $"{message}\n");
        Commit(work, message);
        Git(work, "push", "origin", $"HEAD:refs/heads/team/{team}");
        return RevParse(work, "HEAD");
    }

    /// <summary>Another team's work landing on trunk meanwhile.</summary>
    private string LandOnTrunk(string file, string content)
    {
        Git(_seed, "pull", "--ff-only", "origin", "trunk");
        File.WriteAllText(Path.Combine(_seed, file), content);
        Commit(_seed, $"other {file}");
        Git(_seed, "push", "origin", "trunk");
        return RevParse(_seed, "HEAD");
    }

    private string Clone(string team) =>
        Path.Combine(_factory.Services.GetRequiredService<TeamPaths>().ReposFor(team), Repo, "main");

    private async Task<string> TeamAsync(string name)
    {
        var services = _factory.Services;
        var registry = services.GetRequiredService<TeamRegistry>();
        var agent = services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        // The repository at creation, so no Manager is told anything and no run-end publish races.
        var team = (await registry.CreateAsync(name, agent, memberAgent: agent, repos: [Url], ct: Ct)).Id;

        Assert.True(Directory.Exists(Path.Combine(Clone(team), ".git")), "the platform did not clone");
        Assert.Equal("trunk", registry.DefaultBranchFor(team, Repo).Branch);
        Git(Clone(team), "config", "user.name", "t");
        Git(Clone(team), "config", "user.email", "t@example.invalid");
        return team;
    }

    private bool _personMade;

    private async Task<HttpClient> PersonAsync()
    {
        if (!_personMade)
        {
            await _factory.Services.GetRequiredService<IUserStore>().CreateAsync(Person, Password);
            _personMade = true;
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = Person, password = Password }, Ct);
        login.EnsureSuccessStatusCode();
        return client;
    }

    private static string Refs(string repo) => Run(repo, "for-each-ref").Stdout;

    private static void Commit(string repo, string message)
    {
        Git(repo, "add", ".");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "core.hooksPath=/dev/null", "commit", "-m", message);
    }

    private static bool IsAncestor(string repo, string ancestor, string descendant) =>
        Run(repo, "merge-base", "--is-ancestor", ancestor, descendant).Exit == 0;

    private static string RevParse(string repo, string reference)
    {
        var (exit, stdout) = Run(repo, "rev-parse", "--verify", "--quiet", reference);
        return exit == 0 ? stdout.Trim() : throw new InvalidOperationException($"{reference} does not resolve in {repo}");
    }

    private static void Git(string workingDirectory, params string[] args)
    {
        var (exit, _) = Run(workingDirectory, args);
        if (exit != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed in {workingDirectory}");
    }

    private static (int Exit, string Stdout) Run(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout);
    }

    /// <summary>One request's context for every call, wherever the tool's awaits resume.</summary>
    private sealed class FixedAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => context; set { } }
    }

    private sealed class ServerClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }

    private sealed class NoPublisher : ITeamPublisher
    {
        public Task<TeamPublishReport> PublishAsync(
            string team, IReadOnlyList<string> repoUrls, ContainerId source, long? causation, CancellationToken ct) =>
            Task.FromResult(TeamPublishReport.Nothing);
    }

    /// <summary>The product's own <see cref="RepoClone"/>, pointed at the local origin.</summary>
    private sealed class LocalOriginClone(string origin) : IRepoClone
    {
        private readonly RepoClone _real = new(new GitRunner());

        public async Task<IReadOnlyList<RepoCloneOutcome>> EnsureAllAsync(
            IReadOnlyList<(string Url, string Path)> repos, CancellationToken ct)
        {
            var outcomes = new List<RepoCloneOutcome>();
            foreach (var (url, path) in repos)
            {
                outcomes.Add((await _real.EnsureAsync(origin, path, ct)) with { Url = url });
            }

            return outcomes;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
