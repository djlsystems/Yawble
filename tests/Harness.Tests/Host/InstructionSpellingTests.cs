using System.Net.Http.Json;
using Harness.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// AN INSTRUCTION TYPE IS BUILT FROM THE FOUND CONTAINER'S ID, NOT THE CALLER'S SPELLING. Container
/// lookup is case-insensitive but message types match under binary collation, so `tell MANAGER`
/// written as `...instruction.Alpha/MANAGER` returns 200 and wakes nothing. The team is paused so
/// the tell queues and launches no agent.
/// </summary>
public sealed class InstructionSpellingTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task A_tell_spelled_in_another_case_is_addressed_to_the_registered_container()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();
        (await client.PostAsync($"/api/teams/{host.Alpha}/pause", null, ct)).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            $"/api/teams/{host.Alpha}/containers/MANAGER/tell", new { instruction = "Spelled loudly." }, ct);
        response.EnsureSuccessStatusCode();

        var registered = MessageTypes.InstructionFor(new ContainerId(host.Alpha, "Manager"));
        var rows = await host.Services.GetRequiredService<IMessageLog>().ReadAfterAsync(0, [registered], 100, ct);

        Assert.Contains(rows, r => r.Payload.Contains("Spelled loudly.", StringComparison.Ordinal));
    }
}
