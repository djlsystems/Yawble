using System.Text;
using System.Text.RegularExpressions;
using Yawble.Core.Policy;

namespace Yawble.Core.Agents;

/// <summary>What kind of program a launcher finally runs.</summary>
public enum ProgramKind
{
    /// <summary>A native executable (ELF, Mach-O or PE): runs on its own.</summary>
    Native,

    /// <summary>A JavaScript entry point: needs the Node.js runtime (<see cref="ResolvedProgram.NodeExecutable"/>).</summary>
    NodeScript,

    /// <summary>Could not be told (unreadable, or a script we cannot follow).</summary>
    Unknown,
}

/// <summary>A launcher followed through symlinks, junctions, npm .cmd shims and exec wrappers to the real program.</summary>
public sealed record ResolvedProgram(
    string Launcher,
    string Executable,
    bool Exists,
    ProgramKind Kind,
    string? NodeExecutable,
    IReadOnlyList<string> Problems);

/// <summary>
/// Path logic and launcher resolution for one host (real or fake), in the target OS's path rules: a Windows host is
/// resolved with '\', drive roots and case-insensitive names, even when the tests run on Linux.
/// </summary>
public sealed partial class DetectionHost(HostEnvironment env, IHostFileSystem fs, ICliProcessRunner runner)
{
    public HostEnvironment Env { get; } = env;
    public IHostFileSystem Fs { get; } = fs;
    public ICliProcessRunner Runner { get; } = runner;
    public PolicyPaths Paths { get; } = new(env.Os, env.Home);
    public TargetOs Os => Env.Os;
    public string Home => Paths.Home;
    private char Sep => Os == TargetOs.Windows ? '\\' : '/';

    public static DetectionHost ForThisHost() => new(HostEnvironment.Current(), new RealHostFileSystem(), new RealCliProcessRunner());

    // ------------------------------------------------------------------ paths

    public string? Normalize(string path) => Paths.Normalize(StripVerbatim(path));

    public string Combine(string root, params string[] parts) => Paths.Combine(root, parts);

    /// <summary>A path from the environment ("%APPDATA%", "$GROK_BIN_DIR"), normalised; null when unset or relative.</summary>
    public string? FromVar(string name, params string[] parts)
    {
        var v = Env.Var(name);
        if (v is null || Normalize(v) is not { } root) return null;
        return parts.Length == 0 ? root : Combine(root, parts);
    }

    public string? Parent(string path)
    {
        var root = RootOf(path);
        if (root is null || Paths.Same(path, root)) return null;
        var i = path.LastIndexOf(Sep);
        return i < root.Length ? root : path[..i];
    }

    public string FileName(string path)
    {
        var i = path.LastIndexOf(Sep);
        return i < 0 ? path : path[(i + 1)..];
    }

    /// <summary>The path's segments below its root.</summary>
    public IReadOnlyList<string> Segments(string path)
    {
        var root = RootOf(path) ?? "";
        return path[root.Length..].Split(Sep, StringSplitOptions.RemoveEmptyEntries);
    }

