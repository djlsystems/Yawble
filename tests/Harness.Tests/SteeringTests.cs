using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Pty;
using Harness.Tests.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harness.Tests;

/// <summary>
/// CAUSATION JOINS A WORKFLOW, AND THE CONCIERGE CAN PASS IT. `PUT /api/me/steering` writes the
/// highlighted workflow; the next Concierge launch copies it to `HARNESS_CAUSATION` and writes it
/// into `STEERING.md`, which is what a Concierge already running can read.
/// </summary>
public sealed class SteeringRouteTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task The_steering_route_records_the_persons_highlighted_workflow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();
        var person = await host.Services.GetRequiredService<IUserStore>().FindAsync("person@example.test", ct);
        var dataRoot = host.Services.GetRequiredService<TeamPaths>().DataRoot;

        var workflow = await SteeringBed.OpenWorkflowAsync(host, "Steered", ct);

        (await client.PutAsJsonAsync("/api/me/steering", new { correlationId = workflow.Seq }, ct)).EnsureSuccessStatusCode();
        Assert.Equal(workflow.Seq.ToString(CultureInfo.InvariantCulture), SteeringFile.Read(dataRoot, person!.Id));

        (await client.PutAsJsonAsync("/api/me/steering", new { correlationId = (long?)null }, ct)).EnsureSuccessStatusCode();
        Assert.Null(SteeringFile.Read(dataRoot, person.Id));
    }
}

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SteeringLaunchTests : IDisposable
{
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"harness-steering-{Guid.NewGuid():N}");

    public SteeringLaunchTests() => Directory.CreateDirectory(_dataRoot);

    [Fact]
    public async Task The_next_concierge_launch_carries_the_steered_workflow()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new AgentCatalog(AgentCatalogFile.BuiltIns());
        var agent = catalog.Definitions
            .First(d => d.Mode == AgentMode.Interactive && !d.Hidden && d.Launch.LanguageModel).Name;

        // The launch writes MCP files under HOME; keep them out of the real one.
        using var restore = new EnvironmentScope([new("HOME", _dataRoot)]);

        var factory = new ConciergeLaunchFactory(
            new TeamPaths(_dataRoot), "http://localhost:5000", new MintingPrincipals(), catalog);

        var spec = await factory.ForAsync(
            team: "", teamLabel: "this instance", user: "user-1", login: "person@example.com",
            agent, teamEnv: new Dictionary<string, string>(),
            steeringCausation: "42", ct: ct);

        Assert.Equal("42", spec.Env!["HARNESS_CAUSATION"]);
        Assert.Contains("42", await File.ReadAllTextAsync(Path.Combine(spec.StartingFolder, "STEERING.md"), ct));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }
}

/// <summary>
/// WITH NO WORKFLOW SELECTED, `STEERING.md` STILL SENDS RESUMED WORK TO ITS WORKFLOW. The note a
/// running Concierge reads says what the Concierge prompt and the `tell` skill say: new work starts
/// a workflow; a request naming a workflow, a card or blocked work in an open workflow joins it.
/// </summary>
public sealed class SteeringNoteTests
{
    [Fact]
    public void The_note_with_nothing_selected_joins_named_work_to_its_open_workflow()
    {
        var note = SteeringFile.Note(null);

        Assert.Contains("No workflow is selected.", note);
        Assert.Contains("about new work", note);
        Assert.Contains("a card or a member's blocked work that belongs to an open workflow", note);
        Assert.Contains("latest row as causation", note);
    }
}

