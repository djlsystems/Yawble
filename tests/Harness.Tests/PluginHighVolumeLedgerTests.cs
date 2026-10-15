using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// A PLUGIN EVENT DECLARED <c>highVolume</c> STAYS OUT OF THE LEDGER, as <c>agentContainer.progress</c>
/// does: a firehose a plugin publishes once per item must not lengthen its member's next run. It stays
/// an ordinary row on the log, so a reload, a second window and Replay still show it. Any other plugin
/// event stays in the ledger exactly as before.
/// </summary>
public sealed class PluginHighVolumeLedgerTests : IDisposable
{
    private readonly string _dataRoot = Directory.CreateTempSubdirectory("harness-plugin-ledger-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    private static JsonObject Manifest() => PluginInstall.Manifest("fx",
        edit: m => m["events"] = JsonNode.Parse("""{"publishes":[{"type":"tick","highVolume":true},{"type":"done"}]}"""));

    [Fact]
    public void A_high_volume_plugin_event_is_declared_out_of_the_ledger_and_any_other_in_it()
    {
        var (manifest, refusal) = PluginManifest.Parse(Manifest().ToJsonString());

        Assert.Null(refusal);
        Assert.False(manifest!.Publishes.Single(e => e.Type == "tick").Definition("fx").InLedger);
        Assert.True(manifest.Publishes.Single(e => e.Type == "done").Definition("fx").InLedger);
    }

    [Fact]
    public async Task The_ledger_a_members_next_run_reads_leaves_out_a_high_volume_plugin_event_and_keeps_the_rest()
    {
        PluginInstall.Write(_dataRoot, "fx", manifest: Manifest());

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        var registry = factory.Services.GetRequiredService<TeamRegistry>();
        var team = (await registry.CreateAsync("Plugged", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        var member = new ContainerId(team, "Fx");

        var log = factory.Services.GetRequiredService<IMessageLog>();
        await log.AppendAsync(new NewMessage("plugin.fx.tick", """{"n":1}""", member.ToString()), Ct);
        await log.AppendAsync(new NewMessage("plugin.fx.done", """{"n":2}""", member.ToString()), Ct);
        await log.AppendAsync(new NewMessage(MessageTypes.Progress, """{"status":"working"}""", member.ToString()), Ct);

        // THE HOST'S OWN LEDGER, as its context builder reads it for the member's next run.
        var ledger = await factory.Services.GetRequiredService<ILedger>().ReadAsync(member, 0, null, long.MaxValue, 100, Ct);

        Assert.DoesNotContain(ledger, m => m.Type == "plugin.fx.tick");
        Assert.Contains(ledger, m => m.Type == "plugin.fx.done");
        Assert.DoesNotContain(ledger, m => m.Type == MessageTypes.Progress);

        // On the log all the same: the Monitor, a reload and Replay read it there.
        Assert.Single(await log.ReadAfterAsync(0, ["plugin.fx.tick"], 10, Ct));
    }

    [Fact]
    public async Task A_ledger_given_the_installed_plugins_reads_their_exclusions_on_every_query()
    {
        var database = Path.Combine(_dataRoot, "messages.db");
        await new SchemaMigrator(database).ApplyAsync(SchemaModules.All, Ct);
        var store = new SqliteMessageStore(database);
        var member = new ContainerId("Alpha", "Fx");

        await store.AppendAsync(new NewMessage("plugin.fx.tick", "{}", member.ToString()), Ct);
        await store.AppendAsync(new NewMessage("plugin.fx.done", "{}", member.ToString()), Ct);

        var catalog = new PluginCatalog(PluginInstall.PluginsRoot(_dataRoot));
        var ledger = new SqliteLedger(database, catalog);

        // Nothing installed yet: nothing of the plugin's is excluded.
        Assert.Equal(2, (await ledger.ReadAsync(member, 0, null, long.MaxValue, 100, Ct)).Count);

        PluginInstall.Write(_dataRoot, "fx", manifest: Manifest());
        catalog.Rescan();

        Assert.Equal(["plugin.fx.done"], (await ledger.ReadAsync(member, 0, null, long.MaxValue, 100, Ct)).Select(m => m.Type));
    }
}
