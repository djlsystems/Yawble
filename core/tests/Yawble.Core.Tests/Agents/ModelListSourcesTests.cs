using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Xunit;
using Yawble.Core.Agents;
using Yawble.Core.Agents.Profiles;
using Yawble.Core.Bus;
using Yawble.Core.Launch;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Launch;

namespace Yawble.Core.Tests.Agents;

/// <summary>
/// The pickers' model lists (the CLI's own listing, the person's CLI settings, the models runs used, the built-in list) and
/// the CLI settings' model as the default a member runs with. Fakes only: a fake runner and fake settings files in a temp
/// home; no real CLI is run.
/// </summary>
public sealed class ModelListSourcesTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _home = Directory.CreateTempSubdirectory("yawble-model-home-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private string Put(string relative, string text)
    {
        var path = Path.Combine(_home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private CliModelSettings Settings(Dictionary<string, string>? env = null) => new(_home, n => env is not null && env.TryGetValue(n, out var v) ? v : null);

    private sealed class FakeListing(Func<AgentCli, IReadOnlyList<string>?> answer) : ICliModelListing
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<string>?> ListAsync(AgentCli cli, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(answer(cli));
        }
    }

    private sealed class ThrowingListing : ICliModelListing
    {
        public Task<IReadOnlyList<string>?> ListAsync(AgentCli cli, CancellationToken ct) => throw new InvalidOperationException("listing broke");
    }

    private sealed class FakeSettings(string? model) : ICliModelSettings
    {
        public int Calls { get; private set; }

        public string? DefaultModel(AgentCli cli)
        {
            Calls++;
            return model;
        }
    }

    private sealed class FakeUsed(params string[] models) : IModelsUsed
    {
        public IReadOnlyList<string> Used(AgentCli cli, TimeSpan within) => models;
    }

    private sealed class FakeRunner(Func<CliProbeCommand, CliProbeResult> answer) : ICliProcessRunner
    {
        public List<CliProbeCommand> Commands { get; } = [];

        public Task<CliProbeResult> RunAsync(CliProbeCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult(answer(command));
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private const string CopilotHelp = """
        Usage: copilot [options] [command]

        GitHub Copilot CLI - An AI-powered coding assistant

        Options:
          --allow-all-tools                   Allow all tools to run automatically
          --model <model>                     Set the AI model to use (choices:
                                              "mai-code-2-flash", "claude-sonnet-5-5",
                                              "gpt-6.1-terra")
          -p, --prompt <text>                 Execute a prompt directly
          -h, --help                          display help for command
        """;

    // ------------------------------------------------------------------ the union and its order

    [Fact]
    public async Task The_list_is_the_cli_listing_then_settings_then_runs_then_built_in_each_model_once()
    {
        var catalog = new ModelCatalog(
            new FakeListing(_ => ["grok-4.7-build", "grok-4.6-build"]),
            new FakeSettings("grok-4.8-preview"),
            new FakeUsed("grok-4.6-build", "grok-4.5-build"));
        var listing = await catalog.ListAsync(AgentCli.Grok, false, Ct);

        Assert.Equal(["grok-4.7-build", "grok-4.6-build", "grok-4.8-preview", "grok-4.5-build"], listing.Models.Take(4));
        Assert.Equal(listing.Models.Count, listing.Models.Distinct().Count());
        Assert.Equal("cli", listing.Source);
        Assert.Equal(("cli", "the CLI"), (listing.Items[0].From, listing.Items[0].FromLabel));
        Assert.Equal(("cli", "the CLI"), (listing.Items[1].From, listing.Items[1].FromLabel)); // also in runs and built-in: shown once, as the CLI's
        Assert.Equal(("settings", "your Grok settings"), (listing.Items[2].From, listing.Items[2].FromLabel));
        Assert.Equal(("runs", "your runs"), (listing.Items[3].From, listing.Items[3].FromLabel));
        Assert.All(listing.Items.Skip(4), i => Assert.Equal(("builtin", "built-in"), (i.From, i.FromLabel)));
        Assert.Equal(listing.Models, listing.Items.Select(i => i.Model));
    }

    [Fact]
    public async Task Each_source_alone_is_listed_with_its_own_label_before_the_built_in_list()
    {
        var settingsOnly = await new ModelCatalog(settings: new FakeSettings("gpt-6.1-sol")).ListAsync(AgentCli.Codex, false, Ct);
        Assert.Equal(("gpt-6.1-sol", "settings", "your Codex settings"), (settingsOnly.Items[0].Model, settingsOnly.Items[0].From, settingsOnly.Items[0].FromLabel));
        Assert.Equal("settings", settingsOnly.Source);
        Assert.Equal(1, settingsOnly.Models.Count(m => m == "gpt-6.1-sol")); // also built in: once

        var runsOnly = await new ModelCatalog(used: new FakeUsed("mai-code-3")).ListAsync(AgentCli.Copilot, false, Ct);
        Assert.Equal(("mai-code-3", "runs", "your runs"), (runsOnly.Items[0].Model, runsOnly.Items[0].From, runsOnly.Items[0].FromLabel));

        var copilotSettings = await new ModelCatalog(settings: new FakeSettings("gpt-6.1-terra")).ListAsync(AgentCli.Copilot, false, Ct);
        Assert.Equal("your GitHub Copilot settings", copilotSettings.Items[0].FromLabel);
    }

    [Fact]
    public async Task The_built_in_lists_carry_the_current_names()
    {
        var catalog = new ModelCatalog();
        Assert.Contains("gpt-6.1-terra", (await catalog.ListAsync(AgentCli.Codex, false, Ct)).Models);
        Assert.Contains("gpt-6.1-sol", (await catalog.ListAsync(AgentCli.Codex, false, Ct)).Models);
        Assert.Contains("grok-4.6-build", (await catalog.ListAsync(AgentCli.Grok, false, Ct)).Models);
        Assert.Contains("grok-4.7-build", (await catalog.ListAsync(AgentCli.Grok, false, Ct)).Models);
        Assert.Contains("mai-code-2-flash", (await catalog.ListAsync(AgentCli.Copilot, false, Ct)).Models);
    }

    [Fact]
    public async Task The_list_is_kept_for_a_few_minutes_and_a_refresh_asks_again()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-24T10:00:00Z"));
        var listing = new FakeListing(_ => ["grok-4.7-build"]);
        var catalog = new ModelCatalog(listing, clock: clock);
        await catalog.ListAsync(AgentCli.Grok, false, Ct);
        await catalog.ListAsync(AgentCli.Grok, false, Ct);
        Assert.Equal(1, listing.Calls);
        await catalog.ListAsync(AgentCli.Codex, false, Ct); // per CLI
        Assert.Equal(2, listing.Calls);
        await catalog.ListAsync(AgentCli.Grok, true, Ct);
        Assert.Equal(3, listing.Calls);
        clock.Now += ModelCatalog.CacheFor + TimeSpan.FromSeconds(1);
        await catalog.ListAsync(AgentCli.Grok, false, Ct);
        Assert.Equal(4, listing.Calls);
    }

    // ------------------------------------------------------------------ a listing that fails

    [Fact]
    public async Task A_listing_that_times_out_or_fails_leaves_the_other_sources()
    {
        Put(Path.Combine(".copilot", "config.json"), """{ "model": "gpt-6.1-terra" }""");
        foreach (var result in new[]
                 {
                     new CliProbeResult(null, "", "", TimedOut: true),
                     new CliProbeResult(1, "", "error: not signed in", false),
                     new CliProbeResult(null, "", "", false, StartError: "file not found"),
                     new CliProbeResult(0, "no choices here", "", false),
                 })
        {
            var runner = new FakeRunner(_ => result);
            var cliListing = new CliModelListing(_home, _ => null, runner, _ => @"C:\fake\copilot.exe", timeout: TimeSpan.FromSeconds(2));
            Assert.Null(await cliListing.ListAsync(AgentCli.Copilot, Ct));
            Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(runner.Commands).Timeout);

            var listing = await new ModelCatalog(cliListing, Settings(), new FakeUsed("mai-code-2-flash")).ListAsync(AgentCli.Copilot, false, Ct);
            Assert.Equal(["settings", "runs"], listing.Items.Take(2).Select(i => i.From));
            Assert.Equal(["gpt-6.1-terra", "mai-code-2-flash"], listing.Models.Take(2));
        }

        var broken = await new ModelCatalog(new ThrowingListing(), new FakeSettings("gpt-6.1-sol")).ListAsync(AgentCli.Codex, false, Ct);
        Assert.Equal("settings", broken.Source);
        Assert.Contains("gpt-6.1-terra", broken.Models);
    }

    [Fact]
    public async Task Copilot_lists_the_model_choices_of_its_help_run_with_a_timeout()
    {
        var runner = new FakeRunner(_ => new CliProbeResult(0, CopilotHelp, "", false));
        var listing = new CliModelListing(_home, _ => null, runner, cli => cli == AgentCli.Copilot ? @"C:\fake\copilot.exe" : null);
        Assert.Equal(["mai-code-2-flash", "claude-sonnet-5-5", "gpt-6.1-terra"], await listing.ListAsync(AgentCli.Copilot, Ct));
        var command = Assert.Single(runner.Commands);
        Assert.Equal((@"C:\fake\copilot.exe", "--help"), (command.FileName, Assert.Single(command.Arguments)));
        Assert.Equal(CliModelListing.DefaultTimeout, command.Timeout);
        // A Node.js entry point, or no Copilot at all: nothing is run.
        Assert.Null(await new CliModelListing(_home, _ => null, runner, _ => @"C:\npm\copilot\index.js").ListAsync(AgentCli.Copilot, Ct));
        Assert.Null(await new CliModelListing(_home, _ => null, runner, _ => null).ListAsync(AgentCli.Copilot, Ct));
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task Codex_lists_its_models_cache_by_its_own_priority_and_runs_nothing()
    {
        Put(Path.Combine(".codex", "models_cache.json"), """
            {"fetched_at": "2026-11-24T09:00:00Z", "etag": "x", "models": [
              {"slug": "gpt-6.1-terra", "visibility": "list", "priority": 2},
              {"slug": "gpt-6.1-sol", "visibility": "list", "priority": 1},
              {"slug": "codex-internal-review", "visibility": "hide", "priority": 0},
              {"slug": "gpt-6.1-mini"}
            ]}
            """);
        var runner = new FakeRunner(_ => throw new InvalidOperationException("no CLI may run"));
        var listing = new CliModelListing(_home, _ => null, runner, _ => @"C:\fake\codex.exe");
        Assert.Equal(["gpt-6.1-sol", "gpt-6.1-terra", "gpt-6.1-mini"], await listing.ListAsync(AgentCli.Codex, Ct));
        Assert.Empty(runner.Commands);

        // CODEX_HOME wins over ~/.codex; a missing or broken cache is no listing.
        var elsewhere = Path.Combine(_home, "codex-home");
        Put(Path.Combine("codex-home", "models_cache.json"), "{ not json");
        Assert.Null(await new CliModelListing(_home, n => n == "CODEX_HOME" ? elsewhere : null, runner, _ => null).ListAsync(AgentCli.Codex, Ct));
    }

    [Fact]
    public async Task Grok_reuses_the_keepers_last_models_output_and_its_cache_without_running_grok()
    {
        Put(Path.Combine(".grok", "models_cache.json"), """{"models": [{"id": "grok-4.7-build"}, {"id": "grok-code-2"}]}""");
        var runner = new FakeRunner(_ => throw new InvalidOperationException("no CLI may run"));
        var printed = """
            Available models:
              * grok-4.7-build (default)
              - grok-4.6-build
            """;
        var listing = new CliModelListing(_home, _ => null, runner, _ => @"C:\fake\grok.exe", () => printed);
        Assert.Equal(["grok-4.7-build", "grok-4.6-build", "grok-code-2"], await listing.ListAsync(AgentCli.Grok, Ct));
        Assert.Empty(runner.Commands);
        // Antigravity and Claude Code have no listing.
        Assert.Null(await listing.ListAsync(AgentCli.Antigravity, Ct));
        Assert.Null(await listing.ListAsync(AgentCli.Claude, Ct));
    }

    // ------------------------------------------------------------------ the person's CLI settings

    [Fact]
    public void The_settings_files_are_read_for_their_top_level_model_and_never_written()
    {
        Put(Path.Combine(".codex", "config.toml"), """
            # my settings
            model = "gpt-6.1-sol"   # the one I use
            approval_policy = "never"

            [profiles.fast]
            model = "gpt-6.1-mini"
            """);
        Put(Path.Combine(".grok", "config.toml"), "default_model = 'grok-4.7-build'\n");
        Put(Path.Combine(".copilot", "settings.json"), """{ "model": "mai-code-2-flash", "theme": "dark", }""");
        Put(Path.Combine(".gemini", "antigravity-cli", "settings.json"), """{ "model": { "name": "gemini-3-pro" } }""");
        Put(Path.Combine(".claude", "settings.json"), """{ "model": "opus" }""");
        var before = Directory.GetFiles(_home, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText);

        var settings = Settings();
        Assert.Equal("gpt-6.1-sol", settings.DefaultModel(AgentCli.Codex));
        Assert.Equal("grok-4.7-build", settings.DefaultModel(AgentCli.Grok));
        Assert.Equal("mai-code-2-flash", settings.DefaultModel(AgentCli.Copilot));
        Assert.Equal("gemini-3-pro", settings.DefaultModel(AgentCli.Antigravity));
        Assert.Equal("opus", settings.DefaultModel(AgentCli.Claude));

        Assert.Equal(before, Directory.GetFiles(_home, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText));
    }

    [Fact]
    public void A_settings_file_with_no_top_level_model_or_a_bad_one_names_none()
    {
        var settings = Settings();
        Assert.Null(settings.DefaultModel(AgentCli.Codex)); // no file
        Put(Path.Combine(".codex", "config.toml"), "[profiles.fast]\nmodel = \"gpt-6.1-mini\"\n");
        Assert.Null(settings.DefaultModel(AgentCli.Codex)); // only in a table
        Put(Path.Combine(".codex", "config.toml"), "model = \"gpt 6; rm -rf\"\n");
        Assert.Null(settings.DefaultModel(AgentCli.Codex)); // not a model id
        Put(Path.Combine(".copilot", "config.json"), "{ broken");
        Assert.Null(settings.DefaultModel(AgentCli.Copilot));

        // CODEX_HOME is where Codex reads its config.toml from.
        var codexHome = Path.Combine(_home, "elsewhere");
        Put(Path.Combine("elsewhere", "config.toml"), "model = \"gpt-6.1-terra\"\n");
        Assert.Equal("gpt-6.1-terra", Settings(new() { ["CODEX_HOME"] = codexHome }).DefaultModel(AgentCli.Codex));
    }

    // ------------------------------------------------------------------ the models runs used

    [Fact]
    public void The_models_runs_used_come_from_usage_events_for_that_cli_over_the_last_90_days()
    {
        using var db = new TempDb();
        var now = DateTimeOffset.Parse("2026-11-24T12:00:00Z");
        var records = new RunRecordStore(db.ConnectionString);
        using (Actor.Use(new Actor(Actor.Host, null)))
        {
            records.Started("r-codex", null, "alpha", "dev1", "codex", "headless", "fake");
            records.Started("r-grok", null, "alpha", "dev2", "grok", "headless", "fake");
        }
        void Usage(string run, string model, DateTimeOffset at)
        {
            using var c = new SqliteConnection(db.ConnectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO usage_events (at, run_key, member, vhost, path, status, model, output_tokens)
                VALUES ($at, $run, 'alpha/dev', 'model.yawble.internal', '/v1/responses', 200, $model, 10);
                """;
            cmd.Parameters.AddWithValue("$at", at.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$run", run);
            cmd.Parameters.AddWithValue("$model", model);
            cmd.ExecuteNonQuery();
        }
        Usage("r-codex", "gpt-6.1-sol", now.AddDays(-3));
        Usage("r-codex", "gpt-6.1-terra", now.AddDays(-1));
        Usage("r-codex", "gpt-6.1-terra", now.AddDays(-2));
        Usage("r-codex", "gpt-5-old", now.AddDays(-91));       // too old
        Usage("r-grok", "grok-4.7-build", now.AddDays(-1));    // another CLI
        Usage("r-unknown", "gpt-6.2-elsewhere", now.AddDays(-1)); // no run record: no CLI to count it for

        var used = new SqliteModelsUsed(db.ConnectionString, new Clock(now));
        Assert.Equal(["gpt-6.1-terra", "gpt-6.1-sol"], used.Used(AgentCli.Codex, ModelCatalog.RunsWithin));
        Assert.Equal(["grok-4.7-build"], used.Used(AgentCli.Grok, ModelCatalog.RunsWithin));
        Assert.Empty(used.Used(AgentCli.Copilot, ModelCatalog.RunsWithin));
    }

    // ------------------------------------------------------------------ the settings' model as the default

    [Fact]
    public void Resolution_uses_the_settings_model_only_when_neither_the_member_nor_the_agent_names_one()
    {
        var settings = new FakeSettings("gpt-6.1-sol");
        Assert.Equal(new ModelResolution("gpt-6.1-mini", ModelSources.Member), AgentModels.Resolve("gpt-6.1-mini", "gpt-6.1-terra", AgentCli.Codex, settings));
        Assert.Equal(new ModelResolution("gpt-6.1-terra", ModelSources.Agent), AgentModels.Resolve("", "gpt-6.1-terra", AgentCli.Codex, settings));
        Assert.Equal(0, settings.Calls); // not read when something above it names a model
        var fromSettings = AgentModels.Resolve(null, "", AgentCli.Codex, settings);
        Assert.Equal(new ModelResolution("gpt-6.1-sol", ModelSources.Settings), fromSettings);
        Assert.Equal("Your Codex settings (gpt-6.1-sol)", AgentModels.Label(fromSettings, AgentCli.Codex));
        Assert.Equal(new ModelResolution("", ModelSources.Cli), AgentModels.Resolve(null, null, AgentCli.Codex, new FakeSettings(null)));
    }

    [Fact]
    public async Task A_codex_member_with_no_model_and_no_default_runs_the_config_toml_model()
    {
        Put(Path.Combine(".codex", "config.toml"), "model = \"gpt-6.1-sol\"\n");
        var settings = Settings();
        await using var kit = await LaunchKit.StartAsync(cliSettings: settings);
        await kit.Bus.AddMemberAsync("alpha", "dev3", "codex", Ct);
        var rpc = new AgentInfoRpc(kit.Launcher, kit.Defaults, new ModelCatalog(settings: settings), kit.Clis, kit.Records,
            new SqliteMemberDirectory(kit.Db.ConnectionString), new NoTerminal());

        // The model the member is launched with.
        Assert.Equal(new ModelResolution("gpt-6.1-sol", ModelSources.Settings), kit.Launcher.ResolveModel(new LaunchMember(0, "alpha", "dev3", AgentCli.Codex)));
        using (Actor.Use(Actor.Window))
        {
            var resolved = await rpc.DispatchAsync("agents.resolveModel", new JsonObject { ["team"] = "alpha", ["member"] = "dev3" }, Ct);
            Assert.Equal(("gpt-6.1-sol", "settings", "Your Codex settings (gpt-6.1-sol)"),
                (resolved!["model"]!.GetValue<string>(), resolved["source"]!.GetValue<string>(), resolved["label"]!.GetValue<string>()));
            var shown = await rpc.DispatchAsync("agents.commandLine", new JsonObject { ["team"] = "alpha", ["member"] = "dev3" }, Ct);
            Assert.Contains("gpt-6.1-sol", shown!["headless"]!["argv"]!.AsArray().Select(a => a!.GetValue<string>()));

            // An agent default outranks the settings.
            await rpc.DispatchAsync("agents.setDefaultModel", new JsonObject { ["cli"] = "codex", ["model"] = "gpt-6.1-terra" }, Ct);
            resolved = await rpc.DispatchAsync("agents.resolveModel", new JsonObject { ["team"] = "alpha", ["member"] = "dev3" }, Ct);
            Assert.Equal("agent", resolved!["source"]!.GetValue<string>());
            Assert.Equal("gpt-6.1-terra", kit.Launcher.ResolveModel(new LaunchMember(0, "alpha", "dev3", AgentCli.Codex)).Model);

            // A Claude member: no Claude settings file, so the CLI's own default.
            resolved = await rpc.DispatchAsync("agents.resolveModel", new JsonObject { ["team"] = "alpha", ["member"] = "dev1" }, Ct);
            Assert.Equal(("cli", "The CLI's default"), (resolved!["source"]!.GetValue<string>(), resolved["label"]!.GetValue<string>()));

            var models = await rpc.DispatchAsync("agents.models", new JsonObject { ["cli"] = "codex", ["refresh"] = true }, Ct);
            var first = models!["items"]!.AsArray()[0]!;
            Assert.Equal(("gpt-6.1-sol", "settings", "your Codex settings"),
                (first["model"]!.GetValue<string>(), first["from"]!.GetValue<string>(), first["fromLabel"]!.GetValue<string>()));
            Assert.Equal("settings", models["source"]!.GetValue<string>());
            Assert.Equal("gpt-6.1-sol", models["models"]!.AsArray()[0]!.GetValue<string>());
        }
    }

    [Fact]
    public void The_member_pane_and_the_change_log_name_the_settings_model()
    {
        using var db = new TempDb();
        var defaults = new AgentDefaultsStore(db.ConnectionString);
        var settings = new FakeSettings("gpt-6.1-sol");
        AgentProfileStore? store = null;
        var log = new AgentChangeLog(db.ConnectionString, null, (a, m) => store!.ResolveModel(a, m),
            agent => store!.CliOf(agent) is { } cli && settings.DefaultModel(cli) is { } m ? (m, $"model from your {cli.DisplayName()} settings") : null);
        store = new AgentProfileStore(db.ConnectionString, log, defaults: defaults);

        var models = new MemberModels(db.ConnectionString, store, defaults, settings);
        Assert.Equal(new MemberModel("gpt-6.1-sol", ModelSources.Settings),
            models.For(new MemberRow(1, 1, "alpha", "dev3", "member", "codex", "", "", "default")));

        var change = log.RecordSubjectChange(AgentChangeLog.KindMemberAgent, AgentChangeLog.MemberSubject("alpha", "dev3"),
            new AgentSetting("claude", ""), new AgentSetting("codex", ""), AgentChangeLog.HowSettings, new Actor(Actor.App, null));
        Assert.Equal(("gpt-6.1-sol", "model from your Codex settings"), (change!.EffectiveModel, change.Detail));

        var named = log.RecordSubjectChange(AgentChangeLog.KindMemberAgent, AgentChangeLog.MemberSubject("alpha", "dev3"),
            new AgentSetting("codex", ""), new AgentSetting("codex", "gpt-6.1-terra"), AgentChangeLog.HowSettings, new Actor(Actor.App, null));
        Assert.Equal(("gpt-6.1-terra", (string?)null), (named!.EffectiveModel, named.Detail));
    }

    private sealed class NoTerminal : IUpdateTerminal
    {
        public void Open(string title, IReadOnlyList<string> command) => throw new InvalidOperationException("no terminal in this test");
    }
}
