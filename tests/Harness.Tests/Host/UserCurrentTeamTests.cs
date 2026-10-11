using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// `users.current_team` IS PER PERSON, NOT PER CREDENTIAL. The browser and the Concierge it drives
/// are two principals for one human; keyed on either, one would write a team the other never reads.
/// </summary>
public sealed class UserCurrentTeamTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task A_team_chosen_in_the_browser_is_the_team_the_persons_key_reads_and_back()
    {
        var ct = TestContext.Current.CancellationToken;
        using var browser = await host.PersonAsync();
        using var key = host.Container(await PersonKeyAsync("key-current-team", ct));

        (await browser.PutAsJsonAsync("/api/me/current-team", new { team = host.Alpha }, ct)).EnsureSuccessStatusCode();
        Assert.Equal(host.Alpha, await CurrentTeamAsync(key, ct));

        (await key.PutAsJsonAsync("/api/me/current-team", new { team = host.Beta }, ct)).EnsureSuccessStatusCode();
        Assert.Equal(host.Beta, await CurrentTeamAsync(browser, ct));
    }

    [Fact]
    public async Task The_current_team_survives_a_credential_rotation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var browser = await host.PersonAsync();
        (await browser.PutAsJsonAsync("/api/me/current-team", new { team = host.Beta }, ct)).EnsureSuccessStatusCode();

        // The same principal minted again: the upsert a Concierge relaunch performs.
        await PersonKeyAsync("key-rotated", ct);
        using var rotated = host.Container(await PersonKeyAsync("key-rotated", ct));

        Assert.Equal(host.Beta, await CurrentTeamAsync(rotated, ct));
    }

    private async Task<string> PersonKeyAsync(string id, CancellationToken ct)
    {
        var person = await host.Services.GetRequiredService<IUserStore>().FindAsync("person@example.test", ct);

        return await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            id, PrincipalKind.ApiKey, team: null, Permits.All, ownerUserId: person!.Id, label: id, ct: ct);
    }

    private static async Task<string?> CurrentTeamAsync(HttpClient client, CancellationToken ct)
    {
        using var body = JsonDocument.Parse(await client.GetStringAsync("/api/me/current-team", ct));
        return body.RootElement.GetProperty("team").GetString();
    }
}
