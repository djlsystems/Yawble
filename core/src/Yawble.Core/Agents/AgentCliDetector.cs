using System.Text.RegularExpressions;
using Yawble.Core.Policy;

namespace Yawble.Core.Agents;

/// <summary>Where a program lives and what read-only folders it needs, as one install layout rule decided it.</summary>
public sealed record InstallLayout(string Kind, IReadOnlyList<string> Folders, IReadOnlyList<string>? Problems = null);

/// <summary>
/// One supported CLI. A third CLI is one new subclass added to <see cref="CliDetectorRegistry.Default"/>: its command
/// name, its known install locations, its own native layout rule, its config folders and its install help. Finding
/// the launcher, following it to the program, the shared layouts (npm, Homebrew, WinGet) and the version probe are here.
/// </summary>
public abstract partial class AgentCliDetector
{
    public static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);

    public abstract string CliId { get; }

    /// <summary>The command the person types.</summary>
    protected abstract string Command { get; }

    /// <summary>Where the official installers put the launcher, tried after PATH (a launcher found only here gets a problem).</summary>
    protected abstract IEnumerable<string?> KnownLaunchers(DetectionHost h);

    /// <summary>The CLI's own config and credential folders/files. Reported (never opened) so the launcher keeps them blocked.</summary>
    public abstract IReadOnlyList<string> ConfigFolders(DetectionHost h);

    public abstract string InstallHelp(TargetOs os);

    /// <summary>The CLI's own sign-in on the host, from file presence only; null when it has none.</summary>
    public virtual CliSignIn? SignIn(DetectionHost h) => null;

    /// <summary>The CLI's own installer layout (Claude's versions folder, Grok's downloads folder); null when it is not one.</summary>
    protected abstract InstallLayout? NativeLayout(DetectionHost h, ResolvedProgram program);

    /// <summary>Variables for the version probe: at least the CLI's own switch that turns its auto-update off.</summary>
    protected abstract IReadOnlyDictionary<string, string> VersionEnvironment { get; }

    protected virtual IReadOnlyList<string> VersionArguments => ["--version"];

    /// <summary>A program that answers to the command but is not this CLI (the community grok-cli); the reason, or null.</summary>
    protected virtual string? NotThisCli(DetectionHost h, ResolvedProgram program) => null;

    /// <summary>A problem with output that does not look like this CLI's own "--version".</summary>
    protected virtual string? CheckVersionOutput(string stdout) => null;

    /// <summary>Extra problems for a layout that works but should be changed (Claude's old ~/.claude/local install).</summary>
    protected virtual IEnumerable<string> LayoutWarnings(DetectionHost h, ResolvedProgram program) => [];

    public async Task<DetectedCli> DetectAsync(DetectionHost h, CancellationToken ct)
    {
        var config = ConfigFolders(h);
        var problems = new List<string>();
        var candidates = new List<(string Path, bool OnPath)>();
        if (h.FindOnPath(Command) is { } onPath) candidates.Add((onPath, true));
        foreach (var known in KnownLaunchers(h))
        {
            if (known is null || candidates.Any(c => h.Paths.Same(c.Path, known))) continue;
            if (h.Fs.FileExists(known) || h.Fs.LinkTarget(known) is not null) candidates.Add((known, false));
        }

        ResolvedProgram? program = null;
        string? launcher = null;
        foreach (var (path, isOnPath) in candidates)
        {
            var resolved = h.Resolve(path);
            if (NotThisCli(h, resolved) is { } other)
            {
                problems.Add(other);
                continue;
            }
            if (!resolved.Exists)
            {
                problems.AddRange(resolved.Problems);
                continue;
            }
            if (!isOnPath)
                problems.Add($"Found at {path}, but its folder is not on your PATH. The app runs it from there; your own terminal will not find '{Command}' until you add {h.Parent(path)} to PATH.");
            program = resolved;
            launcher = path;
            break;
        }

        if (program is null)
            return new DetectedCli(CliId, false, null, null, null, "other", [], config, problems, InstallHelp(h.Os)) { SignIn = SignIn(h) };

        problems.AddRange(program.Problems);
        var layout = SharedLayout(h, program) ?? NativeLayout(h, program) ?? OtherLayout(h, program);
        problems.AddRange(layout.Problems ?? []);
        problems.AddRange(LayoutWarnings(h, program));
        var folders = new List<string>();
        foreach (var folder in layout.Folders)
        {
            if (Unsafe(h, folder, config) is { } why) problems.Add(why);
            else if (!folders.Any(f => h.Paths.Same(f, folder))) folders.Add(folder);
        }
        if (program.Kind == ProgramKind.NodeScript && program.NodeExecutable is { } node && h.Parent(node) is { } nodeDir)
        {
            // The Node.js runtime: the folder of the real node program (after links): /usr/local/bin,
            // ~/.nvm/versions/node/<v>/bin, C:\Program Files\nodejs, ... Node needs nothing else beside it.
            if (Unsafe(h, nodeDir, config) is { } why) problems.Add(why);
            else if (!folders.Any(f => h.Paths.Same(f, nodeDir))) folders.Add(nodeDir);
        }

        var version = await ProbeVersionAsync(h, launcher!, program, problems, ct);
        return new DetectedCli(CliId, true, launcher, program.Executable, version, layout.Kind, folders, config, problems, InstallHelp(h.Os))
        {
            SignIn = SignIn(h),
        };
    }

    /// <summary>
    /// A read-only grant must never be the home folder, anything above it, a filesystem root, or a config folder (or
    /// a folder holding one): the app would hand the CLI the person's files or credentials.
    /// </summary>
    private static string? Unsafe(DetectionHost h, string folder, IReadOnlyList<string> config)
    {
        if (h.RootOf(folder) is { } root && h.Paths.Same(folder, root)) return $"Not granted: {folder} is a whole drive.";
        if (h.IsWithin(h.Home, folder)) return $"Not granted: {folder} would expose your whole home folder. Reinstall the CLI with its official installer.";
        foreach (var c in config)
            if (h.IsWithin(c, folder)) return $"Not granted: {folder} holds the CLI's config/credential folder {c}.";
        return null;
    }

    // -------------------------------------------------------------- shared layouts

    /// <summary>npm, Homebrew and WinGet: the same rule whichever CLI it is.</summary>
    private static InstallLayout? SharedLayout(DetectionHost h, ResolvedProgram p)
    {
        var segs = h.Segments(p.Executable);
        var root = h.RootOf(p.Executable)!;

        // npm (any prefix: /usr/local/lib, /opt/homebrew/lib, ~/.npm-global/lib, ~/.nvm/versions/node/<v>/lib,
        // %APPDATA%\npm, ~/.claude/local): the OUTERMOST package under node_modules. E.g.
        // <prefix>/bin/claude -> ../lib/node_modules/@anthropic-ai/claude-code/bin/claude.exe, a native ELF
        // (claude-code 2.x's postinstall hard-links the platform package's binary over the bin stub). Older
        // claude-code (<= 1.x / 2.0) is cli.js run by node: then the node runtime folder is added too.
        for (var i = 0; i < segs.Count - 1; i++)
        {
            if (!segs[i].Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
            var take = segs[i + 1].StartsWith('@') && i + 2 < segs.Count ? i + 3 : i + 2;
            return new InstallLayout("npm", [h.Combine(root, [.. segs.Take(take)])]);
        }

        // Homebrew: <prefix>/Cellar/<formula>/<version>/... (formula) or <prefix>/Caskroom/<cask>/<version>/...
        // (cask: `brew install --cask claude-code`), behind <prefix>/bin links; prefix /opt/homebrew (Apple silicon),
        // /usr/local (Intel), /home/linuxbrew/.linuxbrew (Linuxbrew). Only that one version folder.
        for (var i = 0; i < segs.Count - 2; i++)
        {
            if (segs[i] is not ("Cellar" or "Caskroom")) continue;
            return new InstallLayout("homebrew", [h.Combine(root, [.. segs.Take(i + 3)])]);
        }

        // WinGet portable packages: %LOCALAPPDATA%\Microsoft\WinGet\Packages\<Id>_<source>\ (user scope) or
        // %ProgramFiles%\WinGet\Packages\<Id>_<source>\ (machine scope), behind a symlink in ...\WinGet\Links\.
        // `winget install Anthropic.ClaudeCode` installs this way.
        if (h.Os == TargetOs.Windows)
        {
            for (var i = 0; i < segs.Count - 2; i++)
            {
                if (!segs[i].Equals("WinGet", StringComparison.OrdinalIgnoreCase) || !segs[i + 1].Equals("Packages", StringComparison.OrdinalIgnoreCase)) continue;
                return new InstallLayout("winget", [h.Combine(root, [.. segs.Take(i + 3)])]);
            }
        }
        return null;
    }

    /// <summary>Anything else (a distro package, a hand-copied binary): only the folder that holds the program.</summary>
    private static InstallLayout OtherLayout(DetectionHost h, ResolvedProgram p) =>
        new("other", h.Parent(p.Executable) is { } dir ? [dir] : []);

    // -------------------------------------------------------------- version

    private async Task<string?> ProbeVersionAsync(DetectionHost h, string launcher, ResolvedProgram p, List<string> problems, CancellationToken ct)
    {
        CliProbeCommand command;
        if (p.Kind == ProgramKind.NodeScript)
        {
            if (p.NodeExecutable is null) return null; // already a problem: no Node.js
            command = new CliProbeCommand(p.NodeExecutable, [p.Executable, .. VersionArguments], VersionEnvironment, VersionTimeout);
        }
        else if (p.Kind == ProgramKind.Native)
        {
            command = new CliProbeCommand(p.Executable, VersionArguments, VersionEnvironment, VersionTimeout);
        }
        else if (h.Os == TargetOs.Windows && (launcher.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || launcher.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            command = new CliProbeCommand("cmd.exe", ["/d", "/c", launcher, .. VersionArguments], VersionEnvironment, VersionTimeout);
        }
        else
        {
            command = new CliProbeCommand(launcher, VersionArguments, VersionEnvironment, VersionTimeout);
        }

        var result = await h.Runner.RunAsync(command, ct);
        var shown = $"'{Command} {string.Join(' ', VersionArguments)}'";
        if (result.StartError is not null)
        {
            problems.Add($"{shown} could not start: {result.StartError}");
            return null;
        }
        if (result.TimedOut)
        {
            problems.Add($"{shown} did not answer within {VersionTimeout.TotalSeconds:0} s");
            return null;
        }
        if (result.ExitCode != 0)
        {
            var why = FirstLine(result.Stderr) ?? FirstLine(result.Stdout);
            problems.Add($"{shown} failed with exit code {result.ExitCode}" + (why is null ? "" : $": {why}"));
            return null;
        }
        if (CheckVersionOutput(result.Stdout) is { } notIt) problems.Add(notIt);
        var match = VersionNumber().Match(result.Stdout);
        if (!match.Success)
        {
            problems.Add($"{shown} printed no version number");
            return null;
        }
        return match.Value;
    }

    private static string? FirstLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) is { } l ? (l.Length > 200 ? l[..200] + "…" : l) : null;

    [GeneratedRegex(@"\d+\.\d+\.\d+(?:[-+.][0-9A-Za-z.-]+)?")]
    private static partial Regex VersionNumber();
}
