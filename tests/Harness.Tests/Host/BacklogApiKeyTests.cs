using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A person's own API key reaches the tenant backlog AS THAT PERSON: it lists, reads, creates,
/// edits, reorders, archives and restores, items with no team included, and every tenant row it
/// writes names the person and the key it came through. Starting and deleting an item stay a
/// signed-in person's and the tenant Concierge's, and a member container still reaches none of it,
/// in the words it has always been refused with.
/// </summary>
public sealed class BacklogApiKeyTests(HostFixture host) : IClassFixture<HostFixture>
{
    private const string MemberSentence =
        "The tenant backlog is a person's to change, and a member reaches none of it. "
        + "A person has to do this, and no credential changes that - report what you need done and "
        + "who has to do it rather than looking for one.";

    [Fact]
    public async Task A_persons_key_lists_reads_creates_and_edits_unlinked_items_as_that_person()
    {
        var ct = TestContext.Current.CancellationToken;
        var unlinked = await NewItemAsync("Unlinked by the person", ct);
        var (keyId, secret) = await MintKeyAsync(ct);
        using var key = host.Container(secret);

        var list = JsonDocument.Parse(await key.GetStringAsync("/api/backlog", ct)).RootElement;
        Assert.Contains(list.EnumerateArray(), i => i.GetProperty("id").GetInt64() == unlinked);

        var read = await key.GetAsync($"/api/backlog/{unlinked}", ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var created = await key.PostAsJsonAsync("/api/backlog", new { title = "Made through a key", body = "A spec." }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct)).RootElement;
        var id = item.GetProperty("id").GetInt64();
        Assert.Equal(JsonValueKind.Null, item.GetProperty("team").ValueKind);
        Assert.Equal("person@example.test", item.GetProperty("createdBy").GetString());

        var edited = await key.PatchAsJsonAsync($"/api/backlog/{id}", new { title = "Renamed through a key", state = "ready" }, ct);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal("ready", await StateAsync(id, ct));

        await AssertRowNamesPersonAndKeyAsync(TenantActions.BacklogItemCreated, id, keyId, secret, ct);
        await AssertRowNamesPersonAndKeyAsync(TenantActions.BacklogItemEdited, id, keyId, secret, ct);
    }

    [Fact]
    public async Task A_persons_key_reorders_archives_restores_and_marks_implemented_with_the_key_on_every_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await NewItemAsync("First", ct);
        var id = await NewItemAsync("Second", ct);
        var (keyId, secret) = await MintKeyAsync(ct);
        using var key = host.Container(secret);

        Assert.Equal(HttpStatusCode.NoContent,
            (await key.PostAsJsonAsync($"/api/backlog/{id}/position", new { before = first }, ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await key.PatchAsJsonAsync($"/api/backlog/{id}", new { state = "implemented" }, ct)).StatusCode);
        await AssertRowNamesPersonAndKeyAsync(TenantActions.BacklogItemImplemented, id, keyId, secret, ct);

        Assert.Equal(HttpStatusCode.NoContent, (await key.PostAsync($"/api/backlog/{id}/archive", null, ct)).StatusCode);
        await AssertRowNamesPersonAndKeyAsync(TenantActions.BacklogItemArchived, id, keyId, secret, ct);

        var archive = JsonDocument.Parse(await key.GetStringAsync("/api/backlog?archived=true", ct)).RootElement;
        Assert.Contains(archive.EnumerateArray(), i => i.GetProperty("id").GetInt64() == id);

        Assert.Equal(HttpStatusCode.NoContent, (await key.PostAsync($"/api/backlog/{id}/restore", null, ct)).StatusCode);
        await AssertRowNamesPersonAndKeyAsync(TenantActions.BacklogItemRestored, id, keyId, secret, ct);
    }

    [Fact]
    public async Task A_signed_in_persons_rows_carry_no_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await NewItemAsync("By cookie", ct);

