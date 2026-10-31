using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Harness.Tests;

/// <summary>
/// A PLUGIN UPDATE MOVES A MEMBER'S OWN SECRET INTO A CONNECTION (CON 7): a version declaring
/// <c>secrets.&lt;field&gt;.movedTo</c>, made active by an install, a Replace (the route and the operator
/// CLI's request) or a solution update, turns the member's own values into one API-key connection named
/// after its team and member, assigned to the team and bound to the slot, and removes them, in one
/// transaction. No value is typed, and none reaches a tenant row or a file.
/// </summary>
public sealed partial class ConnectionsTests
{
    private static readonly string[] MovedSecrets =
        ["sk_live_moved-stripe-0001", "nc-moved-api-key-0001", "sk_live_moved-stripe-0002", "sk_live_moved-stripe-0003"];

    /// <summary>A plugin <paramref name="id"/> <paramref name="version"/> writing its request outside the
    /// data root, as a built folder inside it (<c>work/</c>) to install from. <paramref name="moved"/>
    /// declares each field moved to its slot; null declares the fields as the member's own.</summary>
    private string PayPlugin(
        string version, IReadOnlyDictionary<string, string>? moved, string id = "pay", string slot = "stripe", bool multiple = false,
        bool unmasked = false, string? folder = null, bool required = true)
    {
        folder ??= Path.Combine(_dataRoot, "work", id, version + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(folder, "bin"));

        var manifest = PluginInstall.Manifest(id, version, m =>
        {
            var secrets = new JsonObject
            {
                ["apiKey"] = new JsonObject { ["description"] = "The account's API key.", ["required"] = required },
            };
            if (unmasked) secrets["apiUser"] = new JsonObject { ["description"] = "The API user.", ["masked"] = false };

            if (moved is not null)
            {
                foreach (var (field, to) in moved) secrets[field]!["movedTo"] = to;
                m["connections"] = new JsonObject
                {
                    [slot] = new JsonObject
                    {
                        ["description"] = "The account.", ["providers"] = new JsonArray("apikey"), ["required"] = true, ["multiple"] = multiple,
                    },
                };
            }

            m["secrets"] = secrets;
        });

        File.WriteAllText(Path.Combine(folder, PluginManifest.FileName), manifest.ToJsonString());
        var run = Path.Combine(folder, "bin", "run");
        File.WriteAllText(run, $"#!/bin/sh\nreq=$(cat); printf '%s\\n' \"$req\" >> '{Requests}'; echo '{{\"t\":\"result\",\"ok\":true,\"output\":\"paid\"}}'\n");
        File.SetUnixFileMode(run, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return folder;
    }

    private async Task<JsonElement> InstallPluginAsync(string folder, bool replace = false)
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "/api/plugins/install", new { path = folder, replace });
        _responses.Add(body);
        Assert.True(status == HttpStatusCode.OK, body);
        var answer = JsonDocument.Parse(body).RootElement;
        Assert.True(answer.GetProperty("installed").GetBoolean(), body);
        return answer;
    }

    /// <summary>A member of <paramref name="team"/> hired on <paramref name="plugin"/> with its own values.</summary>
    private async Task HireOwnAsync(string name, Dictionary<string, string> values, string plugin = "pay", string? team = null)
    {
        var hired = await _person.PostAsJsonAsync($"/api/teams/{team ?? _team}/containers", new { name, agent = $"plugin:{plugin}", secretValues = values }, Ct);
        var body = await hired.Content.ReadAsStringAsync(Ct);
        _responses.Add(body);
        Assert.True(hired.StatusCode == HttpStatusCode.OK, body);
    }

    private Task<IReadOnlyDictionary<string, string>> OwnValuesAsync(string member, string? team = null) =>
        Services.GetRequiredService<PluginMemberSecretStore>().ValuesAsync(new ContainerId(team ?? _team, member), Ct);

    private async Task<IReadOnlyDictionary<string, string>> BindingsAsync(string member, string? team = null) =>
        (await Services.GetRequiredService<IPluginMemberSettingsStore>().ForAsync(new ContainerId(team ?? _team, member), Ct)).Connections;

    /// <summary>
    /// THE DONE-WHEN, after any path: the member has no own value left, its slot binds an API-key
    /// connection named after its team and member and assigned to the team, and a run receives that
    /// connection holding the same key.
    /// </summary>
    private async Task AssertMovedAndRunsAsync(string member, string key, string teamLabel, string? team = null)
    {
        team ??= _team;
        Assert.Empty(await OwnValuesAsync(member, team));

        var bound = Assert.Single(await BindingsAsync(member, team));
        Assert.Equal("stripe", bound.Key);

        var connection = (await Services.GetRequiredService<ConnectionStore>().GetAsync(bound.Value, Ct))!;
        Assert.Equal($"{teamLabel} - {member}", connection.Name);
        Assert.Equal(ConnectionKinds.ApiKey, connection.Kind);
        Assert.Contains(bound.Value, await Services.GetRequiredService<Connections>().AssignedIdsAsync(team, Ct));

        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync(member, team)).Type);
        var handed = LastRequest()["connections"]!["stripe"]!;
        Assert.Equal("apikey", (string?)handed["kind"]);
        Assert.Equal(key, (string?)handed["fields"]!["apiKey"]);
    }

    /// <summary>No value is in any tenant row, any answer, or any file under the data root.</summary>
    private async Task AssertNoMovedSecretAsync()
    {
        foreach (var row in await TenantRowsAsync())
        {
            foreach (var secret in MovedSecrets) Assert.DoesNotContain(secret, $"{row.Action} {row.Subject} {row.Detail}", StringComparison.Ordinal);
        }

        foreach (var secret in MovedSecrets) Assert.DoesNotContain(_responses, r => r.Contains(secret, StringComparison.Ordinal));

        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(_dataRoot, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, Ct); }
            catch (IOException) { continue; }

            var text = Encoding.Latin1.GetString(bytes);
            var utf16 = Encoding.Unicode.GetString(bytes);
            foreach (var secret in MovedSecrets)
            {
                Assert.False(text.Contains(secret, StringComparison.Ordinal) || utf16.Contains(secret, StringComparison.Ordinal), $"{secret} is in {file}");
            }
        }
    }

    // ---- (a) the Done-when, by each path -------------------------------------------------------

    [Fact]
    public async Task An_install_of_a_version_moving_the_key_gives_the_run_a_connection_holding_it_and_leaves_no_own_value()
    {
        await InstallPluginAsync(PayPlugin("0.1.0", moved: null));
        await HireOwnAsync("Billing", new() { ["apiKey"] = MovedSecrets[0] });

        // Before: the key is the member's own, handed as a secret.
        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Billing")).Type);
        Assert.Equal(MovedSecrets[0], (string?)LastRequest()["secrets"]!["apiKey"]);

        // THE NEW VERSION, installed with nothing but its folder: no key in the request.
        var answer = await InstallPluginAsync(PayPlugin("0.2.0", new Dictionary<string, string> { ["apiKey"] = "stripe" }));
        var move = Assert.Single(answer.GetProperty("moves").EnumerateArray());
        Assert.Equal(("Billing", "stripe", "moved"), (move.GetProperty("member").GetString(), move.GetProperty("slot").GetString(), move.GetProperty("outcome").GetString()));

        await AssertMovedAndRunsAsync("Billing", MovedSecrets[0], "Mail");
        Assert.False(LastRequest()["secrets"]!.AsObject().ContainsKey("apiKey"));

        // (e) THE ROWS NAME FIELDS: the connection, its assignment, the binding and the field moved.
        var actions = await TenantActionsAsync();
        foreach (var action in new[] { TenantActions.ConnectionConnected, TenantActions.TeamConnectionAssigned, TenantActions.MemberConnectionsChanged, TenantActions.MemberSecretMoved })
        {
            Assert.Contains(action, actions);
        }

        Assert.Contains("\"field\":\"apiKey\"", Assert.Single(await TenantDetailsAsync(TenantActions.MemberSecretMoved)));
        await AssertNoMovedSecretAsync();

        // AGAIN, nothing moves twice: no own value is left to move.
        var again = await InstallPluginAsync(PayPlugin("0.2.0", new Dictionary<string, string> { ["apiKey"] = "stripe" }), replace: true);
        Assert.Equal("nothing", Assert.Single(again.GetProperty("moves").EnumerateArray()).GetProperty("outcome").GetString());
        Assert.Single(await BindingsAsync("Billing"));
    }

    [Fact]
    public async Task A_replace_of_the_same_version_with_one_moving_the_key_moves_it()
    {
        await InstallPluginAsync(PayPlugin("0.2.0", moved: null));
        await HireOwnAsync("Billing", new() { ["apiKey"] = MovedSecrets[0] });

        await InstallPluginAsync(PayPlugin("0.2.0", new Dictionary<string, string> { ["apiKey"] = "stripe" }), replace: true);

        await AssertMovedAndRunsAsync("Billing", MovedSecrets[0], "Mail");
        await AssertNoMovedSecretAsync();
    }

    [Fact]
    public async Task The_operator_clis_install_request_moves_the_key_as_the_route_does()
    {
        await InstallPluginAsync(PayPlugin("0.1.0", moved: null));
        await HireOwnAsync("Billing", new() { ["apiKey"] = MovedSecrets[0] });

        var folder = PayPlugin("0.2.0", new Dictionary<string, string> { ["apiKey"] = "stripe" });
        var nonce = Guid.NewGuid().ToString("N");
        var plugins = PluginInstall.PluginsRoot(_dataRoot);
        await File.WriteAllTextAsync(Path.Combine(plugins, PluginRescanRequests.InstallRequestFile),
            JsonSerializer.Serialize(new { request = nonce, path = folder, replace = false }), Ct);

        // Answered by the Host's own poll or here, once either way.
        var requests = Services.GetServices<IHostedService>().OfType<PluginRescanRequests>().Single();
        await requests.AnswerInstallAsync(Ct);
        await WaitUntilAsync(() => File.Exists(Path.Combine(plugins, PluginRescanRequests.InstallReportFile))
            && File.ReadAllText(Path.Combine(plugins, PluginRescanRequests.InstallReportFile)).Contains(nonce));
        await WaitUntilAsync(() => TenantDetailsAsync(TenantActions.MemberSecretMoved).Result.Count == 1);

        await AssertMovedAndRunsAsync("Billing", MovedSecrets[0], "Mail");
        await AssertNoMovedSecretAsync();
    }

    [Fact]
    public async Task A_solution_update_to_a_plugin_version_moving_the_key_moves_it_once_the_update_succeeds()
    {
        const string Plugin = "paysol";
        var older = Package("1.0.0", moved: false);
        var (status, text) = await SendAsync(HttpMethod.Post, "/api/solutions/install", new
        {
            folder = older,
            teamName = "Payments",
            secretValues = new Dictionary<string, Dictionary<string, string>> { ["Keeper"] = new() { ["apiKey"] = MovedSecrets[0] } },
        });
        Assert.True(status == HttpStatusCode.OK, text);
        var installed = JsonDocument.Parse(text).RootElement;
        Assert.True(installed.GetProperty("ok").GetBoolean(), text);
        var team = installed.GetProperty("team").GetString()!;
        Assert.Equal(MovedSecrets[0], (await OwnValuesAsync("Keeper", team))["apiKey"]);

        (status, text) = await SendAsync(HttpMethod.Post, "/api/solutions/update", new { folder = Package("1.1.0", moved: true), team });
        _responses.Add(text);
        Assert.True(status == HttpStatusCode.OK && JsonDocument.Parse(text).RootElement.GetProperty("ok").GetBoolean(), text);

        await AssertMovedAndRunsAsync("Keeper", MovedSecrets[0], "Payments", team);
        await AssertNoMovedSecretAsync();

        string Package(string version, bool moved)
        {
            var folder = SolutionSamples.Neutral(Path.Combine(_dataRoot, "packages", Guid.NewGuid().ToString("N")), Plugin, [], [], version);
            var plugin = Path.Combine(folder, "plugins", Plugin);
            Directory.Delete(plugin, recursive: true);
            PayPlugin(moved ? "0.2.0" : "0.1.0", moved ? new Dictionary<string, string> { ["apiKey"] = "stripe" } : null, id: Plugin, folder: plugin);

            // A PACKAGE ASKS FOR EVERY REQUIRED SLOT; the update binds none, so the move fills it.
            if (moved)
            {
                SolutionSamples.Edit(folder, m => m["inputs"] = new JsonObject
                {
                    ["connections"] = new JsonArray(new JsonObject { ["member"] = "Keeper", ["slot"] = "stripe", ["description"] = "The Stripe account." }),
                });
            }

            return folder;
        }
    }

    // ---- (b) no own value ----------------------------------------------------------------------

    [Fact]
    public async Task A_field_with_no_own_value_makes_no_connection_and_its_required_slot_reads_needs_you()
    {
        await InstallPluginAsync(PayPlugin("0.1.0", moved: null, required: false));
        await HireOwnAsync("Billing", []);
        var before = (await GetAsync("/api/connections")).GetArrayLength();

        var answer = await InstallPluginAsync(PayPlugin("0.2.0", new Dictionary<string, string> { ["apiKey"] = "stripe" }));
        Assert.Equal("nothing", Assert.Single(answer.GetProperty("moves").EnumerateArray()).GetProperty("outcome").GetString());

        Assert.Equal(before, (await GetAsync("/api/connections")).GetArrayLength());
        Assert.Empty(await BindingsAsync("Billing"));
        Assert.DoesNotContain(TenantActions.MemberSecretMoved, await TenantActionsAsync());

        // NEEDS YOU: the slot, not the moved field, is what the member waits for.
        var waits = Services.GetRequiredService<Harness.Containers.ContainerHost>().Find(new ContainerId(_team, "Billing"))!.Snapshot().NeedsPerson;
        Assert.Equal("Waits for a person to bind a connection for the slot `stripe`, in the member's settings.", waits);

        var row = await TellAndAwaitAsync("Billing");
        Assert.Equal(MessageTypes.Failed, row.Type);
        Assert.Contains("no connection bound for the plugin's required slot `stripe`", Words(row));
    }

    // ---- (c) several fields, one slot ----------------------------------------------------------

    [Fact]
    public async Task Several_fields_moved_to_one_slot_make_one_connection_holding_them_all()
    {
        await InstallPluginAsync(PayPlugin("0.1.0", moved: null, slot: "namecheap", unmasked: true));
        await HireOwnAsync("Domains", new() { ["apiUser"] = "acme-dns", ["apiKey"] = MovedSecrets[1] });

        await InstallPluginAsync(PayPlugin(
            "0.2.0", new Dictionary<string, string> { ["apiUser"] = "namecheap", ["apiKey"] = "namecheap" }, slot: "namecheap", unmasked: true));

        Assert.Empty(await OwnValuesAsync("Domains"));
        var bound = Assert.Single(await BindingsAsync("Domains"));
        Assert.Equal("namecheap", bound.Key);

        // ITS FIELDS ARE NAMECHEAP'S, so it is that preset: the user shown as the account, the key masked,
        // and the whitelisted IP the member never held left empty.
        var connection = (await Services.GetRequiredService<ConnectionStore>().GetAsync(bound.Value, Ct))!;
        Assert.Equal(("namecheap", "acme-dns", "Mail - Domains"), (connection.Credential!.Preset, connection.Account, connection.Name));
        Assert.Equal([("apiUser", false, true), ("apiKey", true, true), ("clientIp", false, false)],
            connection.Credential.Fields.Select(f => (f.Name, f.Masked, f.Set)));

        // A PERSON FILLS IT IN, and the run is handed every field.
        await EditCredentialAsync(bound.Value, ("clientIp", "203.0.113.7", null));
        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Domains")).Type);
        var fields = LastRequest()["connections"]!["namecheap"]!["fields"]!;
        Assert.Equal(("acme-dns", MovedSecrets[1], "203.0.113.7"), ((string?)fields["apiUser"], (string?)fields["apiKey"], (string?)fields["clientIp"]));

        Assert.Equal(["apiKey", "apiUser"], (await TenantDetailsAsync(TenantActions.MemberSecretMoved))
            .Select(d => JsonDocument.Parse(d).RootElement.GetProperty("field").GetString()));
        await AssertNoMovedSecretAsync();
    }

    /// <summary>
    /// A REQUIRED PRESET FIELD THE MEMBER DOES NOT HOLD is left empty, not made up: the connection reads
    /// Needs you (needs reconnect, naming the field) and a run is refused with it, until a person fills it in.
    /// </summary>
    [Fact]
    public async Task A_connection_moved_without_a_required_preset_field_reads_needs_you_until_a_person_fills_it_in()
    {
        await InstallPluginAsync(PayPlugin("0.1.0", moved: null, slot: "namecheap", unmasked: true));
        await HireOwnAsync("Domains", new() { ["apiUser"] = "acme-dns", ["apiKey"] = MovedSecrets[1] });

        await InstallPluginAsync(PayPlugin(
            "0.2.0", new Dictionary<string, string> { ["apiUser"] = "namecheap", ["apiKey"] = "namecheap" }, slot: "namecheap", unmasked: true));

        Assert.Empty(await OwnValuesAsync("Domains"));
        var id = Assert.Single(await BindingsAsync("Domains")).Value;
        var listed = (await GetAsync("/api/connections")).EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);
        Assert.Equal("needs-reconnect", listed.GetProperty("status").GetString());
        Assert.Equal("`clientIp` (Whitelisted IP) is required.", listed.GetProperty("statusReason").GetString());

        var refused = await TellAndAwaitAsync("Domains");
        Assert.Equal(MessageTypes.Failed, refused.Type);
        Assert.Contains("needs you: `clientIp` (Whitelisted IP) is required.", Words(refused));

        await EditCredentialAsync(id, ("clientIp", "203.0.113.7", null));
        listed = (await GetAsync("/api/connections")).EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);
        Assert.Equal("ok", listed.GetProperty("status").GetString());
        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Domains")).Type);
        await AssertNoMovedSecretAsync();
    }

    // ---- a slot already bound ------------------------------------------------------------------

    [Fact]
    public async Task A_slot_already_bound_is_not_overwritten_and_the_member_keeps_its_own_value()
    {
        var existing = await AddCredentialAsync("apikey", "other", "Stripe shared", ("apiKey", MovedSecrets[2], null));
        PayPlugin("0.1.0", new Dictionary<string, string> { ["apiKey"] = "stripe" }, folder: Path.Combine(PluginInstall.PluginsRoot(_dataRoot), "pay", "0.1.0"));
        await File.WriteAllTextAsync(Path.Combine(PluginInstall.PluginsRoot(_dataRoot), "pay", PluginCatalog.ActiveFile), "0.1.0\n", Ct);
        await Services.GetRequiredService<PluginCatalog>().RescanAsync(Ct);

        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Billing", agent = "plugin:pay",
            connections = new Dictionary<string, string> { ["stripe"] = existing },
            secretValues = new Dictionary<string, string> { ["apiKey"] = MovedSecrets[0] },
        }, Ct);
        Assert.True(hired.IsSuccessStatusCode, await hired.Content.ReadAsStringAsync(Ct));

        var answer = await InstallPluginAsync(PayPlugin("0.2.0", new Dictionary<string, string> { ["apiKey"] = "stripe" }));
        Assert.Equal("kept", Assert.Single(answer.GetProperty("moves").EnumerateArray()).GetProperty("outcome").GetString());

        // THE BINDING STANDS, no connection is made, and the own value is kept for a person to clear.
        Assert.Equal([KeyValuePair.Create("stripe", existing)], await BindingsAsync("Billing"));
        Assert.Equal(MovedSecrets[0], (await OwnValuesAsync("Billing"))["apiKey"]);
        Assert.DoesNotContain(TenantActions.MemberSecretMoved, await TenantActionsAsync());
        Assert.Contains("\"fields\":[\"apiKey\"]", Assert.Single(await TenantDetailsAsync(TenantActions.MemberSecretMoveKept)));

        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Billing")).Type);
        Assert.Equal(MovedSecrets[2], (string?)LastRequest()["connections"]!["stripe"]!["fields"]!["apiKey"]);
        await AssertNoMovedSecretAsync();
    }

    [Fact]
    public async Task A_multiple_slot_already_bound_takes_the_moved_key_as_its_next_connection()
    {
        var existing = await AddCredentialAsync("apikey", "other", "Stripe EU", ("apiKey", MovedSecrets[2], null));
        PayPlugin("0.1.0", new Dictionary<string, string> { ["apiKey"] = "stripe" }, multiple: true,
            folder: Path.Combine(PluginInstall.PluginsRoot(_dataRoot), "pay", "0.1.0"));
        await File.WriteAllTextAsync(Path.Combine(PluginInstall.PluginsRoot(_dataRoot), "pay", PluginCatalog.ActiveFile), "0.1.0\n", Ct);
        await Services.GetRequiredService<PluginCatalog>().RescanAsync(Ct);

        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Billing", agent = "plugin:pay",
            connections = new Dictionary<string, string> { ["stripe"] = existing },
            secretValues = new Dictionary<string, string> { ["apiKey"] = MovedSecrets[0] },
        }, Ct);
        Assert.True(hired.IsSuccessStatusCode, await hired.Content.ReadAsStringAsync(Ct));

        await InstallPluginAsync(PayPlugin("0.2.0", new Dictionary<string, string> { ["apiKey"] = "stripe" }, multiple: true));

        var bindings = await BindingsAsync("Billing");
        Assert.Equal(["stripe", "stripe#2"], bindings.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(existing, bindings["stripe"]);
        Assert.Empty(await OwnValuesAsync("Billing"));

        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Billing")).Type);
        Assert.Equal([MovedSecrets[2], MovedSecrets[0]],
            LastRequest()["connections"]!["stripe"]!.AsArray().Select(c => (string?)c!["fields"]!["apiKey"]));
        await AssertNoMovedSecretAsync();
    }

    // ---- (d) a failure partway -----------------------------------------------------------------

    [Fact]
    public async Task A_move_failing_partway_rolls_back_whole_and_the_member_keeps_its_own_value()
    {
        await InstallPluginAsync(PayPlugin("0.1.0", moved: null));
        await HireOwnAsync("Billing", new() { ["apiKey"] = MovedSecrets[0] });
        var connectionsBefore = (await GetAsync("/api/connections")).GetArrayLength();

        // THE LAST WRITE BEFORE THE ROWS FAILS: the connection and the binding were already written in
        // the transaction when the own value's removal is refused.
        await ExecuteAsync("CREATE TRIGGER refuse_move BEFORE DELETE ON plugin_member_secrets BEGIN SELECT RAISE(ABORT, 'forced failure'); END;");
        try
        {
            var answer = await InstallPluginAsync(PayPlugin("0.2.0", new Dictionary<string, string> { ["apiKey"] = "stripe" }));
            var move = Assert.Single(answer.GetProperty("moves").EnumerateArray());
            Assert.Equal("failed", move.GetProperty("outcome").GetString());
            Assert.Contains("The database refused the move", move.GetProperty("reason").GetString());
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER refuse_move;");
        }

        Assert.Equal(MovedSecrets[0], (await OwnValuesAsync("Billing"))["apiKey"]);
        Assert.Empty(await BindingsAsync("Billing"));
        Assert.Equal(connectionsBefore, (await GetAsync("/api/connections")).GetArrayLength());
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM credential_connections"));
        Assert.Equal(0L, await ScalarAsync($"SELECT COUNT(*) FROM team_connections WHERE team = '{_team}'"));

        var actions = await TenantActionsAsync();
        Assert.DoesNotContain(TenantActions.MemberSecretMoved, actions);
        Assert.DoesNotContain(TenantActions.ConnectionConnected, actions);
        Assert.Contains("\"fields\":[\"apiKey\"]", Assert.Single(await TenantDetailsAsync(TenantActions.MemberSecretMoveFailed)));

        // THE NEXT INSTALL MOVES IT, now that nothing refuses.
        await InstallPluginAsync(PayPlugin("0.2.0", new Dictionary<string, string> { ["apiKey"] = "stripe" }), replace: true);
        await AssertMovedAndRunsAsync("Billing", MovedSecrets[0], "Mail");
        await AssertNoMovedSecretAsync();
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
