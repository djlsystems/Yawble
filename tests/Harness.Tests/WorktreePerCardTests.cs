using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// ONE WORKTREE PER CARD, NOT PER MEMBER.
///
/// A single tree per member, taken in turns across tasks, would leave an interrupted card's
/// uncommitted edits in the way of the next card. Each card has `wt_&lt;Member&gt;_&lt;key&gt;`, keyed by the instruction's `card` or else `w&lt;correlation&gt;`,
/// and the platform hands the path over per invocation as `HARNESS_WORKTREE`.
/// </summary>
public sealed class WorktreePerCardTests
{
    [Fact]
    public void The_key_is_the_card_when_one_is_named_and_the_correlation_otherwise()
    {
        Assert.Equal("1234", Worktrees.KeyFor("1234", 77));
        Assert.Equal("w77", Worktrees.KeyFor(null, 77));
        Assert.Equal("w77", Worktrees.KeyFor("   ", 77));
    }

    [Theory]
    [InlineData("B000F", "B000F")]
    [InlineData(" 12 ", "12")]
    [InlineData("a/b c", "a-b-c")]
    [InlineData("../..", null)]
    [InlineData("x_y", "x-y")]
    [InlineData("__", null)]
    [InlineData("é1", "1")]
    public void A_key_is_sanitised_to_one_segment_of_letters_digits_and_dashes(string raw, string? expected)
    {
        Assert.Equal(expected, Worktrees.Sanitise(raw));
    }

    [Fact]
    public void A_key_that_sanitises_to_nothing_falls_back_to_the_correlation()
    {
        Assert.Equal("w9", Worktrees.KeyFor("../", 9));
    }

    [Fact]
    public void A_key_is_bounded_in_length()
    {
        Assert.Equal(Worktrees.MaxKeyLength, Worktrees.Sanitise(new string('a', 500))!.Length);
    }

    [Fact]
    public void The_tree_for_a_card_is_named_by_member_and_key_under_the_repository()
    {
        var root = Path.Combine(Path.GetTempPath(), $"harness-wt-{Guid.NewGuid():N}");
        var paths = new TeamPaths(root);
        paths.Register("Alpha", null);

        var id = new ContainerId("Alpha", "DeveloperAna");

        Assert.Equal(
            Path.Combine(paths.ReposFor("Alpha"), "Repo", "wt_DeveloperAna_1234"),
            paths.WorktreeFor(id, "Repo", "1234"));
        Assert.Equal(
            Path.Combine(paths.ReposFor("Alpha"), "Repo", "wt_DeveloperAna_w42"),
            paths.WorktreeFor(id, "Repo", Worktrees.KeyFor(null, 42)));
        Assert.Throws<ArgumentException>(() => paths.WorktreeFor(id, "Repo", "../"));
    }

    [Fact]
    public void The_branch_hint_is_member_slash_key_in_lower_case()
    {
        Assert.Equal("developerana/b000f", Worktrees.BranchHint("DeveloperAna", "B000F"));
    }

