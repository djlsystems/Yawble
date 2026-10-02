using Harness.Contracts;
using Harness.Host;
using Harness.Tests.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// THE HOST COMPOSES ONE WORKER, and everything that reaches runs reaches them through it. A part
/// built with a constructor that makes a worker of its own would pass every other test and still
/// talk to a different worker from the rest; this resolves the real Host's parts and compares.
/// </summary>
public sealed class RunWorkerCompositionTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public void The_host_shares_one_worker_across_runs_leases_reports_and_capacity()
    {
        var worker = host.Services.GetRequiredService<IRunWorker>();

        var clients = new Dictionary<string, IRunWorkerClient>
        {
            ["runner"] = Runner(),
        };

        Assert.All(clients, client => Assert.Same(worker, client.Value.Worker));
    }

    [Fact]
    public void The_registered_runner_is_the_credential_runner_over_the_protocol_client()
    {
        var registered = Assert.IsType<CredentialUseRunner>(host.Services.GetRequiredService<IAgentRunner>());

        Assert.Same(host.Services.GetRequiredService<ProcessAgentRunner>(), registered.Inner);
    }

    private ProcessAgentRunner Runner() =>
        Assert.IsType<ProcessAgentRunner>(Assert.IsType<CredentialUseRunner>(host.Services.GetRequiredService<IAgentRunner>()).Inner);
}