    public string? RootOf(string path)
    {
        if (Os != TargetOs.Windows) return path.StartsWith('/') ? "/" : null;
        if (path.Length >= 3 && path[1] == ':' && path[2] == '\\') return path[..3];
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = path[2..].Split('\\');
            return parts.Length >= 2 ? @"\\" + parts[0] + "\\" + parts[1] + "\\" : null;
        }
        return null;
    }

    public bool IsWithin(string path, string root) => Paths.IsWithin(path, root);

    /// <summary>A link target (relative to the link's folder, or absolute) as an absolute normalised path.</summary>
    public string? ResolveAgainst(string folder, string target)
    {
        target = StripVerbatim(target);
        if (Os == TargetOs.Windows) target = target.Replace('/', '\\');
        return RootOf(target) is not null ? Normalize(target) : Normalize(folder + Sep + target);
    }

    private static string StripVerbatim(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
        : path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal) ? path[4..]
        : path;

    /// <summary>
    /// Every symlink or junction on the way is followed, component by component (a linked parent folder counts as much
    /// as a linked file), up to 40 links: the path the OS would really open.
    /// </summary>
    public string Canonical(string path)
    {
        var current = Normalize(path) ?? path;
        for (var hops = 0; hops < 40; hops++)
        {
            var root = RootOf(current);
            if (root is null) return current;
            var segments = Segments(current);
            var walked = root;
            string? next = null;
            for (var i = 0; i < segments.Count; i++)
            {
                var candidate = walked.EndsWith(Sep) ? walked + segments[i] : walked + Sep + segments[i];
                if (Fs.LinkTarget(candidate) is { } target && ResolveAgainst(walked, target) is { } resolved)
                {
                    var rest = segments.Skip(i + 1).ToArray();
                    next = rest.Length == 0 ? resolved : Combine(resolved, rest);
                    break;
                }
                walked = candidate;
            }
            if (next is null) return current;
            current = next;
        }
        return current;
    }

    // ------------------------------------------------------------------ PATH

    /// <summary>The first match on PATH, as the person's own shell would find it (PATHEXT order on Windows).</summary>
    public string? FindOnPath(string command)
    {
        foreach (var raw in Env.PathDirectories)
        {
            if (Normalize(raw) is not { } dir) continue; // a relative PATH entry is not something we would run
            foreach (var name in CommandNames(command))
            {
                var candidate = Combine(dir, name);
                if (Fs.FileExists(candidate)) return candidate;
            }
        }
        return null;
    }

    public IEnumerable<string> CommandNames(string command)
    {
        if (Os != TargetOs.Windows || Path.HasExtension(command)) return [command];
        return Env.PathExtensions.Select(e => command + e.ToLowerInvariant());
    }

    /// <summary>The executable file name for this OS ("claude" or "claude.exe").</summary>
    public string Exe(string name) => Os == TargetOs.Windows ? name + ".exe" : name;

    // ------------------------------------------------------------------ programs

    /// <summary>
    /// Follows a launcher to the program it runs:
    /// <list type="bullet">
    /// <item>symlinks/junctions anywhere on the path (npm's Unix bin links, Homebrew's bin links, ~/.local/bin/claude,
    /// ~/.grok/bin/grok, WinGet\Links);</item>
    /// <item>npm's Windows .cmd shim: <c>"%dp0%\node_modules\&lt;pkg&gt;\...\cli.js"</c> (or <c>%~dp0</c> in older npm),
    /// with node.exe beside it or from PATH;</item>
    /// <item>an exec wrapper script (<c>exec "/abs/path" "$@"</c>, Claude's old ~/.claude/local/claude);</item>
    /// <item>a <c>#!/usr/bin/env node</c> or <c>#!/abs/node</c> script: needs Node.js.</item>
    /// </list>
    /// </summary>
    public ResolvedProgram Resolve(string launcher)
    {
        var problems = new List<string>();
        var current = Canonical(launcher);
        string? node = null;
        var kind = ProgramKind.Unknown;
        for (var depth = 0; depth < 4; depth++)
        {
            if (!Fs.FileExists(current))
                return new ResolvedProgram(launcher, current, false, ProgramKind.Unknown, null,
                    [Paths.Same(current, launcher) ? $"{launcher} is missing" : $"{launcher} points to {current}, which is missing (a broken or half-removed install)"]);

            if (Os == TargetOs.Windows && (current.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || current.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
            {
                var text = Text(current, 16 * 1024);
                var targets = text is null ? [] : CmdShimTarget().Matches(text).Select(m => m.Groups[1].Value)
                    .Where(t => !t.EndsWith("node.exe", StringComparison.OrdinalIgnoreCase) && !t.EndsWith("node", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var dir = Parent(current)!;
                if (targets.Count == 0 || ResolveAgainst(dir, targets[0]) is not { } next)
                {
                    problems.Add($"could not read which program {FileName(current)} starts");
                    return new ResolvedProgram(launcher, current, true, ProgramKind.Unknown, null, problems);
                }
                current = Canonical(next);
                if (current.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || current.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase) || current.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))
                {
                    // cmd-shim: IF EXIST "%dp0%\node.exe" (SET "_prog=%dp0%\node.exe") ELSE (SET "_prog=node")
                    var beside = Combine(dir, "node.exe");
                    node = Fs.FileExists(beside) ? beside : FindOnPath("node");
                    kind = ProgramKind.NodeScript;
                    break;
                }
                continue;
            }

            var head = Fs.ReadHead(current, 512);
            if (head is null)
            {
                problems.Add($"could not read {current}");
                break;
            }
            if (IsNativeBinary(head))
            {
                kind = ProgramKind.Native;
                break;
            }
            if (head.Length > 2 && head[0] == '#' && head[1] == '!')
            {
                var text = Encoding.UTF8.GetString(head);
                var shebang = text.Split('\n')[0].Trim();
                if (shebang.Contains("node", StringComparison.Ordinal))
                {
                    var interpreter = shebang[2..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    node = interpreter.Length > 0 && interpreter[0].EndsWith("/node", StringComparison.Ordinal) ? interpreter[0]
                        : FindOnPath("node");
                    kind = ProgramKind.NodeScript;
                    break;
                }
                var full = Text(current, 16 * 1024) ?? text;
                if (ExecWrapper().Match(full) is { Success: true } m && ResolveAgainst(Parent(current)!, m.Groups[1].Value) is { } wrapped)
                {
                    current = Canonical(wrapped);
                    continue;
                }
                problems.Add($"{current} is a script whose program could not be followed");
                break;
            }
            if (current.EndsWith(".js", StringComparison.Ordinal) || current.EndsWith(".cjs", StringComparison.Ordinal) || current.EndsWith(".mjs", StringComparison.Ordinal))
            {
                node = FindOnPath("node");
                kind = ProgramKind.NodeScript;
                break;
            }
            problems.Add($"{current} is not a program this app recognises");
            break;
        }

        if (kind == ProgramKind.NodeScript)
        {
            if (node is null) problems.Add("it is a Node.js program, but Node.js (node) was not found on PATH");
            else
            {
                node = Canonical(node);
                if (!Fs.FileExists(node))
                {
                    problems.Add($"it is a Node.js program, but {node} is missing");
                    node = null;
                }
            }
        }
        return new ResolvedProgram(launcher, current, true, kind, node, problems);
    }

    private string? Text(string path, int max) => Fs.ReadHead(path, max) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>ELF, Mach-O (thin and fat) or PE ("MZ").</summary>
    public static bool IsNativeBinary(ReadOnlySpan<byte> head)
    {
        if (head.Length < 4) return head.Length >= 2 && head[0] == 'M' && head[1] == 'Z';
        if (head[0] == 0x7f && head[1] == 'E' && head[2] == 'L' && head[3] == 'F') return true;
        if (head[0] == 'M' && head[1] == 'Z') return true;
        uint magic = (uint)(head[0] << 24 | head[1] << 16 | head[2] << 8 | head[3]);
        return magic is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xCAFEBABE;
    }

    // npm cmd-shim: "%dp0%\node_modules\..." (npm 7+) and "%~dp0\node_modules\..." (older).
    [GeneratedRegex(@"""%(?:~dp0|dp0%)\\([^""]+)""")]
    private static partial Regex CmdShimTarget();

    [GeneratedRegex(@"exec\s+""([^""]+)""")]
    private static partial Regex ExecWrapper();
}
