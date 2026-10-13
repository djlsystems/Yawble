using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// The Users screen counts each account's API keys and revokes them all at once, leaving the
/// account: for a key that may have been seen while its owner is away. A key's owner still revokes
/// one key at a time from their own profile, and nobody else revokes one key of theirs.
/// </summary>
public sealed class UserKeysRevokeTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task A_person_sees_another_accounts_key_count_and_revokes_them_all_leaving_the_account()
    {
        var ct = TestContext.Current.CancellationToken;
        var users = host.Services.GetRequiredService<IUserStore>();
        var principals = host.Services.GetRequiredService<IPrincipalStore>();

        var other = await users.CreateAsync($"away-{Guid.NewGuid():N}@example.test", HostFixture.Password, ct);
        var firstId = $"apikey-{Guid.NewGuid():N}";
        var secondId = $"apikey-{Guid.NewGuid():N}";
        var first = await principals.MintAsync(
            firstId, PrincipalKind.ApiKey, team: null, Permits.All,
            ownerUserId: other.Id, label: "laptop", ct: ct);
        await principals.MintAsync(
            secondId, PrincipalKind.ApiKey, team: null, Permits.All,
            ownerUserId: other.Id, label: "build box", ct: ct);

        using var person = await host.PersonAsync();

        Assert.Equal(2, await KeyCountAsync(person, other.Id, ct));

        // The key works before the revoke.
        using (var withKey = host.Container(first))
        {
            Assert.Equal(HttpStatusCode.OK, (await withKey.GetAsync("/api/overview", ct)).StatusCode);
        }

        var response = await person.PostAsync($"/api/users/{other.Id}/revoke-keys", null, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)))
        {
            Assert.Equal(2, body.RootElement.GetProperty("revoked").GetInt32());
        }

        Assert.Equal(0, await KeyCountAsync(person, other.Id, ct));
        Assert.Empty(await principals.ListApiKeysForAsync(other.Id, ct));
        Assert.NotNull(await users.FindByIdAsync(other.Id, ct));

        using (var withKey = host.Container(first))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await withKey.GetAsync("/api/overview", ct)).StatusCode);
        }

        // One key.revoked row per key, naming whose it was.
        var log = host.Services.GetRequiredService<ITenantLog>();
        foreach (var id in new[] { firstId, secondId })
        {
            var row = await log.FindLatestAsync(TenantActions.KeyRevoked, id, ct);
            Assert.NotNull(row);
            Assert.Contains(other.Email, row.Detail ?? "", StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Revoking_the_keys_of_an_unknown_account_is_404()
    {
        var ct = TestContext.Current.CancellationToken;
        using var person = await host.PersonAsync();

        var response = await person.PostAsync("/api/users/nobody-here/revoke-keys", null, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task There_is_no_route_listing_every_key_on_the_instance()
    {
        var ct = TestContext.Current.CancellationToken;
        using var person = await host.PersonAsync();

        var response = await person.GetAsync("/api/admin/keys", ct);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<int> KeyCountAsync(HttpClient person, string userId, CancellationToken ct)
    {
        var list = await person.GetFromJsonAsync<JsonElement>("/api/users", ct);
        return list.EnumerateArray()
            .First(u => u.GetProperty("id").GetString() == userId)
            .GetProperty("apiKeys").GetInt32();
    }
}
