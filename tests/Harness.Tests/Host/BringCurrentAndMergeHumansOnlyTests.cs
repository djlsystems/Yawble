using System.Net;
using System.Text.Json;
using Harness.Host.Auth;

namespace Harness.Tests.Host;

/// <summary>
/// Bring current and merge, and Push, are a person's buttons: a container key - the Manager's or a
/// member's, on its own team - is refused before the handler runs, with the humans-only sentence.
/// </summary>
public sealed class BringCurrentAndMergeHumansOnlyTests(CrossTeamFixture host) : IClassFixture<CrossTeamFixture>
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("bring-current-and-merge")]
    [InlineData("push")]
    public async Task A_container_key_is_refused(string action)
    {
        foreach (var member in new[] { host.ManagerName, "Worker" })
        {
            using var client = host.WithKey(host.ContainerKeys[CrossTeamFixture.Key(host.Alpha, member)]);

            var response = await client.PostAsync($"/api/teams/{host.Alpha}/repos/Widget/{action}", null, Ct);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal(PermitGate.HumansOnlyMessage, body.RootElement.GetProperty("error").GetString());
        }
    }
}
