using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// ONE STORE OF TYPED CREDENTIALS, EACH KNOWN BY ITS NAME: a website, an API key (Stripe, Namecheap,
/// Twilio, Other) and a username and password are each created, edited, renamed and deleted through
/// the routes the wizard uses; every name is unique; a changed password reaches every member bound to
/// the name at its next run; and a delete names its users first, then unbinds them and leaves each one
/// needing a person, or changes nothing when its row cannot be written. No masked value is in any
/// answer or any file under the data root.
/// </summary>
public sealed partial class ConnectionsTests
{
    private static readonly string[] KindSecrets =
        ["sk_live_stripe-key-0001", "sk_live_stripe-key-0002", "nc-api-key-0001", "twilio-secret-0001", "other-token-0001",
         "other-token-0002", "login-password-0001", "login-password-0002"];

    [Fact]
    public async Task Each_credential_kind_is_created_edited_renamed_and_deleted_and_no_value_reaches_an_answer_or_a_file()
    {
        var presets = await GetAsync("/api/connections/credentials/presets");
        Assert.Equal(
            ["apikey/stripe", "apikey/namecheap", "apikey/twilio", "apikey/other", "login/", "website/"],
            presets.EnumerateArray().Select(p => $"{p.GetProperty("kind").GetString()}/{p.GetProperty("id").GetString()}"));
        var twilio = presets.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "twilio");
        Assert.Equal([false, false, true], twilio.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("masked").GetBoolean()));

        var stripe = await AddCredentialAsync("apikey", "stripe", "Stripe live", ("key", KindSecrets[0], null));
        var namecheap = await AddCredentialAsync("apikey", "namecheap", "Namecheap", ("apiUser", "nc-user", null), ("apiKey", KindSecrets[2], null), ("clientIp", "203.0.113.7", null));
        var twilioKey = await AddCredentialAsync(
            "apikey", "twilio", "Twilio", ("accountSid", "AC0001", null), ("apiKeySid", "SK0001", null), ("apiKeySecret", KindSecrets[3], null));
        var other = await AddCredentialAsync("apikey", "other", "Acme API", ("tenant", "acme-eu", false), ("token", KindSecrets[4], null));
        var login = await AddCredentialAsync("login", null, "Router", ("username", "admin", null), ("password", KindSecrets[6], null), ("url", "https://router.example.test", null));
        var website = await AddCredentialAsync("website", null, "Supplier portal");

        // Every kind is listed with its kind, and a masked field says only that it is set.
        var listed = (await GetAsync("/api/connections")).EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!);
        Assert.Equal(
            [("apikey", "Stripe"), ("apikey", "nc-user"), ("apikey", "AC0001"), ("apikey", "acme-eu"), ("login", "admin"), ("website", "")],
            new[] { stripe, namecheap, twilioKey, other, login, website }.Select(id => (listed[id].GetProperty("kind").GetString(), listed[id].GetProperty("account").GetString())));
        var token = listed[other].GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "token");
        Assert.Equal((true, true, JsonValueKind.Null), (token.GetProperty("masked").GetBoolean(), token.GetProperty("set").GetBoolean(), token.GetProperty("value").ValueKind));
        Assert.Empty(listed[website].GetProperty("fields").EnumerateArray());

        // EDITED: a new key, a new username, a new value for one of Other's fields; what is left out is kept.
        await EditCredentialAsync(stripe, ("key", KindSecrets[1], null));
        await EditCredentialAsync(login, ("username", "root", null));
        await EditCredentialAsync(other, ("tenant", "acme-us", false), ("token", KindSecrets[5], null));
        var store = Services.GetRequiredService<ConnectionStore>();
        Assert.Equal(KindSecrets[1], (await store.CredentialSecretsAsync(stripe, Ct))!["key"]);
        Assert.Equal(KindSecrets[6], (await store.CredentialSecretsAsync(login, Ct))!["password"]);
        Assert.Equal("root", (await store.GetAsync(login, Ct))!.Account);
        Assert.Equal(KindSecrets[5], (await store.CredentialSecretsAsync(other, Ct))!["token"]);

        // A field a preset does not have is refused, naming it.
        var unknown = await SendAsync(HttpMethod.Put, $"/api/connections/{stripe}/credentials", new { fields = new[] { new { name = "secret", value = "x" } } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.Status);
        Assert.Contains("`secret` is not a field of Stripe", unknown.Body);

        // RENAMED, with the id kept; then DELETED, with nothing left of any.
        foreach (var id in new[] { stripe, namecheap, twilioKey, other, login, website })
        {
            var renamed = await SendAsync(HttpMethod.Patch, $"/api/connections/{id}", new { name = $"Renamed {id}" });
            Assert.True(renamed.Status == HttpStatusCode.OK, renamed.Body);
            Assert.Equal(id, JsonDocument.Parse(renamed.Body).RootElement.GetProperty("id").GetString());
        }

        foreach (var id in new[] { stripe, namecheap, twilioKey, other, login, website })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/api/connections/{id}", null)).Status);
            Assert.Null(await store.CredentialSecretsAsync(id, Ct));
        }

        Assert.Equal(0, (await GetAsync("/api/connections")).GetArrayLength());

        var actions = await TenantActionsAsync();
        Assert.Equal(6, actions.Count(a => a == TenantActions.ConnectionConnected));
        Assert.Equal(3, actions.Count(a => a == TenantActions.ConnectionCredentialsChanged));
        Assert.Equal(6, actions.Count(a => a == TenantActions.ConnectionRenamed));
        Assert.Equal(6, actions.Count(a => a == TenantActions.ConnectionDisconnected));

        await AssertNoKindSecretAsync();
    }

    [Fact]
    public async Task Every_name_is_unique_ignoring_case_at_every_route_that_names_a_connection()
    {
        var first = await AddCredentialAsync("login", null, "Shared", ("username", "a", null), ("password", KindSecrets[6], null));
        var second = await AddCredentialAsync("apikey", "stripe", "Stripe", ("key", KindSecrets[0], null));

        var added = await SendAsync(HttpMethod.Post, "/api/connections/credentials",
            new { kind = "login", name = " SHARED ", fields = new[] { new { name = "username", value = "b" }, new { name = "password", value = KindSecrets[7] } } });
        Assert.Equal(HttpStatusCode.Conflict, added.Status);
        Assert.Contains("Another connection is already called 'SHARED'", added.Body);

        var renamed = await SendAsync(HttpMethod.Patch, $"/api/connections/{second}", new { name = "shared" });
        Assert.Equal(HttpStatusCode.Conflict, renamed.Status);

        var mailbox = await SendAsync(HttpMethod.Post, "/api/connections/imap", new
        {
            preset = "gmail", name = "Shared", account = "box@example.test", password = "abcd efgh ijkl mnop",
            imap = new { host = "imap.gmail.com", port = 993, security = "TLS" }, smtp = new { host = "smtp.gmail.com", port = 465, security = "TLS" },
        });
        Assert.Equal(HttpStatusCode.Conflict, mailbox.Status);

        // A sign-in is refused before anyone signs in, not after.
        var start = await SendAsync(HttpMethod.Post, "/api/connections/start", new { provider = "google", scopes = new[] { MailScope }, name = "shared" });
        Assert.Equal(HttpStatusCode.BadRequest, start.Status);
        Assert.Contains("Another connection is already called 'shared'", start.Body);

        // Renaming to its own name in another case is no clash.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Patch, $"/api/connections/{first}", new { name = "SHARED" })).Status);
        Assert.Equal(["SHARED", "Stripe"], (await GetAsync("/api/connections")).EnumerateArray().Select(c => c.GetProperty("name").GetString()));
    }

    /// <summary>
    /// THE NAME IS THE KEY: a login used by a member on each of two teams. Its password changed once, in
    /// Connections, reaches both members' next runs with no prompt, and the tenant row names the field
    /// that changed and who changed it, never either value.
    /// </summary>
    [Fact]
    public async Task A_changed_password_reaches_two_members_on_two_teams_at_their_next_run_and_its_row_names_the_change_not_the_value()
    {
        var id = await AddCredentialAsync("login", null, "Supplier", ("username", "buyer", null), ("password", KindSecrets[6], null));
        var other = (await Services.GetRequiredService<TeamRegistry>().CreateAsync("Other", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        await HireAsync("Buyer", id, plugin: "keeper", slot: "login");
        await HireAsync("Clerk", id, team: other, plugin: "keeper", slot: "login");

        var members = new[] { ("Buyer", _team), ("Clerk", other) };
        foreach (var (member, team) in members)
        {
            Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync(member, team)).Type);
            var slot = LastRequest()["connections"]!["login"]!;
            Assert.Equal(("login", "buyer", KindSecrets[6]), ((string?)slot["kind"], (string?)slot["fields"]!["username"], (string?)slot["fields"]!["password"]));
        }

        await EditCredentialAsync(id, ("password", KindSecrets[7], null));

        foreach (var (member, team) in members)
        {
            Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync(member, team)).Type);
            Assert.Equal(KindSecrets[7], (string?)LastRequest()["connections"]!["login"]!["fields"]!["password"]);
        }

        var row = Assert.Single(await TenantRowsAsync(), r => r.Action == TenantActions.ConnectionCredentialsChanged);
        Assert.Equal((Email, id), (row.ActorEmail, row.Subject));
        Assert.Equal(["password"], JsonDocument.Parse(row.Detail!).RootElement.GetProperty("changed").EnumerateArray().Select(c => c.GetString()));
        Assert.DoesNotContain(KindSecrets[6], row.Detail);
        Assert.DoesNotContain(KindSecrets[7], row.Detail);

        await AssertNoKindSecretAsync();
    }

    /// <summary>
    /// A DELETE ASKS FIRST: a connection a member uses answers 409 naming the member, and nothing goes.
    /// Confirmed, the connection and the member's binding go with their rows, and the member is marked
    /// blocked with a sentence naming the connection - listed in Needs you - and its next run does not
    /// start for want of the slot.
    /// </summary>
    [Fact]
    public async Task Deleting_a_used_connection_names_its_member_first_then_unbinds_it_and_leaves_it_needing_a_person()
    {
        var id = await AddCredentialAsync("login", null, "Supplier", ("username", "buyer", null), ("password", KindSecrets[6], null));
        await HireAsync("Buyer", id, plugin: "keeper", slot: "login");

        var asked = await SendAsync(HttpMethod.Delete, $"/api/connections/{id}", null);
        Assert.Equal(HttpStatusCode.Conflict, asked.Status);
        var user = Assert.Single(JsonDocument.Parse(asked.Body).RootElement.GetProperty("usedBy").EnumerateArray());
        Assert.Equal((_team, "Buyer", "login"), (user.GetProperty("team").GetString(), user.GetProperty("member").GetString(), user.GetProperty("slot").GetString()));
        Assert.Contains("stops their runs until another is chosen", asked.Body);
        Assert.Single((await GetAsync("/api/connections")).EnumerateArray());

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/api/connections/{id}?unbind=true", null)).Status);

        Assert.Equal(0, (await GetAsync("/api/connections")).GetArrayLength());
        var settings = await Services.GetRequiredService<IPluginMemberSettingsStore>().ForAsync(new ContainerId(_team, "Buyer"), Ct);
        Assert.Empty(settings.Connections);

        var needs = (await GetAsync("/api/needs-you?part=live")).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("member").GetString() == "Buyer");
        Assert.Equal("blocked", needs.GetProperty("kind").GetString());
        Assert.Contains("Connection 'Supplier' (buyer) was deleted", needs.GetProperty("words").GetString());

        var rows = await TenantRowsAsync();
        Assert.Single(rows, r => r.Action == TenantActions.ConnectionDisconnected && r.Subject == id);
        var unbound = Assert.Single(rows, r => r.Action == TenantActions.MemberConnectionsChanged && r.Subject == $"{_team}/Buyer"
            && r.Detail!.Contains("connection deleted"));
        Assert.Contains(id, unbound.Detail);

        var next = await TellAndAwaitAsync("Buyer");
        Assert.Equal(MessageTypes.Failed, next.Type);
        Assert.Contains("no connection bound for the plugin's required slot `login`", Words(next));
    }

    [Fact]
    public async Task A_confirmed_delete_whose_row_cannot_be_written_changes_nothing()
    {
        var id = await AddCredentialAsync("login", null, "Supplier", ("username", "buyer", null), ("password", KindSecrets[6], null));
        await HireAsync("Buyer", id, plugin: "keeper", slot: "login");
        await ExecuteAsync("DROP TABLE tenant_events");

        var refused = await SendAsync(HttpMethod.Delete, $"/api/connections/{id}?unbind=true", null);

        Assert.Equal(HttpStatusCode.InternalServerError, refused.Status);
        Assert.Contains("was not disconnected", refused.Body);
        var store = Services.GetRequiredService<ConnectionStore>();
        Assert.NotNull(await store.GetAsync(id, Ct));
        Assert.Equal(KindSecrets[6], (await store.CredentialSecretsAsync(id, Ct))!["password"]);
        Assert.Equal([new ConnectionUse(_team, "Buyer", "login")], await store.UsedByAsync(id, Ct));
        Assert.Null(Services.GetRequiredService<Harness.Containers.ContainerHost>().Find(new ContainerId(_team, "Buyer"))!.Snapshot().Blocked);
    }

    private async Task<string> AddCredentialAsync(string kind, string? preset, string name, params (string Name, string Value, bool? Masked)[] fields)
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "/api/connections/credentials", new
        {
            kind,
            preset,
            name,
            fields = fields.Select(f => new { name = f.Name, value = f.Value, masked = f.Masked }),
        });
        Assert.True(status == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("connection").GetProperty("id").GetString()!;
    }

    private async Task EditCredentialAsync(string id, params (string Name, string Value, bool? Masked)[] fields)
    {
        var (status, body) = await SendAsync(HttpMethod.Put, $"/api/connections/{id}/credentials", new
        {
            fields = fields.Select(f => new { name = f.Name, value = f.Value, masked = f.Masked }),
        });
        Assert.True(status == HttpStatusCode.OK, body);
    }

    /// <summary>No masked value in any answer, and in no file the Host wrote under its data root.</summary>
    private async Task AssertNoKindSecretAsync()
    {
        foreach (var secret in KindSecrets) Assert.DoesNotContain(_responses, r => r.Contains(secret, StringComparison.Ordinal));

        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(_dataRoot, "*", SearchOption.AllDirectories))
        {
            if (file.StartsWith(PluginInstall.PluginsRoot(_dataRoot), StringComparison.Ordinal)) continue;

            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, Ct); }
            catch (IOException) { continue; }

            var text = Encoding.Latin1.GetString(bytes);
            var utf16 = Encoding.Unicode.GetString(bytes);
            foreach (var secret in KindSecrets)
            {
                Assert.False(text.Contains(secret, StringComparison.Ordinal) || utf16.Contains(secret, StringComparison.Ordinal), $"{secret} is in {file}");
            }
        }
    }
}
