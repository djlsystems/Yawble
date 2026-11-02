using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// EACH BOUND CONNECTION CARRIES ITS NAME ON STDIN (CON 15): the name a person chose and sees in
/// Admin → Connections, just before <c>account</c>, on a mailbox, an API key, a username and password, a
/// website and a sign-in alike, and on each element of a multiple slot - so two API keys whose preset
/// is Other are told apart. A name is not a secret and is not redacted; a masked value that is a bound
/// connection's name refuses the run before it starts, naming the field and never the value.
/// </summary>
public sealed partial class ConnectionsTests
{
    private const string WebsitePassword = "website-password-0003";

    [Fact]
    public async Task Every_bound_connection_carries_its_stored_name_before_its_account_and_a_multiple_slot_names_each()
    {
        var bound = await BindEveryKindAsync();

        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Every")).Type);

        var connections = LastRequest()["connections"]!;
        var store = Services.GetRequiredService<ConnectionStore>();
        async Task<string> StoredName(string id) => (await store.GetAsync(id, Ct))!.Name;

        foreach (var slot in new[] { "mail", "key", "login", "site", "account" })
        {
            Assert.Equal(await StoredName(bound[slot]), (string?)connections[slot]!["name"]);
        }

        Assert.Equal(["Shop mail", "Acme EU", "Router admin", "Supplier portal"], new[] { "mail", "key", "login", "site" }.Select(s => (string?)connections[s]!["name"]));

        // INSERTED JUST BEFORE `account`: every other key keeps its order.
        Assert.Equal(["kind", "name", "account", "username", "password", "imap", "smtp"], connections["mail"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["kind", "name", "account", "preset", "fields"], connections["key"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["kind", "name", "account", "preset", "fields"], connections["login"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["kind", "name", "account", "preset", "fields"], connections["site"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["provider", "name", "account", "accessToken", "expiresAt", "scopes"], connections["account"]!.AsObject().Select(p => p.Key));
        Assert.Equal(("apikey", "other"), ((string?)connections["key"]!["kind"], (string?)connections["key"]!["preset"]));

        // A MULTIPLE SLOT OF TWO OTHER API KEYS: each element names its own connection.
        var keys = connections["keys"]!.AsArray();
        Assert.Equal(["Acme EU", "Acme US"], keys.Select(k => (string?)k!["name"]));
        Assert.All(keys, k => Assert.Equal(("apikey", "other"), ((string?)k!["kind"], (string?)k["preset"])));
        Assert.Equal([KindSecrets[4], KindSecrets[5]], keys.Select(k => (string?)k!["fields"]!["token"]));
    }

    [Fact]
    public async Task A_bound_website_is_handed_its_fields_its_masked_values_redacted_from_output_and_its_name_not()
    {
        await BindEveryKindAsync();

        var row = await TellAndAwaitAsync("Every");
        Assert.Equal(MessageTypes.Completed, row.Type);

        var site = LastRequest()["connections"]!["site"]!;
        Assert.Equal(("website", "Supplier portal", "supplier.example.test"), ((string?)site["kind"], (string?)site["name"], (string?)site["account"]));
        Assert.Equal("https://supplier.example.test/portal", (string?)site["fields"]!["siteUrl"]);
        Assert.Equal(("password", "buyer", WebsitePassword), ((string?)site["fields"]!["method"], (string?)site["fields"]!["username"], (string?)site["fields"]!["password"]));

        // The plugin printed its whole request: the run's output keeps every name, never a masked value.
        var output = Words(row);
        Assert.Contains("\"name\":\"Supplier portal\"", output);
        Assert.Contains("\"name\":\"Acme US\"", output);
        Assert.Contains("\"username\":\"buyer\"", output);
        foreach (var secret in new[] { WebsitePassword, KindSecrets[4], KindSecrets[5], KindSecrets[6], "shop-app-password-0001" })
        {
            Assert.DoesNotContain(secret, output);
        }
    }

    [Fact]
    public async Task A_masked_value_that_is_a_bound_connections_name_refuses_the_run_before_it_starts_naming_the_field_not_the_value()
    {
        // The second Other key's token IS the first key's name, which goes on stdin unredacted.
        await BindEveryKindAsync(secondToken: "Acme EU");
        var before = File.Exists(Requests) ? File.ReadAllLines(Requests).Length : 0;

        var row = await TellAndAwaitAsync("Every");

        Assert.Equal(MessageTypes.Failed, row.Type);
        var words = Words(row);
        Assert.Contains("The value of `token` in the connection bound for `keys` is the name of a connection this member's plugin is told, and connection names are not secret.", words);
        Assert.Contains(FailureClasses.NeedsPerson, words);
        Assert.DoesNotContain("Acme EU", words);
        Assert.Equal(before, File.Exists(Requests) ? File.ReadAllLines(Requests).Length : 0);
    }

    /// <summary>Hires Every bound to a mailbox, an Other API key, a username and password, a website, a
    /// Google sign-in, and two Other API keys in its multiple slot; answers each slot's connection id.</summary>
    private async Task<Dictionary<string, string>> BindEveryKindAsync(string? secondToken = null)
    {
        // A MAILBOX, stored as just verified, so the run hands it on without a login.
        var mailId = $"conn-{Guid.NewGuid():N}";
        var now = _clock.GetUtcNow();
        await Services.GetRequiredService<ConnectionStore>().InsertMailboxAsync(
            new ConnectionRecord(mailId, "Shop mail", MailboxSettings.Kind, "shop@example.test", [], now, now, ConnectionRecord.Ok, null),
            new MailboxSettings("shop@example.test", new MailServer("imap.example.test", 993, "TLS"), new MailServer("smtp.example.test", 465, "TLS"), "other", true),
            "shop-app-password-0001", Email, new TriggerAudit(null, Email, TenantActions.ConnectionConnected, mailId, null, null), Ct);

        var bound = new Dictionary<string, string>
        {
            ["mail"] = mailId,
            ["key"] = await AddCredentialAsync("apikey", "other", "Acme EU", ("tenant", "acme-eu", false), ("token", KindSecrets[4], null)),
            ["login"] = await AddCredentialAsync("login", null, "Router admin", ("username", "admin", null), ("password", KindSecrets[6], null)),
            ["site"] = await AddCredentialAsync("website", null, "Supplier portal",
                ("siteUrl", "supplier.example.test/portal", null), ("method", "password", null), ("username", "buyer", null), ("password", WebsitePassword, null)),
            ["account"] = await ConnectAsync(),
        };
        bound["keys"] = bound["key"];
        bound["keys#2"] = await AddCredentialAsync("apikey", "other", "Acme US", ("tenant", "acme-us", false), ("token", secondToken ?? KindSecrets[5], null));

        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name = "Every", agent = "plugin:every", connections = bound }, Ct);
        Assert.True(hired.IsSuccessStatusCode, await hired.Content.ReadAsStringAsync(Ct));

        return bound;
    }
}
