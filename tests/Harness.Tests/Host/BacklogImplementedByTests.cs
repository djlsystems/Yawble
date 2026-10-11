using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// Marking an item implemented records who confirmed it, and the item's detail says so. When a
/// person merged the work outside the platform and landed cannot be proven, their word is what
/// marks it, so the item's history has to name them - and say when a Concierge wrote it for them.
/// </summary>
public sealed class BacklogImplementedByTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task A_person_marking_an_item_implemented_is_named_in_its_history()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await NewItemAsync(ct);

        Assert.Equal(JsonValueKind.Null, (await DetailAsync(id, ct)).GetProperty("implementedBy").ValueKind);

        using (var person = await host.PersonAsync())
        {
            (await person.PatchAsJsonAsync($"/api/backlog/{id}", new { state = "implemented" }, ct)).EnsureSuccessStatusCode();
        }

        var by = (await DetailAsync(id, ct)).GetProperty("implementedBy");
        Assert.Equal("person@example.test", by.GetProperty("by").GetString());
        Assert.False(by.GetProperty("viaConcierge").GetBoolean());
        Assert.True(by.TryGetProperty("at", out _));
    }

    [Fact]
    public async Task The_concierge_marking_an_item_implemented_on_the_persons_word_names_the_person_and_says_so()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await NewItemAsync(ct);

        using (var concierge = host.Container(await ConciergeKeyAsync(ct)))
        {
            var response = await concierge.PatchAsJsonAsync($"/api/backlog/{id}", new { state = "implemented" }, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var by = (await DetailAsync(id, ct)).GetProperty("implementedBy");
        Assert.Equal("person@example.test", by.GetProperty("by").GetString());
        Assert.True(by.GetProperty("viaConcierge").GetBoolean());

        var row = await host.Services.GetRequiredService<ITenantLog>()
            .FindLatestAsync(TenantActions.BacklogItemImplemented, PlatformBacklogId.Format(id), ct);
        Assert.NotNull(row);
        Assert.StartsWith("concierge-", row.ActorId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_item_reopened_after_being_implemented_no_longer_reads_implemented_by_anyone()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await NewItemAsync(ct);

        using var person = await host.PersonAsync();
        (await person.PatchAsJsonAsync($"/api/backlog/{id}", new { state = "implemented" }, ct)).EnsureSuccessStatusCode();
        (await person.PatchAsJsonAsync($"/api/backlog/{id}", new { state = "pending" }, ct)).EnsureSuccessStatusCode();

        Assert.Equal(JsonValueKind.Null, (await DetailAsync(id, ct)).GetProperty("implementedBy").ValueKind);
    }

    private async Task<long> NewItemAsync(CancellationToken ct)
    {
        using var person = await host.PersonAsync();
        var created = await person.PostAsJsonAsync("/api/backlog", new { title = "An item", body = "A spec." }, ct);
        created.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("id").GetInt64();
    }

    private async Task<JsonElement> DetailAsync(long id, CancellationToken ct)
    {
        using var person = await host.PersonAsync();
        return JsonDocument.Parse(await person.GetStringAsync($"/api/backlog/{id}", ct)).RootElement.Clone();
    }

    private async Task<string> ConciergeKeyAsync(CancellationToken ct)
    {
        var user = await host.Services.GetRequiredService<IUserStore>().FindAsync("person@example.test", ct);
        return await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            ConciergeLaunchFactory.PrincipalId(user!.Id), PrincipalKind.TenantConcierge, null,
            ConciergeLaunchFactory.ConciergePermits, ownerUserId: user.Id, ct: ct);
    }
}
