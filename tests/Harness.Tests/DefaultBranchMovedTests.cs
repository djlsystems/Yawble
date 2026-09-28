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
/// A MANAGER THAT MOVES ITS CLONE'S DEFAULT BRANCH IS REPORTED, ON THE REAL HOST WITH A REAL CLONE.
///
/// The real Host in memory, real git against a local bare origin, and a scripted Manager told by a
/// person through the tell route. At the end of its run the platform compares the clone's STORED
/// default branch with origin's and, when it holds commits origin's lacks, reports it on the
/// Manager's card, in the feed and in the Git dialog's status - and resets nothing.
/// </summary>
public sealed class DefaultBranchMovedTests : IAsyncDisposable
{
    private const string Repo = "Repo";
    private const string Password = "correct horse battery";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"harness-default-moved-{Guid.NewGuid():N}");
    private readonly Scripted _agent = new();

    private WebApplicationFactory<Program> _factory = null!;
    private string _team = "";
    private string _clone = "";
    private string _origin = "";

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_manager_run_that_moves_the_clones_default_branch_is_reported_on_the_card_the_feed_and_the_dialog()
    {
        await StartAsync(defaultBranch: "trunk");
        var person = await PersonAsync();

        // The Manager breaks the rule: a commit straight onto the clone's default branch.
        _agent.Behaviour = (invocation, _) =>
        {
            if (invocation.Container.Name == "Manager") Commit(_clone, "on-trunk");
            return Task.FromResult(new AgentResult(0, "ok"));
        };

        await TellAsync(person, "Manager", "merge the work");
        var row = await EventuallyAsync(async () => (await RowsAsync(MessageTypes.RepoDefaultBranchMoved)).FirstOrDefault());

        var moved = Git(_clone, "rev-parse", "refs/heads/trunk").Output.Trim();
        var sentence = $"moved trunk in the clone; the work is on {moved}; the team branch is team/{_team}";

        // The row: the Manager's, inside the workflow it was running, naming the stored branch.
        Assert.Equal(new ContainerId(_team, "Manager").ToString(), row.Source);
        Assert.Equal(sentence, Field(row, PayloadFields.Reason));
        Assert.Equal("trunk", Field(row, PayloadFields.Branch));
        Assert.Equal(moved, Field(row, PayloadFields.Commit));
        Assert.Equal($"team/{_team}", Field(row, PayloadFields.TeamBranch));

        // Before the terminal row of the run that moved it, so it is part of the run the card shows.
        async Task<List<Message>> ManagerRowsAsync() =>
            [.. (await _factory.Services.GetRequiredService<IMessageLog>().ReadRangeAsync(0, 500, Ct))
                .Where(m => m.Source == row.Source)];
        var started = (await ManagerRowsAsync()).Last(m => m.Type == MessageTypes.Started && m.Seq < row.Seq);
        var terminal = await EventuallyAsync(async () => (await ManagerRowsAsync())
            .FirstOrDefault(m => m.Seq > started.Seq && m.Type is MessageTypes.Completed or MessageTypes.Failed));
        Assert.True(row.Seq < terminal.Seq, "the report must land before the terminal row of the run that moved it");

        // The card: the Manager's card for this workflow carries the sentence on its trail.
        var board = JsonDocument.Parse(await person.GetStringAsync($"/api/teams/{_team}/kanban/board", Ct)).RootElement;
        var card = board.GetProperty("cards").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == $"{row.CorrelationId}_Manager");
        var detail = await person.GetStringAsync(
            $"/api/teams/{_team}/kanban/cards/{Uri.EscapeDataString(card.GetProperty("id").GetString()!)}", Ct);
        var trail = JsonDocument.Parse(detail).RootElement.GetProperty("trail").EnumerateArray().ToList();
        Assert.Contains(trail, t => t.GetProperty("type").GetString() == MessageTypes.RepoDefaultBranchMoved
            && t.GetProperty("text").GetString() == sentence);

        // The feed: the person's activity window serves the row.
        var feed = JsonDocument.Parse(await person.GetStringAsync("/api/messages?after=0&take=500", Ct)).RootElement;
        Assert.Contains(feed.EnumerateArray(), m => m.GetProperty("type").GetString() == MessageTypes.RepoDefaultBranchMoved
            && m.GetProperty("payload").GetString()!.Contains(sentence, StringComparison.Ordinal));

        // What an agent reads back says it too.
        Assert.Contains(sentence, MessageText.Of(row), StringComparison.Ordinal);

        // The dialog: the repo status names it.
        Assert.Equal(sentence, await DialogSaysAsync(person));

        // Nothing was reset: the clone's default branch is still where the Manager left it.
        Assert.Equal(moved, Git(_clone, "rev-parse", "refs/heads/trunk").Output.Trim());
        Assert.NotEqual(moved, Git(_origin, "rev-parse", "refs/heads/trunk").Output.Trim());
    }

    [Fact]
    public async Task A_manager_run_that_keeps_its_work_on_the_team_branch_is_not_reported()
    {
        await StartAsync(defaultBranch: "trunk");
        var person = await PersonAsync();

        // The rule kept: the work goes on team/<id>, and the default branch is left where origin's is.
        _agent.Behaviour = (invocation, _) =>
        {
            if (invocation.Container.Name == "Manager")
            {
                Git(_clone, "checkout", "-b", $"team/{_team}");
                Commit(_clone, "on-team");
                Git(_clone, "checkout", "trunk");
            }

            return Task.FromResult(new AgentResult(0, "ok"));
        };

        // A person's Fetch moved origin on while the clone's branch stayed behind: behind is not moved.
        var other = Path.Combine(_root, "other");
        Git(_root, "clone", _origin, other);
        Commit(other, "elsewhere");
        Git(other, "push", "origin", "trunk");
        Git(_clone, "fetch", "origin");

        await TellAsync(person, "Manager", "merge the work");
        await TerminalAsync("Manager");

        Assert.Empty(await RowsAsync(MessageTypes.RepoDefaultBranchMoved));
        Assert.Null(await DialogSaysAsync(person));
    }

    [Fact]
    public async Task A_default_branch_that_is_not_known_is_not_measured_and_not_reported()
    {
        await StartAsync(defaultBranch: null);
        var person = await PersonAsync();

        _agent.Behaviour = (invocation, _) =>
        {
            if (invocation.Container.Name == "Manager") Commit(_clone, "on-trunk");
            return Task.FromResult(new AgentResult(0, "ok"));
        };

        await TellAsync(person, "Manager", "merge the work");
        await TerminalAsync("Manager");

        Assert.Empty(await RowsAsync(MessageTypes.RepoDefaultBranchMoved));
        Assert.Null(await DialogSaysAsync(person));
    }

    [Fact]
    public async Task A_members_run_is_not_checked()
    {
        await StartAsync(defaultBranch: "trunk");
        var person = await PersonAsync();

        _agent.Behaviour = (invocation, _) =>
        {
            if (invocation.Container.Name == "Dev") Commit(_clone, "on-trunk");
            return Task.FromResult(new AgentResult(0, "ok"));
        };

        await TellAsync(person, "Dev", "do the work");
        await TerminalAsync("Dev");

        Assert.Empty(await RowsAsync(MessageTypes.RepoDefaultBranchMoved));

        // The dialog measures the clone, whoever moved it.
        Assert.NotNull(await DialogSaysAsync(person));
    }

    [Fact]
    public void The_manager_skill_states_the_delivery_rule_and_never_sends_work_to_the_clones_default_branch()
    {
        var manager = BuiltInSkills.Find("manager")!.Body;
        var wrapUp = BuiltInSkills.Find("wrap-up")!.Body;

        Assert.Contains("Merge the member branches you have accepted into `team/<id>`", manager, StringComparison.Ordinal);
        Assert.Contains("Push `team/<id>` when the work is ready.", manager, StringComparison.Ordinal);
        Assert.Contains("Never commit to, merge into or move the clone's default branch", manager, StringComparison.Ordinal);

        // No wording left that sends the work to the clone's `main`.
        foreach (var body in new[] { manager, wrapUp })
        {
            Assert.DoesNotContain("into the clone's `main`", body, StringComparison.Ordinal);
            Assert.DoesNotContain("sha of the clone's `main`", body, StringComparison.Ordinal);
        }
    }

    // --- host and git -----------------------------------------------------------------------------

    private async Task<string?> DialogSaysAsync(HttpClient person)
    {
        var status = JsonDocument.Parse(await person.GetStringAsync($"/api/teams/{_team}/repo-status", Ct)).RootElement;
        var repo = status.GetProperty("repos").EnumerateArray().Single();
        return repo.TryGetProperty("defaultBranchMoved", out var moved) && moved.ValueKind == JsonValueKind.String
            ? moved.GetString()
            : null;
    }

    private async Task StartAsync(string? defaultBranch)
    {
        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataRoot);

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
        var paths = services.GetRequiredService<TeamPaths>();
        var agent = services.GetRequiredService<AgentCatalog>().Definitions
            .First(d => d.Mode == AgentMode.Headless).Name;

        _team = (await registry.CreateAsync("Alpha", agent, memberAgent: agent, ct: Ct)).Id;
        await registry.SetReposAsync(_team, [$"https://github.com/example/{Repo}.git"], Ct);
        await registry.AddContainerAsync(
            _team, "Dev", agent, "", [], permits: new HashSet<string>(Permits.All), ct: Ct);

        // A default branch that is not `main`, so nothing here can pass by assuming it.
        _origin = Path.Combine(_root, "origin.git");
        _clone = Path.Combine(paths.ReposFor(_team), Repo, "main");
        Git(_root, "init", "--bare", "-b", "trunk", _origin);
        Git(_root, "clone", _origin, _clone);
        Commit(_clone, "base");
        Git(_clone, "push", "origin", "trunk");

        if (defaultBranch is not null) await registry.RecordRemoteDefaultBranchAsync(_team, Repo, defaultBranch, Ct);

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

    private async Task<T> EventuallyAsync<T>(Func<Task<T?>> probe) where T : class
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() is { } found) return found;
            await Task.Delay(100, Ct);
        }

        throw new TimeoutException("Timed out waiting for the row.");
    }

    /// <summary>The member's run's terminal row: completed, or failed (a scripted agent that never
    /// calls the platform is not a success), either way after the run-end hook.</summary>
    private Task<Message> TerminalAsync(string member) =>
        EventuallyAsync(async () =>
        {
            var source = new ContainerId(_team, member).ToString();
            return (await RowsAsync(MessageTypes.Completed)).Concat(await RowsAsync(MessageTypes.Failed))
                .FirstOrDefault(r => r.Source == source);
        });

    private async Task<IReadOnlyList<Message>> RowsAsync(string type) =>
        await _factory.Services.GetRequiredService<IMessageLog>().ReadAfterAsync(0, [type], 500, Ct);

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
