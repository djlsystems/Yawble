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
        Assert.StartsWith("Job Tracker 1.0.0 did not pass the check: solution.json triggers[0].member:", text);
        Assert.DoesNotContain("Review and install", text);
        Assert.EndsWith("Coordinator, Scout, Writer.", text);

        using var person = await host.PersonAsync();
        var detail = await person.GetFromJsonAsync<JsonElement>($"/api/backlog/{item}", Ct);
        var shown = Assert.Single(detail.GetProperty("stats")[0].GetProperty("notices").EnumerateArray());
        Assert.False(shown.GetProperty("ok").GetBoolean());
        Assert.Equal(problem, Assert.Single(shown.GetProperty("problems").EnumerateArray()).GetString());
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

    private string Documents(string team) => host.Services.GetRequiredService<TeamDocuments>().EnsureFor(team);

    /// <summary>A team of its own, a backlog item dispatched to it, and the workflow's root: an
    /// instruction to its Manager.</summary>
    private async Task<(string Team, long Root, long Item)> WorkflowAsync(string name)
    {
        var registry = host.Services.GetRequiredService<TeamRegistry>();
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var team = (await registry.CreateAsync(name, agent, memberAgent: agent)).Id;

        var log = host.Services.GetRequiredService<IMessageLog>();
        var root = await log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(new ContainerId(team, TeamRegistry.DefaultManagerName)),
            """{"instruction":"Build the job tracker package."}""", "console"), Ct);

        var backlog = host.Services.GetRequiredService<IBacklogStore>();
        var item = await backlog.CreateAsync(null, "Job tracker", "Build it as a package.", "person@example.test", Ct);
        await backlog.AddDispatchAsync(item.Id, team, name, root.CorrelationId, "person@example.test", Ct);

        return (team, root.CorrelationId, item.Id);
    }

    [Theory]
    [InlineData("solution.json version: is required.", "Job Tracker 1.0.0 did not pass the check: solution.json version: is required.")]
    [InlineData("solution.json version: is required", "Job Tracker 1.0.0 did not pass the check: solution.json version: is required.")]
    public void The_failing_sentence_ends_in_one_full_stop(string problem, string expected)
    {
        Assert.Equal(expected, SolutionNotice.FailingText("Job Tracker", "1.0.0", [problem]));
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
