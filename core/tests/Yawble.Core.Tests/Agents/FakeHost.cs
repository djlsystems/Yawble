using System.Text;
using Yawble.Core.Agents;
using Yawble.Core.Policy;

namespace Yawble.Core.Tests.Agents;

/// <summary>
/// A host described as data: files (with their first bytes), folders, symlinks/junctions, PATH and variables, and a
/// scripted "--version". It records every file the detector READ, so a test can prove no config/credential file was
/// opened. Paths follow the target OS's rules (Windows paths on Linux included).
/// </summary>
public sealed class FakeHost : IHostFileSystem, ICliProcessRunner
{
    public static readonly byte[] Elf = [0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0];
    public static readonly byte[] MachO = [0xCF, 0xFA, 0xED, 0xFE, 7, 0, 0, 1];
    public static readonly byte[] Pe = [(byte)'M', (byte)'Z', 0x90, 0, 3, 0, 0, 0];

    private readonly Dictionary<string, byte[]> _files;
    private readonly HashSet<string> _dirs;
    private readonly Dictionary<string, string> _links;
    private readonly PolicyPaths _paths;

    public FakeHost(TargetOs os, string home)
    {
        Os = os;
        Home = home;
        _paths = new PolicyPaths(os, home);
        _files = new(_paths.Comparer);
        _dirs = new(_paths.Comparer);
        _links = new(_paths.Comparer);
        Dir(home);
    }

    public TargetOs Os { get; }
    public string Home { get; }
    public List<string> PathDirs { get; } = [];
    public Dictionary<string, string> Vars { get; } = [];
    public List<string> Reads { get; } = [];
    public List<CliProbeCommand> Probes { get; } = [];

    /// <summary>What "--version" answers, by the probed file name; default exit 0 with <see cref="VersionOutput"/>.</summary>
    public Func<CliProbeCommand, CliProbeResult>? Respond { get; set; }

    public string VersionOutput { get; set; } = "1.2.3";

    private char Sep => Os == TargetOs.Windows ? '\\' : '/';

    public FakeHost File(string path, byte[] head)
    {
        _files[path] = head;
        DirsUpTo(path);
        return this;
    }

    public FakeHost File(string path, string text) => File(path, Encoding.UTF8.GetBytes(text));

    public FakeHost Dir(string path)
    {
        _dirs.Add(path);
        DirsUpTo(path);
        return this;
    }

    public FakeHost Link(string path, string target)
    {
        _links[path] = target;
        DirsUpTo(path);
        return this;
    }

    public FakeHost OnPath(params string[] dirs)
    {
        PathDirs.AddRange(dirs);
        return this;
    }

    private void DirsUpTo(string path)
    {
        var i = path.LastIndexOf(Sep);
        while (i > 0)
        {
            var parent = path[..i];
            if (Os == TargetOs.Windows && parent.Length == 2) parent += "\\";
            if (!_dirs.Add(parent)) break;
            i = parent.TrimEnd(Sep).LastIndexOf(Sep);
        }
    }

    public DetectionHost Host()
    {
        var env = new HostEnvironment(Os, Home, PathDirs, Os == TargetOs.Windows ? [".COM", ".EXE", ".BAT", ".CMD"] : [], Vars);
        return new DetectionHost(env, this, this);
    }

    /// <summary>Follows links the way the OS does when a file is opened.</summary>
    private string Final(string path) => new DetectionHost(
        new HostEnvironment(Os, Home, [], [], new Dictionary<string, string>()), new LinksOnly(_links), this).Canonical(path);

    public bool FileExists(string path) => _files.ContainsKey(Final(path));

    public bool DirectoryExists(string path) => _dirs.Contains(Final(path));

    public string? LinkTarget(string path) => _links.TryGetValue(path, out var t) ? t : null;

    public byte[]? ReadHead(string path, int maxBytes)
    {
        var final = Final(path);
        Reads.Add(final);
        return _files.TryGetValue(final, out var bytes) ? bytes.Take(maxBytes).ToArray() : null;
    }

    public Task<CliProbeResult> RunAsync(CliProbeCommand command, CancellationToken ct)
    {
        Probes.Add(command);
        return Task.FromResult(Respond?.Invoke(command) ?? new CliProbeResult(0, VersionOutput + "\n", "", false));
    }

    private sealed class LinksOnly(Dictionary<string, string> links) : IHostFileSystem
    {
        public bool FileExists(string path) => false;
        public bool DirectoryExists(string path) => false;
        public string? LinkTarget(string path) => links.TryGetValue(path, out var t) ? t : null;
        public byte[]? ReadHead(string path, int maxBytes) => null;
    }
}
