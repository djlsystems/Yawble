using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// THE CONCIERGE OPENS ON A FRESH INSTANCE. It is what a person types to first, so with nobody
/// having chosen an agent or a prompt it must launch the seeded Concierge prompt on an agent that is
/// actually installed - not refuse, and not pick the first preset in the catalog whether or not it
/// is on PATH.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ConciergeDefaultsTests : IDisposable
{
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"harness-concierge-test-{Guid.NewGuid():N}");

    public ConciergeDefaultsTests() => Directory.CreateDirectory(_dataRoot);

    [Fact]
    public async Task A_fresh_instance_opens_the_built_in_prompt_on_an_installed_agent()
    {
        var catalog = new AgentCatalog(
            [.. AgentCatalogFile.BuiltIns(), .. AgentCatalogFile.LoadCustom(_dataRoot, TextWriter.Null)]);

        // Nothing built-in is written to the volume.
        Assert.False(File.Exists(AgentCatalogFile.PathIn(_dataRoot)));

        // Exactly one interactive language-model preset is installed, and it is NOT the first in
        // the catalog - so "first that exists" and "first that is installed" give different answers.
        var interactive = catalog.Definitions
            .Where(d => d.Mode == AgentMode.Interactive && !d.Hidden && d.Launch.LanguageModel)
            .ToList();
        Assert.True(interactive.Count >= 2, "The seed needs two interactive presets for this test to mean anything.");
        var installed = interactive[1];
        Assert.NotEqual(interactive[0].Launch.FileName, installed.Launch.FileName);

        var bin = Path.Combine(_dataRoot, "bin");
        Directory.CreateDirectory(bin);
        var executable = Path.Combine(bin, installed.Launch.FileName);
        await TestExecutable.WriteAsync(executable, "#!/bin/sh\nexit 1\n");

        // Nothing authenticated: no provider key and no saved login under HOME, so the probe's
        // first preference is empty and "installed" is what decides.
        using var restore = new EnvironmentScope(
        [
            new("PATH", bin),
            new("HOME", _dataRoot),
            new("ANTHROPIC_API_KEY", ""),
            new("OPENAI_API_KEY", ""),
            new("GH_TOKEN", ""),
            new("XAI_API_KEY", ""),
            new("GEMINI_API_KEY", ""),
        ]);

        // WHAT A FRESH INSTANCE STORES, not a literal null: the launch in Program.cs passes the
        // stored agent, so a seed that names a preset would never reach the default below.
        var database = Path.Combine(_dataRoot, "messages.db");
        await new SchemaMigrator(database).ApplyAsync(SchemaModules.All, ct: TestContext.Current.CancellationToken);
        var stored = await new SqliteTeamStore(database).ConciergeSettingsAsync(TestContext.Current.CancellationToken);
        Assert.Null(stored.Agent);

        var agent = await ConciergeAgentDefault.ResolveAsync(
            stored.Agent, catalog, new AgentAuthProbe(catalog), TestContext.Current.CancellationToken);

        Assert.Equal(installed.Name, agent);

        var factory = new ConciergeLaunchFactory(
            new TeamPaths(_dataRoot), "http://localhost:5000", new MintingPrincipals(), catalog);

        var spec = await factory.ForAsync(
            team: "", teamLabel: "this instance", user: "user-1", login: "person@example.com",
            agent, teamEnv: new Dictionary<string, string>(),
            steeringCausation: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(installed.Launch.FileName, spec.Argv![0]);

        var command = catalog.Interactive(agent)!;
        var promptPath = spec.TempFiles is { Count: > 0 } temp
            ? temp[0]
            : Path.Combine(spec.StartingFolder, command.InstructionsFile!);

        var written = await File.ReadAllTextAsync(promptPath, TestContext.Current.CancellationToken);

        Assert.StartsWith(BuiltInPrompts.For(SkillRoles.Concierge).TrimEnd(), written, StringComparison.Ordinal);
        Assert.Contains(BuiltInPrompts.AvailableSkillsHeading, written, StringComparison.Ordinal);
        Assert.Contains("- `concierge` - ", written, StringComparison.Ordinal);
        Assert.DoesNotContain("- `worktrees` - ", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Under_issued_the_concierge_keeps_its_home_and_tools_and_gains_the_credential()
    {
        const string Key = "fake-concierge-issued-key-4e2";
        var catalog = new AgentCatalog(AgentCatalogFile.BuiltIns());
        var declaration = catalog.Definition("claude")!.IssuedCredential!;

        using var restore = new EnvironmentScope([new("HOME", _dataRoot)]);

        async Task<Harness.Pty.PtySpec> OpenAsync(RunCredential credential) =>
            await new ConciergeLaunchFactory(
                    new TeamPaths(_dataRoot), "http://localhost:5000", new MintingPrincipals(), catalog,
                    credentials: new Fixed(credential))
                .ForAsync(
                    team: "", teamLabel: "this instance", user: "user-1", login: "person@example.com",
                    "claude", teamEnv: new Dictionary<string, string> { ["ANTHROPIC_AUTH_TOKEN"] = "from-the-team" },
                    ct: TestContext.Current.CancellationToken);

        var home = await OpenAsync(RunCredential.Home);
        var issued = await OpenAsync(new RunCredential(
            CredentialSource.Issued,
            new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = Key },
            [.. declaration.Displaces.Where(d => d != "ANTHROPIC_API_KEY"), .. declaration.HomeVariables ?? []],
            [],
            PerRunHome: true,
            Missing: null));

        // The credential, and every variable it would compete with set empty - the PTY cannot remove one.
        Assert.Equal(Key, issued.Env!["ANTHROPIC_API_KEY"]);
        Assert.Equal("", issued.Env["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.Equal("", issued.Env["ANTHROPIC_AUTH_TOKEN"]);

        // Its own home and its tools: no HOME of its own, its config directory untouched, and the
        // same command line as a home Concierge, with no value on it.
        Assert.False(issued.Env.ContainsKey("HOME"));
        Assert.False(issued.Env.ContainsKey("CLAUDE_CONFIG_DIR"));
        Assert.Equal(home.Argv!.Count, issued.Argv!.Count);
        Assert.Equal(home.Argv[0], issued.Argv[0]);
        Assert.Contains("--dangerously-skip-permissions", issued.Argv);
        Assert.DoesNotContain(issued.Argv, a => a.Contains(Key, StringComparison.Ordinal));
        Assert.Equal(home.StartingFolder, issued.StartingFolder);

        // Home is exactly as before: the team's value stands and nothing is added.
        Assert.Equal("from-the-team", home.Env!["ANTHROPIC_AUTH_TOKEN"]);
        Assert.False(home.Env.ContainsKey("ANTHROPIC_API_KEY"));

        // Not set, it opens on the person's own login as before.
        var notSet = await OpenAsync(RunCredential.NotSet("not set"));
        Assert.False(notSet.Env!.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.Equal("from-the-team", notSet.Env["ANTHROPIC_AUTH_TOKEN"]);
    }

    private sealed class Fixed(RunCredential credential) : IRunCredentials
    {
        public Task<RunCredential> ResolveAsync(string agent, AgentDefinition? definition, CancellationToken ct) =>
            Task.FromResult(credential);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