        var row = await host.Services.GetRequiredService<ITenantLog>()
            .FindLatestAsync(TenantActions.BacklogItemCreated, PlatformBacklogId.Format(id), ct);
        Assert.NotNull(row);
        Assert.Equal("person@example.test", row.ActorEmail);
        Assert.False(JsonDocument.Parse(row.Detail!).RootElement.TryGetProperty("viaApiKey", out _));
    }

    [Fact]
    public async Task A_member_container_is_refused_list_create_and_edit_in_todays_words_and_the_item_is_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await NewItemAsync("Not the member's", ct);
        using var member = host.Container(host.AlphaContainerKey);

        await AssertMemberRefusalAsync(await member.GetAsync("/api/backlog", ct), ct);
        await AssertMemberRefusalAsync(await member.GetAsync($"/api/backlog/{id}", ct), ct);
        await AssertMemberRefusalAsync(
            await member.PostAsJsonAsync("/api/backlog", new { title = "A member's", team = host.Alpha }, ct), ct);
        await AssertMemberRefusalAsync(
            await member.PatchAsJsonAsync($"/api/backlog/{id}", new { title = "Changed", state = "ready" }, ct), ct);

        using var person = await host.PersonAsync();
        var item = JsonDocument.Parse(await person.GetStringAsync($"/api/backlog/{id}", ct)).RootElement.GetProperty("item");
        Assert.Equal("Not the member's", item.GetProperty("title").GetString());
        Assert.Equal("pending", item.GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_persons_key_is_refused_dispatch_dispatch_to_new_and_delete_and_nothing_happens()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await NewItemAsync("Ready but not the key's to start", ct);
        using (var person = await host.PersonAsync())
        {
            (await person.PatchAsJsonAsync($"/api/backlog/{id}", new { state = "ready" }, ct)).EnsureSuccessStatusCode();
        }

        var (_, secret) = await MintKeyAsync(ct);
        using var key = host.Container(secret);
        var teamsBefore = host.Services.GetRequiredService<TeamRegistry>().All().Count;

        var dispatch = await key.PostAsync($"/api/teams/{host.Alpha}/backlog/{id}/dispatch", null, ct);
        var dispatchError = await RefusalAsync(dispatch, ct);
        Assert.Contains("signed-in person", dispatchError, StringComparison.Ordinal);
        Assert.Contains("Concierge", dispatchError, StringComparison.Ordinal);

        var toNew = await key.PostAsJsonAsync(
            $"/api/backlog/{id}/dispatch-to-new", new { name = "Made by a key", agent = "echo" }, ct);
        var toNewError = await RefusalAsync(toNew, ct);
        Assert.Contains("signed-in person", toNewError, StringComparison.Ordinal);

        Assert.Empty(await host.Services.GetRequiredService<IBacklogStore>().DispatchesAsync(id, ct));
        Assert.Equal(teamsBefore, host.Services.GetRequiredService<TeamRegistry>().All().Count);

        using (var person = await host.PersonAsync())
        {
            (await person.PostAsync($"/api/backlog/{id}/archive", null, ct)).EnsureSuccessStatusCode();
        }

        var delete = await key.DeleteAsync($"/api/backlog/{id}", ct);
        var deleteError = await RefusalAsync(delete, ct);
        Assert.Contains("signed-in person", deleteError, StringComparison.Ordinal);
        Assert.NotNull(await host.Services.GetRequiredService<IBacklogStore>().GetAsync(id, ct));
    }

    [Fact]
    public async Task Record_start_stays_refused_to_a_persons_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await NewItemAsync("No start for a key", ct);
        var (_, secret) = await MintKeyAsync(ct);
        using var key = host.Container(secret);

        var response = await key.PostAsync($"/api/backlog/{id}/record-start", null, ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_concierge_still_marks_an_item_ready()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await NewItemAsync("For the Concierge", ct);
        var user = await host.Services.GetRequiredService<IUserStore>().FindAsync("person@example.test", ct);
        var conciergeKey = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            ConciergeLaunchFactory.PrincipalId(user!.Id), PrincipalKind.TenantConcierge, null,
            ConciergeLaunchFactory.ConciergePermits, ownerUserId: user.Id, ct: ct);

        using var concierge = host.Container(conciergeKey);
        var response = await concierge.PatchAsJsonAsync($"/api/backlog/{id}", new { state = "ready" }, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ready", await StateAsync(id, ct));
    }

    [Fact]
    public async Task An_api_key_with_no_owner_does_not_administer_the_backlog()
    {
        var ct = TestContext.Current.CancellationToken;
        var unlinked = await NewItemAsync("Not an orphan key's", ct);
        var orphan = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            $"apikey-{Guid.NewGuid():N}", PrincipalKind.ApiKey, null, Permits.All, ct: ct);
        using var key = host.Container(orphan);

        var list = await key.GetAsync("/api/backlog", ct);
        if (list.StatusCode == HttpStatusCode.OK)
        {
            var items = JsonDocument.Parse(await list.Content.ReadAsStringAsync(ct)).RootElement;
            Assert.DoesNotContain(items.EnumerateArray(), i => i.GetProperty("id").GetInt64() == unlinked);
        }
        else
        {
            Assert.True(list.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        }

        var create = await key.PostAsJsonAsync("/api/backlog", new { title = "An orphan's" }, ct);
        Assert.NotEqual(HttpStatusCode.Created, create.StatusCode);
    }

    private async Task AssertRowNamesPersonAndKeyAsync(
        string action, long id, string keyId, string secret, CancellationToken ct)
    {
        var row = await host.Services.GetRequiredService<ITenantLog>()
            .FindLatestAsync(action, PlatformBacklogId.Format(id), ct);
        Assert.NotNull(row);
        Assert.Equal("person@example.test", row.ActorEmail);
        Assert.Equal(keyId, row.ActorId);
        Assert.Equal(keyId, JsonDocument.Parse(row.Detail!).RootElement.GetProperty("viaApiKey").GetString());
        Assert.DoesNotContain(secret, row.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, row.ActorId ?? "", StringComparison.Ordinal);
    }

    private static async Task AssertMemberRefusalAsync(HttpResponseMessage response, CancellationToken ct)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        Assert.Equal(MemberSentence, body.GetProperty("error").GetString());
    }

    private static async Task<string> RefusalAsync(HttpResponseMessage response, CancellationToken ct)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement
            .GetProperty("error").GetString()!;
        Assert.NotEqual(MemberSentence, error);
        Assert.DoesNotContain("reaches none of it", error, StringComparison.Ordinal);
        return error;
    }

    private async Task<(string Id, string Secret)> MintKeyAsync(CancellationToken ct)
    {
        using var person = await host.PersonAsync();
        var minted = await person.PostAsJsonAsync("/api/keys", new { label = $"tool {Guid.NewGuid():N}" }, ct);
        minted.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await minted.Content.ReadAsStringAsync(ct)).RootElement;
        return (body.GetProperty("id").GetString()!, body.GetProperty("credential").GetString()!);
    }

    private async Task<long> NewItemAsync(string title, CancellationToken ct)
    {
        using var person = await host.PersonAsync();
        var created = await person.PostAsJsonAsync("/api/backlog", new { title, body = "A spec." }, ct);
        created.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("id").GetInt64();
    }

    private async Task<string> StateAsync(long id, CancellationToken ct)
    {
        using var person = await host.PersonAsync();
        var item = await person.GetStringAsync($"/api/backlog/{id}", ct);
        return JsonDocument.Parse(item).RootElement.GetProperty("item").GetProperty("state").GetString()!;
    }
}
