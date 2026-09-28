using System.Collections.Concurrent;
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
/// A WORKTREE PER CARD, END TO END ON THE REAL HOST.
///
/// The real Host in memory, with real git against a local bare origin. Nothing is called on a
/// helper directly: a scripted Manager plans cards and tells the member through the routes with its
/// own minted key, the member cuts and works in the tree named by <c>HARNESS_WORKTREE</c> exactly as
/// the served <c>worktrees</c> skill says, a person stops a run through the route the board's Stop
/// calls, the member hands back, the Manager declares through <c>workflow-complete</c>, and a person
/// deletes the member through <c>DELETE /containers/{name}</c>. Only the agent is scripted.
/// </summary>
public sealed class WorktreePerCardAcceptanceTests : IAsyncDisposable
{
    private const string Repo = "Repo";
    private const string Password = "correct horse battery";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"harness-worktree-per-card-{Guid.NewGuid():N}");
    private readonly Scripted _agent = new();
    private readonly ConcurrentDictionary<string, string> _cards = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Tag, HttpStatusCode Status, string Body)> _declarations = new();
    private readonly ConcurrentDictionary<string, Func<AgentInvocation, string, CancellationToken, Task>> _work =
        new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Tag, string Tree, string Hint, bool Existed)> _entered = new();

    private WebApplicationFactory<Program> _factory = null!;
    private string _team = "";
    private string _clone = "";
    private string _origin = "";
    private TeamPaths _paths = null!;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Two cards in two workflows get two trees on two branches; an interrupted
    /// card's uncommitted edit survives the other card's run and is there when the card is resumed
    /// with the same <c>card</c>; the resumed branch only moves forward on origin.
    /// </summary>
    [Fact]
    public async Task Two_cards_two_trees_and_an_interrupted_card_resumes_in_its_own_tree_and_only_moves_forward()
    {
        await StartAsync();
        var person = await PersonAsync();

        var c1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The first card, first run: one commit, one uncommitted edit, then wait to be stopped.
        _work["c1"] = async (_, tree, ct) =>
        {
            Commit(tree, "c1-first");
            File.WriteAllText(Path.Combine(tree, "c1-wip.txt"), "half done, not committed");
            c1Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };

        // The second card: its own edit, committed, handed back.
        var c2SawC1Edit = (bool?)null;
        _work["c2"] = async (invocation, tree, ct) =>
        {
            c2SawC1Edit = File.Exists(Path.Combine(tree, "c1-wip.txt"))
                || File.Exists(Path.Combine(tree, "c1-first.txt"));
            File.WriteAllText(Path.Combine(tree, "c2-edit.txt"), "card two");
            Git(tree, "add", ".");
            Git(tree, "commit", "-m", "c2");
            await HandBackAsync(invocation, "#done c2", ct);
        };

        // The first card, resumed: the edit must be there; commit it and hand back.
        string? resumedWip = null;
        _work["c1-resume"] = async (invocation, tree, ct) =>
        {
            var wip = Path.Combine(tree, "c1-wip.txt");
            resumedWip = File.Exists(wip) ? File.ReadAllText(wip) : null;
            Git(tree, "add", ".");
            Git(tree, "commit", "-m", "c1 finished");
            await HandBackAsync(invocation, "#done c1", ct);
        };

        // Workflow A: the Manager plans the first card and tells Dev.
        await TellAsync(person, "Manager", "#plan c1");
        await c1Started.Task.WaitAsync(Patience, Ct);

        // Stop it the way the board's Stop button does: a person on the stop route.
        var stop = await person.PostAsync($"/api/teams/{_team}/containers/Dev/stop", null, Ct);
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);

        var c1 = _cards["c1"];
        var c1Tree = _paths.WorktreeFor(new ContainerId(_team, "Dev"), Repo, c1);
        var c1Branch = Worktrees.BranchHint("Dev", c1);

        // The platform publishes the stopped run's committed work (TerminalPublish).
        var c1OnOriginAfterStop = await EventuallyAsync(() => OriginSha(c1Branch));

        // Workflow B: the second card, on the same member, while the first is interrupted.
        await TellAsync(person, "Manager", "#plan c2");
        await EventuallyAsync(() => _declarations.Any(d => d.Tag == "c2" && d.Status == HttpStatusCode.NoContent)
            ? "yes" : null);

        var c2 = _cards["c2"];
        var c2Tree = _paths.WorktreeFor(new ContainerId(_team, "Dev"), Repo, c2);

        // --- Two trees, named by card, on their own branches, edits not shared.
        var entered = _entered.ToList();
        Assert.Equal(c1Tree, entered.Single(e => e.Tag == "c1").Tree);
        Assert.Equal(c2Tree, entered.Single(e => e.Tag == "c2").Tree);
        Assert.Equal($"wt_Dev_{c1}", Path.GetFileName(c1Tree));
        Assert.Equal($"wt_Dev_{c2}", Path.GetFileName(c2Tree));
        Assert.NotEqual(c1Tree, c2Tree);
        Assert.False(entered.Single(e => e.Tag == "c2").Existed);
        Assert.Equal($"dev/{c1}", entered.Single(e => e.Tag == "c1").Hint);
        Assert.Equal($"dev/{c2}", entered.Single(e => e.Tag == "c2").Hint);
        Assert.False(c2SawC1Edit);

        // While the first card is interrupted its tree is on its own branch with the edit uncommitted.
        Assert.Equal(c1Branch, Git(c1Tree, "rev-parse", "--abbrev-ref", "HEAD").Output.Trim());
        Assert.Contains("c1-wip.txt", Git(c1Tree, "status", "--porcelain").Output, StringComparison.Ordinal);

        // The second card's settled tree went when workflow B was declared (clean and pushed).
        Assert.False(Directory.Exists(c2Tree), "workflow B's settled card tree should have been removed");
        Assert.NotNull(OriginSha(Worktrees.BranchHint("Dev", c2)));

        // --- Resume the first card with the same card, in a new workflow.
        await TellAsync(person, "Manager", "#resume c1");
        await EventuallyAsync(() => resumedWip is not null ? "yes" : null);

        var resumed = _entered.Single(e => e.Tag == "c1-resume");
        Assert.Equal(c1Tree, resumed.Tree);
        Assert.True(resumed.Existed);
        Assert.Equal("half done, not committed", resumedWip);

        // --- The resumed branch only moved forward. The platform's own publish is
        // fast-forward-only; wait for it, then prove ancestry and a plain push from the clone.
        var c1OnOriginAfterResume = await EventuallyAsync(() =>
            OriginSha(c1Branch) is { } sha && sha != c1OnOriginAfterStop ? sha : null);
        Assert.Equal(0, Git(_origin, "merge-base", "--is-ancestor", c1OnOriginAfterStop, c1OnOriginAfterResume).ExitCode);
        var push = Git(_clone, "push", "origin", c1Branch);
        Assert.Equal(0, push.ExitCode);
        Assert.DoesNotContain("forced update", push.Output, StringComparison.Ordinal);
        Assert.Empty(await RowsAsync(MessageTypes.RepoPushFailed));
    }

    /// <summary>
    /// The resume case. A card planned in workflow A, interrupted, then resumed with the
    /// same <c>card</c> from workflow C and handed back there: declaring C settles it (clean, pushed),
    /// so its tree should go - or, if it cannot, be named on the feed.
    ///
    /// A planned card keeps the correlation of the row that PLANNED it, so matching the declared
    /// workflow's cards on <c>WorkflowSeq</c> alone would miss it; <c>KanbanCard.BelongsTo</c>
    /// counts the workflow that resumed it too.
    /// </summary>
    [Fact]
    public async Task Declaring_the_workflow_that_resumed_a_card_removes_that_cards_tree()
    {
        await StartAsync();
        var person = await PersonAsync();

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _work["c1"] = async (_, tree, ct) =>
        {
            Commit(tree, "c1-first");
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        _work["c1-resume"] = async (invocation, tree, ct) =>
        {
            Commit(tree, "c1-finished");
            await HandBackAsync(invocation, "#done c1", ct);
        };

        await TellAsync(person, "Manager", "#plan c1");
        await started.Task.WaitAsync(Patience, Ct);
        Assert.Equal(HttpStatusCode.OK,
            (await person.PostAsync($"/api/teams/{_team}/containers/Dev/stop", null, Ct)).StatusCode);

        await TellAsync(person, "Manager", "#resume c1");
        await EventuallyAsync(() => _declarations.Any(d => d.Tag == "c1" && d.Status == HttpStatusCode.NoContent)
            ? "yes" : null);

        var c1Tree = _paths.WorktreeFor(new ContainerId(_team, "Dev"), Repo, _cards["c1"]);
        var leftRows = (await RowsAsync(MessageTypes.RepoWorktreeLeft)).Select(r => Field(r, PayloadFields.Worktree));

        Assert.True(!Directory.Exists(c1Tree) || leftRows.Contains(c1Tree),
            $"{c1Tree} is still on disk and not named on the feed after the workflow that finished it was declared.");
    }

    /// <summary>
    /// The other half of the same defect: a card resumed in workflow C and still open is a loose end
    /// of C, so C's declaration is refused and names it - even though the card was planned in A.
    /// C also has a sibling card that is handed back, which is what wakes the Manager to declare.
    /// </summary>
    [Fact]
    public async Task A_resumed_card_that_is_still_open_is_a_loose_end_of_the_workflow_that_resumed_it()
    {
        await StartAsync();
        var person = await PersonAsync();

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gaveUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _work["c5"] = async (_, tree, ct) =>
        {
            Commit(tree, "c5-first");
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        _work["c5-resume"] = async (invocation, _, ct) =>
        {
            var blocked = await ContainerClient(invocation).PostAsJsonAsync(
                $"/api/teams/{_team}/containers/Dev/blocked", new { reason = "cannot finish c5" }, ct);
            Assert.Equal(HttpStatusCode.NoContent, blocked.StatusCode);
            gaveUp.TrySetResult();
        };
        _work["c6"] = async (invocation, tree, ct) =>
        {
            Commit(tree, "c6");
            await gaveUp.Task.WaitAsync(Patience, ct);

            // Dev's run ending wakes the Manager too. Hand back only once that wake is over, or the
            // declaration it causes is refused for the Manager's own pending delivery instead.
            await QuietAsync(except: "Dev2");
            await HandBackAsync(invocation, "#declare c6", ct);
        };

        await TellAsync(person, "Manager", "#plan c5");
        await started.Task.WaitAsync(Patience, Ct);
        Assert.Equal(HttpStatusCode.OK,
            (await person.PostAsync($"/api/teams/{_team}/containers/Dev/stop", null, Ct)).StatusCode);

        await TellAsync(person, "Manager", "#reopen c5 c6:Dev2");
        await EventuallyAsync(() => _declarations.Any(d => d.Tag == "c6") ? "yes" : null);

        var (_, status, body) = _declarations.Single(d => d.Tag == "c6");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.True(body.Contains("not finished", StringComparison.Ordinal), body);
        Assert.Contains($"card {_cards["c5"]} ", body, StringComparison.Ordinal);
        Assert.DoesNotContain($"card {_cards["c6"]} ", body, StringComparison.Ordinal);
        Assert.Empty(await RowsAsync(MessageTypes.WorkflowCompleted));
    }

    /// <summary>
    /// Settling through the real <c>workflow-complete</c> route: one workflow, two cards on Dev,
    /// one clean and one left dirty. The declaration removes the clean one and leaves the dirty one,
    /// named on the feed.
    /// </summary>
    [Fact]
    public async Task Declaring_the_workflow_removes_its_settled_clean_tree_and_names_the_dirty_one_it_left()
    {
        await StartAsync();
        var person = await PersonAsync();

        _work["c3"] = async (invocation, tree, ct) =>
        {
            Commit(tree, "c3");
            await HandBackAsync(invocation, "#done c3", ct);
        };
        _work["c4"] = async (invocation, tree, ct) =>
        {
            Commit(tree, "c4");
            File.WriteAllText(Path.Combine(tree, "c4-left.txt"), "not committed");
            await HandBackAsync(invocation, "#done c4", ct);
        };

        await TellAsync(person, "Manager", "#plan c3 c4:Dev2");
        await EventuallyAsync(() => _declarations.Any(d => d.Status == HttpStatusCode.NoContent) ? "yes" : null);

        var c3Tree = _paths.WorktreeFor(new ContainerId(_team, "Dev"), Repo, _cards["c3"]);
        var c4Tree = _paths.WorktreeFor(new ContainerId(_team, "Dev2"), Repo, _cards["c4"]);

        Assert.False(Directory.Exists(c3Tree), "the clean settled tree should have been removed");
        Assert.True(File.Exists(Path.Combine(c4Tree, "c4-left.txt")), "the dirty tree must be left, not forced");
        Assert.Contains(c4Tree, Git(_clone, "worktree", "list").Output, StringComparison.Ordinal);

        var left = Assert.Single(await RowsAsync(MessageTypes.RepoWorktreeLeft));
        Assert.Equal($"{_team}/Dev2", left.Source);
        Assert.Equal(c4Tree, Field(left, PayloadFields.Worktree));
        Assert.Contains(c4Tree, MessageText.Of(left), StringComparison.Ordinal);

        // The row belongs to the declared workflow's thread, not a root of its own.
        var declared = (await RowsAsync(MessageTypes.WorkflowCompleted)).Single();
        Assert.Equal(declared.CorrelationId, left.CorrelationId);
    }

    /// <summary>
    /// Deleting a member through the real <c>DELETE /containers/{name}</c> route as a person: every tree of the
    /// member goes, and each one it cannot remove (dirty; commits not on origin) is named in the
    /// answer and on the feed. Another member's tree for the same card is untouched.
    /// </summary>
    [Fact]
    public async Task Deleting_the_member_removes_its_trees_and_names_the_ones_it_left()
    {
        await StartAsync();
        var person = await PersonAsync();

        // Nothing may run while the trees are cut: any run that ends publishes every unpublished
        // branch in the clone (TerminalPublish), which would push the tree meant to stay unpushed.
        Assert.True((await person.PostAsync($"/api/teams/{_team}/pause", null, Ct)).IsSuccessStatusCode);
        await NothingRunningAsync();

        var dev = new ContainerId(_team, "Dev");
        var clean = CutTree(dev, "901", push: true);
        var otherClean = CutTree(dev, "w902", push: true);
        var dirty = CutTree(dev, "903", push: true);
        File.WriteAllText(Path.Combine(dirty, "half.txt"), "not committed");
        var unpushed = CutTree(dev, "904", push: false);
        var devTwos = CutTree(new ContainerId(_team, "Dev2"), "901", push: true);

        var response = await person.DeleteAsync($"/api/teams/{_team}/containers/Dev", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        var failures = body.GetProperty("failures").EnumerateArray().Select(f => f.GetString() ?? "").ToList();

        Assert.False(Directory.Exists(clean));
        Assert.False(Directory.Exists(otherClean));
        Assert.True(Directory.Exists(dirty));
        Assert.True(Directory.Exists(unpushed));
        Assert.Contains(failures, f => f.StartsWith(dirty, StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith(unpushed, StringComparison.Ordinal));
        Assert.True(Directory.Exists(devTwos));

        var named = (await RowsAsync(MessageTypes.RepoWorktreeLeft)).Select(r => Field(r, PayloadFields.Worktree)).ToList();
        Assert.Equal(new[] { dirty, unpushed }.Order(), named.Order());
    }

    /// <summary>No source names a single per-member tree: every tree is a card's.</summary>
    [Fact]
    public void No_source_still_names_the_single_per_member_tree()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var hits = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains("node_modules", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("wt_{id.Name}\"", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(hits);
    }

    // --- the scripted agents ---------------------------------------------------------------------

    private async Task<AgentResult> BehaveAsync(AgentInvocation invocation, CancellationToken ct)
    {
        var prompt = invocation.Prompt;

        if (invocation.Container.Name == "Manager")
        {
            var client = ContainerClient(invocation);
            var causation = invocation.Environment["HARNESS_CAUSATION"];

            if (Directive(prompt, "#plan") is { } plan)
            {
                foreach (var entry in plan.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var tag = entry.Split(':')[0];
                    var who = entry.Contains(':') ? entry.Split(':')[1] : "Dev";
                    var planned = await client.PostAsJsonAsync(
                        $"/api/teams/{_team}/kanban/plan", new { title = tag, causation = long.Parse(causation) }, ct);
                    planned.EnsureSuccessStatusCode();
                    var card = JsonDocument.Parse(await planned.Content.ReadAsStringAsync(ct))
                        .RootElement.GetProperty("card").GetString()!;
                    _cards[tag] = card;

                    var told = await client.PostAsJsonAsync(
                        $"/api/teams/{_team}/containers/{who}/tell",
                        new { instruction = $"#work {tag}", causation, card }, ct);
                    told.EnsureSuccessStatusCode();
                }
            }
            else if (Directive(prompt, "#reopen") is { } reopen)
            {
                // "#reopen c5 c6:Dev2": resume the first card on Dev, plan the rest, one workflow.
                var tags = reopen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var resumed = await client.PostAsJsonAsync(
                    $"/api/teams/{_team}/containers/Dev/tell",
                    new { instruction = $"#work {tags[0]}-resume", causation, card = _cards[tags[0]] }, ct);
                resumed.EnsureSuccessStatusCode();

                foreach (var entry in tags.Skip(1))
                {
                    var tag = entry.Split(':')[0];
                    var who = entry.Split(':')[1];
                    var planned = await client.PostAsJsonAsync(
                        $"/api/teams/{_team}/kanban/plan", new { title = tag, causation = long.Parse(causation) }, ct);
                    planned.EnsureSuccessStatusCode();
                    var card = JsonDocument.Parse(await planned.Content.ReadAsStringAsync(ct))
                        .RootElement.GetProperty("card").GetString()!;
                    _cards[tag] = card;

                    var told = await client.PostAsJsonAsync(
                        $"/api/teams/{_team}/containers/{who}/tell",
                        new { instruction = $"#work {tag}", causation, card }, ct);
                    told.EnsureSuccessStatusCode();
                }
            }
            else if (Directive(prompt, "#declare") is { } declare)
            {
                // Declared once the member that woke it has finished its run: asks again only while
                // work is still in flight, and records the first answer about the cards.
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
                while (true)
                {
                    var declared = await client.PostAsJsonAsync(
                        $"/api/teams/{_team}/containers/Manager/workflow-complete",
                        new { delivered = $"delivered {declare}" }, ct);
                    var text = await declared.Content.ReadAsStringAsync(ct);

                    if (declared.StatusCode == HttpStatusCode.Conflict && DateTime.UtcNow < deadline
                        && text.Contains("still in flight", StringComparison.Ordinal))
                    {
                        await Task.Delay(200, ct);
                        continue;
                    }

                    _declarations.Enqueue((declare, declared.StatusCode, text));
                    break;
                }
            }
            else if (Directive(prompt, "#resume") is { } resume)
            {
                var told = await client.PostAsJsonAsync(
                    $"/api/teams/{_team}/containers/Dev/tell",
                    new { instruction = $"#work {resume}-resume", causation, card = _cards[resume] }, ct);
                told.EnsureSuccessStatusCode();
            }
            else if (Directive(prompt, "#done") is { } done)
            {
                // Two cards handed back are two wakes of this Manager in one workflow. The first can
                // be accepted in the moment after the second member went idle and before its
                // handback reached this Manager's queue, and the route accepts a second declaration
                // of a declared workflow - a repeat removal pass that names the dirty tree again. A
                // Manager reads its own history: a workflow already declared is not declared twice.
                var waking = await _factory.Services.GetRequiredService<IMessageLog>()
                    .FindAsync(long.Parse(causation), ct);
                if (waking is not null && (await RowsAsync(MessageTypes.WorkflowCompleted))
                        .Any(row => row.CorrelationId == waking.CorrelationId))
                {
                    return new AgentResult(0, "ok");
                }

                // Declared as a Manager would: refused while the member is still finishing its run
                // or a sibling card is open, so it asks again for a while.
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
                while (true)
                {
                    var declared = await client.PostAsJsonAsync(
                        $"/api/teams/{_team}/containers/Manager/workflow-complete",
                        new { delivered = $"delivered {done}" }, ct);
                    var text = await declared.Content.ReadAsStringAsync(ct);
                    _declarations.Enqueue((done, declared.StatusCode, text));

                    if (declared.StatusCode != HttpStatusCode.Conflict || DateTime.UtcNow > deadline
                        || text.Contains("not currently running", StringComparison.Ordinal))
                        break;

                    await Task.Delay(200, ct);
                }
            }

            return new AgentResult(0, "ok");
        }

        if (Directive(prompt, "#work") is { } work)
        {
            // What the served `worktrees` skill says: create it from the main clone when it does not
            // exist, otherwise work in it.
            var tree = invocation.Environment[MemberRuntime.WorktreeVariable];
            var hint = invocation.Environment[MemberRuntime.BranchHintVariable];
            var existed = Directory.Exists(tree);

            if (!existed)
            {
                var clone = Path.Combine(_paths.ReposFor(_team), Repo, "main");
                Assert.Equal(0, Git(clone, "worktree", "add", tree, "-b", hint).ExitCode);
            }

            _entered.Enqueue((work, tree, hint, existed));

            if (_work.TryGetValue(work, out var act)) await act(invocation, tree, ct);
        }

        return new AgentResult(0, "ok");
    }

    private static string? Directive(string prompt, string verb)
    {
        var at = prompt.IndexOf(verb + " ", StringComparison.Ordinal);
        if (at < 0) return null;

        var rest = prompt[(at + verb.Length + 1)..];
        var end = rest.IndexOfAny(['\n', '"', '.']);
        var words = (end < 0 ? rest : rest[..end]).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // `#plan` and `#reopen` take a list of tags ("#plan c3 c4:Dev2"); every other verb takes one.
        return verb is "#plan" or "#reopen" ? string.Join(' ', words) : words[0];
    }

    private async Task HandBackAsync(AgentInvocation invocation, string delivered, CancellationToken ct)
    {
        var response = await ContainerClient(invocation).PostAsJsonAsync(
            $"/api/teams/{_team}/containers/{invocation.Container.Name}/handback", new { delivered }, ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private HttpClient ContainerClient(AgentInvocation invocation)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, invocation.Environment["HARNESS_KEY"]);
        return client;
    }

    // --- host and git -----------------------------------------------------------------------------

    private async Task StartAsync()
    {
        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataRoot);
        _agent.Behaviour = BehaveAsync;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(_agent);
                services.AddSingleton<IRepoClone>(new NoClone());
            }));

        var services = _factory.Services;
        var registry = services.GetRequiredService<TeamRegistry>();
        _paths = services.GetRequiredService<TeamPaths>();
        var agent = services.GetRequiredService<AgentCatalog>().Definitions
            .First(d => d.Mode == AgentMode.Headless).Name;

        _team = (await registry.CreateAsync(
            "Alpha", agent, memberAgent: agent,
            ct: Ct)).Id;
        await registry.SetReposAsync(_team, [$"https://github.com/example/{Repo}.git"], Ct);

        foreach (var name in new[] { "Dev", "Dev2" })
        {
            await registry.AddContainerAsync(
                _team, name, agent, "", [], permits: new HashSet<string>(Permits.All),
                ct: Ct);
        }

        _origin = Path.Combine(_root, "origin.git");
        _clone = Path.Combine(_paths.ReposFor(_team), Repo, "main");
        Git(_root, "init", "--bare", "-b", "main", _origin);
        Git(_root, "clone", _origin, _clone);
        Commit(_clone, "base");
        Git(_clone, "push", "origin", "main");

        await services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", Password);
    }

    private async Task<HttpClient> PersonAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { email = "person@example.test", password = Password }, Ct);
        login.EnsureSuccessStatusCode();
        return client;
    }

    private async Task TellAsync(HttpClient person, string member, string instruction)
    {
        var told = await person.PostAsJsonAsync(
            $"/api/teams/{_team}/containers/{member}/tell", new { instruction }, Ct);
        told.EnsureSuccessStatusCode();
    }

    private async Task<string> EventuallyAsync(Func<string?> probe)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } found) return found;
            await Task.Delay(100, Ct);
        }

        var said = string.Join(" | ", _declarations.Select(d => $"{d.Tag}:{(int)d.Status}:{d.Body}"));
        throw new TimeoutException($"Timed out. Declarations: {said}");
    }

    /// <summary>Waits until every container has been idle with nothing queued for a second -
    /// every one but <paramref name="except"/>, when it is the one waiting.</summary>
    private async Task QuietAsync(string? except = null)
    {
        var host = _factory.Services.GetRequiredService<ContainerHost>();
        var deadline = DateTime.UtcNow + Patience;
        var quietSince = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            if (host.Snapshots().Any(s => s.Name != except && (s.State != ContainerState.Idle || s.QueueDepth > 0)))
                quietSince = DateTime.UtcNow;
            else if (DateTime.UtcNow - quietSince > TimeSpan.FromSeconds(1))
                return;

            await Task.Delay(50, Ct);
        }

        // Says WHAT never went quiet, so a failure under load can be read rather than guessed at.
        var still = string.Join(", ", host.Snapshots().Select(s => $"{s.Name}={s.State}/q{s.QueueDepth}"));
        throw new TimeoutException($"The host never went quiet: {still}.");
    }

    /// <summary>
    /// Waits until no container has been RUNNING for a second - the quiet a PAUSED team can reach.
    /// <see cref="QuietAsync"/> also waits for empty queues, and a paused team's queue does not
    /// empty: work delivered before the pause but not yet started stays queued, because a paused
    /// team "takes no slot and holds no place" (ContainerHost.TryClaimStart). Waiting for it after a
    /// pause could only time out. Queued
    /// work on a paused team cannot start, so it cannot push a tree; a run already going can.
    /// </summary>
    private async Task NothingRunningAsync()
    {
        var host = _factory.Services.GetRequiredService<ContainerHost>();
        var deadline = DateTime.UtcNow + Patience;
        var quietSince = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            if (host.Snapshots().Any(s => s.State == ContainerState.Running))
                quietSince = DateTime.UtcNow;
            else if (DateTime.UtcNow - quietSince > TimeSpan.FromSeconds(1))
                return;

            await Task.Delay(50, Ct);
        }

        var still = string.Join(", ", host.Snapshots().Select(s => $"{s.Name}={s.State}/q{s.QueueDepth}"));
        throw new TimeoutException($"A run never ended after the pause: {still}.");
    }

    private async Task<IReadOnlyList<Message>> RowsAsync(string type) =>
        await _factory.Services.GetRequiredService<IMessageLog>().ReadAfterAsync(0, [type], 500, Ct);

    private string? OriginSha(string branch)
    {
        var (code, output) = Git(_origin, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");
        return code == 0 ? output.Trim() : null;
    }

    private string CutTree(ContainerId member, string key, bool push)
    {
        var tree = _paths.WorktreeFor(member, Repo, key);
        var branch = Worktrees.BranchHint(member.Name, key);
        var add = Git(_clone, "worktree", "add", tree, "-b", branch);
        Assert.True(add.ExitCode == 0, add.Output);
        Commit(tree, $"{member.Name}-{key}");
        if (push)
        {
            var pushed = Git(tree, "push", "origin", branch);
            Assert.True(pushed.ExitCode == 0, pushed.Output);
        }

        return tree;
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx"))
               && !Directory.Exists(Path.Combine(directory.FullName, "src", "Harness.Host")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("No repository root above the test binary.");
    }

    private static string Field(Message message, string name) =>
        JsonDocument.Parse(message.Payload).RootElement.GetProperty(name).GetString() ?? "";

    private static void Commit(string repo, string name)
    {
        File.WriteAllText(Path.Combine(repo, $"{name}.txt"), name);
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", name);
    }

    private static (int ExitCode, string Output) Git(string workingDirectory, params string[] args)
    {
        Directory.CreateDirectory(workingDirectory);

        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["GIT_AUTHOR_NAME"] = "t";
        start.Environment["GIT_AUTHOR_EMAIL"] = "t@example.invalid";
        start.Environment["GIT_COMMITTER_NAME"] = "t";
        start.Environment["GIT_COMMITTER_EMAIL"] = "t@example.invalid";

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    private sealed class Scripted : IAgentRunner
    {
        public Func<AgentInvocation, CancellationToken, Task<AgentResult>> Behaviour { get; set; } =
            (_, _) => Task.FromResult(new AgentResult(0, "ok"));

        public Task<AgentResult> RunAsync(AgentInvocation invocation, CancellationToken ct = default) =>
            Behaviour(invocation, ct);
    }

    private sealed class NoClone : IRepoClone
    {
        public Task<IReadOnlyList<RepoCloneOutcome>> EnsureAllAsync(
            IReadOnlyList<(string Url, string Path)> repos, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RepoCloneOutcome>>([]);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();

        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
