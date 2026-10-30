using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// A SLOT THAT TAKES SEVERAL CONNECTIONS (<c>"multiple": true</c>, CON 7): a member binds them under
/// <c>&lt;slot&gt;</c>, <c>&lt;slot&gt;#2</c>, <c>&lt;slot&gt;#3</c>, and its run is handed all of them
/// as one array in that order; a slot that is not multiple keeps refusing a second binding.
/// </summary>
public sealed partial class ConnectionsTests
{
    [Fact]
    public async Task A_multiple_slot_binds_several_connections_and_a_run_receives_them_all_in_order()
    {
        var first = await AddCredentialAsync("apikey", "stripe", "Stripe EU", ("key", KindSecrets[0], null));
        var second = await AddCredentialAsync("apikey", "stripe", "Stripe US", ("key", KindSecrets[1], null));
        var third = await AddCredentialAsync("apikey", "other", "Azure app", ("tenant", "contoso", false), ("token", KindSecrets[4], null));

        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Vault",
            agent = "plugin:vault",
            connections = new Dictionary<string, string> { ["keys"] = first, ["keys#3"] = third, ["keys#2"] = second },
        }, Ct);
        Assert.True(hired.IsSuccessStatusCode, await hired.Content.ReadAsStringAsync(Ct));

        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Vault")).Type);

        var keys = LastRequest()["connections"]!["keys"]!.AsArray();
        static string Key(JsonNode? grant) => (string)(grant!["fields"]!["key"] ?? grant["fields"]!["token"])!;
        Assert.Equal([KindSecrets[0], KindSecrets[1], KindSecrets[4]], keys.Select(Key));
        Assert.All(keys, k => Assert.Equal("apikey", (string?)k!["kind"]));
        Assert.Single(LastRequest()["connections"]!.AsObject());

        // EVERY ONE IS ASSIGNED TO THE TEAM AND NAMES THIS MEMBER as its user, as a single binding does.
        var store = Services.GetRequiredService<ConnectionStore>();
        foreach (var id in new[] { first, second, third })
        {
            Assert.Contains(id, await Services.GetRequiredService<Connections>().AssignedIdsAsync(_team, Ct));
            Assert.Single(await store.UsedByAsync(id, Ct), u => u.Member == "Vault");
        }

        // One deleted with its bindings leaves the others, still handed on in order.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/api/connections/{second}?unbind=true", null)).Status);
        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Vault")).Type);
        Assert.Equal([KindSecrets[0], KindSecrets[4]], LastRequest()["connections"]!["keys"]!.AsArray().Select(Key));

        await AssertNoKindSecretAsync();
    }

    [Fact]
    public async Task A_multiple_slot_with_one_connection_is_handed_an_array_of_one()
    {
        var only = await AddCredentialAsync("apikey", "stripe", "Stripe", ("key", KindSecrets[0], null));
        await HireAsync("Vault", only, plugin: "vault", slot: "keys");

        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Vault")).Type);

        var keys = LastRequest()["connections"]!["keys"]!.AsArray();
        Assert.Equal(KindSecrets[0], (string?)Assert.Single(keys)!["fields"]!["key"]);
    }

    [Fact]
    public async Task A_slot_that_is_not_multiple_refuses_a_second_binding_and_a_multiple_one_refuses_the_same_connection_twice()
    {
        var a = await AddCredentialAsync("apikey", "stripe", "Stripe A", ("key", KindSecrets[0], null));
        var b = await AddCredentialAsync("apikey", "stripe", "Stripe B", ("key", KindSecrets[1], null));
        await HireAsync("Buyer", a, plugin: "keeper", slot: "login");
        await HireAsync("Vault", a, plugin: "vault", slot: "keys");

        var second = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Buyer/plugin-settings",
            new { config = new { }, connections = new Dictionary<string, string> { ["login"] = a, ["login#2"] = b } });
        Assert.Equal(HttpStatusCode.BadRequest, second.Status);
        Assert.Contains("Slot `login` takes one connection, so `login#2` cannot bind another.", second.Body);

        var twice = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Vault/plugin-settings",
            new { config = new { }, connections = new Dictionary<string, string> { ["keys"] = a, ["keys#2"] = a } });
        Assert.Equal(HttpStatusCode.BadRequest, twice.Status);
        Assert.Contains($"Slot `keys` binds the connection '{a}' twice.", twice.Body);

        var malformed = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Vault/plugin-settings",
            new { config = new { }, connections = new Dictionary<string, string> { ["keys"] = a, ["keys#1"] = b } });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.Status);
        Assert.Contains("`keys#1` is not a connection slot of plugin 'vault'.", malformed.Body);

        // Nothing landed: each member keeps its one binding.
        var settings = Services.GetRequiredService<IPluginMemberSettingsStore>();
        Assert.Equal([KeyValuePair.Create("login", a)], (await settings.ForAsync(new ContainerId(_team, "Buyer"), Ct)).Connections);
        Assert.Equal([KeyValuePair.Create("keys", a)], (await settings.ForAsync(new ContainerId(_team, "Vault"), Ct)).Connections);
    }
}
