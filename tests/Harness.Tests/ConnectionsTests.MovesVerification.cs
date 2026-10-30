using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Harness.Tests;

/// <summary>
/// CON 7, INDEPENDENT VERIFICATION (card 23041): the paths and edges the move's own tests do not take -
/// the operator CLI's install request as a Replace, a second solution update to the same version, a
/// team with several members and a member of the same name on another team, a connection name already
/// taken, a slot kept twice, and the answers a person reads scanned for the moved value.
/// </summary>
public sealed partial class ConnectionsTests
{
    private static readonly Dictionary<string, string> MovesKey = new() { ["apiKey"] = "stripe" };

    private async Task AnswerCliInstallAsync(string folder, bool replace)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var plugins = PluginInstall.PluginsRoot(_dataRoot);
        var report = Path.Combine(plugins, PluginRescanRequests.InstallReportFile);
        await File.WriteAllTextAsync(Path.Combine(plugins, PluginRescanRequests.InstallRequestFile),
            JsonSerializer.Serialize(new { request = nonce, path = folder, replace }), Ct);

        await Services.GetServices<IHostedService>().OfType<PluginRescanRequests>().Single().AnswerInstallAsync(Ct);
        await WaitUntilAsync(() => File.Exists(report) && File.ReadAllText(report).Contains(nonce));
        _responses.Add(await File.ReadAllTextAsync(report, Ct));
    }

    /// <summary>Every answer a person reads about the result, kept for the no-value scan.</summary>
    private async Task ReadEveryAnswerAsync(params (string Team, string Member)[] members)
    {
        await GetAsync("/api/connections");
        await GetAsync("/api/tenant-log?take=200");
        foreach (var (team, member) in members)
        {
            await GetAsync($"/api/teams/{team}/members/{member}/plugin-settings");
            await GetAsync($"/api/teams/{team}/connections");
        }
    }

    [Fact]
    public async Task Verify_the_operator_clis_install_request_as_a_replace_of_the_same_version_moves_the_key()
    {
        await InstallPluginAsync(PayPlugin("0.2.0", moved: null));
        await HireOwnAsync("Billing", new() { ["apiKey"] = MovedSecrets[0] });

        // `plugin install --from-instance --force`: the same version, replaced, now moving the key.
        await AnswerCliInstallAsync(PayPlugin("0.2.0", MovesKey), replace: true);
        await WaitUntilAsync(() => TenantDetailsAsync(TenantActions.MemberSecretMoved).Result.Count == 1);

        await AssertMovedAndRunsAsync("Billing", MovedSecrets[0], "Mail");
        await ReadEveryAnswerAsync((_team, "Billing"));
        await AssertNoMovedSecretAsync();
    }

    [Fact]
    public async Task Verify_several_members_on_one_team_and_one_on_another_team_each_get_their_own_connection()
    {
        // NOT REQUIRED in the old version, so a member may be hired with no value.
        await InstallPluginAsync(PayPlugin("0.1.0", moved: null, required: false));
        var other = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Shop", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await HireOwnAsync("Billing", new() { ["apiKey"] = MovedSecrets[0] });
        await HireOwnAsync("Refunds", new() { ["apiKey"] = MovedSecrets[2] });
        await HireOwnAsync("Empty", []);
        await HireOwnAsync("Billing", new() { ["apiKey"] = MovedSecrets[3] }, team: other);

        var answer = await InstallPluginAsync(PayPlugin("0.2.0", MovesKey));
        var outcomes = answer.GetProperty("moves").EnumerateArray()
            .Select(m => $"{m.GetProperty("team").GetString()}/{m.GetProperty("member").GetString()}:{m.GetProperty("outcome").GetString()}")
            .Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            new[] { $"{_team}/Billing:moved", $"{_team}/Empty:nothing", $"{_team}/Refunds:moved", $"{other}/Billing:moved" }.Order(StringComparer.Ordinal),
            outcomes);

        await AssertMovedAndRunsAsync("Billing", MovedSecrets[0], "Mail");
        await AssertMovedAndRunsAsync("Refunds", MovedSecrets[2], "Mail");
        await AssertMovedAndRunsAsync("Billing", MovedSecrets[3], "Shop", other);

        // THREE DISTINCT CONNECTIONS, each assigned only to its own member's team.
        var ids = new[]
        {
            (await BindingsAsync("Billing"))["stripe"], (await BindingsAsync("Refunds"))["stripe"], (await BindingsAsync("Billing", other))["stripe"],
        };
        Assert.Equal(3, ids.Distinct().Count());
        var connections = Services.GetRequiredService<Connections>();
        Assert.DoesNotContain(ids[2], await connections.AssignedIdsAsync(_team, Ct));
        Assert.DoesNotContain(ids[0], await connections.AssignedIdsAsync(other, Ct));

        // The member with no value made nothing and waits for a person.
        Assert.Empty(await BindingsAsync("Empty"));
        var waits = Services.GetRequiredService<Harness.Containers.ContainerHost>().Find(new ContainerId(_team, "Empty"))!.Snapshot().NeedsPerson;
        Assert.Contains("slot `stripe`", waits);

        Assert.Equal(3, (await TenantDetailsAsync(TenantActions.MemberSecretMoved)).Count);
        await ReadEveryAnswerAsync((_team, "Billing"), (_team, "Refunds"), (other, "Billing"));
        await AssertNoMovedSecretAsync();
    }

    [Fact]
    public async Task Verify_a_taken_connection_name_gets_a_suffix_and_the_move_still_lands()
    {
        await AddCredentialAsync("apikey", "other", "Mail - Billing", ("apiKey", "unrelated-key-value", null));
        await InstallPluginAsync(PayPlugin("0.1.0", moved: null));
        await HireOwnAsync("Billing", new() { ["apiKey"] = MovedSecrets[0] });

        await InstallPluginAsync(PayPlugin("0.2.0", MovesKey));

        Assert.Empty(await OwnValuesAsync("Billing"));
        var bound = (await BindingsAsync("Billing"))["stripe"];
        Assert.Equal("Mail - Billing (2)", (await Services.GetRequiredService<ConnectionStore>().GetAsync(bound, Ct))!.Name);
        await AssertNoMovedSecretAsync();
    }

    [Fact]
    public async Task Verify_a_kept_slot_stays_kept_on_every_later_install_and_nothing_is_made()
    {
        var existing = await AddCredentialAsync("apikey", "other", "Stripe shared", ("apiKey", MovedSecrets[2], null));
        PayPlugin("0.1.0", MovesKey, folder: Path.Combine(PluginInstall.PluginsRoot(_dataRoot), "pay", "0.1.0"));
        await File.WriteAllTextAsync(Path.Combine(PluginInstall.PluginsRoot(_dataRoot), "pay", PluginCatalog.ActiveFile), "0.1.0\n", Ct);
        await Services.GetRequiredService<PluginCatalog>().RescanAsync(Ct);

        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Billing", agent = "plugin:pay",
            connections = new Dictionary<string, string> { ["stripe"] = existing },
            secretValues = new Dictionary<string, string> { ["apiKey"] = MovedSecrets[0] },
        }, Ct);
        Assert.True(hired.IsSuccessStatusCode, await hired.Content.ReadAsStringAsync(Ct));
        var connectionsBefore = (await GetAsync("/api/connections")).GetArrayLength();

        var folder = PayPlugin("0.2.0", MovesKey);
        await InstallPluginAsync(folder);
        var again = await InstallPluginAsync(folder, replace: true);
        Assert.Equal("kept", Assert.Single(again.GetProperty("moves").EnumerateArray()).GetProperty("outcome").GetString());

        Assert.Equal([KeyValuePair.Create("stripe", existing)], await BindingsAsync("Billing"));
        Assert.Equal(MovedSecrets[0], (await OwnValuesAsync("Billing"))["apiKey"]);
        Assert.Equal(connectionsBefore, (await GetAsync("/api/connections")).GetArrayLength());

        // One kept row per install that met the binding: a person reads it again each time.
        Assert.Equal(2, (await TenantDetailsAsync(TenantActions.MemberSecretMoveKept)).Count);
        await ReadEveryAnswerAsync((_team, "Billing"));
        await AssertNoMovedSecretAsync();
    }

    [Fact]
    public async Task Verify_a_second_solution_update_to_the_same_version_moves_nothing_twice()
    {
        const string Plugin = "paysol";
        var (status, text) = await SendAsync(HttpMethod.Post, "/api/solutions/install", new
        {
            folder = Package("1.0.0", moved: false),
            teamName = "Payments",
            secretValues = new Dictionary<string, Dictionary<string, string>> { ["Keeper"] = new() { ["apiKey"] = MovedSecrets[0] } },
        });
        Assert.True(status == HttpStatusCode.OK, text);
        var team = JsonDocument.Parse(text).RootElement.GetProperty("team").GetString()!;

        var newer = Package("1.1.0", moved: true);
        (status, text) = await SendAsync(HttpMethod.Post, "/api/solutions/update", new { folder = newer, team });
        Assert.True(status == HttpStatusCode.OK && JsonDocument.Parse(text).RootElement.GetProperty("ok").GetBoolean(), text);
        await AssertMovedAndRunsAsync("Keeper", MovedSecrets[0], "Payments", team);
        var first = (await BindingsAsync("Keeper", team))["stripe"];

        // THE SAME UPDATE AGAIN, from the same package and a fresh copy of it: answered either way, and
        // nothing is moved, made or bound a second time.
        (status, text) = await SendAsync(HttpMethod.Post, "/api/solutions/update", new { folder = newer, team });
        _responses.Add(text);
        (status, text) = await SendAsync(HttpMethod.Post, "/api/solutions/update", new { folder = Package("1.1.0", moved: true), team });
        _responses.Add(text);

        Assert.Equal([KeyValuePair.Create("stripe", first)], await BindingsAsync("Keeper", team));
        Assert.Single(await TenantDetailsAsync(TenantActions.MemberSecretMoved));
        Assert.Single(await TenantDetailsAsync(TenantActions.ConnectionConnected));
        await AssertMovedAndRunsAsync("Keeper", MovedSecrets[0], "Payments", team);
        await ReadEveryAnswerAsync((team, "Keeper"));
        await AssertNoMovedSecretAsync();

        string Package(string version, bool moved)
        {
            var folder = SolutionSamples.Neutral(Path.Combine(_dataRoot, "packages", Guid.NewGuid().ToString("N")), Plugin, [], [], version);
            var plugin = Path.Combine(folder, "plugins", Plugin);
            Directory.Delete(plugin, recursive: true);
            PayPlugin(moved ? "0.2.0" : "0.1.0", moved ? MovesKey : null, id: Plugin, folder: plugin);
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

    [Fact]
    public async Task Verify_a_failed_move_leaves_no_value_in_any_answer_or_row()
    {
        await InstallPluginAsync(PayPlugin("0.1.0", moved: null));
        await HireOwnAsync("Billing", new() { ["apiKey"] = MovedSecrets[0] });

        // The binding write itself refused, earlier than the developer's own case (the removal).
        await ExecuteAsync("CREATE TRIGGER refuse_bind BEFORE INSERT ON credential_connections BEGIN SELECT RAISE(ABORT, 'forced failure'); END;");
        try
        {
            var answer = await InstallPluginAsync(PayPlugin("0.2.0", MovesKey));
            Assert.Equal("failed", Assert.Single(answer.GetProperty("moves").EnumerateArray()).GetProperty("outcome").GetString());
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER refuse_bind;");
        }

        Assert.Equal(MovedSecrets[0], (await OwnValuesAsync("Billing"))["apiKey"]);
        Assert.Empty(await BindingsAsync("Billing"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM connections"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM credential_connections"));
        Assert.DoesNotContain(TenantActions.ConnectionConnected, await TenantActionsAsync());
        await ReadEveryAnswerAsync((_team, "Billing"));
        await AssertNoMovedSecretAsync();
    }
}
