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
/// A new team whose repositories all clone is not told so by an instruction: that notice woke the
/// Manager into a paid run with nothing to do. The clone paths, and the rule that a member cuts its
/// own worktree, reach the Manager in its prompt on its first real run instead. A clone that failed
/// still roots its notice workflow.
/// </summary>
public sealed class NewTeamReposNoticeTests : IAsyncDisposable
{
    private const string Url = "https://github.com/example/Widget.git";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-repos-notice-").FullName;
    private readonly FakeAgent _agent = new();
    private readonly FixedClone _cloner = new();
    private readonly WebApplicationFactory<Program> _factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public NewTeamReposNoticeTests()
    {
        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(_agent);
                services.AddSingleton<IRepoClone>(_cloner);
            }));
    }

    [Fact]
    public async Task A_team_whose_repositories_all_clone_sends_no_instruction_and_runs_no_manager()
    {
        var (team, manager, instructions) = await CreateAsync("Alpha");

        Assert.Empty(await ManagerInstructionsAsync(instructions));

        // Something the Manager does subscribe to, so a run it WOULD have made has had its chance:
        // the pump delivers in order, and the first run it makes answers this, not a notice.
        var messages = _factory.Services.GetRequiredService<IMessageLog>();
        await messages.AppendAsync(
            new NewMessage(instructions, JsonSerializer.Serialize(new { instruction = "plan the release" }), "host"), Ct);
        var run = await FirstRunAsync(manager);

        Assert.Contains("plan the release", run.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("repositories are ready", run.Prompt, StringComparison.Ordinal);
        Assert.Equal(1, _agent.RunsFor(manager));
    }

    [Fact]
    public async Task The_managers_first_real_run_carries_the_clone_paths_and_the_worktree_rule()
    {
        var (team, manager, instructions) = await CreateAsync("Beta");
        var clonePath = Path.Combine(_factory.Services.GetRequiredService<TeamPaths>().ReposFor(team), "Widget", "main");

        var messages = _factory.Services.GetRequiredService<IMessageLog>();
        await messages.AppendAsync(
            new NewMessage(instructions, JsonSerializer.Serialize(new { instruction = "plan the release" }), "host"), Ct);
        var run = await FirstRunAsync(manager);

        Assert.Contains(RepoSetupMessage.PromptHeading, run.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains($"{Url} -> {clonePath}", run.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("A member cuts its own worktree", run.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clone_that_failed_at_creation_still_roots_its_notice_workflow()
    {
        _cloner.Fail = true;
        var (team, _, instructions) = await CreateAsync("Gamma");
        var clonePath = Path.Combine(_factory.Services.GetRequiredService<TeamPaths>().ReposFor(team), "Widget", "main");

        // The notice's run may be followed by the platform's idle-workflow offer in the same
        // workflow; the notice is the one row that roots it.
        var notice = Assert.Single(await ManagerInstructionsAsync(instructions),
            m => m.Payload.Contains($"\"{PayloadFields.RepoNotReady}\"", StringComparison.Ordinal));
        Assert.Equal(notice.Seq, notice.CorrelationId);
        var payload = JsonDocument.Parse(notice.Payload).RootElement;
        Assert.Contains("COULD NOT CLONE", payload.GetProperty(PayloadFields.Instruction).GetString(), StringComparison.Ordinal);
        Assert.Equal(clonePath, Assert.Single(payload.GetProperty(PayloadFields.RepoNotReady).EnumerateArray()).GetString());
    }

    [Fact]
    public void The_prompt_section_takes_the_credential_out_of_a_url()
    {
        var section = RepoSetupMessage.PromptSection([("https://x-access-token:secret123@github.com/example/Widget.git", "/r/Widget/main")])!;

        Assert.DoesNotContain("secret123", section, StringComparison.Ordinal);
        Assert.Contains("/r/Widget/main", section, StringComparison.Ordinal);
        Assert.Null(RepoSetupMessage.PromptSection([]));
    }

    private async Task<(string Team, ContainerId Manager, string Instructions)> CreateAsync(string name)
    {
        var services = _factory.Services;
        var registry = services.GetRequiredService<TeamRegistry>();
        var agent = services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var team = (await registry.CreateAsync(name, agent, memberAgent: agent, repos: [Url], ct: Ct)).Id;
        var manager = new ContainerId(team, TeamRegistry.DefaultManagerName);
        return (team, manager, MessageTypes.InstructionFor(manager));
    }

    private async Task<IReadOnlyList<Message>> ManagerInstructionsAsync(string instructions) =>
        await _factory.Services.GetRequiredService<IMessageLog>().ReadAfterAsync(0, [instructions], 100, Ct);

    private async Task<AgentInvocation> FirstRunAsync(ContainerId manager)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (_agent.Invocations.FirstOrDefault(i => i.Container == manager) is { } run) return run;
            await Task.Delay(50, Ct);
        }

        throw new TimeoutException("The Manager never ran.");
    }

    /// <summary>A cloner that reports every repository cloned, or every one refused.</summary>
    private sealed class FixedClone : IRepoClone
    {
        public volatile bool Fail;

        public Task<IReadOnlyList<RepoCloneOutcome>> EnsureAllAsync(IReadOnlyList<(string Url, string Path)> repos, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RepoCloneOutcome>>(
                [.. repos.Select(r => Fail
                    ? new RepoCloneOutcome(r.Url, r.Path, RepoCloneResult.Failed, "fatal: Authentication failed")
                    : new RepoCloneOutcome(r.Url, r.Path, RepoCloneResult.Cloned))]);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
