using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// Who may mark a backlog item `ready`, the one state that dispatches. The person, and the
/// Concierge acting for that person, so a person can group a wave and have the Concierge mark its
/// items ready. A TEAM's credential does not reach
/// the tenant backlog at all, so a Manager cannot clear its own drafts for dispatch.
/// </summary>
public sealed class BacklogReadyTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task The_concierge_marks_an_item_ready_for_its_person()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await NewItemAsync(ct);

        using var concierge = host.Container(await ConciergeKeyAsync(ct));
        var response = await concierge.PatchAsJsonAsync($"/api/backlog/{id}", new { state = "ready" }, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ready", await StateAsync(id, ct));
    }

    [Fact]
    public async Task A_team_container_cannot_mark_an_item_ready()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await NewItemAsync(ct);

        using var container = host.Container(host.AlphaContainerKey);
        var response = await container.PatchAsJsonAsync($"/api/backlog/{id}", new { state = "ready" }, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("pending", await StateAsync(id, ct));
    }

    private async Task<long> NewItemAsync(CancellationToken ct)
    {
        using var person = await host.PersonAsync();
        var created = await person.PostAsJsonAsync("/api/backlog", new { title = "An item", body = "A spec." }, ct);
        created.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("id").GetInt64();
    }

    private async Task<string> StateAsync(long id, CancellationToken ct)
    {
        using var person = await host.PersonAsync();
        var item = await person.GetStringAsync($"/api/backlog/{id}", ct);
        return JsonDocument.Parse(item).RootElement.GetProperty("item").GetProperty("state").GetString()!;
    }

    private async Task<string> ConciergeKeyAsync(CancellationToken ct)
    {
        var user = await host.Services.GetRequiredService<IUserStore>().FindAsync("person@example.test", ct);
        return await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            ConciergeLaunchFactory.PrincipalId(user!.Id), PrincipalKind.TenantConcierge, null,
            ConciergeLaunchFactory.ConciergePermits, ownerUserId: user.Id, ct: ct);
    }
}
