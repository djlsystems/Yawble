using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A MEMBER'S REMOVAL IS NOT A WORKFLOW, on the real Host. Removing a member appends one
/// <c>agentContainer.removed</c> row with no causation, so it sits alone in a correlation of its own
/// that nothing ever declares; read as a workflow it is open forever, and the team's tile reads NO
/// RESULT over work that completed. The team's workflow reads (<c>GET /api/teams/{team}/workflow</c>,
/// <c>/workflows</c> and the rollup) leave it out by <c>NotAWorkflowSql</c> - for a removal made
/// now and for a row already on the log - while the row itself stays as written and still unassigns
/// the member's cards.
/// </summary>
public sealed class MemberRemovalWorkflowTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string Dev = "Dev";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-removal-workflow-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMessageLog Log => _factory.Services.GetRequiredService<IMessageLog>();

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        var registry = _factory.Services.GetRequiredService<TeamRegistry>();
        _team = (await registry.CreateAsync("Removals", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        await registry.AddContainerAsync(_team, Dev, "claude-headless", "", [], permits: new HashSet<string>(Permits.All), ct: Ct);

        await _factory.Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task A_member_removed_after_the_teams_one_workflow_completed_is_no_workflow()
    {
        var workflow = await CompletedWorkflowAsync();

        var removed = await _person.DeleteAsync($"/api/teams/{_team}/containers/{Dev}", Ct);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        // THE ROW IS STILL WRITTEN AS IT WAS: its type, an empty payload, the member as its source, and
        // no causation - the root of a correlation of its own.
        var row = Assert.Single(await Log.ReadAfterAsync(0, [MessageTypes.ContainerRemoved], 10, Ct));
        Assert.Equal("{}", row.Payload);
        Assert.Equal(new ContainerId(_team, Dev).ToString(), row.Source);
        Assert.Null(row.CausationSeq);
        Assert.Equal(row.Seq, row.CorrelationId);

        await AssertOnlyAsync(workflow);
    }

    [Fact]
    public async Task A_removal_row_already_on_the_log_is_no_workflow()
    {
        // WRITTEN AS `MemberDeletion` WRITES IT TODAY, before any read rule existed: a member long gone.
        var old = await Log.AppendAsync(new NewMessage(MessageTypes.ContainerRemoved, "{}", new ContainerId(_team, "Gone").ToString()), Ct);
        Assert.Equal(old.Seq, old.CorrelationId);

        var workflow = await CompletedWorkflowAsync();

        // AND ONE MORE AFTER THE WORK, so it is the team's most recently active correlation too.
        var later = await Log.AppendAsync(new NewMessage(MessageTypes.ContainerRemoved, "{}", new ContainerId(_team, "Left").ToString()), Ct);
        Assert.Equal(later.Seq, later.CorrelationId);

        await AssertOnlyAsync(workflow);
    }

    [Fact]
    public async Task The_removal_row_still_unassigns_the_removed_members_cards()
    {
        await CompletedWorkflowAsync();

        Assert.Contains(await CardsAsync(), card => card.GetProperty("member").GetString() == Dev);

        (await _person.DeleteAsync($"/api/teams/{_team}/containers/{Dev}", Ct)).EnsureSuccessStatusCode();

        var cards = await CardsAsync();
        Assert.NotEmpty(cards);
        Assert.DoesNotContain(cards, card => card.GetProperty("member").GetString() == Dev);
    }

    /// <summary>
    /// The team runs one workflow: a person tells the Manager, the Manager puts Dev on it, both run,
    /// and the Manager declares it. Answers the workflow's correlation.
    /// </summary>
    private async Task<long> CompletedWorkflowAsync()
    {
        var told = await _person.PostAsJsonAsync(
            $"/api/teams/{_team}/containers/{TeamRegistry.DefaultManagerName}/tell",
            new { instruction = "Build the widget", subject = "Build the widget" }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));
        var root = JsonDocument.Parse(await told.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("correlationId").GetInt64();

        var manager = new ContainerId(_team, TeamRegistry.DefaultManagerName);
        await Log.AppendAsync(new NewMessage(
            MessageTypes.InstructionPrefix + new ContainerId(_team, Dev),
            """{"instruction":"Build the widget","subject":"Build the widget"}""",
            manager.ToString(), root), Ct);

        await RanAsync(root, manager.ToString());
        await RanAsync(root, new ContainerId(_team, Dev).ToString());

        await Log.AppendAsync(new NewMessage(MessageTypes.WorkflowCompleted, """{"delivered":"Widget built."}""", manager.ToString(), root), Ct);
        return root;
    }

    private async Task RanAsync(long correlation, string source)
    {
        for (var tries = 0; ; tries++)
        {
            if ((await Log.ReadCorrelationAsync(correlation, Ct)).Any(m => m.Type == MessageTypes.Completed && m.Source == source)) return;
            Assert.True(tries < 200, $"{source} never ran workflow {correlation}.");
            await Task.Delay(50, Ct);
        }
    }

    /// <summary>The team's workflow reads answer <paramref name="workflow"/>, Completed, and nothing open.</summary>
    private async Task AssertOnlyAsync(long workflow)
    {
        var listed = JsonDocument.Parse(await _person.GetStringAsync($"/api/teams/{_team}/workflows", Ct)).RootElement;
        var only = Assert.Single(listed.GetProperty("workflows").EnumerateArray());
        Assert.Equal(workflow, only.GetProperty("correlation").GetInt64());
        Assert.Equal("Completed", only.GetProperty("state").GetString());
        Assert.Equal(0, listed.GetProperty("openCount").GetInt32());
        Assert.Equal(1, listed.GetProperty("totalCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("earliestStartedAt").ValueKind);

        var single = JsonDocument.Parse(await _person.GetStringAsync($"/api/teams/{_team}/workflow", Ct)).RootElement;
        Assert.Equal(workflow, single.GetProperty("correlation").GetInt64());
        Assert.Equal("Completed", single.GetProperty("state").GetString());

        var rolled = JsonDocument.Parse(await _person.GetStringAsync("/api/teams/rollup", Ct)).RootElement
            .GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("team").GetString() == _team);
        Assert.Equal(workflow, rolled.GetProperty("workflow").GetProperty("correlation").GetInt64());
        Assert.Equal("Completed", rolled.GetProperty("workflow").GetProperty("state").GetString());
        Assert.Equal(0, rolled.GetProperty("openWorkflows").GetProperty("openCount").GetInt32());
        Assert.Equal(workflow, Assert.Single(rolled.GetProperty("openWorkflows").GetProperty("workflows").EnumerateArray())
            .GetProperty("correlation").GetInt64());
    }

    private async Task<IReadOnlyList<JsonElement>> CardsAsync() =>
        JsonDocument.Parse(await _person.GetStringAsync($"/api/teams/{_team}/kanban/board", Ct)).RootElement
            .GetProperty("cards").EnumerateArray().Select(card => card.Clone()).ToList();
}
