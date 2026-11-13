using Yawble.Core.Agents;
using Xunit;

namespace Yawble.Core.Tests.Agents;

/// <summary>
/// The real claude and grok installed on this machine, through the real filesystem and a real "--version" (with a
/// throwaway HOME and auto-update off).
/// <para>Trait Category=HostCli: run with the HostCli filter (scripts/test-all.sh's
/// header shows it) on each machine whose CLI layout is being checked. Set YAWBLE_HOSTCLI_EXPECT ("&lt;cliId&gt;:&lt;InstallKind&gt; ..." e.g. "claude:native grok:native"): each listed
/// CLI MUST be found with that install kind. A CLI not listed there (or every CLI, when the variable is unset, as on
/// a developer machine) is skipped, with the reason, when it is not installed.</para>
/// </summary>
[Trait("Category", "HostCli")]
public class RealHostDetectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>YAWBLE_HOSTCLI_EXPECT parsed into cliId -> InstallKind; empty when unset.</summary>
    internal static IReadOnlyDictionary<string, string> Expected(string? raw) =>
        (raw ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split(':', 2))
            .ToDictionary(p => p[0], p => p.Length == 2 ? p[1] : "", StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> ExpectedHere =>
        Expected(Environment.GetEnvironmentVariable("YAWBLE_HOSTCLI_EXPECT"));

    [Theory]
    [InlineData("claude")]
    [InlineData("grok")]
    public async Task TheCliInstalledOnThisHostIsDetectedWithItsRealFolders(string cliId)
    {
        var all = await CliDetectorRegistry.ForThisHost().DetectAllAsync(Ct);
        var cli = all.Single(d => d.CliId == cliId);
        TestContext.Current.SendDiagnosticMessage($"{cliId}: {System.Text.Json.JsonSerializer.Serialize(cli)}");

        if (ExpectedHere.TryGetValue(cliId, out var kind))
        {
            Assert.True(cli.Found, $"YAWBLE_HOSTCLI_EXPECT lists {cliId}:{kind} but it was not found ({string.Join("; ", cli.Problems)})");
            Assert.Equal(kind, cli.InstallKind);
        }
        else
        {
            Assert.SkipUnless(cli.Found, $"{cliId} is not installed on this host ({string.Join("; ", cli.Problems)})");
        }

        Assert.True(File.Exists(cli.ExecutablePath), cli.ExecutablePath);
        Assert.False(string.IsNullOrWhiteSpace(cli.Version), $"{cliId} reported no version");
        Assert.Matches(@"^\d+\.\d+\.\d+", cli.Version);
        Assert.NotEmpty(cli.ReadOnlyFolders);
        Assert.All(cli.ReadOnlyFolders, f => Assert.True(Directory.Exists(f), f));
        // The program lies inside one of its grants (after links), and no grant is the home folder or above it.
        Assert.Contains(cli.ReadOnlyFolders, f => Paths.Under(cli.ExecutablePath!, f));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.DoesNotContain(cli.ReadOnlyFolders, f => Paths.Same(home, f) || Paths.Under(home, f));
        Assert.Contains(Path.Combine(home, "." + cliId), cli.ConfigFolders);
        Assert.Empty(ConfigInsideGrants(cli));
        Assert.Empty(cli.Problems);
    }

    /// <summary>Every CLI YAWBLE_HOSTCLI_EXPECT names is one the app detects (a typo there would otherwise test nothing).</summary>
    [Fact]
    public async Task EveryExpectedCliIsOneTheAppDetects()
    {
        var expected = ExpectedHere;
        Assert.SkipWhen(expected.Count == 0, "YAWBLE_HOSTCLI_EXPECT is not set (set it when running the HostCli filter)");
        var ids = (await CliDetectorRegistry.ForThisHost().DetectAllAsync(Ct)).Select(d => d.CliId).ToHashSet();
        Assert.All(expected, e =>
        {
            Assert.Contains(e.Key, ids);
            Assert.Contains(e.Value, new[] { "native", "npm", "homebrew", "winget", "other" });
        });
    }

    [Fact]
    public void ExpectParsesPairs()
    {
        Assert.Empty(Expected(null));
        Assert.Empty(Expected("  "));
        var e = Expected(" claude:native  grok:native ");
        Assert.Equal("native", e["claude"]);
        Assert.Equal("native", e["grok"]);
        Assert.Equal(2, e.Count);
    }

    [Fact]
    public void GrantsMayNotReachConfigFoldersExceptGrokProgramFolder()
    {
        var home = Path.Combine(Path.GetTempPath(), "h");
        string P(params string[] parts) => Path.Combine([home, .. parts]);
        DetectedCli Cli(string id, string[] grants, string[] configs) =>
            new(id, true, null, null, "1.0.0", "native", grants, configs, [], "");

        var grokProgram = P(".grok", OperatingSystem.IsWindows() ? "bin" : "downloads");
        Assert.Empty(ConfigInsideGrants(Cli("grok", [grokProgram], [P(".grok")])));
        Assert.Single(ConfigInsideGrants(Cli("grok", [P(".grok", "sessions")], [P(".grok")])));
        Assert.Single(ConfigInsideGrants(Cli("grok", [P(".grok")], [P(".grok")])));
        Assert.Single(ConfigInsideGrants(Cli("claude", [P(".claude", "local")], [P(".claude"), P(".claude.json")])));
        Assert.Equal(2, ConfigInsideGrants(Cli("claude", [home], [P(".claude"), P(".claude.json")])).Count);
        Assert.Empty(ConfigInsideGrants(Cli("claude", [P(".local", "share", "claude", "versions")], [P(".claude"), P(".claude.json")])));
    }

    /// <summary>
    /// Grants that reach a config/credential folder: a grant that is, or lies inside, a config folder, or one that
    /// contains a config folder. The one allowed case is Grok's own program folder inside ~/.grok: ~/.grok/downloads
    /// (macOS, Linux: the bin entries are links into it) and ~\.grok\bin (Windows: install.ps1 copies grok.exe there).
    /// The launcher keeps the rest of ~/.grok blocked.
    /// </summary>
    internal static IReadOnlyList<string> ConfigInsideGrants(DetectedCli cli)
    {
        var bad = new List<string>();
        foreach (var grant in cli.ReadOnlyFolders)
        foreach (var config in cli.ConfigFolders)
        {
            if (Paths.Under(config, grant)) bad.Add($"{grant} contains {config}");
            else if (Paths.Same(grant, config)) bad.Add($"{grant} is {config}");
            else if (Paths.Under(grant, config) && !AllowedInside(cli.CliId, grant, config)) bad.Add($"{grant} is inside {config}");
        }
        return bad;
    }

    private static bool AllowedInside(string cliId, string grant, string config) =>
        cliId == "grok"
        && Path.GetFileName(config.TrimEnd(Path.DirectorySeparatorChar)) == ".grok"
        && Paths.Same(grant, Path.Combine(config, OperatingSystem.IsWindows() ? "bin" : "downloads"));

    private static class Paths
    {
        private static StringComparison Cmp => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static string Norm(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        public static bool Same(string a, string b) => string.Equals(Norm(a), Norm(b), Cmp);

        /// <summary>path lies strictly inside folder.</summary>
        public static bool Under(string path, string folder) =>
            Norm(path).StartsWith(Norm(folder) + Path.DirectorySeparatorChar, Cmp);
    }
}
