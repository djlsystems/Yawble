using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.DataProtection;

namespace Harness.Tests;

/// <summary>
/// A HOME-SOURCE RUN OF A BUILT-IN PRESET, THROUGH THE REAL LAUNCH PATH: the built-in catalog, the
/// environment <see cref="AgentEnvironment.ForContainerAsync"/> builds for a team with a GitHub
/// remote, and the real <see cref="ProcessAgentRunner"/> with the real <see cref="RunCredentials"/>.
/// Each preset's command is a fake on PATH that writes its environment to a file outside the run.
///
/// The Host holds every credential variable any preset declares, every provider key and the git
/// token, each with a value naming it. The resolver's store is a database that does not exist, so a
/// home run that opened it would create it.
/// </summary>
internal sealed class HomeLaunchBed : IDisposable
{
    public const string GitHubRemote = "https://github.com/example/repo.git";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-home-launch-").FullName;
    private readonly EnvironmentScope _host;

    public HomeLaunchBed(Func<AgentDefinition, AgentDefinition>? change = null)
    {
        Catalog = new AgentCatalog([.. AgentCatalogFile.BuiltIns().Select(d => change?.Invoke(d) ?? d)]);

        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        foreach (var command in Catalog.Definitions.Select(RunCredentials.CommandOf).Distinct(StringComparer.Ordinal))
        {
            File.WriteAllText(
                Path.Combine(bin, command),
                "#!/bin/sh\n"
                + $"env | sort > '{Path.Combine(Outside, command + ".env")}'\n"
                + "if [ \"$1\" = plugin ]; then echo '[]'; fi\n"
                + "exit 0\n");
            File.SetUnixFileMode(Path.Combine(bin, command), (UnixFileMode)0b111_101_101);
        }

        // A launch may write its harness entry into the Host's config directory for its CLI: each
        // one a declaration names points into this bed, never at the real one.
        var homes = Catalog.Definitions
            .SelectMany(d => d.IssuedCredential?.HomeVariables ?? [])
            .Distinct(StringComparer.Ordinal)
            .Select(n => new KeyValuePair<string, string>(
                n, Directory.CreateDirectory(Path.Combine(_root, "homes", n)).FullName));

        _host = new EnvironmentScope(
            HostCredentials.Select(n => new KeyValuePair<string, string>(n, HostValue(n)))
                .Concat(homes)
                .Append(new("PATH", bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"))));

        Credentials = new RunCredentials(
            Catalog, _ => CredentialSource.Home, new AgentCredentialStore(Database, new EphemeralDataProtectionProvider()));
        Runner = new ProcessAgentRunner(Catalog, new RunHeartbeat(), credentials: Credentials);
        Containers = new AgentEnvironment(
            new MintingPrincipals(), Catalog, "http://127.0.0.1:5391",
            new TeamDocuments(new TeamPaths(Directory.CreateDirectory(Path.Combine(_root, "teams")).FullName)));
    }

    public AgentCatalog Catalog { get; }

    public RunCredentials Credentials { get; }

    public ProcessAgentRunner Runner { get; }

    private AgentEnvironment Containers { get; }

    public string Database => Path.Combine(_root, "never-created.db");

    private string Outside => Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;

    /// <summary>Every built-in headless preset that declares an issued credential.</summary>
    public static TheoryData<string> HeadlessPresets()
    {
        var data = new TheoryData<string>();
        foreach (var definition in AgentCatalogFile.BuiltIns())
        {
            if (definition is { Mode: AgentMode.Headless, IssuedCredential: not null }) data.Add(definition.Name);
        }

        return data;
    }

    /// <summary>The variables <paramref name="definition"/>'s issued-credential declaration names.</summary>
    public static IEnumerable<string> Declared(AgentDefinition definition) =>
        definition.IssuedCredential is { } declaration
            ? declaration.Displaces.Concat(declaration.Kinds.Select(k => k.Variable))
            : [];

    /// <summary>Every credential the Host holds in this bed.</summary>
    public static IReadOnlySet<string> HostCredentials { get; } = new SortedSet<string>(
        AgentCatalogFile.BuiltIns().SelectMany(Declared)
            .Concat(AgentEnvironment.ProviderVariables)
            .Append(AgentEnvironment.GitHubVariable),
        StringComparer.Ordinal);

    public static string HostValue(string name) => "host-" + name;

    public AgentDefinition Definition(string preset) => Catalog.Definition(preset)!;

    /// <summary>Every variable a command other than <paramref name="preset"/>'s declares.</summary>
    public IReadOnlySet<string> OtherCommandsDeclare(string preset)
    {
        var command = RunCredentials.CommandOf(Definition(preset));
        return new SortedSet<string>(
            Catalog.Definitions
                .Where(d => !string.Equals(RunCredentials.CommandOf(d), command, StringComparison.OrdinalIgnoreCase))
                .SelectMany(Declared),
            StringComparer.Ordinal);
    }

    /// <summary>The environment the container builds for a member of a GitHub team running <paramref name="preset"/>.</summary>
    public Task<IReadOnlyDictionary<string, string>> ContainerEnvironmentAsync(
        string preset, IReadOnlyDictionary<string, string>? teamEnv = null) =>
        Containers.ForContainerAsync(
            new ContainerId("Alpha", "DeveloperRowan"), preset, new HashSet<string> { Permits.Progress },
            teamEnv ?? new Dictionary<string, string> { ["TEAM_VAR"] = "team" }, [GitHubRemote],
            TestContext.Current.CancellationToken);

    /// <summary>One run of <paramref name="preset"/>, and the environment its child was handed.</summary>
    public async Task<IReadOnlyDictionary<string, string>> RunAsync(
        string preset, IReadOnlyDictionary<string, string>? teamEnv = null)
    {
        var variables = await ContainerEnvironmentAsync(preset, teamEnv);
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;

        var result = await Runner.RunAsync(
            new AgentInvocation(new ContainerId("Alpha", "DeveloperRowan"), "You are a member.", "hello", workspace, variables, Agent: preset),
            TestContext.Current.CancellationToken);

        Assert.True(result.LaunchError is null, result.LaunchError);
        return Seen(preset);
    }

    /// <summary>What the last child of <paramref name="preset"/>'s command was handed.</summary>
    public IReadOnlyDictionary<string, string> Seen(string preset) =>
        File.ReadAllLines(Path.Combine(Outside, RunCredentials.CommandOf(Definition(preset)) + ".env"))
            .Where(l => l.Contains('='))
            .Select(l => l.Split('=', 2))
            .GroupBy(p => p[0])
            .ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.Ordinal);

    public void Dispose()
    {
        _host.Dispose();
        MemberTempCleanup.Remove(_root);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
