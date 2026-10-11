using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host.Auth;

namespace Harness.Tests.Host;

/// <summary>
/// Listing, creating and deleting the instance's local repositories are a person's: a container
/// key - the Manager's or a member's - is refused before the handler runs, with the humans-only
/// sentence, and nothing is created.
/// </summary>
public sealed class LocalReposHumansOnlyTests(CrossTeamFixture host) : IClassFixture<CrossTeamFixture>
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_container_key_is_refused_every_local_repository_route()
    {
        foreach (var member in new[] { host.ManagerName, "Worker" })
        {
            using var client = host.WithKey(host.ContainerKeys[CrossTeamFixture.Key(host.Alpha, member)]);

            foreach (var response in new[]
                     {
                         await client.GetAsync("/api/local-repos", Ct),
                         await client.PostAsJsonAsync("/api/local-repos", new { name = "sneaky" }, Ct),
                         await client.DeleteAsync("/api/local-repos/sneaky", Ct),
                     })
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
                Assert.Equal(PermitGate.HumansOnlyMessage, body.RootElement.GetProperty("error").GetString());
            }
        }
    }
}
