using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Xunit;
using Yawble.Core.Agents;
using Yawble.Core.Bus;
using Yawble.Core.Launch;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Launch;

namespace Yawble.Core.Tests.Agents;

/// <summary>
/// The pickers' model list and a member's default model end to end, through the agents.* calls, with the real settings
/// reader over a temp home (~/.codex/config.toml names gpt-6.1-sol), the real usage_events reader over the test database
/// (runs used gpt-6.1-terra) and a CLI listing that fails. Fakes only: no CLI is run.
/// </summary>
public sealed class ModelListEndToEndTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _home = Directory.CreateTempSubdirectory("yawble-model-e2e-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private sealed class FailingListing : ICliModelListing
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<string>?> ListAsync(AgentCli cli, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("the CLI's listing failed");
        }
    }

    private sealed class NoTerminal : IUpdateTerminal
    {
        public void Open(string title, IReadOnlyList<string> command) => throw new InvalidOperationException("no terminal in this test");
    }

    private static void Usage(string connectionString, string run, string model, DateTimeOffset at)
    {
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO usage_events (at, run_key, member, vhost, path, status, model, output_tokens)
            VALUES ($at, $run, 'alpha/dev3', 'model.yawble.internal', '/v1/responses', 200, $model, 10);
            """;
        cmd.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$run", run);
        cmd.Parameters.AddWithValue("$model", model);
        cmd.ExecuteNonQuery();
    }

    private static List<(string Model, string From, string FromLabel)> Items(JsonNode? models) =>
        [.. models!["items"]!.AsArray().Select(i => (i!["model"]!.GetValue<string>(), i["from"]!.GetValue<string>(), i["fromLabel"]!.GetValue<string>()))];

    [Fact]
    public async Task Settings_runs_and_built_in_make_the_list_when_the_listing_fails_and_the_settings_model_is_the_members_default()
    {
        var codexDir = Path.Combine(_home, ".codex");
        Directory.CreateDirectory(codexDir);
        File.WriteAllText(Path.Combine(codexDir, "config.toml"), "model = \"gpt-6.1-sol\"\napproval_policy = \"never\"\n\n[profiles.fast]\nmodel = \"gpt-6.1-mini\"\n");
        var settings = new CliModelSettings(_home, _ => null);

        await using var kit = await LaunchKit.StartAsync(cliSettings: settings);
        await kit.Bus.AddMemberAsync("alpha", "dev3", "codex", Ct);
        var now = DateTimeOffset.UtcNow;
        using (Actor.Use(new Actor(Actor.Host, null)))
        {
            kit.Records.Started("r-codex", null, "alpha", "dev3", "codex", "headless", "fake");
            kit.Records.Started("r-grok", null, "alpha", "dev1", "grok", "headless", "fake");
        }
        Usage(kit.Db.ConnectionString, "r-codex", "gpt-6.1-terra", now.AddDays(-1));
        Usage(kit.Db.ConnectionString, "r-codex", "gpt-5-retired", now.AddDays(-120)); // older than 90 days
        Usage(kit.Db.ConnectionString, "r-grok", "grok-4.5-build", now.AddDays(-2));   // another CLI's run

        var listing = new FailingListing();
        var catalog = new ModelCatalog(listing, settings, new SqliteModelsUsed(kit.Db.ConnectionString));
        var rpc = new AgentInfoRpc(kit.Launcher, kit.Defaults, catalog, kit.Clis, kit.Records,
            new SqliteMemberDirectory(kit.Db.ConnectionString), new NoTerminal());
        var before = File.ReadAllText(Path.Combine(codexDir, "config.toml"));

        using (Actor.Use(Actor.Window))
        {
            // Codex: the listing failed, so the settings' model first, then the model runs used; the built-in list names
            // both already, so each is shown once with the first source that named it.
            var codex = await rpc.DispatchAsync("agents.models", new JsonObject { ["cli"] = "codex" }, Ct);
            Assert.Equal(
                [("gpt-6.1-sol", "settings", "your Codex settings"), ("gpt-6.1-terra", "runs", "your runs")],
                Items(codex));
            Assert.Equal(["gpt-6.1-sol", "gpt-6.1-terra"], codex!["models"]!.AsArray().Select(m => m!.GetValue<string>()));
            Assert.Equal("settings", codex["source"]!.GetValue<string>());
            Assert.DoesNotContain("gpt-5-retired", codex["models"]!.AsArray().Select(m => m!.GetValue<string>()));
            Assert.DoesNotContain("gpt-6.1-mini", codex["models"]!.AsArray().Select(m => m!.GetValue<string>())); // a profile's, not top-level

            // Grok: no settings file, so the runs' model, then the built-in list.
            var grok = Items(await rpc.DispatchAsync("agents.models", new JsonObject { ["cli"] = "grok" }, Ct));
            Assert.Equal(("grok-4.5-build", "runs", "your runs"), grok[0]);
            Assert.Equal(["grok-4.7-build", "grok-4.6-build"], grok.Skip(1).Select(i => i.Model));
            Assert.All(grok.Skip(1), i => Assert.Equal(("builtin", "built-in"), (i.From, i.FromLabel)));

            // Cached per CLI: asking again does not ask the listing; refresh does.
            var calls = listing.Calls;
            await rpc.DispatchAsync("agents.models", new JsonObject { ["cli"] = "codex" }, Ct);
            Assert.Equal(calls, listing.Calls);
            var refreshed = await rpc.DispatchAsync("agents.models", new JsonObject { ["cli"] = "codex", ["refresh"] = true }, Ct);
            Assert.Equal(calls + 1, listing.Calls);
            Assert.Equal("settings", refreshed!["source"]!.GetValue<string>());

            // A Codex member with no model and no Codex default: the config.toml model, said to be from the settings.
            Assert.Null(kit.Defaults.Get(AgentCli.Codex) is { Length: > 0 } d ? d : null);
            var resolved = await rpc.DispatchAsync("agents.resolveModel", new JsonObject { ["team"] = "alpha", ["member"] = "dev3" }, Ct);
            Assert.Equal(("gpt-6.1-sol", "settings", "Your Codex settings (gpt-6.1-sol)"),
                (resolved!["model"]!.GetValue<string>(), resolved["source"]!.GetValue<string>(), resolved["label"]!.GetValue<string>()));
            var byCli = await rpc.DispatchAsync("agents.resolveModel", new JsonObject { ["cli"] = "codex" }, Ct);
            Assert.Equal(("gpt-6.1-sol", "settings"), (byCli!["model"]!.GetValue<string>(), byCli["source"]!.GetValue<string>()));
        }

        // The run is launched with it, and the person's settings file is unchanged.
        Assert.Equal(new ModelResolution("gpt-6.1-sol", ModelSources.Settings), kit.Launcher.ResolveModel(new LaunchMember(0, "alpha", "dev3", AgentCli.Codex)));
        Assert.Equal(before, File.ReadAllText(Path.Combine(codexDir, "config.toml")));
        Assert.Equal(["config.toml"], Directory.GetFiles(codexDir).Select(Path.GetFileName));
    }
}
