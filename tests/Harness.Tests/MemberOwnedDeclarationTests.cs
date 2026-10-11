using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// A WORKFLOW A MEMBER OWNS CAN BE DECLARED. A person tells a member other than the Manager
/// directly, so that member owns the workflow. Its own `completed` row wakes the Manager, and:
///
/// - the owner's declaration is not refused for a Manager that is only WATCHING - woken by a row
///   the owner or another member wrote - though a Manager TOLD to work in it still counts;
/// - the Manager may declare it on the owner's behalf once the owner and every other member are
///   idle in it, and the row says `declaredBy` and `onBehalfOf`;
/// - the idle offer goes to the owner only, and the run that answers it by declaring does not
///   wake the Manager a second time;
/// - a plugin-owned workflow is still declared by the platform.
///
/// On the real Host, with a fake agent for every agent member.
/// </summary>
public sealed class MemberOwnedDeclarationTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string OfferText = "This workflow is open and nobody is working it";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-member-owned-{Guid.NewGuid():N}");
    private readonly FakeAgent _agents = new();
    private readonly ConcurrentQueue<(string Who, HttpStatusCode Status, string Body)> _declarations = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private IMessageLog Log => Services.GetRequiredService<IMessageLog>();

    private ContainerHost Host => Services.GetRequiredService<ContainerHost>();

    private ContainerId Dev => new(_team, "Dev");

    private ContainerId Dev2 => new(_team, "Dev2");

    private ContainerId Echo => new(_team, "Echo");

    private ContainerId Manager => new(_team, TeamRegistry.DefaultManagerName);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        PluginMemberEndToEndTests.InstallSampleEcho(_dataRoot);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(_agents)));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Owned", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();

        foreach (var name in new[] { "Dev", "Dev2" })
        {
            var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name, agent = "claude-headless" }, Ct);
            Assert.Equal(HttpStatusCode.OK, hired.StatusCode);
        }

        var echo = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Echo",
            agent = "plugin:sample-echo",
            config = new { mode = "upper" },
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, echo.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    // --- the case a team met: the owner declares on its offer while the Manager reads its completion

    [Fact]
    public async Task The_owner_declares_although_its_completion_woke_the_manager_and_the_manager_is_woken_once()
    {
        var managerWatching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerDeclared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _agents.Behaviour = async invocation =>
        {
            if (invocation.Container == Manager)
            {
                // Reading Dev's completion: stays Running until Dev's declaration has been answered.
                managerWatching.TrySetResult();
                await ownerDeclared.Task.WaitAsync(Patience, Ct);
                return new AgentResult(0, "read it");
            }

            if (invocation.Container == Dev && invocation.Prompt.Contains(OfferText, StringComparison.Ordinal))
            {
                await managerWatching.Task.WaitAsync(Patience, Ct);
                await UntilAsync(() => Host.Find(Manager)!.State == ContainerState.Running, "the Manager running");
                await DeclareAsync(invocation, "called progress once");
                ownerDeclared.TrySetResult();
                return new AgentResult(0, "declared");
            }

            return new AgentResult(0, "did it");
        };

        var root = await PersonTellsAsync(Dev, "call progress once, then finish");
        var declaration = await DeclarationAsync(root);
        await SettleAsync();

        Assert.Equal(Dev.ToString(), declaration.Source);
        var (who, status, body) = Assert.Single(_declarations);
        Assert.Equal((Dev.Name, HttpStatusCode.NoContent), (who, status));
        Assert.Equal("", body);

        // THE RUNS: Dev's run, one Manager wake, and the run that declared. No more.
        Assert.Equal(2, _agents.RunsFor(Dev));
        Assert.Equal(1, _agents.RunsFor(Manager));

        var thread = await Log.ReadCorrelationAsync(root, Ct);
        var completions = thread.Where(m => m.Type == MessageTypes.Completed && m.Source == Dev.ToString()).ToList();
        Assert.Equal(2, completions.Count);

        // The first woke the Manager; the one answering the offer by declaring did not.
        Assert.False(DeclaredKey(completions[0]));
        Assert.True(DeclaredKey(completions[1]));
        Assert.Equal(1, await ManagerRunsOnAsync(completions[0]));
        Assert.Equal(0, await ManagerRunsOnAsync(completions[1]));

        // The offer went to the owner only.
        Assert.Single(thread, IdleWorkflowOffer.IsOffer);
        Assert.DoesNotContain(thread, m => m.Type == MessageTypes.InstructionFor(Manager));
        Assert.Single(thread, m => m.Type == MessageTypes.WorkflowCompleted);
    }

    // --- the Manager declares for its member ---------------------------------------------------------

    [Fact]
    public async Task The_manager_declares_an_idle_members_workflow_on_its_behalf()
    {
        _agents.Behaviour = async invocation =>
        {
            if (invocation.Container == Manager)
            {
                await DeclareAsync(invocation, "Dev did it");
                return new AgentResult(0, "declared");
            }

            // Dev never declares, offer or not.
            return new AgentResult(0, "did it");
        };

        var root = await PersonTellsAsync(Dev, "do it");
        var declaration = await DeclarationAsync(root);
        await SettleAsync();

        Assert.Equal(Manager.ToString(), declaration.Source);
        var payload = JsonDocument.Parse(declaration.Payload).RootElement;
        Assert.Equal(Manager.ToString(), payload.GetProperty(WorkflowDeclaration.DeclaredByField).GetString());
        Assert.Equal(Dev.ToString(), payload.GetProperty(WorkflowDeclaration.OnBehalfOfField).GetString());
        Assert.Equal("Dev did it", payload.GetProperty("delivered").GetString());

        // Never refused as not the owner; a refusal can only have been for work still in flight.
        Assert.DoesNotContain(_declarations, d => d.Status == HttpStatusCode.Forbidden);
        Assert.Contains(_declarations, d => d.Status == HttpStatusCode.NoContent);
        Assert.Single(await Log.ReadCorrelationAsync(root, Ct), m => m.Type == MessageTypes.WorkflowCompleted);
    }

    [Fact]
    public async Task The_managers_declaration_is_refused_while_another_member_still_runs_in_it()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refused = new TaskCompletionSource<(HttpStatusCode, string)>(TaskCreationOptions.RunContinuationsAsynchronously);

        _agents.Behaviour = async invocation =>
        {
            if (invocation.Container == Dev2)
            {
                await release.Task.WaitAsync(Patience, Ct);
                return new AgentResult(0, "helped");
            }

            if (invocation.Container == Manager)
            {
                if (!refused.Task.IsCompleted)
                {
                    await UntilAsync(() => Host.Find(Dev2)!.State == ContainerState.Running, "Dev2 running");
                    refused.TrySetResult(await DeclareAsync(invocation, "too soon"));
                    release.TrySetResult();
                }

                return new AgentResult(0, "read it");
            }

            if (!invocation.Prompt.Contains(OfferText, StringComparison.Ordinal))
            {
                await TellsAsync(invocation, Dev2, "help");
            }

            return new AgentResult(0, "did it");
        };

        await PersonTellsAsync(Dev, "do it with Dev2");

        var (status, body) = await refused.Task.WaitAsync(Patience, Ct);
        await SettleAsync();

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("Dev2 is Running", body, StringComparison.Ordinal);
    }

    // --- guards ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_manager_told_to_work_in_the_workflow_still_refuses_the_owner()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answered = new TaskCompletionSource<(HttpStatusCode, string)>(TaskCreationOptions.RunContinuationsAsynchronously);

        _agents.Behaviour = async invocation =>
        {
            if (invocation.Container == Manager)
            {
                if (invocation.Prompt.Contains("review this", StringComparison.Ordinal))
                {
                    await release.Task.WaitAsync(Patience, Ct);
                }

                return new AgentResult(0, "reviewed");
            }

            if (invocation.Container == Dev && !answered.Task.IsCompleted)
            {
                await TellsAsync(invocation, Manager, "review this");

                await UntilAsync(() => Host.Find(Manager)!.State == ContainerState.Running, "the Manager running");
                answered.TrySetResult(await DeclareAsync(invocation, "too soon"));
                release.TrySetResult();
            }

            return new AgentResult(0, "did it");
        };

        await PersonTellsAsync(Dev, "do it and have it reviewed");

        var (status, body) = await answered.Task.WaitAsync(Patience, Ct);
        await SettleAsync();

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains($"{Manager.Name} is Running", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_workflow_told_to_a_plugin_is_still_declared_by_the_platform()
    {
        _agents.Behaviour = _ => Task.FromResult(new AgentResult(0, "noted"));

        var root = await PersonTellsAsync(Echo, "go");
        var declaration = await DeclarationAsync(root);
        await SettleAsync();

        Assert.Equal(Echo.ToString(), declaration.Source);
        var payload = JsonDocument.Parse(declaration.Payload).RootElement;
        Assert.True(payload.GetProperty(UndeclarableWorkflows.DeclaredByPlatformField).GetBoolean());
        Assert.False(payload.TryGetProperty(WorkflowDeclaration.DeclaredByField, out _));
        Assert.Equal(0, _agents.RunsFor(Manager));
    }

    [Fact]
    public async Task Another_member_may_not_declare_a_workflow_it_does_not_own()
    {
        var answered = new TaskCompletionSource<(HttpStatusCode, string)>(TaskCreationOptions.RunContinuationsAsynchronously);

        _agents.Behaviour = async invocation =>
        {
            if (invocation.Container == Dev && !answered.Task.IsCompleted)
            {
                await TellsAsync(invocation, Dev2, "declare it");
            }
            else if (invocation.Container == Dev2)
            {
                answered.TrySetResult(await DeclareAsync(invocation, "not mine"));
            }

            return new AgentResult(0, "did it");
        };

        await PersonTellsAsync(Dev, "do it");

        var (status, _) = await answered.Task.WaitAsync(Patience, Ct);
        await SettleAsync();

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    // --- helpers --------------------------------------------------------------------------------------

    /// <summary>A person tells <paramref name="who"/> directly; returns the workflow it roots.</summary>
    private async Task<long> PersonTellsAsync(ContainerId who, string instruction)
    {
        var after = await Log.HighestSeqAsync(Ct);
        var told = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers/{who.Name}/tell", new { instruction }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));

        var root = (await Log.ReadAfterAsync(after, [MessageTypes.InstructionFor(who)], int.MaxValue, Ct)).First();
        return root.CorrelationId;
    }

    /// <summary>The member running <paramref name="invocation"/> tells <paramref name="who"/> inside
    /// the workflow it is running - the row the `tell` route appends, written directly because a
    /// worker holds no Tell permit.</summary>
    private async Task TellsAsync(AgentInvocation invocation, ContainerId who, string instruction) =>
        await Log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(who),
            JsonSerializer.Serialize(new { instruction }),
            invocation.Container.ToString(),
            long.Parse(invocation.Environment["HARNESS_CAUSATION"])), Ct);

    private async Task<(HttpStatusCode, string)> DeclareAsync(AgentInvocation invocation, string delivered)
    {
        var response = await ContainerClient(invocation).PostAsJsonAsync(
            $"/api/teams/{_team}/containers/{invocation.Container.Name}/workflow-complete", new { delivered }, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        _declarations.Enqueue((invocation.Container.Name, response.StatusCode, body));
        return (response.StatusCode, body);
    }

    private HttpClient ContainerClient(AgentInvocation invocation)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, invocation.Environment["HARNESS_KEY"]);
        return client;
    }

    private async Task<Message> DeclarationAsync(long correlation)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            if ((await Log.ReadCorrelationAsync(correlation, Ct)).FirstOrDefault(m => m.Type == MessageTypes.WorkflowCompleted) is { } row)
            {
                return row;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"Workflow {correlation} was never declared. Declarations: "
            + string.Join(" | ", _declarations.Select(d => $"{d.Who} {d.Status} {d.Body}")));
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Never saw {what}.");
            await Task.Delay(20);
        }
    }

    /// <summary>Waits until every member is idle and nothing is pending, several passes in a row:
    /// after this, any wake the rows so far were going to cause has happened.</summary>
    private async Task SettleAsync()
    {
        var pending = Services.GetRequiredService<IPendingDeliveries>();
        var deadline = DateTime.UtcNow.AddSeconds(15);

        for (var quietPasses = 0; quietPasses < 10 && DateTime.UtcNow < deadline;)
        {
            var read = (await pending.ForTeamAsync(_team, Ct)).Count == 0;
            var idle = new[] { Manager, Dev, Dev2, Echo }.All(id => Host.Find(id)!.State == ContainerState.Idle);
            quietPasses = read && idle ? quietPasses + 1 : 0;
            await Task.Delay(100, Ct);
        }
    }

    private async Task<int> ManagerRunsOnAsync(Message row) =>
        (await Log.ReadAfterAsync(row.Seq, [MessageTypes.Started], int.MaxValue, Ct))
            .Count(m => m.Source == Manager.ToString() && m.CausationSeq == row.Seq);

    private static bool DeclaredKey(Message row) =>
        JsonDocument.Parse(row.Payload).RootElement.TryGetProperty(PayloadFields.WorkflowDeclared, out var value)
        && value.ValueKind == JsonValueKind.True;
}
