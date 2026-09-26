using System.Diagnostics;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Kanban;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// A settled card's worktree is removed with `git worktree remove` and never forced:
/// a tree with uncommitted edits, or commits not on origin, is left on disk and named. Real git,
/// in a temp directory, against a bare origin so "pushed" means what it means in production.
/// </summary>
public sealed class WorktreeRemovalTests : IAsyncDisposable
{
    private const string Team = "Alpha";
    private const string Repo = "Repo";

    private readonly ContainerTestBed _bed = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"harness-wtrm-{Guid.NewGuid():N}");

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_clean_settled_tree_is_removed()
    {
        var (paths, clone) = Setup();
        var tree = CutTree(paths, clone, "Dev", "101", push: true);

        var report = await Removal().RemoveAsync(clone, [tree], Ct);

        Assert.Equal([tree], report.Removed);
        Assert.Empty(report.Left);
        Assert.False(Directory.Exists(tree));
        Assert.DoesNotContain(tree, Git(clone, "worktree", "list").Output, StringComparison.Ordinal);

        // The branch is never deleted with the tree.
        Assert.Equal(0, Git(clone, "rev-parse", "--verify", "refs/heads/dev/101").ExitCode);
    }

    [Fact]
    public async Task A_dirty_tree_is_left_and_named_on_the_feed_not_forced()
    {
        var (paths, clone) = Setup();
        var tree = CutTree(paths, clone, "Dev", "101", push: true);
        var edit = Path.Combine(tree, "half-done.txt");
        File.WriteAllText(edit, "not committed");

        var report = await Removal().RemoveKeysAsync(
            paths, Team, [$"https://example.invalid/org/{Repo}.git"], new HashSet<string> { "101" },
            causation: null, Ct);

        Assert.Empty(report.Removed);
        var left = Assert.Single(report.Left);
        Assert.Equal(tree, left.Path);
        Assert.Contains("git worktree remove refused it", left.Reason, StringComparison.Ordinal);

        // Still there, with the edit, and still registered: nothing was forced.
        Assert.True(File.Exists(edit));
        Assert.Contains(tree, Git(clone, "worktree", "list").Output, StringComparison.Ordinal);

        var row = Assert.Single(await LeftRowsAsync());
        Assert.Equal($"{Team}/Dev", row.Source);
        Assert.Equal(tree, Field(row, PayloadFields.Worktree));
        Assert.Equal(Repo, Field(row, PayloadFields.Repo));
        Assert.Contains(tree, MessageText.Of(row), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tree_whose_commits_are_not_on_origin_is_left()
    {
        var (paths, clone) = Setup();
        var tree = CutTree(paths, clone, "Dev", "101", push: false);

        var report = await Removal().RemoveAsync(clone, [tree], Ct);

        Assert.Empty(report.Removed);
        Assert.Contains("not on origin", Assert.Single(report.Left).Reason, StringComparison.Ordinal);
        Assert.True(Directory.Exists(tree));
    }

    [Fact]
    public async Task Cleanup_removes_settled_trees_and_keeps_an_open_cards_tree()
    {
        var (paths, clone) = Setup();

        // One workflow, two cards: one handed back (settled), one still running (open).
        var root = await _bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(new ContainerId(Team, "Manager")),
            """{"instruction":"build it"}""", "console"), Ct);
        var done = await _bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(new ContainerId(Team, "Dev")),
            """{"instruction":"part 1"}""", $"{Team}/Manager", root.Seq), Ct);
        var running = await _bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(new ContainerId(Team, "Dev2")),
            """{"instruction":"part 2"}""", $"{Team}/Manager", root.Seq), Ct);
        await _bed.Store.AppendAsync(new NewMessage(
            MessageTypes.Handback, """{"delivered":"part 1"}""", $"{Team}/Dev", done.Seq), Ct);

        var kanban = new KanbanStore(_bed.Store);
        var board = await kanban.GetBoardAsync(filter: new KanbanFilter(Team: Team));
        var settledCard = board.Cards.Single(c => c.Member == "Dev").Id;
        var openCard = board.Cards.Single(c => c.Member == "Dev2").Id;

        var settledTree = CutTree(paths, clone, "Dev", settledCard, push: true);
        var openTree = CutTree(paths, clone, "Dev2", openCard, push: true);

        var (report, kept) = await Removal().CleanUpSettledAsync(
            paths, kanban, Team, Repo, clone, Registered(clone), Ct);

        Assert.Equal([settledTree], report.Removed);
        Assert.Equal([openTree], kept);
        Assert.True(Directory.Exists(openTree));
        Assert.False(Directory.Exists(settledTree));
        _ = running;
    }

    [Fact]
    public async Task A_correlation_keyed_tree_is_open_while_its_workflow_is()
    {
        var root = await _bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(new ContainerId(Team, "Dev")),
            """{"instruction":"errand"}""", "console"), Ct);
        var key = Worktrees.KeyFor(null, root.CorrelationId);
        var kanban = new KanbanStore(_bed.Store);

        Assert.Contains(key, await WorktreeRemoval.OpenKeysAsync(kanban, _bed.Store, Team, [key], Ct));

        await _bed.Store.AppendAsync(new NewMessage(
            MessageTypes.WorkflowCompleted, """{"delivered":"done"}""", $"{Team}/Dev", root.Seq), Ct);

        Assert.DoesNotContain(key, await WorktreeRemoval.OpenKeysAsync(kanban, _bed.Store, Team, [key], Ct));
    }

    [Fact]
    public async Task A_declared_workflow_settles_its_handed_back_cards_and_its_own_key_but_not_a_loose_end()
    {
        var root = await _bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(new ContainerId(Team, "Manager")),
            """{"instruction":"build it"}""", "console"), Ct);
        var done = await _bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(new ContainerId(Team, "Dev")),
            """{"instruction":"part 1"}""", $"{Team}/Manager", root.Seq), Ct);
        var dropped = await _bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(new ContainerId(Team, "Dev2")),
            """{"instruction":"part 2"}""", $"{Team}/Manager", root.Seq), Ct);
        await _bed.Store.AppendAsync(new NewMessage(
            MessageTypes.Handback, """{"delivered":"part 1"}""", $"{Team}/Dev", done.Seq), Ct);
        await _bed.Store.AppendAsync(new NewMessage(
            MessageTypes.Failed, """{"exitCode":1,"output":"gave out"}""", $"{Team}/Dev2", dropped.Seq), Ct);

        var kanban = new KanbanStore(_bed.Store);
        var board = await kanban.GetBoardAsync(filter: new KanbanFilter(Team: Team));

        var keys = await WorktreeRemoval.SettledKeysAsync(kanban, Team, root.CorrelationId, "Manager");

        Assert.Contains(Worktrees.Sanitise(board.Cards.Single(c => c.Member == "Dev").Id)!, keys);
        Assert.DoesNotContain(Worktrees.Sanitise(board.Cards.Single(c => c.Member == "Dev2").Id)!, keys);
        Assert.Contains($"w{root.CorrelationId}", keys);
    }

    [Fact]
    public async Task Removing_by_key_touches_every_members_tree_for_that_card_and_nothing_else()
    {
        var (paths, clone) = Setup();
        var devTree = CutTree(paths, clone, "Dev", "101", push: true);
        var dev2Tree = CutTree(paths, clone, "Dev2", "101", push: true);
        var otherCard = CutTree(paths, clone, "Dev", "102", push: true);

        var report = await Removal().RemoveKeysAsync(
            paths, Team, [$"https://example.invalid/org/{Repo}.git"], new HashSet<string> { "101" },
            causation: null, Ct);

        Assert.Equal(new[] { devTree, dev2Tree }.Order(), report.Removed.Order());
        Assert.True(Directory.Exists(otherCard));
    }

    /// <summary>
    /// The real Host: member deletion removes every one of Dev's trees under the no-force rule,
    /// names the dirty one it left, and never touches Dev2's - a member whose name Dev prefixes.
    /// </summary>
    [Fact]
    public async Task Member_deletion_removes_all_its_trees_names_the_ones_left_and_leaves_Dev2s_alone()
    {
        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataRoot);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());
                services.AddSingleton<IRepoClone>(new NoClone());
            }));

        try
        {
            var services = factory.Services;
            var registry = services.GetRequiredService<TeamRegistry>();
            var paths = services.GetRequiredService<TeamPaths>();
            var agent = services.GetRequiredService<AgentCatalog>().Definitions
                .First(d => d.Mode == AgentMode.Headless).Name;

            var team = (await registry.CreateAsync(
                "Alpha", agent, memberAgent: agent,
                ct: Ct)).Id;
            await registry.SetReposAsync(team, [$"https://github.com/example/{Repo}.git"], Ct);

            foreach (var name in new[] { "Dev", "Dev2" })
            {
                await registry.AddContainerAsync(
                    team, name, agent, "", [], permits: new HashSet<string>(Permits.All),
                    ct: Ct);
            }

            var clone = MakeClone(Path.Combine(paths.ReposFor(team), Repo, "main"));
            var clean = CutTree(paths, clone, "Dev", "101", push: true, team: team);
            var dirty = CutTree(paths, clone, "Dev", "102", push: true, team: team);
            File.WriteAllText(Path.Combine(dirty, "half-done.txt"), "not committed");
            var dev2s = CutTree(paths, clone, "Dev2", "101", push: true, team: team);

            var deleted = await services.GetRequiredService<MemberDeletion>().DeleteAsync(team, "Dev", Ct);

            Assert.NotNull(deleted);
            Assert.Contains(clean, deleted.Directories);
            Assert.False(Directory.Exists(clean));
            Assert.True(Directory.Exists(dirty));
            Assert.Contains(deleted.Failures, f => f.StartsWith(dirty, StringComparison.Ordinal));
            Assert.True(Directory.Exists(dev2s));

            var log = services.GetRequiredService<IMessageLog>();
            var rows = await log.ReadAfterAsync(0, [MessageTypes.RepoWorktreeLeft], 50, Ct);
            var row = Assert.Single(rows);
            Assert.Equal(new ContainerId(team, "Dev").ToString(), row.Source);
            Assert.Equal(dirty, Field(row, PayloadFields.Worktree));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // --- helpers ---

    private WorktreeRemoval Removal() => new(new GitRunner(), _bed.Store);

    private async Task<IReadOnlyList<Message>> LeftRowsAsync() =>
        await _bed.Store.ReadAfterAsync(0, [MessageTypes.RepoWorktreeLeft], 50, Ct);

    private (TeamPaths Paths, string Clone) Setup()
    {
        var paths = new TeamPaths(_root);
        paths.Register(Team, null);
        return (paths, MakeClone(Path.Combine(paths.ReposFor(Team), Repo, "main")));
    }

    private string MakeClone(string clone)
    {
        var origin = Path.Combine(_root, $"origin-{Guid.NewGuid():N}.git");
        Git(_root, "init", "--bare", "-b", "main", origin);
        Git(_root, "clone", origin, clone);
        Commit(clone, "base");
        Git(clone, "push", "origin", "main");
        return clone;
    }

    private string CutTree(TeamPaths paths, string clone, string member, string key, bool push, string team = Team)
    {
        var tree = paths.WorktreeFor(new ContainerId(team, member), Repo, key);
        var branch = Worktrees.BranchHint(member, key);

        // Git's own words on a failure, so a setup step that fails says why.
        var added = Git(clone, "worktree", "add", tree, "-b", branch);
        Assert.True(added.ExitCode == 0, $"git worktree add {tree}: exit {added.ExitCode}: {added.Output}");
        Commit(tree, $"{member}-{key}");
        if (push)
        {
            var pushed = Git(tree, "push", "origin", branch);
            Assert.True(pushed.ExitCode == 0, $"git push {branch}: exit {pushed.ExitCode}: {pushed.Output}");
        }

        return tree;
    }

    private static IEnumerable<string> Registered(string clone) =>
        Git(clone, "worktree", "list", "--porcelain").Output
            .Split('\n')
            .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(line => line["worktree ".Length..].Trim())
            .ToList();

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

    private sealed class NoClone : IRepoClone
    {
        public Task<IReadOnlyList<RepoCloneOutcome>> EnsureAllAsync(
            IReadOnlyList<(string Url, string Path)> repos, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RepoCloneOutcome>>([]);
    }

    public async ValueTask DisposeAsync()
    {
        await _bed.DisposeAsync();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
