using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// The wait route's verdict on a timeout: the team is quiet when every member's latest terminal row
/// is a completion, read one row per member, and working while any member's is not.
/// </summary>
public sealed class WorkflowWaitTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_timed_out_wait_reads_each_members_latest_terminal_and_calls_the_team_quiet_when_every_one_completed()
    {
        var log = host.Services.GetRequiredService<IMessageLog>();
        // The members on the board: Team Connections is hidden while the team has no connection.
        var registry = host.Services.GetRequiredService<TeamRegistry>();
        var members = registry.ContainerIdsOf(host.Alpha).Where(registry.Shows).ToList();
        Assert.NotEmpty(members);

        // Rooted by a row the pump delivers to nobody: an instruction would start a real run.
        var root = await log.AppendAsync(new NewMessage("test.root", "{}", "person-1"), Ct);
        Message? newest = null;
        foreach (var member in members)
        {
            await log.AppendAsync(new NewMessage(MessageTypes.Progress, """{"status":"working"}""", member.ToString(), root.Seq), Ct);
            newest = await log.AppendAsync(new NewMessage(MessageTypes.Completed, """{"output":"done"}""", member.ToString(), root.Seq), Ct);
        }

        using var person = await host.PersonAsync();
        var reply = await person.GetFromJsonAsync<JsonElement>($"/api/workflows/{root.Seq}/wait?timeoutSeconds=1", Ct);

        Assert.Equal("timeout", reply.GetProperty("outcome").GetString());
        Assert.Equal("quiet", reply.GetProperty("timeoutVerdict").GetString());
        Assert.Equal(newest!.OccurredAt, reply.GetProperty("idleSince").GetDateTimeOffset());

        // One member blocked since, in another workflow (a block in this one would end the wait itself):
        // its latest terminal is no completion, so the team is working whatever the others say.
        var other = await log.AppendAsync(new NewMessage("test.root", "{}", "person-1"), Ct);
        await log.AppendAsync(new NewMessage(MessageTypes.Blocked, """{"reason":"stuck"}""", members[0].ToString(), other.Seq), Ct);
        var working = await person.GetFromJsonAsync<JsonElement>($"/api/workflows/{root.Seq}/wait?timeoutSeconds=1", Ct);

        Assert.Equal("working", working.GetProperty("timeoutVerdict").GetString());
    }
}
