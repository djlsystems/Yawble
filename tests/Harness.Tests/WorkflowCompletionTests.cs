using System.Net;
using System.Net.Http.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Tests.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// `workflow.completed` IS DECLARED BY THE OWNER OF THAT CORRELATION, AND A CORRELATION OF 0 IS
/// REFUSED. The owner is the container the workflow's root instruction addressed.
/// </summary>
public sealed class WorkflowOwnerTests
{
    [Fact]
    public async Task The_owner_is_the_container_the_root_instruction_addressed()
    {
        await using var bed = new ContainerTestBed();
        var ct = TestContext.Current.CancellationToken;
        var dev = new ContainerId("Alpha", "Dev");

        var root = await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(dev), """{"instruction":"build it"}""", "console"), ct);

        Assert.Equal(dev, await WorkflowOwner.OfAsync(bed.Store, root.CorrelationId, ct));
        Assert.Null(await WorkflowOwner.OfAsync(bed.Store, 0, ct));
    }
}

public sealed class WorkflowCompleteRouteTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task A_member_running_no_workflow_is_refused_a_declaration()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new ContainerId(host.Alpha, TeamRegistry.DefaultManagerName);
        var key = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            manager.ToString(), PrincipalKind.Container, host.Alpha, Permits.All, ct: ct);
        using var client = host.Container(key);

        var response = await client.PostAsJsonAsync(
            $"/api/teams/{host.Alpha}/containers/{manager.Name}/workflow-complete",
            new { delivered = "nothing" }, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await host.Services.GetRequiredService<IMessageLog>().ReadAfterAsync(
            0, [MessageTypes.WorkflowCompleted], 10, ct));
    }
}
