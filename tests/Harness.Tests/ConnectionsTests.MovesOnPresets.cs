using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// An independent tester's checks of which preset a moved connection takes, now that Namecheap's
/// preset has a required whitelisted IP: the IP is never made up, the connection reads Needs you until
/// a person saves it, a plugin holding its own IP moves all three, and a lone API key stays Other.
/// </summary>
public sealed partial class ConnectionsTests
{
    private static readonly Dictionary<string, string> MovesNamecheapPair = new() { ["apiUser"] = "namecheap", ["apiKey"] = "namecheap" };

    /// <summary>A Namecheap-shaped plugin whose manifest is changed by <paramref name="edit"/> on its secrets.</summary>
    private string PresetPlugin(string version, IReadOnlyDictionary<string, string>? moved, Action<JsonObject> edit)
    {
        var folder = PayPlugin(version, moved, slot: "namecheap", unmasked: true);
        var path = Path.Combine(folder, PluginManifest.FileName);
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(manifest["secrets"]!.AsObject());
        File.WriteAllText(path, manifest.ToJsonString());
        return folder;
    }

    private async Task<JsonElement> ListedAsync(string id) =>
        (await GetAsync("/api/connections")).EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);

    [Fact]
    public async Task Verify_a_namecheap_pair_makes_one_needs_you_connection_with_no_ip_until_a_person_saves_one()
    {
        await InstallPluginAsync(PayPlugin("0.1.0", moved: null, slot: "namecheap", unmasked: true));
        await HireOwnAsync("Domains", new() { ["apiUser"] = "acme-dns", ["apiKey"] = MovedSecrets[1] });
        var before = (await GetAsync("/api/connections")).GetArrayLength();

        var answer = await InstallPluginAsync(PayPlugin("0.2.0", MovesNamecheapPair, slot: "namecheap", unmasked: true));
        Assert.Equal("moved", Assert.Single(answer.GetProperty("moves").EnumerateArray()).GetProperty("outcome").GetString());

        // ONE CONNECTION, Namecheap's, named after team and member, assigned and bound; no own value left.
        Assert.Equal(before + 1, (await GetAsync("/api/connections")).GetArrayLength());
        Assert.Empty(await OwnValuesAsync("Domains"));
        var bound = Assert.Single(await BindingsAsync("Domains"));
        Assert.Equal("namecheap", bound.Key);
        Assert.Contains(bound.Value, await Services.GetRequiredService<Connections>().AssignedIdsAsync(_team, Ct));
        var connection = (await Services.GetRequiredService<ConnectionStore>().GetAsync(bound.Value, Ct))!;
        Assert.Equal(("namecheap", "Mail - Domains", ConnectionKinds.ApiKey), (connection.Credential!.Preset, connection.Name, connection.Kind));

        // THE IP IS EMPTY: not set, no value, in the store and in the answer a person reads.
        var ip = Assert.Single(connection.Credential.Fields, f => f.Name == "clientIp");
        Assert.Equal((false, (string?)null), (ip.Set, ip.Value));

        // NEEDS YOU, through the status an incomplete connection already shows.
        var listed = await ListedAsync(bound.Value);
        Assert.Equal("needs-reconnect", listed.GetProperty("status").GetString());
        Assert.Equal("`clientIp` (Whitelisted IP) is required.", listed.GetProperty("statusReason").GetString());
        Assert.DoesNotContain("clientIp\":\"", listed.GetRawText().Replace(" ", ""), StringComparison.Ordinal);

        // A RUN IS REFUSED, and no request reached the plugin.
        var refused = await TellAndAwaitAsync("Domains");
        Assert.Equal(MessageTypes.Failed, refused.Type);
        Assert.Contains("needs you: `clientIp` (Whitelisted IP) is required.", Words(refused));

        // A PERSON SAVES THE IP: ok, and the run holds all three, the two moved values unchanged.
        await EditCredentialAsync(bound.Value, ("clientIp", "198.51.100.4", null));
        listed = await ListedAsync(bound.Value);
        Assert.Equal("ok", listed.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("statusReason").ValueKind);
        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Domains")).Type);
        var fields = LastRequest()["connections"]!["namecheap"]!["fields"]!;
        Assert.Equal(("acme-dns", MovedSecrets[1], "198.51.100.4"),
            ((string?)fields["apiUser"], (string?)fields["apiKey"], (string?)fields["clientIp"]));

        // THE ROW NAMES THE PRESET; the move rows name each field once.
        var connected = JsonDocument.Parse(Assert.Single(await TenantDetailsAsync(TenantActions.ConnectionConnected))).RootElement;
        Assert.Equal("namecheap", connected.GetProperty("preset").GetString());
        Assert.Equal(["apiKey", "apiUser"], (await TenantDetailsAsync(TenantActions.MemberSecretMoved))
            .Select(d => JsonDocument.Parse(d).RootElement.GetProperty("field").GetString()).Order());

        await GetAsync("/api/tenant-log?take=200");
        await AssertNoMovedSecretAsync();
    }

    [Fact]
    public async Task Verify_a_plugin_holding_its_own_ip_moves_all_three_and_the_connection_is_ok()
    {
        static void WithIp(JsonObject secrets) => secrets["clientIp"] = new JsonObject { ["description"] = "The IP.", ["masked"] = false };

        await InstallPluginAsync(PresetPlugin("0.1.0", moved: null, WithIp));
        await HireOwnAsync("Domains", new() { ["apiUser"] = "acme-dns", ["apiKey"] = MovedSecrets[1], ["clientIp"] = "192.0.2.9" });

        await InstallPluginAsync(PresetPlugin("0.2.0", MovesNamecheapPair, secrets =>
        {
            WithIp(secrets);
            secrets["clientIp"]!["movedTo"] = "namecheap";
        }));

        Assert.Empty(await OwnValuesAsync("Domains"));
        var bound = Assert.Single(await BindingsAsync("Domains"));
        var connection = (await Services.GetRequiredService<ConnectionStore>().GetAsync(bound.Value, Ct))!;
        Assert.Equal("namecheap", connection.Credential!.Preset);
        Assert.All(connection.Credential.Fields, f => Assert.True(f.Set, f.Name));

        var listed = await ListedAsync(bound.Value);
        Assert.Equal("ok", listed.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("statusReason").ValueKind);

        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Domains")).Type);
        var fields = LastRequest()["connections"]!["namecheap"]!["fields"]!;
        Assert.Equal(("acme-dns", MovedSecrets[1], "192.0.2.9"),
            ((string?)fields["apiUser"], (string?)fields["apiKey"], (string?)fields["clientIp"]));
        Assert.Equal(3, (await TenantDetailsAsync(TenantActions.MemberSecretMoved)).Count);
        await AssertNoMovedSecretAsync();
    }

    [Fact]
    public async Task Verify_a_lone_api_key_stays_other_and_reads_ok()
    {
        await InstallPluginAsync(PayPlugin("0.1.0", moved: null));
        await HireOwnAsync("Billing", new() { ["apiKey"] = MovedSecrets[0] });

        await InstallPluginAsync(PayPlugin("0.2.0", MovesKey));

        var bound = Assert.Single(await BindingsAsync("Billing"));
        var connection = (await Services.GetRequiredService<ConnectionStore>().GetAsync(bound.Value, Ct))!;
        Assert.Equal(CredentialPresets.Other, connection.Credential!.Preset);
        Assert.Equal([("apiKey", true)], connection.Credential.Fields.Select(f => (f.Name, f.Masked)));
        Assert.Equal("ok", (await ListedAsync(bound.Value)).GetProperty("status").GetString());
        await AssertMovedAndRunsAsync("Billing", MovedSecrets[0], "Mail");
        await AssertNoMovedSecretAsync();
    }

    [Fact]
    public async Task Verify_a_pair_masked_unlike_namecheaps_stays_other_and_still_moves()
    {
        // apiUser MASKED, where Namecheap shows it: no preset holds it so, so the pair is Other.
        static void UserMasked(JsonObject secrets) => secrets["apiUser"]!["masked"] = true;

        await InstallPluginAsync(PresetPlugin("0.1.0", moved: null, UserMasked));
        await HireOwnAsync("Domains", new() { ["apiUser"] = "acme-dns", ["apiKey"] = MovedSecrets[1] });

        var answer = await InstallPluginAsync(PresetPlugin("0.2.0", MovesNamecheapPair, UserMasked));
        var move = Assert.Single(answer.GetProperty("moves").EnumerateArray());
        Assert.True(move.GetProperty("outcome").GetString() == "moved", move.GetRawText());

        Assert.Empty(await OwnValuesAsync("Domains"));
        var bound = Assert.Single(await BindingsAsync("Domains"));
        var connection = (await Services.GetRequiredService<ConnectionStore>().GetAsync(bound.Value, Ct))!;
        Assert.Equal(CredentialPresets.Other, connection.Credential!.Preset);
        Assert.Equal("ok", (await ListedAsync(bound.Value)).GetProperty("status").GetString());

        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Domains")).Type);
        var fields = LastRequest()["connections"]!["namecheap"]!["fields"]!;
        Assert.Equal(("acme-dns", MovedSecrets[1]), ((string?)fields["apiUser"], (string?)fields["apiKey"]));
        await AssertNoMovedSecretAsync();
    }

    [Fact]
    public async Task Verify_a_needs_you_move_failing_partway_leaves_no_connection_and_the_values_stay()
    {
        await InstallPluginAsync(PayPlugin("0.1.0", moved: null, slot: "namecheap", unmasked: true));
        await HireOwnAsync("Domains", new() { ["apiUser"] = "acme-dns", ["apiKey"] = MovedSecrets[1] });
        var before = (await GetAsync("/api/connections")).GetArrayLength();

        await ExecuteAsync("CREATE TRIGGER refuse_move BEFORE DELETE ON plugin_member_secrets BEGIN SELECT RAISE(ABORT, 'forced failure'); END;");
        try
        {
            var answer = await InstallPluginAsync(PayPlugin("0.2.0", MovesNamecheapPair, slot: "namecheap", unmasked: true));
            Assert.Equal("failed", Assert.Single(answer.GetProperty("moves").EnumerateArray()).GetProperty("outcome").GetString());
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER refuse_move;");
        }

        Assert.Equal(("acme-dns", MovedSecrets[1]), ((await OwnValuesAsync("Domains"))["apiUser"], (await OwnValuesAsync("Domains"))["apiKey"]));
        Assert.Empty(await BindingsAsync("Domains"));
        Assert.Equal(before, (await GetAsync("/api/connections")).GetArrayLength());
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM credential_connections"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM connections WHERE status = 'needs-reconnect'"));
        Assert.Contains("\"fields\":[\"apiKey\",\"apiUser\"]", Assert.Single(await TenantDetailsAsync(TenantActions.MemberSecretMoveFailed)));
        await GetAsync("/api/tenant-log?take=200");
        await AssertNoMovedSecretAsync();
    }

    [Fact]
    public async Task Verify_a_second_install_of_the_pair_keeps_the_needs_you_connection_and_makes_no_other()
    {
        await InstallPluginAsync(PayPlugin("0.1.0", moved: null, slot: "namecheap", unmasked: true));
        await HireOwnAsync("Domains", new() { ["apiUser"] = "acme-dns", ["apiKey"] = MovedSecrets[1] });
        await InstallPluginAsync(PayPlugin("0.2.0", MovesNamecheapPair, slot: "namecheap", unmasked: true));
        var count = (await GetAsync("/api/connections")).GetArrayLength();
        var first = Assert.Single(await BindingsAsync("Domains")).Value;

        var again = await InstallPluginAsync(PayPlugin("0.2.0", MovesNamecheapPair, slot: "namecheap", unmasked: true), replace: true);

        Assert.Equal("nothing", Assert.Single(again.GetProperty("moves").EnumerateArray()).GetProperty("outcome").GetString());
        Assert.Equal(count, (await GetAsync("/api/connections")).GetArrayLength());
        Assert.Equal(first, Assert.Single(await BindingsAsync("Domains")).Value);
        Assert.Single(await TenantDetailsAsync(TenantActions.ConnectionConnected));
    }
}
