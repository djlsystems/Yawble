using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Harness.Messaging;
using Harness.Tests.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// A RUN WHOSE AGENT NEVER CALLED THE PLATFORM IS NOT A SUCCESS. <see cref="CredentialUseRunner"/>
/// compares the credential's `last_used_at` before and after the run; a store that cannot answer
/// means not measured, and not measured is not a failure. The decorator the Host registers is what
/// makes the check real: unwrapped, every run reports "not measured" and passes.
/// </summary>
public sealed class AgentRunnerCompositionTests : IDisposable
{
    private static readonly ContainerId Dev = new("Alpha", "Dev");

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"harness-runner-{Guid.NewGuid():N}");

    private readonly SqlitePrincipalStore _principals;

    public AgentRunnerCompositionTests()
    {
        Directory.CreateDirectory(_directory);
        var database = Path.Combine(_directory, "auth.db");
        new SchemaMigrator(database).ApplyAsync(SchemaModules.All).GetAwaiter().GetResult();
        _principals = new SqlitePrincipalStore(database);
    }

    private static AgentInvocation Invocation => new(Dev, "", "go", ".", new Dictionary<string, string>());

    [Fact]
    public async Task A_run_that_used_its_credential_reached_the_platform()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = await _principals.MintAsync(Dev.ToString(), PrincipalKind.Container, "Alpha", Permits.All, ct: ct);
        var agent = new FakeAgent
        {
            Behaviour = async _ =>
            {
                await _principals.ResolveAsync(key, ct);
                return new AgentResult(0, "");
            },
        };

        var result = await new CredentialUseRunner(agent, _principals, new AgentCatalog([])).RunAsync(Invocation, ct);

        Assert.True(result.ReachedThePlatform);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task A_run_that_never_called_the_platform_and_left_no_evidence_is_not_a_success()
    {
        var ct = TestContext.Current.CancellationToken;
        await _principals.MintAsync(Dev.ToString(), PrincipalKind.Container, "Alpha", Permits.All, ct: ct);
        var agent = new FakeAgent { Behaviour = _ => Task.FromResult(new AgentResult(0, "")) };

        var result = await new CredentialUseRunner(agent, _principals, new AgentCatalog([])).RunAsync(Invocation, ct);

        Assert.False(result.ReachedThePlatform);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task A_store_that_cannot_answer_is_not_measured_and_not_a_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = new FakeAgent { Behaviour = _ => Task.FromResult(new AgentResult(0, "")) };

        // MintingPrincipals throws on LastUsedAtAsync.
        var result = await new CredentialUseRunner(agent, new MintingPrincipals(), new AgentCatalog([])).RunAsync(Invocation, ct);

        Assert.Null(result.ReachedThePlatform);
        Assert.True(result.Succeeded);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}

public sealed class HostAgentRunnerTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public void The_hosts_agent_runner_is_the_credential_use_decorator()
    {
        Assert.IsType<CredentialUseRunner>(host.Services.GetRequiredService<IAgentRunner>());
    }
}
