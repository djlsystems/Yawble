using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Solutions;
using Harness.Kanban;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.Tests.Host;

/// <summary>
/// THE BOARD'S NOTICE. When a workflow is declared complete and a solution package written during it
/// sits in the team's documents folder, the platform checks it: a pass appends a `solution.checked`
/// row reading "&lt;name&gt; &lt;version&gt; is ready. **Review and install**" with the install link,
/// a fail names the problems by file and field, and the backlog item the workflow was dispatched
/// from shows the notice. A package not written during the workflow is not checked.
/// </summary>
public sealed class SolutionNoticeTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_valid_package_written_during_the_workflow_is_ready_to_review_and_install_on_the_board_and_the_backlog_item()
    {
        var (team, root, item) = await WorkflowAsync("Notice Pass");
        var folder = SolutionSamples.JobTracker("1.0.0", Documents(team));
        File.SetLastWriteTimeUtc(Path.Combine(folder, "solution.json"), DateTime.UtcNow);

        await DeclareAsync(team, root);

        var notice = Assert.Single(await NoticesAsync(root));
        var payload = JsonDocument.Parse(notice.Payload).RootElement;

        Assert.Equal(new ContainerId(team, TeamRegistry.DefaultManagerName).ToString(), notice.Source);
        Assert.True(payload.GetProperty(PayloadFields.Ok).GetBoolean());
        Assert.Equal(folder, payload.GetProperty(PayloadFields.Path).GetString());
        Assert.Equal("Job Tracker 1.0.0 is ready. **Review and install**", payload.GetProperty(PayloadFields.Text).GetString());
        Assert.Equal(
            "#/solutions/install?folder=" + Uri.EscapeDataString(folder),
            payload.GetProperty(PayloadFields.Link).GetString());
        Assert.Equal(0, payload.GetProperty(PayloadFields.Problems).GetArrayLength());

        // On the team's board: the feed a person reads carries it, on the declarer's card.
        Assert.Equal(team, MessageTeam.Of(notice));
        Assert.Contains(MessageTypes.SolutionChecked, EventCatalog.Types);

        // And in the backlog item, under the dispatch whose workflow wrote it.
        using var person = await host.PersonAsync();
        var detail = await person.GetFromJsonAsync<JsonElement>($"/api/backlog/{item}", Ct);
        var shown = Assert.Single(detail.GetProperty("stats")[0].GetProperty("notices").EnumerateArray());
        Assert.True(shown.GetProperty("ok").GetBoolean());
        Assert.Equal("Job Tracker 1.0.0 is ready. **Review and install**", shown.GetProperty("text").GetString());
        Assert.Equal("#/solutions/install?folder=" + Uri.EscapeDataString(folder), shown.GetProperty("link").GetString());
    }

    [Fact]
    public async Task An_invalid_package_names_its_problems_by_file_and_field_and_offers_no_install()
    {
        var (team, root, item) = await WorkflowAsync("Notice Fail");
        var folder = SolutionSamples.JobTracker("1.0.0", Documents(team));
        SolutionSamples.Edit(folder, m => m["triggers"]![0]!["member"] = "Nobody");

        await DeclareAsync(team, root);

        var payload = JsonDocument.Parse(Assert.Single(await NoticesAsync(root)).Payload).RootElement;

        Assert.False(payload.GetProperty(PayloadFields.Ok).GetBoolean());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty(PayloadFields.Link).ValueKind);
        var problem = Assert.Single(payload.GetProperty(PayloadFields.Problems).EnumerateArray()).GetString()!;
        Assert.StartsWith("solution.json triggers[0].member:", problem);
        Assert.Contains("Nobody", problem);
        var text = payload.GetProperty(PayloadFields.Text).GetString()!;
        Assert.StartsWith("Job Tracker 1.0.0 did not pass the check:\nsolution.json triggers[0].member:", text);
        Assert.DoesNotContain("Review and install", text);
        Assert.EndsWith("Coordinator, Scout, Writer.", text);

        using var person = await host.PersonAsync();
        var detail = await person.GetFromJsonAsync<JsonElement>($"/api/backlog/{item}", Ct);
        var shown = Assert.Single(detail.GetProperty("stats")[0].GetProperty("notices").EnumerateArray());
        Assert.False(shown.GetProperty("ok").GetBoolean());
        Assert.Equal(problem, Assert.Single(shown.GetProperty("problems").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task A_package_whose_solution_json_links_outside_is_named_by_its_folder_and_nothing_of_the_target_is_shown()
    {
        const string outside = "OUTSIDE-3f9c1e-not-the-packages";
        var (team, root, _) = await WorkflowAsync("Notice Link");
        var folder = SolutionSamples.JobTracker("1.0.0", Documents(team));
        var manifest = Path.Combine(folder, "solution.json");
        var target = Path.Combine(Path.GetDirectoryName(Documents(team))!, $"outside-{Guid.NewGuid():N}.json");
        File.WriteAllText(target, $$"""{"id":"{{outside}}","name":"{{outside}}","version":"{{outside}}"}""");
        File.Delete(manifest);
        File.CreateSymbolicLink(manifest, Path.GetRelativePath(folder, target));
        // Written during the workflow: a link is not walked, so another file says so.
        File.SetLastWriteTimeUtc(Path.Combine(folder, "skills", "job-search-playbook.md"), DateTime.UtcNow);

        try
        {
            await DeclareAsync(team, root);

            var payload = Assert.Single(await NoticesAsync(root)).Payload;
            var notice = JsonDocument.Parse(payload).RootElement;
            Assert.False(notice.GetProperty(PayloadFields.Ok).GetBoolean());
            Assert.Equal(
                $"{Path.GetFileName(folder)} did not pass the check:\nsolution.json (link): solution.json is a link leading outside the package ({Path.GetRelativePath(folder, target)}).",
                notice.GetProperty(PayloadFields.Text).GetString());
            Assert.DoesNotContain(outside, payload, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(target);
        }
    }

    [Fact]
    public async Task A_package_folder_linked_outside_is_named_by_its_folder_and_nothing_of_the_target_is_shown()
    {
        const string outside = "OUTSIDE-7a2d4b-not-the-packages";
        var (team, root, _) = await WorkflowAsync("Notice Folder Link");
        // Outside the data root: the service refuses the folder before any check.
        var target = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        File.WriteAllText(
            Path.Combine(target, "solution.json"),
            $$"""{"id":"{{outside}}","name":"{{outside}}","version":"{{outside}}"}""");
        var folder = Path.Combine(Documents(team), "linked-package");
        Directory.CreateSymbolicLink(folder, target);

        try
        {
            await DeclareAsync(team, root);

            var payload = Assert.Single(await NoticesAsync(root)).Payload;
            var notice = JsonDocument.Parse(payload).RootElement;
            Assert.False(notice.GetProperty(PayloadFields.Ok).GetBoolean());
            Assert.Equal("linked-package", notice.GetProperty(PayloadFields.Name).GetString());
            Assert.Equal(JsonValueKind.Null, notice.GetProperty(PayloadFields.Solution).ValueKind);
            Assert.Equal(JsonValueKind.Null, notice.GetProperty(PayloadFields.Version).ValueKind);
            var text = notice.GetProperty(PayloadFields.Text).GetString()!;
            Assert.StartsWith("linked-package did not pass the check:\n", text);
            Assert.Contains("goes through a link that leaves the data root", text);
            Assert.DoesNotContain(outside, payload, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public async Task A_package_not_written_during_the_workflow_is_not_checked()
    {
        var (team, root, _) = await WorkflowAsync("Notice Old");
        var folder = SolutionSamples.JobTracker("1.0.0", Documents(team));

        // Written an hour before the workflow began: an earlier round's package.
        var before = DateTime.UtcNow.AddHours(-1);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, before);
        }

        await DeclareAsync(team, root);

        Assert.Empty(await NoticesAsync(root));
        Assert.Contains(
            await host.Services.GetRequiredService<IMessageLog>().ReadCorrelationAsync(root, Ct),
            m => m.Type == MessageTypes.WorkflowCompleted);
    }

    [Fact]
    public async Task Two_workflows_declared_after_one_package_was_written_show_one_notice_and_a_changed_folder_a_new_one()
    {
        // Two workflows open on one team, then the package is written: both saw it written during
        // them (job-tracker-builder, workflows 2229 and 2302).
        var (team, first, _) = await WorkflowAsync("Notice Once");
        var (second, _) = await AnotherWorkflowAsync(team, "Notice Once again");
        var (third, _) = await AnotherWorkflowAsync(team, "Notice Once changed");
        var folder = SolutionSamples.JobTracker("1.0.0", Documents(team));
        File.SetLastWriteTimeUtc(Path.Combine(folder, "solution.json"), DateTime.UtcNow);

        await DeclareAsync(team, first);
        await DeclareAsync(team, second);

        var notice = Assert.Single(await NoticesAsync(first));
        Assert.Empty(await NoticesAsync(second));
        var hash = JsonDocument.Parse(notice.Payload).RootElement.GetProperty(PayloadFields.ContentHash).GetString();
        Assert.Equal(SolutionNotice.ContentHash(folder), hash);

        // The folder changes, same version: the change is news, so a new notice with a new digest.
        File.AppendAllText(Path.Combine(folder, "README.md"), "\nOne more line.\n");

        await DeclareAsync(team, third);

        var again = JsonDocument.Parse(Assert.Single(await NoticesAsync(third)).Payload).RootElement;
        Assert.True(again.GetProperty(PayloadFields.Ok).GetBoolean());
        Assert.Equal("Job Tracker 1.0.0 is ready. **Review and install**", again.GetProperty(PayloadFields.Text).GetString());
        Assert.NotEqual(hash, again.GetProperty(PayloadFields.ContentHash).GetString());
    }

    private string Documents(string team) => host.Services.GetRequiredService<TeamDocuments>().EnsureFor(team);

    /// <summary>A team of its own, a backlog item dispatched to it, and the workflow's root: an
    /// instruction to its Manager.</summary>
    private async Task<(string Team, long Root, long Item)> WorkflowAsync(string name)
    {
        var registry = host.Services.GetRequiredService<TeamRegistry>();
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var team = (await registry.CreateAsync(name, agent, memberAgent: agent)).Id;
        var (root, item) = await AnotherWorkflowAsync(team, name);
        return (team, root, item);
    }

    /// <summary>A second workflow on the same team, dispatched from its own backlog item.</summary>
    private async Task<(long Root, long Item)> AnotherWorkflowAsync(string team, string name)
    {
        var log = host.Services.GetRequiredService<IMessageLog>();
        var root = await log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(new ContainerId(team, TeamRegistry.DefaultManagerName)),
            """{"instruction":"Build the job tracker package."}""", "console"), Ct);

        var backlog = host.Services.GetRequiredService<IBacklogStore>();
        var item = await backlog.CreateAsync(null, "Job tracker", "Build it as a package.", "person@example.test", Ct);
        await backlog.AddDispatchAsync(item.Id, team, name, root.CorrelationId, "person@example.test", Ct);

        return (root.CorrelationId, item.Id);
    }

    [Theory]
    [InlineData("solution.json version: is required.", "Job Tracker 1.0.0 did not pass the check:\nsolution.json version: is required.")]
    [InlineData("solution.json version: is required", "Job Tracker 1.0.0 did not pass the check:\nsolution.json version: is required.")]
    public void The_failing_sentence_ends_in_one_full_stop(string problem, string expected)
    {
        Assert.Equal(expected, SolutionNotice.FailingText("Job Tracker", "1.0.0", [problem]));
    }

    [Fact]
    public void Each_problem_keeps_its_own_full_stop_on_its_own_line_and_nothing_reads_dot_semicolon()
    {
        var text = SolutionNotice.FailingText("Job Tracker", "1.0.0",
        [
            "solution.json triggers[0].member: 'Nobody' names no member of this package; its members are Coordinator, Scout, Writer.",
            "solution.json members[1].pluginId: 'linkedin-scraper' is not a plugin in this package",
            "skills/job-search-playbook.md roles: must say who it is for.",
        ]);

        Assert.DoesNotContain(".;", text);
        Assert.Equal(
            [
                "Job Tracker 1.0.0 did not pass the check:",
                "solution.json triggers[0].member: 'Nobody' names no member of this package; its members are Coordinator, Scout, Writer.",
                "solution.json members[1].pluginId: 'linkedin-scraper' is not a plugin in this package.",
                "skills/job-search-playbook.md roles: must say who it is for.",
            ],
            text.Split('\n'));
        Assert.All(text.Split('\n').Skip(1), line => Assert.True(line.EndsWith('.') && !line.EndsWith(".."), line));
    }

    /// <summary>The Manager's declaration, through the one sequence the route runs.</summary>
    private async Task DeclareAsync(string team, long correlation)
    {
        var services = host.Services;
        var registry = services.GetRequiredService<TeamRegistry>();

        await WorkflowDeclaration.AppendAsync(
            team, correlation, new ContainerId(team, TeamRegistry.DefaultManagerName), correlation,
            """{"delivered":"The job tracker package."}""",
            services.GetRequiredService<IMessageLog>(), services.GetRequiredService<IBacklogStore>(),
            services.GetRequiredService<ContainerHost>(), services.GetRequiredService<KanbanStore>(),
            services.GetRequiredService<WorktreeRemoval>(), services.GetRequiredService<TeamPaths>(),
            registry.ReposFor(team), NullLogger.Instance, Ct, services.GetRequiredService<SolutionNotice>());
    }

    private async Task<IReadOnlyList<Message>> NoticesAsync(long correlation) =>
        [.. (await host.Services.GetRequiredService<IMessageLog>().ReadCorrelationAsync(correlation, Ct))
            .Where(m => m.Type == MessageTypes.SolutionChecked)];
}
