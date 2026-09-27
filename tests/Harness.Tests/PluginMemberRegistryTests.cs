using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// A `plugin:&lt;id&gt;` member through the REAL registry: hired with no prompt, no permits, no
/// credential and no environment; restored without a missing-Agent mark; never repointed across
/// kinds. The agent side of every one of these is pinned by <see cref="MemberGoldenTests"/>.
/// </summary>
public sealed class PluginMemberRegistryTests : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-plugin-reg-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private readonly FakeAgent _fake = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Start() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(_fake)));

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        PluginInstall.Write(_dataRoot, "sample-echo");
        PluginInstall.Write(_dataRoot, "other-echo");
        _factory = Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    private TeamRegistry Registry => _factory.Services.GetRequiredService<TeamRegistry>();

    private async Task<string> TeamAsync() =>
        (await Registry.CreateAsync("Mixed", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

    [Fact]
    public async Task A_plugin_member_is_hired_with_no_prompt_permits_credential_or_environment()
    {
        var team = await TeamAsync();

        var snapshot = await Registry.AddContainerAsync(team, "Echo", "plugin:sample-echo", "", [], ct: Ct);
        var agentMember = await Registry.AddContainerAsync(team, "Dev", "claude-headless", "", [], ct: Ct);

        Assert.Equal(MemberRef.PluginKind, snapshot.Kind);
        Assert.Equal("plugin:sample-echo", snapshot.Agent);
        Assert.Null(snapshot.MissingAgent);
        Assert.Equal(MemberRef.AgentKind, agentMember.Kind);

        var container = _factory.Services.GetRequiredService<ContainerHost>().Find(new ContainerId(team, "Echo"))!;
        Assert.Equal("", container.SystemPrompt);
        Assert.Empty(container.Environment);

        var row = (await _factory.Services.GetRequiredService<ITeamStore>().MembersAsync(Ct)).Single(m => m.Name == "Echo");
        Assert.Equal("plugin:sample-echo", row.Agent);
        Assert.Empty(row.Permits);

        // The agent member beside it WAS minted a credential; the plugin member was not.
        var principals = _factory.Services.GetRequiredService<IPrincipalStore>();
        Assert.NotNull(await principals.TeamForAsync(new ContainerId(team, "Dev").ToString(), Ct));
        Assert.Null(await principals.TeamForAsync(new ContainerId(team, "Echo").ToString(), Ct));
    }

    [Fact]
    public async Task A_malformed_plugin_id_is_refused_before_anything_is_made()
    {
        var team = await TeamAsync();

        var refused = await Assert.ThrowsAsync<NoSuchAgentException>(() =>
            Registry.AddContainerAsync(team, "Bad", "plugin:Not Valid", "", [], ct: Ct));

        Assert.Contains("is not a plugin id", refused.Message);
        Assert.Null(_factory.Services.GetRequiredService<ContainerHost>().Find(new ContainerId(team, "Bad")));
    }

    [Fact]
    public async Task A_plugin_that_is_not_installed_is_refused_by_name()
    {
        var team = await TeamAsync();

        var refused = await Assert.ThrowsAsync<NoSuchAgentException>(() =>
            Registry.AddContainerAsync(team, "Ghost", "plugin:no-such-plugin", "", [], ct: Ct));

        Assert.Contains("'plugin:no-such-plugin' is not installed on this Host", refused.Message);
    }

    [Fact]
    public async Task A_member_is_not_repointed_across_kinds()
    {
        var team = await TeamAsync();
        await Registry.AddContainerAsync(team, "Echo", "plugin:sample-echo", "", [], ct: Ct);
        await Registry.AddContainerAsync(team, "Dev", "claude-headless", "", [], ct: Ct);

        await Assert.ThrowsAsync<NoSuchAgentException>(() =>
            Registry.UpdateContainerAsync(team, "Echo", null, null, "claude-headless", Ct));
        await Assert.ThrowsAsync<NoSuchAgentException>(() =>
            Registry.UpdateContainerAsync(team, "Dev", null, null, "plugin:sample-echo", Ct));

        // Plugin to plugin is a repoint like any other, and takes effect on the next wake.
        var repointed = await Registry.UpdateContainerAsync(team, "Echo", null, null, "plugin:other-echo", Ct);
        Assert.Equal("plugin:other-echo", repointed.Agent);
        Assert.Equal(MemberRef.PluginKind, repointed.Kind);
    }

    [Fact]
    public async Task A_plugin_member_is_restored_without_a_missing_agent_mark()
    {
        var team = await TeamAsync();
        await Registry.AddContainerAsync(team, "Echo", "plugin:sample-echo", "", [], ct: Ct);

        await _factory.DisposeAsync();
        _factory = Start();

        var restored = _factory.Services.GetRequiredService<ContainerHost>().Find(new ContainerId(team, "Echo"))!;
        var snapshot = restored.Snapshot();

        Assert.Equal("plugin:sample-echo", snapshot.Agent);
        Assert.Equal(MemberRef.PluginKind, snapshot.Kind);
        Assert.Null(snapshot.MissingAgent);
        Assert.Equal("", restored.SystemPrompt);
        Assert.Empty(restored.Environment);
    }

    [Fact]
    public async Task The_router_sends_plugins_and_agents_to_their_own_runners()
    {
        var agents = new FakeAgent();
        var plugins = new CountingRunner();
        var router = new MemberRunnerRouter(new AgentMemberRunner(agents), plugins);
        var work = new List<Message> { new(1, "t", "{}", "console", 1, null, 0, DateTimeOffset.UnixEpoch) };
        var context = new MemberRunContext(0, ArtifactLimits.Default, [], null, null, "You are Dev.");

        await router.RunAsync(new MemberInvocation(new("a", "p"), "plugin:x", work, ".", new Dictionary<string, string>(), context), Ct);
        await router.RunAsync(new MemberInvocation(new("a", "d"), "claude-headless", work, ".", new Dictionary<string, string>(), context), Ct);

        Assert.Equal(1, plugins.Runs);
        Assert.Single(agents.Invocations);

        var unrouted = await new MemberRunnerRouter(new AgentMemberRunner(agents)).RunAsync(
            new MemberInvocation(new("a", "p"), "plugin:x", work, ".", new Dictionary<string, string>(), context), Ct);
        Assert.False(unrouted.Succeeded);
        Assert.Contains("runs no plugin members", unrouted.FailureReason);
    }

    private sealed class CountingRunner : IMemberRunner
    {
        public int Runs;

        public Task<MemberResult> RunAsync(MemberInvocation invocation, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Runs);
            return Task.FromResult(new MemberResult(true, 0, "ok"));
        }
    }
}
