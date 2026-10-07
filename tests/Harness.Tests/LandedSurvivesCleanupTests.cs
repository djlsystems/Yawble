using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// A dispatch records its team branch's tip when the publish pushes it, and <c>landed</c>,
/// once proven, is stored on the dispatch and kept after the branch, the clone and the team are
/// gone. A team that is gone is read from its recorded tip in another team's clone; with no clone
/// anywhere it is <c>unknown</c>, saying so. Merge to main stores landed at once. Every origin is a
/// local bare repository; the URLs only name it.
/// </summary>
public sealed class LandedSurvivesCleanupTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";
    private const string Repo = "Widget";
    private const string Url = "https://github.com/owner/Widget.git";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-landed-kept-").FullName;
    private readonly string _origin;
    private readonly WebApplicationFactory<Program> _factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public LandedSurvivesCleanupTests()
    {
        _origin = Path.Combine(_root, "origin.git");
        var seed = Path.Combine(_root, "seed");
        Git(_root, "init", "--bare", "-b", "trunk", _origin);
        Git(_root, "clone", _origin, seed);
        Git(seed, "checkout", "-b", "trunk");
        File.WriteAllText(Path.Combine(seed, "README.md"), "widget\n");
        Commit(seed, "base");
        Git(seed, "push", "origin", "trunk");

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
            }));
    }

    private IBacklogStore Backlog => _factory.Services.GetRequiredService<IBacklogStore>();

    [Fact]
    public async Task Landed_then_branch_and_team_deleted_still_reads_landed_with_landed_at()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Alpha");
        var item = await DispatchAsync(team);

        // The work reaches origin as team/{id} and is merged to trunk outside the platform.
        var sha = PushWork(team, "the work", alsoTo: "trunk");
        Git(Clone(team), "fetch", "origin");

        var first = await LandedOverHttpAsync(person, item);
        Assert.Equal(BacklogLandedStates.Landed, first.GetProperty("state").GetString());

        var stored = await CurrentDispatchAsync(item);
        Assert.NotNull(stored.LandedAt);
        Assert.Equal(sha, stored.LandedSha);
        Assert.Equal("trunk", stored.LandedBranch);

        // NEVER DOWNGRADED OR MOVED: a second proof stores nothing.
        Assert.False(await Backlog.RecordLandedAsync(stored.Id, "0000000", "other", Ct));

        // Tidied: the branch goes on origin, then the team with its clone.
        var clone = Clone(team);
        Git(_origin, "branch", "-D", $"team/{team}");
        Assert.Equal(HttpStatusCode.OK, (await person.DeleteAsync($"/api/teams/{team}", Ct)).StatusCode);
        Assert.False(Directory.Exists(clone));

        _factory.Services.GetRequiredService<BacklogLandedCache>().Clear();
        var after = await LandedOverHttpAsync(person, item);

        Assert.Equal(BacklogLandedStates.Landed, after.GetProperty("state").GetString());
        Assert.Equal(DateTimeOffset.Parse(stored.LandedAt!, System.Globalization.CultureInfo.InvariantCulture),
            after.GetProperty("landedAt").GetDateTimeOffset());
        Assert.Contains(sha[..7], after.GetProperty("detail").GetString(), StringComparison.Ordinal);
        // WHEN IT WAS PROVEN rides as landedAt for the screen to read on the person's own clock; the
        // sentence carries no server-formatted UTC stamp.
        Assert.DoesNotContain("UTC", after.GetProperty("detail").GetString(), StringComparison.Ordinal);

        var kept = await CurrentDispatchAsync(item);
        Assert.Equal(stored.LandedAt, kept.LandedAt);
        Assert.Equal(sha, kept.LandedSha);
    }

    [Fact]
    public async Task The_publish_records_the_tip_and_a_gone_teams_tip_reachable_in_another_teams_clone_reads_landed()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Beta");
        var item = await DispatchAsync(team);

        // A member's commit on the clone's team branch; the platform's publish pushes it.
        var clone = Clone(team);
        Git(clone, "checkout", "-b", $"team/{team}");
        File.WriteAllText(Path.Combine(clone, "beta.txt"), "beta\n");
        Commit(clone, "beta work");
        var sha = Run(clone, "rev-parse", "HEAD").Stdout.Trim();

        var registry = _factory.Services.GetRequiredService<TeamRegistry>();
        var report = await _factory.Services.GetRequiredService<ITeamPublisher>().PublishAsync(
            team, registry.ReposFor(team), new ContainerId(team, "Manager"), null, Ct);

        var tip = await SingleTipAsync(team, item, report, sha);
        Assert.Equal(Repo, tip.Repo);

        // Another team's clone exists BEFORE the merge, so only a fetch can show it.
        var other = await TeamAsync("Gamma");

        // Merged outside the platform, then the branch and the team are tidied away unread.
        Git(clone, "checkout", "trunk");
        Git(clone, "branch", "-D", $"team/{team}");
        Git(_origin, "update-ref", "refs/heads/trunk", sha);
        Git(_origin, "branch", "-D", $"team/{team}");
        Assert.Equal(HttpStatusCode.OK, (await person.DeleteAsync($"/api/teams/{team}", Ct)).StatusCode);

        var landed = await LandedOverHttpAsync(person, item);

        Assert.Equal(BacklogLandedStates.Landed, landed.GetProperty("state").GetString());
        Assert.Contains(registry.LabelFor(other), landed.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(sha, (await CurrentDispatchAsync(item)).LandedSha);
    }

    [Fact]
    public async Task A_gone_teams_recorded_tip_with_no_clone_anywhere_reads_unknown_and_says_why()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Delta");
        var item = await DispatchAsync(team);

        var sha = PushWork(team, "delta work", alsoTo: "trunk");
        await Backlog.RecordTipAsync((await CurrentDispatchAsync(item)).Id, Repo, sha, Ct);
        Git(_origin, "branch", "-D", $"team/{team}");
        Assert.Equal(HttpStatusCode.OK, (await person.DeleteAsync($"/api/teams/{team}", Ct)).StatusCode);

        var landed = await LandedOverHttpAsync(person, item);

        Assert.Equal(BacklogLandedStates.Unknown, landed.GetProperty("state").GetString());
        Assert.Contains(
            $"no clone of {Repo} on this instance to check the recorded tip against",
            landed.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Null((await CurrentDispatchAsync(item)).LandedAt);
    }

    [Fact]
    public async Task Merge_to_main_stores_landed_at_once()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Epsilon");
        var item = await DispatchAsync(team);

        var sha = PushWork(team, "epsilon work");
        Assert.Null((await CurrentDispatchAsync(item)).LandedAt);

        var merged = await ActAsync(person, team, "merge-to-main");
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);

        // Straight from the store - no backlog read has derived anything.
        var stored = await CurrentDispatchAsync(item);
        Assert.NotNull(stored.LandedAt);
        Assert.Equal(sha, stored.LandedSha);
        Assert.Equal("trunk", stored.LandedBranch);
        Assert.Equal(sha, Assert.Single(await Backlog.TipsAsync(stored.Id, Ct)).Sha);
    }

    [Fact]
    public async Task Dispatching_records_where_the_dispatch_started_before_the_manager_is_told()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Nu");
        PushWork(team, "earlier work");

        var item = await Backlog.CreateAsync(null, "Item for Nu", "body", "person@example.test", Ct);
        (await person.PatchAsJsonAsync($"/api/backlog/{item.Id}", new { state = "ready" }, Ct)).EnsureSuccessStatusCode();
        var dispatched = await person.PostAsync($"/api/teams/{team}/backlog/{item.Id}/dispatch", null, Ct);
        Assert.Equal(HttpStatusCode.OK, dispatched.StatusCode);

        var start = Assert.Single(await Backlog.BasesAsync((await CurrentDispatchAsync(item.Id)).Id, Ct));
        Assert.Equal(Repo, start.Repo);
        Assert.Equal(Run(_origin, "rev-parse", "trunk").Stdout.Trim(), start.DefaultSha);
        Assert.Equal(Run(_origin, "rev-parse", $"team/{team}").Stdout.Trim(), start.TeamSha);
    }

    [Fact]
    public async Task A_fresh_team_branch_with_no_work_of_its_own_is_not_landed_and_nothing_is_stored()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Zeta");
        var item = await DispatchAsync(team);

        // The Manager cuts the team branch from trunk; no work yet. It is reachable from
        // origin/trunk, and that proves nothing about the item.
        var clone = Clone(team);
        Git(clone, "branch", $"team/{team}");

        var first = await ReadAsync(person, item);
        Assert.Equal(BacklogLandedStates.Unknown, first.GetProperty("state").GetString());
        Assert.Contains("own work", first.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Null((await CurrentDispatchAsync(item)).LandedAt);

        // The real work arrives, pushed and not merged: it reads pushed, not a kept landed.
        Git(clone, "checkout", $"team/{team}");
        File.WriteAllText(Path.Combine(clone, "zeta.txt"), "zeta\n");
        Commit(clone, "zeta work");
        Git(clone, "push", "origin", $"team/{team}");

        Assert.Equal(BacklogLandedStates.Pushed, (await ReadAsync(person, item)).GetProperty("state").GetString());
        Assert.Null((await CurrentDispatchAsync(item)).LandedAt);
    }

    [Fact]
    public async Task A_second_item_on_a_team_whose_branch_already_landed_is_not_landed_before_its_own_work()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Eta");
        var first = await DispatchAsync(team);
        PushWork(team, "eta one", alsoTo: "trunk");
        var clone = Clone(team);
        Git(clone, "fetch", "origin");
        Git(clone, "branch", $"team/{team}", $"origin/team/{team}");
        Assert.Equal(BacklogLandedStates.Landed, (await ReadAsync(person, first)).GetProperty("state").GetString());

        // The same team is handed a second item; its branch is the first item's, already landed.
        var second = await DispatchAsync(team);
        var read = await ReadAsync(person, second);
        Assert.Equal(BacklogLandedStates.Unknown, read.GetProperty("state").GetString());
        Assert.Null((await CurrentDispatchAsync(second)).LandedAt);

        // Its own work, pushed and not merged, reads pushed.
        Git(clone, "checkout", $"team/{team}");
        File.WriteAllText(Path.Combine(clone, "eta2.txt"), "eta2\n");
        Commit(clone, "eta two");
        var own = Run(clone, "rev-parse", "HEAD").Stdout.Trim();
        Git(clone, "push", "origin", $"team/{team}");
        Assert.Equal(BacklogLandedStates.Pushed, (await ReadAsync(person, second)).GetProperty("state").GetString());
        Assert.Null((await CurrentDispatchAsync(second)).LandedAt);

        // Merged: now it is landed, on its own sha.
        Git(clone, "push", "origin", $"team/{team}:trunk");
        Git(clone, "fetch", "origin");
        Assert.Equal(BacklogLandedStates.Landed, (await ReadAsync(person, second)).GetProperty("state").GetString());
        Assert.Equal(own, (await CurrentDispatchAsync(second)).LandedSha);
    }

    /// <summary>
    /// A MEMBER PUSHES TO ORIGIN'S TEAM BRANCH FROM ITS OWN WORKTREE, so the clone's local
    /// <c>team/{id}</c> can stay where an earlier item left it. Read off that stale local branch,
    /// the second item's merged work looked like nothing of its own and read unknown.
    /// </summary>
    [Fact]
    public async Task A_second_items_work_pushed_past_a_stale_local_team_branch_reads_landed_once_merged()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Theta");
        var first = await DispatchAsync(team);
        PushWork(team, "theta one", alsoTo: "trunk");
        var clone = Clone(team);
        Git(clone, "fetch", "origin");
        Git(clone, "branch", $"team/{team}", $"origin/team/{team}");
        Assert.Equal(BacklogLandedStates.Landed, (await ReadAsync(person, first)).GetProperty("state").GetString());

        var second = await DispatchAsync(team);

        // The second item's work, committed and pushed somewhere else, then merged; the clone's own
        // team branch is never moved.
        var work = Path.Combine(_root, $"work2-{team}");
        Git(_root, "clone", "-b", $"team/{team}", _origin, work);
        File.WriteAllText(Path.Combine(work, "theta2.txt"), "theta two\n");
        Commit(work, "theta two");
        var own = Run(work, "rev-parse", "HEAD").Stdout.Trim();
        Git(work, "push", "origin", $"HEAD:refs/heads/team/{team}");
        Git(work, "push", "origin", "HEAD:refs/heads/trunk");
        Git(clone, "fetch", "origin");

        Assert.NotEqual(own, Run(clone, "rev-parse", $"refs/heads/team/{team}").Stdout.Trim());
        Assert.Equal(BacklogLandedStates.Landed, (await ReadAsync(person, second)).GetProperty("state").GetString());
        Assert.Equal(own, (await CurrentDispatchAsync(second)).LandedSha);
    }

    [Fact]
    public async Task Merge_to_main_stores_landed_only_on_dispatches_whose_work_is_in_the_merge()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Lambda");
        var earlier = await DispatchAsync(team);
        var sha = PushWork(team, "lambda work");

        // Dispatched after the work it is about to be merged with was already on the team branch.
        var later = await DispatchAsync(team);

        var merged = await ActAsync(person, team, "merge-to-main");
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);

        var landed = await CurrentDispatchAsync(earlier);
        Assert.NotNull(landed.LandedAt);
        Assert.Equal(sha, landed.LandedSha);

        var notItsWork = await CurrentDispatchAsync(later);
        Assert.Null(notItsWork.LandedAt);
        Assert.Empty(await Backlog.TipsAsync(notItsWork.Id, Ct));
        Assert.NotEqual(BacklogLandedStates.Landed, (await ReadAsync(person, later)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task The_landed_at_answered_is_the_stored_one_even_when_another_reader_stored_it_first()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Mu");
        var item = await DispatchAsync(team);
        var sha = PushWork(team, "mu work", alsoTo: "trunk");
        Git(Clone(team), "fetch", "origin");

        // A reader that loaded the dispatch before another reader stored landed on it.
        var before = await CurrentDispatchAsync(item);
        Assert.True(await Backlog.RecordLandedAsync(before.Id, sha, "trunk", Ct));
        var stored = await CurrentDispatchAsync(item);
        await Task.Delay(50, Ct);   // so a fresh UtcNow could not pass for the stored value

        var services = _factory.Services;
        var late = (await BacklogLandedState.ForAsync(
            [before], services.GetRequiredService<TeamRegistry>(), services.GetRequiredService<TeamPaths>(),
            services.GetRequiredService<GitRunner>(), new BacklogLandedCache(TimeSpan.Zero), Ct, store: Backlog))[item];

        Assert.Equal(BacklogLandedStates.Landed, late.State);
        Assert.Equal(Parse(stored.LandedAt!), late.LandedAt);

        // And the first read over HTTP answers exactly what it stored.
        var other = await DispatchAsync(team);
        var clone = Clone(team);
        Git(clone, "checkout", "-b", $"team/{team}", "origin/trunk");
        File.WriteAllText(Path.Combine(clone, "mu2.txt"), "mu2\n");
        Commit(clone, "mu two");
        Git(clone, "push", "origin", $"team/{team}", $"team/{team}:trunk");
        Git(clone, "fetch", "origin");

        var first = await ReadAsync(person, other);
        Assert.Equal(BacklogLandedStates.Landed, first.GetProperty("state").GetString());
        Assert.Equal(Parse((await CurrentDispatchAsync(other)).LandedAt!), first.GetProperty("landedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task A_newer_unmerged_publish_replaces_the_tip_and_a_gone_team_is_not_claimed_landed()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Theta");
        var item = await DispatchAsync(team);
        var registry = _factory.Services.GetRequiredService<TeamRegistry>();
        var publisher = _factory.Services.GetRequiredService<ITeamPublisher>();
        var clone = Clone(team);
        Git(clone, "checkout", "-b", $"team/{team}");
        File.WriteAllText(Path.Combine(clone, "a.txt"), "a\n");
        Commit(clone, "A");
        var a = Run(clone, "rev-parse", "HEAD").Stdout.Trim();
        await publisher.PublishAsync(team, registry.ReposFor(team), new ContainerId(team, "Manager"), null, Ct);

        // A is merged outside the platform and never read; B follows and is not merged.
        Git(_origin, "update-ref", "refs/heads/trunk", a);
        File.WriteAllText(Path.Combine(clone, "b.txt"), "b\n");
        Commit(clone, "B");
        var b = Run(clone, "rev-parse", "HEAD").Stdout.Trim();
        var report = await publisher.PublishAsync(team, registry.ReposFor(team), new ContainerId(team, "Manager"), null, Ct);
        await SingleTipAsync(team, item, report, b);

        await TeamAsync("Iota");
        Assert.Equal(HttpStatusCode.OK, (await person.DeleteAsync($"/api/teams/{team}", Ct)).StatusCode);

        Assert.Equal(BacklogLandedStates.Unknown, (await ReadAsync(person, item)).GetProperty("state").GetString());
        Assert.Null((await CurrentDispatchAsync(item)).LandedAt);
    }

    [Fact]
    public async Task A_stored_landed_is_not_moved_by_a_later_proof_of_newer_work()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Kappa");
        var item = await DispatchAsync(team);
        var sha = PushWork(team, "kappa", alsoTo: "trunk");
        var clone = Clone(team);
        Git(clone, "fetch", "origin");
        Assert.Equal(BacklogLandedStates.Landed, (await ReadAsync(person, item)).GetProperty("state").GetString());
        var stored = await CurrentDispatchAsync(item);

        // More work merged later: a derive would now prove a newer sha.
        Git(clone, "checkout", "-b", $"team/{team}", $"origin/team/{team}");
        File.WriteAllText(Path.Combine(clone, "k2.txt"), "k2\n");
        Commit(clone, "k2");
        Git(clone, "push", "origin", $"team/{team}", $"team/{team}:trunk");
        Git(clone, "fetch", "origin");
        await ReadAsync(person, item);

        var kept = await CurrentDispatchAsync(item);
        Assert.Equal(stored.LandedAt, kept.LandedAt);
        Assert.Equal(sha, kept.LandedSha);
    }

    /// <summary>
    /// The dispatch's one recorded tip, at <paramref name="sha"/> - or, when it is not, a failure
    /// naming which of the publish's silent returns was taken: the outcome, the clone's team
    /// branch, contributor mode, whether the dispatch is still the team's current one above its
    /// floor, its start, and every push row written so far.
    /// </summary>
    private async Task<BacklogDispatchTip> SingleTipAsync(string team, long item, TeamPublishReport report, string sha)
    {
        var dispatch = await CurrentDispatchAsync(item);
        var tips = await Backlog.TipsAsync(dispatch.Id, Ct);
        if (tips.Count == 1 && tips[0].Sha == sha) return tips[0];

        var registry = _factory.Services.GetRequiredService<TeamRegistry>();
        var floor = registry.FloorFor(team);
        var current = (await Backlog.LatestDispatchesAsync(Ct)).Any(d => d.Id == dispatch.Id
            && string.Equals(d.TeamId, team, StringComparison.OrdinalIgnoreCase) && d.Correlation > floor);
        var bases = await Backlog.BasesAsync(dispatch.Id, Ct);
        var missed = await Backlog.MissedStartsAsync(dispatch.Id, Ct);
        var branch = Run(Clone(team), "rev-parse", "--verify", $"refs/heads/team/{team}");
        var log = _factory.Services.GetRequiredService<IMessageLog>();
        var rows = new List<Message>();
        for (var seq = 1L; seq <= await log.HighestSeqAsync(Ct); seq++)
        {
            if (await log.FindAsync(seq, Ct) is { } row) rows.Add(row);
        }

        // A TIP RECORDED LATE, by a publish still running when this one returned, is told apart
        // from one never recorded at all.
        var waited = Stopwatch.StartNew();
        var later = tips;
        while (waited.Elapsed < TimeSpan.FromSeconds(10) && !later.Any(t => t.Sha == sha))
        {
            await Task.Delay(50, Ct);
            later = await Backlog.TipsAsync(dispatch.Id, Ct);
        }

        Assert.Fail(string.Join("\n",
        [
            $"expected the one tip {sha}; recorded [{string.Join(", ", tips.Select(t => $"{t.Repo} {t.Sha} at {t.RecordedAt}"))}] for dispatch {dispatch.Id} (team {dispatch.TeamId}, correlation {dispatch.Correlation}).",
            .. report.Repos.Select(r =>
                $"publish {r.Repo}: {r.Result}; branches [{string.Join(", ", r.Branches)}]; published [{string.Join(", ", r.Published ?? [])}]; reason {r.Reason ?? "-"}"),
            $"clone team/{team}: exit {branch.Exit} {branch.Stdout.Trim()}",
            $"contributor mode: {registry.ContributorFor(team, Repo).ContributorMode}",
            $"team floor {floor}; dispatch current for the team above it: {current}",
            $"bases: [{string.Join("; ", bases.Select(b => $"{b.Repo} default {b.DefaultSha} team {b.TeamSha ?? "-"}"))}]",
            $"missed starts: [{string.Join("; ", missed.Select(m => $"{m.Repo}: {m.Reason}"))}]",
            later.Any(t => t.Sha == sha)
                ? $"the tip {sha} was recorded {waited.ElapsedMilliseconds} ms after this publish returned"
                : "the tip was still not recorded 10 s after this publish returned",
            .. rows.Select(m => $"row {m.Seq} {m.Type} from {m.Source} causation {m.CausationSeq?.ToString() ?? "-"} at {m.OccurredAt:HH:mm:ss.fff}: {(m.Payload.Length > 120 ? m.Payload[..120] : m.Payload)}"),
        ]));
        throw new UnreachableException();
    }

    private static DateTimeOffset Parse(string at) =>
        DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture);

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

    private async Task<JsonElement> LandedOverHttpAsync(HttpClient person, long item)
    {
        var body = await person.GetFromJsonAsync<JsonElement>($"/api/backlog/{item}", Ct);
        return body.GetProperty("item").GetProperty("landed");
    }

    private async Task<BacklogDispatch> CurrentDispatchAsync(long item) =>
        (await Backlog.DispatchesAsync(item, Ct))[^1];

    private async Task<long> DispatchAsync(string team)
    {
        var item = await Backlog.CreateAsync(null, $"Item for {team}", "body", "person@example.test", Ct);

        // ABOVE THE TEAM'S FLOOR, as a real dispatch's correlation is: the row it wrote.
        var row = await _factory.Services.GetRequiredService<IMessageLog>().AppendAsync(
            new NewMessage("test.dispatched", "{}", "test", null), Ct);
        var dispatch = await Backlog.AddDispatchAsync(item.Id, team, team, row.Seq, "person@example.test", Ct);

        // WHERE IT STARTS, read as the dispatch routes read it before the Manager is told.
        await _factory.Services.GetRequiredService<BacklogTipRecorder>().RecordBaseAsync(dispatch, Ct);
        return item.Id;
    }

    private async Task<JsonElement> ReadAsync(HttpClient person, long item)
    {
        _factory.Services.GetRequiredService<BacklogLandedCache>().Clear();
        return await LandedOverHttpAsync(person, item);
    }

    /// <summary>Commits on top of trunk from a scratch clone and pushes them as team/{id} (and to
    /// <paramref name="alsoTo"/> when given), answering the sha.</summary>
    private string PushWork(string team, string message, string? alsoTo = null)
    {
        var work = Path.Combine(_root, $"work-{team}");
        Git(_root, "clone", "-b", "trunk", _origin, work);
        File.WriteAllText(Path.Combine(work, $"{team}.txt"), $"{message}\n");
        Commit(work, message);
        var sha = Run(work, "rev-parse", "HEAD").Stdout.Trim();

        Git(work, "push", "origin", $"HEAD:refs/heads/team/{team}");
        if (alsoTo is not null) Git(work, "push", "origin", $"HEAD:refs/heads/{alsoTo}");
        return sha;
    }

    private string Clone(string team) =>
        Path.Combine(_factory.Services.GetRequiredService<TeamPaths>().ReposFor(team), Repo, "main");

    private async Task<string> TeamAsync(string name)
    {
        var services = _factory.Services;
        var registry = services.GetRequiredService<TeamRegistry>();
        var agent = services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        // THE REPOSITORY AT CREATION, NOT SET AFTERWARDS. Set afterwards, the platform tells the
        // Manager its repositories are ready, and each Manager run that follows (that one, then the
        // "nobody is working it" nudge) ends in a run-end publish of this clone. Under load one of
        // those lands between a test's commit and its own publish: it pushes the commit, the test's
        // publish then has nothing to push, and the tip is recorded by that other publish moments
        // after the test has read it. Created with it, the Manager is told nothing.
        var team = (await registry.CreateAsync(name, agent, memberAgent: agent, repos: [Url], ct: Ct)).Id;

        Assert.True(Directory.Exists(Path.Combine(Clone(team), ".git")), "the platform did not clone");
        Assert.Equal("trunk", registry.DefaultBranchFor(team, Repo).Branch);
        return team;
    }

    private bool _personMade;

    private async Task<HttpClient> PersonAsync()
    {
        if (!_personMade)
        {
            await _factory.Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", Password);
            _personMade = true;
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = Password }, Ct);
        login.EnsureSuccessStatusCode();
        return client;
    }

    private static void Commit(string repo, string message)
    {
        Git(repo, "add", ".");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "core.hooksPath=/dev/null", "commit", "-m", message);
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
