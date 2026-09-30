using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Identity;
using Microsoft.AspNetCore.Http;
using Harness.Tests.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// The Concierge resumes work inside the workflow it belongs to: its prompt and the skill that
/// teaches `tell` say so with the worked case, and `kanban` and `status` name the open workflow each
/// card belongs to and the latest row that joins it.
/// </summary>
public sealed class ConciergeContinuesWorkflowTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_concierge_prompt_says_to_continue_the_workflow_the_work_belongs_to_with_the_worked_case()
    {
        var prompt = BuiltInPrompts.ConciergePromptText;

        Assert.Contains("Continue the workflow the work belongs to.", prompt, StringComparison.Ordinal);
        Assert.Contains("pass that workflow's\nlatest row as causation on tell", prompt, StringComparison.Ordinal);
        Assert.Contains("Start a new workflow only for new work, or when the person asks for one.", prompt, StringComparison.Ordinal);
        Assert.Contains("ask the person rather than guessing either way", prompt, StringComparison.Ordinal);
        Assert.Contains("A\nworkflow selected in STEERING.md still wins.", prompt, StringComparison.Ordinal);

        // The worked case, every fact of it.
        foreach (var fact in new[] { "job-tracker-builder", "B001P", "2229", "card 2236", "Tester Maren", "2302" })
            Assert.Contains(fact, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_concierge_skill_teaches_tell_to_continue_the_open_workflow_with_the_worked_case()
    {
        var skill = BuiltInSkills.All.Single(s => s.Name == "concierge").Body;

        Assert.Contains("## Continue the workflow the work belongs to", skill, StringComparison.Ordinal);
        Assert.Contains("Causation joins a workflow and omitting it roots a new one.", skill, StringComparison.Ordinal);
        Assert.Contains("A workflow selected in `STEERING.md` still wins", skill, StringComparison.Ordinal);
        Assert.Contains("`openWorkflow`", skill, StringComparison.Ordinal);
        Assert.Contains("`latestSeq`", skill, StringComparison.Ordinal);
        Assert.Contains("causation: <latestSeq>", skill, StringComparison.Ordinal);
        Assert.Contains("ask the person which, rather than guessing either\nway", Unindent(skill), StringComparison.Ordinal);
        Assert.Contains("only a person\ncloses one", Unindent(skill), StringComparison.Ordinal);

        foreach (var fact in new[] { "job-tracker-builder", "B001P", "2229", "2236", "Tester Maren", "2302" })
            Assert.Contains(fact, skill, StringComparison.Ordinal);

        // Recovery is the other place a Concierge resumes work, and it says the same.
        var recovery = BuiltInSkills.All.Single(s => s.Name == "recovery").Body;
        Assert.Contains("`openWorkflow`", recovery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kanban_and_status_name_the_open_workflow_a_card_belongs_to_and_its_latest_row()
    {
        var (team, manager) = await PausedTeamAsync();
        var member = await HireAsync(team, "Tester Maren");

        var workflow = await TellAsync(manager, team, member, "Re-check the job tracker.", causation: null);
        var latest = await TellAsync(manager, team, member, "And the export.", causation: workflow);

        var card = SingleCard(await manager.Kanban("board", team: team, member: member, cancellationToken: Ct));
        var id = card.GetProperty("id").GetString()!;
        AssertOpen(card, workflow, latest);

        AssertOpen(Body(await manager.Kanban("show", team: team, card: id, cancellationToken: Ct)), workflow, latest);

        var status = await manager.Status(team: team, cancellationToken: Ct);
        Assert.Contains($"- card {id} \"", status, StringComparison.Ordinal);
        Assert.Contains($"workflow {workflow}, latest row {latest}", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_card_whose_workflow_ended_names_no_open_workflow()
    {
        var (team, manager) = await PausedTeamAsync();
        var member = await HireAsync(team, "Tester Maren");

        var workflow = await TellAsync(manager, team, member, "Re-check the job tracker.", causation: null);
        await CloseAsync(team, workflow);

        var card = SingleCard(await manager.Kanban("board", team: team, member: member, cancellationToken: Ct));
        Assert.Equal(JsonValueKind.Null, card.GetProperty("openWorkflow").ValueKind);

        var status = await manager.Status(team: team, cancellationToken: Ct);
        Assert.Contains("No card belongs to an open workflow.", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_card_told_again_in_a_later_workflow_names_the_newest_open_one()
    {
        var (team, manager) = await PausedTeamAsync();
        var member = await HireAsync(team, "Tester Maren");

        var first = await TellAsync(manager, team, member, "Re-check the job tracker.", causation: null);
        var id = SingleCard(await manager.Kanban("board", team: team, member: member, cancellationToken: Ct))
            .GetProperty("id").GetString()!;

        // Resumed in a new workflow while the first is still open: the newer one is named.
        var reply = await manager.Tell(member, "Re-send the re-check.", team: team, card: id, cancellationToken: Ct);
        Assert.StartsWith("HTTP 200", reply);
        var later = Body(reply).GetProperty("seq").GetInt64();
        Assert.NotEqual(first, later);

        var card = Body(await manager.Kanban("show", team: team, card: id, cancellationToken: Ct));
        AssertOpen(card, later, later);

        // The newer one ends and the first is still open: the first is named again.
        await CloseAsync(team, later);
        card = Body(await manager.Kanban("show", team: team, card: id, cancellationToken: Ct));
        Assert.Equal(first, card.GetProperty("openWorkflow").GetProperty("workflow").GetInt64());
    }

    private static void AssertOpen(JsonElement card, long workflow, long latest)
    {
        var open = card.GetProperty("openWorkflow");
        Assert.Equal(workflow, open.GetProperty("workflow").GetInt64());
        Assert.Equal(latest, open.GetProperty("latestSeq").GetInt64());
    }

    private static JsonElement SingleCard(string board) =>
        Assert.Single(Body(board).GetProperty("cards").EnumerateArray().ToList());

    private static string Unindent(string text) =>
        string.Join('\n', text.Split('\n').Select(line => line.TrimStart()));

    private async Task CloseAsync(string team, long correlation) =>
        await host.Services.GetRequiredService<IMessageLog>().AppendAsync(
            new NewMessage(
                MessageTypes.WorkflowClosed,
                JsonSerializer.Serialize(new { team, reason = "test" }),
                "person@example.com",
                correlation),
            Ct);

    private static JsonElement Body(string reply) =>
        JsonDocument.Parse(reply[reply.IndexOf('{')..]).RootElement.Clone();

    private static async Task<long> TellAsync(
        PlatformMcpTools manager, string team, string member, string instruction, long? causation)
    {
        var reply = await manager.Tell(
            member, instruction, team: team, causation: causation?.ToString(), cancellationToken: Ct);
        Assert.StartsWith("HTTP 200", reply);
        return Body(reply).GetProperty("seq").GetInt64();
    }

    private async Task<(string Team, PlatformMcpTools Manager)> PausedTeamAsync()
    {
        var registry = host.Services.GetRequiredService<TeamRegistry>();
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions
            .First(d => d.Mode == AgentMode.Headless).Name;

        var team = (await registry.CreateAsync($"Open {Guid.NewGuid():N}"[..13], agent, memberAgent: agent, ct: Ct)).Id;
        await registry.SetPausedAsync(team, true, Ct);

        var managerId = new ContainerId(team, TeamRegistry.DefaultManagerName);
        var key = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            managerId.ToString(), PrincipalKind.Container, team, Permits.All);

        return (team, Tools(managerId, key));
    }

    private async Task<string> HireAsync(string team, string label)
    {
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions
            .First(d => d.Mode == AgentMode.Headless).Name;

        return (await host.Services.GetRequiredService<TeamRegistry>()
            .HireMemberAsync(team, label, agent, "", [], ct: Ct)).Id;
    }

    private PlatformMcpTools Tools(ContainerId caller, string key)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = key;
        context.User = PrincipalClaims.ToClaimsPrincipal(
            new Principal(caller.ToString(), PrincipalKind.Container, new HashSet<string>()), "test");

        return new PlatformMcpTools(
            new FixedAccessor(context),
            host.Services.GetRequiredService<IPrincipalStore>(),
            host.Services.GetRequiredService<AgentCatalog>(),
            new ServerClients(host.ServerHandler()));
    }

    /// <summary>Held in a field: <see cref="HttpContextAccessor"/> is async-local.</summary>
    private sealed class FixedAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => context; set { } }
    }

    private sealed class ServerClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }
}