/// <summary>
/// THE STEERING IS ONLY AN OPEN WORKFLOW OF A LIVE, ACTIVE TEAM. A stored workflow that is completed or
/// closed, or whose team was deleted or archived, is treated as none at every launch and every read:
/// no `HARNESS_CAUSATION`, "No workflow is selected" in `STEERING.md` (the existing workspace's too),
/// the stored file removed and one log line naming the workflow and why. The route refuses such a
/// workflow with 409 and stores nothing; an open one's note names its team and its state.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SteeringOverTests(HostFixture host) : IClassFixture<HostFixture>, IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"harness-steering-home-{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("completed", "is completed")]
    [InlineData("closed", "is closed")]
    [InlineData("deleted", "team no longer exists")]
    [InlineData("archived", "is archived")]
    public async Task A_stored_workflow_that_is_over_launches_a_concierge_steered_at_nothing(string how, string why)
    {
        using var client = await host.PersonAsync();
        var user = await UserAsync();
        var workflow = await SteeringBed.OpenWorkflowAsync(host, $"Over {how}", Ct);

        (await client.PutAsJsonAsync("/api/me/steering", new { correlationId = workflow.Seq }, Ct)).EnsureSuccessStatusCode();
        await EndAsync(client, workflow, how);

        var lines = new LineCapture();
        var spec = await LaunchAsync(user, lines);

        Assert.False(spec.Env!.ContainsKey("HARNESS_CAUSATION"));
        Assert.Contains("No workflow is selected.", await File.ReadAllTextAsync(Path.Combine(spec.StartingFolder, "STEERING.md"), Ct));
        Assert.False(File.Exists(SteeringFile.PathFor(host.DataRoot, user)));
        var line = Assert.Single(lines.Lines);
        Assert.Contains($"dropped workflow {workflow.Seq}", line);
        Assert.Contains(why, line);
    }

    [Fact]
    public async Task The_route_refuses_a_completed_workflow_with_a_sentence_and_stores_nothing()
    {
        using var client = await host.PersonAsync();
        var user = await UserAsync();
        var open = await SteeringBed.OpenWorkflowAsync(host, "Kept open", Ct);
        var done = await SteeringBed.OpenWorkflowAsync(host, "Done here", Ct);
        await EndAsync(client, done, "completed");

        (await client.PutAsJsonAsync("/api/me/steering", new { correlationId = open.Seq }, Ct)).EnsureSuccessStatusCode();
        var refused = await client.PutAsJsonAsync("/api/me/steering", new { correlationId = done.Seq }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var error = (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString();
        Assert.Equal(
            $"Workflow {done.Seq} is completed, so the Concierge cannot follow it. Follow an open workflow of an active team, or none.",
            error);
        Assert.Equal(open.Seq.ToString(CultureInfo.InvariantCulture), SteeringFile.Read(host.DataRoot, user));
    }

    [Fact]
    public async Task The_note_for_an_open_workflow_names_its_team_and_its_state_and_causation_stays_the_number()
    {
        using var client = await host.PersonAsync();
        var user = await UserAsync();
        var workflow = await SteeringBed.OpenWorkflowAsync(host, "Open Label", Ct);
        var later = await host.Services.GetRequiredService<IMessageLog>().AppendAsync(
            new NewMessage(MessageTypes.Progress, """{"status":"working"}""", $"{workflow.Team}/Nobody", workflow.Seq), Ct);

        (await client.PutAsJsonAsync("/api/me/steering", new { correlationId = workflow.Seq }, Ct)).EnsureSuccessStatusCode();
        var spec = await LaunchAsync(user, new LineCapture());

        var state = (await host.Services.GetRequiredService<IMessageLog>()
            .WorkflowForTeamAsync(workflow.Team, workflow.Seq, 0, Ct)).State!.ToLowerInvariant();
        Assert.Equal(workflow.Seq.ToString(CultureInfo.InvariantCulture), spec.Env!["HARNESS_CAUSATION"]);
        Assert.Contains(
            $"The person is looking at workflow {workflow.Seq} (team {workflow.Label}, {state}), latest row {later.Seq}.",
            await File.ReadAllTextAsync(Path.Combine(spec.StartingFolder, "STEERING.md"), Ct));
    }

    [Fact]
    public async Task A_dropped_stored_workflow_rewrites_the_existing_workspaces_steering_note()
    {
        using var client = await host.PersonAsync();
        var user = await UserAsync();
        var workflow = await SteeringBed.OpenWorkflowAsync(host, "Running Now", Ct);

        (await client.PutAsJsonAsync("/api/me/steering", new { correlationId = workflow.Seq }, Ct)).EnsureSuccessStatusCode();
        var spec = await LaunchAsync(user, new LineCapture());
        var note = Path.Combine(spec.StartingFolder, "STEERING.md");
        Assert.Contains($"looking at workflow {workflow.Seq}", await File.ReadAllTextAsync(note, Ct));

        // The Concierge is running; the workflow ends; the next read of the steering drops it.
        await EndAsync(client, workflow, "completed");
        var lines = new LineCapture();
        Assert.Null(await Steering(lines).ReadAsync(user, Ct));

        Assert.Contains("No workflow is selected.", await File.ReadAllTextAsync(note, Ct));
        Assert.False(File.Exists(SteeringFile.PathFor(host.DataRoot, user)));
        Assert.Single(lines.Lines);
    }

    [Fact]
    public async Task A_refusal_of_the_stored_workflow_drops_it_and_rewrites_the_running_workspaces_note()
    {
        using var client = await host.PersonAsync();
        var user = await UserAsync();
        var workflow = await SteeringBed.OpenWorkflowAsync(host, "Refused Now", Ct);

        (await client.PutAsJsonAsync("/api/me/steering", new { correlationId = workflow.Seq }, Ct)).EnsureSuccessStatusCode();
        var spec = await HostLaunchAsync(user);
        var note = Path.Combine(spec.StartingFolder, "STEERING.md");
        Assert.Contains($"looking at workflow {workflow.Seq}", await File.ReadAllTextAsync(note, Ct));

        await EndAsync(client, workflow, "completed");
        var refused = await client.PutAsJsonAsync("/api/me/steering", new { correlationId = workflow.Seq }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.False(File.Exists(SteeringFile.PathFor(host.DataRoot, user)));
        Assert.Contains("No workflow is selected.", await File.ReadAllTextAsync(note, Ct));
        var line = Assert.Single(host.SteeringLog, l => l.Contains($"dropped workflow {workflow.Seq}:", StringComparison.Ordinal));
        Assert.Contains("is completed", line);
    }

    [Fact]
    public async Task The_hosts_own_concierge_launch_drops_a_stored_workflow_that_is_over()
    {
        using var client = await host.PersonAsync();
        var user = await UserAsync();
        var workflow = await SteeringBed.OpenWorkflowAsync(host, "Host Launch", Ct);

        (await client.PutAsJsonAsync("/api/me/steering", new { correlationId = workflow.Seq }, Ct)).EnsureSuccessStatusCode();
        await EndAsync(client, workflow, "completed");

        var spec = await HostLaunchAsync(user);

        Assert.False(spec.Env!.ContainsKey("HARNESS_CAUSATION"));
        Assert.Contains("No workflow is selected.", await File.ReadAllTextAsync(Path.Combine(spec.StartingFolder, "STEERING.md"), Ct));
        Assert.False(File.Exists(SteeringFile.PathFor(host.DataRoot, user)));
        Assert.Single(host.SteeringLog, l => l.Contains($"dropped workflow {workflow.Seq}:", StringComparison.Ordinal));
    }

    private async Task EndAsync(HttpClient client, (string Team, string Label, long Seq) workflow, string how)
    {
        var log = host.Services.GetRequiredService<IMessageLog>();
        switch (how)
        {
            case "completed":
                await log.AppendAsync(new NewMessage(
                    MessageTypes.WorkflowCompleted, """{"delivered":"done"}""", $"{workflow.Team}/Nobody", workflow.Seq), Ct);
                break;
            case "closed":
                var closed = await client.PostAsJsonAsync(
                    $"/api/teams/{workflow.Team}/workflows/{workflow.Seq}/close", new { reason = "not needed" }, Ct);
                Assert.True(closed.IsSuccessStatusCode, await closed.Content.ReadAsStringAsync(Ct));
                break;
            case "deleted":
                var deleted = await client.DeleteAsync($"/api/teams/{workflow.Team}", Ct);
                Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync(Ct));
                break;
            case "archived":
                var archived = await client.PostAsync($"/api/teams/{workflow.Team}/archive", null, Ct);
                Assert.True(archived.IsSuccessStatusCode, await archived.Content.ReadAsStringAsync(Ct));
                break;
        }
    }

    private async Task<PtySpec> LaunchAsync(string user, LineCapture lines)
    {
        var catalog = new AgentCatalog(AgentCatalogFile.BuiltIns());
        var agent = catalog.Definitions
            .First(d => d.Mode == AgentMode.Interactive && !d.Hidden && d.Launch.LanguageModel).Name;

        // The launch writes MCP files under HOME; keep them out of the real one.
        Directory.CreateDirectory(_home);
        using var restore = new EnvironmentScope([new("HOME", _home)]);

        var factory = new ConciergeLaunchFactory(
            host.Services.GetRequiredService<TeamPaths>(), "http://localhost:5000", new MintingPrincipals(), catalog);

        return await factory.ForAsync(
            team: "", teamLabel: "this instance", user: user, login: "person@example.test",
            agent, teamEnv: new Dictionary<string, string>(),
            ct: Ct, steered: await Steering(lines).ReadAsync(user, Ct));
    }

    /// <summary>The launch the Host hands its Concierge session store, as the Host built it.</summary>
    private async Task<PtySpec> HostLaunchAsync(string user)
    {
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions
            .First(d => d.Mode == AgentMode.Interactive && !d.Hidden && d.Launch.LanguageModel).Name;
        await host.Services.GetRequiredService<TeamRegistry>().SetInteractiveAsync(agent, Ct);

        // The launch writes MCP files under HOME; keep them out of the real one.
        Directory.CreateDirectory(_home);
        using var restore = new EnvironmentScope([new("HOME", _home)]);

        var spec = await host.Services.GetRequiredService<ConciergeLaunches>()
            .LaunchAsync(new ConciergeSessionKey(user), "", null, Ct);
        await host.Services.GetRequiredService<ConciergeLaunchFactory>().RevokeAsync(user, Ct);
        return spec;
    }

    private SteeringWorkflows Steering(LineCapture lines) => new(
        host.Services.GetRequiredService<TeamPaths>(),
        host.Services.GetRequiredService<IMessageLog>(),
        host.Services.GetRequiredService<TeamRegistry>(),
        host.Services.GetRequiredService<ITeamStore>(),
        lines);

    private async Task<string> UserAsync() =>
        (await host.Services.GetRequiredService<IUserStore>().FindAsync("person@example.test", Ct))!.Id;

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { }
    }

    private sealed class LineCapture : ILogger<SteeringWorkflows>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public IEnumerable<string> Lines => _lines;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _lines.Enqueue(formatter(state, exception));
    }
}

/// <summary>A workflow for the steering to name: a paused team and a root instruction to a member it
/// does not have, so nothing runs and the team stays quiet enough to archive or delete.</summary>
internal static class SteeringBed
{
    public static async Task<(string Team, string Label, long Seq)> OpenWorkflowAsync(
        HostFixture host, string label, CancellationToken ct)
    {
        var registry = host.Services.GetRequiredService<TeamRegistry>();
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions
            .First(d => d.Mode == AgentMode.Headless).Name;

        var named = $"{label} {Guid.NewGuid():N}"[..(label.Length + 7)];
        var team = (await registry.CreateAsync(named, agent, memberAgent: agent, ct: ct)).Id;
        await registry.SetPausedAsync(team, true, ct);

        var root = await host.Services.GetRequiredService<IMessageLog>().AppendAsync(
            new NewMessage(
                MessageTypes.InstructionPrefix + team + "/Nobody",
                JsonSerializer.Serialize(new { instruction = "Look into it." }),
                "person@example.test"),
            ct);

        return (team, registry.LabelFor(team), root.Seq);
    }
}