    [Fact]
    public void A_members_trees_never_include_those_of_a_member_whose_name_it_prefixes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"harness-wt-{Guid.NewGuid():N}");
        var paths = new TeamPaths(root);
        paths.Register("Alpha", null);
        var repo = Path.Combine(paths.ReposFor("Alpha"), "Repo");

        try
        {
            foreach (var name in new[]
                     {
                         "main", "wt_Dev", "wt_Dev_1", "wt_Dev_w5", "wt_Dev2_3", "wt_Dev_2_4", "wt_Devon_7",
                     })
            {
                Directory.CreateDirectory(Path.Combine(repo, name));
            }

            string[] Names(string member) =>
                [.. paths.WorktreesFor(new ContainerId("Alpha", member), "Repo").Select(Path.GetFileName)!];

            Assert.Equal(["wt_Dev_1", "wt_Dev_w5"], Names("Dev"));
            Assert.Equal(["wt_Dev2_3"], Names("Dev2"));
            Assert.Equal(["wt_Dev_2_4"], Names("Dev_2"));
            Assert.Empty(paths.WorktreesFor(new ContainerId("Alpha", "Dev"), "Missing"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Two_instructions_naming_two_cards_get_two_trees_and_one_without_a_card_gets_its_correlation()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = new ContainerId("Alpha", "DeveloperAna");

        IReadOnlyList<RepoWorktree> Trees(ContainerId member, string key) =>
        [
            new("Repo", "/teams/Alpha/repos/Repo/main", $"/teams/Alpha/repos/Repo/wt_{member.Name}_{key}"),
        ];

        await using var bed = new ContainerTestBed(worktrees: Trees);
        await bed.Host.AddAsync(
            ContainerTestBed.Definition(id) with
            {
                Environment = new Dictionary<string, string> { ["HARNESS_TEAM"] = "Alpha" },
            },
            bed.Agent, ct);

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(id), """{"instruction":"first","card":"101"}""", "console"), ct);
        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(id), """{"instruction":"second","card":"102"}""", "console"), ct);
        var third = await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(id), """{"instruction":"third"}""", "console"), ct);

        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(id) == 3));

        var runs = bed.Agent.Invocations.ToList();

        Assert.Equal(
            [
                "/teams/Alpha/repos/Repo/wt_DeveloperAna_101",
                "/teams/Alpha/repos/Repo/wt_DeveloperAna_102",
                $"/teams/Alpha/repos/Repo/wt_DeveloperAna_w{third.CorrelationId}",
            ],
            runs.Select(r => r.Environment[AgentContainer.WorktreeVariable]));
        Assert.Equal(
            ["developerana/101", "developerana/102", $"developerana/w{third.CorrelationId}"],
            runs.Select(r => r.Environment[AgentContainer.BranchHintVariable]));

        // The instruction says it too, as it would the clone.
        Assert.Contains("/teams/Alpha/repos/Repo/wt_DeveloperAna_101", runs[0].Prompt);
        Assert.Contains("/teams/Alpha/repos/Repo/main", runs[0].Prompt);
        Assert.StartsWith("first", runs[0].Prompt);

        // Causation is still per invocation beside it.
        Assert.Equal(third.Seq.ToString(), runs[2].Environment["HARNESS_CAUSATION"]);
    }

    [Fact]
    public async Task A_team_without_repositories_gets_no_worktree_variables()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = new ContainerId("Alpha", "DeveloperAna");

        await using var bed = new ContainerTestBed(worktrees: (_, _) => []);
        await bed.Host.AddAsync(
            ContainerTestBed.Definition(id) with
            {
                Environment = new Dictionary<string, string> { ["HARNESS_TEAM"] = "Alpha" },
            },
            bed.Agent, ct);

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(id), """{"instruction":"first","card":"101"}""", "console"), ct);

        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(id) == 1));

        var run = bed.Agent.Invocations.Single();
        Assert.False(run.Environment.ContainsKey(AgentContainer.WorktreeVariable));
        Assert.False(run.Environment.ContainsKey(AgentContainer.BranchHintVariable));
        Assert.Equal("first", run.Prompt);
    }

    [Fact]
    public void Several_repositories_are_each_named_in_the_instruction()
    {
        var text = AgentContainer.WorktreeText(
            "dev/101",
            [new("One", "/r/One/main", "/r/One/wt_Dev_101"), new("Two", "/r/Two/main", "/r/Two/wt_Dev_101")]);

        Assert.Contains("- One: /r/One/wt_Dev_101", text);
        Assert.Contains("- Two: /r/Two/wt_Dev_101", text);
        Assert.Contains("dev/101", text);
    }

    [Fact]
    public async Task The_real_host_starts_a_member_in_its_workspace_and_names_the_cards_tree()
    {
        var ct = TestContext.Current.CancellationToken;
        var dataRoot = Path.Combine(Path.GetTempPath(), $"harness-wt-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        var fake = new FakeAgent();

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(fake)));

        try
        {
            var services = factory.Services;
            var registry = services.GetRequiredService<TeamRegistry>();
            var paths = services.GetRequiredService<TeamPaths>();
            var agent = services.GetRequiredService<AgentCatalog>().Definitions
                .First(d => d.Mode == AgentMode.Headless).Name;

            var team = (await registry.CreateAsync(
                "Alpha", agent, memberAgent: agent,
                ct: ct)).Id;
            await registry.SetReposAsync(team, ["https://github.com/example/Repo.git"], ct);

            var member = new ContainerId(team, "Worker");

            // A per-member tree present on disk: it must not be where the member starts.
            Directory.CreateDirectory(Path.Combine(paths.ReposFor(team), "Repo", "wt_Worker"));

            await registry.AddContainerAsync(
                team, "Worker", agent, "", [], permits: new HashSet<string>(Permits.All),
                ct: ct);

            var log = services.GetRequiredService<IMessageLog>();
            await log.AppendAsync(new NewMessage(
                MessageTypes.InstructionFor(member), """{"instruction":"go","card":"555"}""", "console"), ct);

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (fake.RunsFor(member) == 0 && DateTime.UtcNow < deadline) await Task.Delay(50, ct);

            var run = Assert.Single(fake.Invocations, i => i.Container == member);

            Assert.Equal(paths.WorkspaceFor(member), run.WorkingDirectory);
            Assert.Equal(
                Path.Combine(paths.ReposFor(team), "Repo", "wt_Worker_555"),
                run.Environment[AgentContainer.WorktreeVariable]);
            Assert.Equal("worker/555", run.Environment[AgentContainer.BranchHintVariable]);
            Assert.Contains(Path.Combine(paths.ReposFor(team), "Repo", "wt_Worker_555"), run.Prompt);
        }
        finally
        {
            await factory.DisposeAsync();
            try { Directory.Delete(dataRoot, recursive: true); }
            catch (IOException) { }
        }
    }
}
