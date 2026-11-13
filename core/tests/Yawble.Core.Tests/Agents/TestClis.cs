using Yawble.Core.Agents;
using Yawble.Core.Policy;

namespace Yawble.Core.Tests.Agents;

/// <summary>A detector that answers what the test says, and counts how often it was asked.</summary>
internal sealed class FakeCliDetector(params DetectedCli[] clis) : ICliDetector
{
    private readonly Lock _lock = new();

    public List<DetectedCli> Clis { get; } = [.. clis];

    public int Calls { get; private set; }

    public Task<IReadOnlyList<DetectedCli>> DetectAllAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<DetectedCli>>(Clis.ToList());
        }
    }

    public void Replace(DetectedCli cli)
    {
        lock (_lock)
        {
            Clis.RemoveAll(c => c.CliId == cli.CliId);
            Clis.Add(cli);
        }
    }
}

/// <summary>A fixed set of host paths (folders list their direct children).</summary>
internal sealed class FakeFolders(TargetOs os, params string[] paths) : IHostFolders
{
    private readonly PolicyPaths _p = new(os, os == TargetOs.Windows ? @"C:\" : "/");

    public bool Exists(string path) => paths.Any(x => _p.IsWithin(x, path));

    public IReadOnlyList<string> Children(string path)
    {
        var sep = os == TargetOs.Windows ? '\\' : '/';
        var prefix = path.TrimEnd(sep) + sep;
        return paths.Where(x => x.StartsWith(prefix, _p.Comparison))
            .Select(x => prefix + x[prefix.Length..].Split(sep)[0])
            .Distinct(_p.Comparer).Order(StringComparer.Ordinal).ToList();
    }
}

internal static class TestClis
{
    /// <summary>The CLI's config/credential folders in a home, as the detector reports them.</summary>
    public static IReadOnlyList<string> ConfigFolders(string cliId, string home, char sep = '/') => cliId == "grok"
        ? [home + sep + ".grok"]
        : [home + sep + ".claude", home + sep + ".claude.json"];

    public static DetectedCli Found(string cliId, string executable, IReadOnlyList<string> readOnly, IReadOnlyList<string> config,
        string kind = "native", string? version = null) =>
        new(cliId, true, executable, executable, version ?? (cliId == "claude" ? "2.1.318" : "1.0.77"), kind, readOnly, config, [], "");

    public static DetectedCli Missing(string cliId) =>
        new(cliId, false, null, null, null, "other", [], [], ["not found on PATH"], $"Install {cliId} from its vendor.");

    /// <summary>
    /// A stand-in install on THIS host, outside the home and the app root: &lt;root&gt;/host-clis/&lt;cli&gt;/&lt;cli&gt;(.exe),
    /// granted as the folder &lt;root&gt;/host-clis/&lt;cli&gt;. The program file is empty unless <paramref name="content"/>.
    /// </summary>
    public static DetectedCli StandIn(string root, string home, string cliId, string? content = null)
    {
        var dir = Path.Combine(root, "host-clis", cliId);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, cliId + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (!File.Exists(exe) || content is not null) File.WriteAllText(exe, content ?? "");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        return Found(cliId, exe, [dir], ConfigFolders(cliId, home, Path.DirectorySeparatorChar));
    }

    /// <summary>A stand-in install's grants on this host (see <see cref="StandIn"/>).</summary>
    public static CliInstall InstallFor(string root, string home, AgentCli cli) =>
        CliInstall.FromDetected(TargetOsExtensions.Host, home, StandIn(root, home, cli == AgentCli.Claude ? "claude" : "grok"), DiskFolders.Instance);

    /// <summary>Stand-ins for both CLIs (see <see cref="StandIn"/>).</summary>
    public static FakeCliDetector StandIns(string root, string home) =>
        new(StandIn(root, home, "claude"), StandIn(root, home, "grok"));
}

internal static class RunLayoutTestExtensions
{
    /// <summary>The folder of the CLI's program (the stand-in's scenario.json lives beside it).</summary>
    public static string CliFolder(this Yawble.Core.Policy.RunLayout l) => Path.GetDirectoryName(l.CliInstall.ExecutablePath)!;
}
