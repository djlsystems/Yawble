using Yawble.Core.Agents;

namespace Yawble.Core.Policy;

/// <summary>What the launcher needs to know about the host's folders, so the grants are testable without a real disk.</summary>
public interface IHostFolders
{
    bool Exists(string path);

    /// <summary>The full paths of a folder's entries; empty when it is not a folder or cannot be listed.</summary>
    IReadOnlyList<string> Children(string path);
}

/// <summary>The real disk.</summary>
public sealed class DiskFolders : IHostFolders
{
    public static readonly DiskFolders Instance = new();

    public bool Exists(string path) => Path.Exists(path);

    public IReadOnlyList<string> Children(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal).ToList() : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>
/// The member's agent CLI as installed on this host, turned into what one run grants (no pinned version, no app
/// toolchain folder: the person or the CLI itself keeps it up to date). Built from the launch-time
/// <see cref="DetectedCli"/>:
/// <list type="bullet">
/// <item><see cref="Command"/>: the real program (ExecutablePath), or <c>node &lt;script&gt;</c> when the CLI is a
/// JavaScript entry point and one of its read-only folders holds node.</item>
/// <item><see cref="ReadOnlyFolders"/>: EXACTLY the detected install folders, read-only. Nothing else from the home.</item>
/// <item><see cref="ConfigDenied"/>: the CLI's own config/credential folders, denied. When an install folder lies
/// inside one (xAI's grok lives in ~/.grok/downloads), the config folder itself cannot be denied (a denial would
/// cover the grant), so every entry of it that is not on the way to a grant is denied instead, at launch time.</item>
/// </list>
/// Paths are in the target OS's form; <see cref="FromDetected"/> refuses an install it cannot grant safely.
/// </summary>
public sealed record CliInstall
{
    public required string ExecutablePath { get; init; }

    /// <summary>argv[0..]: the program, then the script for a JavaScript entry point.</summary>
    public required IReadOnlyList<string> Command { get; init; }

    public required IReadOnlyList<string> ReadOnlyFolders { get; init; }

    public required IReadOnlyList<string> ConfigDenied { get; init; }

    /// <summary>The CLI's config/credential folders as detected (before <see cref="ConfigDenied"/> expands them).</summary>
    public IReadOnlyList<string> ConfigFolders { get; init; } = [];

    /// <summary>Folders of <see cref="Command"/>, first on the sandbox's PATH (a CLI that re-runs itself by name).</summary>
    public required IReadOnlyList<string> PathFolders { get; init; }

    public string? Version { get; init; }

    public string InstallKind { get; init; } = "other";

    /// <summary>True when the CLI was not found and this stands in for a probe run only (no CLI grant at all).</summary>
    public bool Placeholder { get; init; }

    /// <summary>
    /// The host's own sign-in for this CLI: status, the exact files and how the CLI is pointed at them.
    /// Null when the run does not use the host's sign-in (the launcher clears it for any other credential mode).
    /// </summary>
    public CliSignIn? SignIn { get; init; }

    /// <summary>The sign-in files granted read-only: exactly those present on the host whose access is read-only.</summary>
    public IReadOnlyList<string> SignInReadOnly =>
        SignIn?.Files.Where(f => f.Present && f.Access == SignInAccess.ReadOnly).Select(f => f.Path).ToList() ?? [];

    /// <summary>The sign-in files granted read-write (none while every catalog entry is read-only).</summary>
    public IReadOnlyList<string> SignInReadWrite =>
        SignIn?.Files.Where(f => f.Present && f.Access == SignInAccess.ReadWrite).Select(f => f.Path).ToList() ?? [];

    /// <summary>
    /// A sign-in the launcher read from the host's credential store for this one run (Copilot's token), as the CLI's
    /// own variable. Set at launch only, never by detection; never logged.
    /// </summary>
    public IReadOnlyDictionary<string, string> SecretEnvironment { get; init; } = new Dictionary<string, string>();

    /// <summary>The sign-in files COPIED into the member's folders before a run (and back after it), never granted.</summary>
    public IReadOnlyList<CliSignInFile> SignInCopies =>
        SignIn?.Files.Where(f => f.Present && f.Access == SignInAccess.CopyIn).ToList() ?? [];

    /// <summary>The variables that point the CLI at the host's sign-in files (CLAUDE_SECURESTORAGE_CONFIG_DIR, GROK_AUTH_PATH).</summary>
    public IReadOnlyDictionary<string, string> SignInEnvironment => SignIn?.Environment ?? new Dictionary<string, string>();

    private static readonly string[] ScriptExtensions = [".js", ".cjs", ".mjs"];

    /// <summary>
    /// The grants for <paramref name="cli"/> on <paramref name="os"/>. Throws <see cref="ArgumentException"/> naming the
    /// problem when the CLI is missing or its folders cannot be granted without exposing more than the install.
    /// </summary>
    public static CliInstall FromDetected(TargetOs os, string home, DetectedCli cli, IHostFolders folders, Func<string, string>? canonicalize = null)
    {
        var p = new PolicyPaths(os, home, canonicalize);
        if (!cli.Found || string.IsNullOrWhiteSpace(cli.ExecutablePath))
        {
            var why = cli.Problems.Count > 0 ? string.Join("; ", cli.Problems) : "not found on this computer";
            throw new ArgumentException($"{cli.CliId} is not installed ({why}). {cli.InstallHelp}".Trim());
        }
        string R(string path, string what) => p.Resolve(path) ?? throw new ArgumentException($"{cli.CliId}: {what} is not an absolute {os} path: {path}");

        var exe = R(cli.ExecutablePath, "the executable");
        var readOnly = cli.ReadOnlyFolders.Select(f => R(f, "a read-only folder")).Distinct(p.Comparer).ToList();
        var config = cli.ConfigFolders.Select(f => R(f, "a config folder")).Distinct(p.Comparer).ToList();
        if (readOnly.Count == 0) throw new ArgumentException($"{cli.CliId}: detection named no install folder to grant");

        var root = os == TargetOs.Windows ? null : "/";
        foreach (var f in readOnly)
        {
            if (p.IsWithin(p.Home, f))
                throw new ArgumentException($"{cli.CliId}: the install folder {f} contains the home folder; only the install itself may be granted");
            if (root is not null && p.Same(f, root) || f.Length <= 3 && os == TargetOs.Windows)
                throw new ArgumentException($"{cli.CliId}: a whole drive cannot be an install folder ({f})");
            foreach (var c in config)
            {
                if (p.IsWithin(c, f))
                    throw new ArgumentException($"{cli.CliId}: the install folder {f} contains its config folder {c}, which must stay blocked");
            }
        }
        if (!readOnly.Any(f => p.IsWithin(exe, f)))
            throw new ArgumentException($"{cli.CliId}: the executable {exe} is not inside its read-only folders");

        var ext = Path.GetExtension(exe).ToLowerInvariant();
        if (os == TargetOs.Windows && ext is ".cmd" or ".bat" or ".ps1")
            throw new ArgumentException($"{cli.CliId}: {exe} is a {ext} wrapper; the launcher runs the real program, not a shell script");

        var signIn = SignInFor(p, cli, config, readOnly);

        var command = new List<string>();
        if (ScriptExtensions.Contains(ext))
        {
            var nodeName = os == TargetOs.Windows ? "node.exe" : "node";
            var node = readOnly.Select(f => p.Combine(f, nodeName)).FirstOrDefault(folders.Exists)
                ?? throw new ArgumentException($"{cli.CliId}: {exe} is a JavaScript entry point, but no read-only folder holds {nodeName}");
            command.Add(node);
        }
        command.Add(exe);

        var pathFolders = command.Select(c => Parent(p, c)).Where(d => d is not null).Select(d => d!).Distinct(p.Comparer).ToList();

        return new CliInstall
        {
            ExecutablePath = exe,
            Command = command,
            ReadOnlyFolders = readOnly,
            ConfigDenied = ConfigDenials(p, config, [.. readOnly, .. Granted(signIn)], folders),
            ConfigFolders = config,
            PathFolders = pathFolders,
            Version = cli.Version,
            InstallKind = cli.InstallKind,
            SignIn = signIn,
        };
    }

    /// <summary>
    /// The host's sign-in files, resolved and checked: each is one file INSIDE one of the CLI's own config folders
    /// (never a folder, never the config folder itself, never inside an install folder), so the grant can only be that
    /// CLI's own sign-in. The variables must name those files or their folder.
    /// </summary>
    private static CliSignIn? SignInFor(PolicyPaths p, DetectedCli cli, IReadOnlyList<string> config, IReadOnlyList<string> readOnly)
    {
        if (cli.SignIn is not { } s) return null;
        var files = new List<CliSignInFile>();
        foreach (var f in s.Files)
        {
            // A credential-store entry (Copilot) is not a file: nothing is granted for it, so there is nothing to check.
            if (f.Access == SignInAccess.HostStore)
            {
                files.Add(f);
                continue;
            }
            var path = p.Resolve(f.Path) ?? throw new ArgumentException($"{cli.CliId}: the sign-in file is not an absolute {p.Os} path: {f.Path}");
            if (!config.Any(c => p.IsWithin(path, c) && !p.Same(path, c)))
                throw new ArgumentException($"{cli.CliId}: the sign-in file {path} is not inside the CLI's own config folder");
            if (readOnly.Any(r => p.Overlaps(path, r)))
                throw new ArgumentException($"{cli.CliId}: the sign-in file {path} overlaps an install folder");
            files.Add(f with { Path = path });
        }
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in s.Environment)
        {
            var value = p.Resolve(v) ?? throw new ArgumentException($"{cli.CliId}: {k} is not an absolute {p.Os} path: {v}");
            if (!files.Any(f => p.Same(f.Path, value) || p.IsWithin(f.Path, value)))
                throw new ArgumentException($"{cli.CliId}: {k} names {value}, which is not a sign-in file or its folder");
            env[k] = value;
        }
        return s with { Files = files, Environment = env };
    }

    private static IEnumerable<string> Granted(CliSignIn? s) => s?.Files.Where(f => f.Present).Select(f => f.Path) ?? [];

    /// <summary>
    /// A stand-in for a probe run when the member's CLI is not installed: the probe never runs the CLI, so nothing of
    /// it is granted. Its "program" is a name in the app's own read-only bin/ that the probe replaces.
    /// </summary>
    public static CliInstall Missing(TargetOs os, string appBinDir, string cliId) => new()
    {
        ExecutablePath = appBinDir.TrimEnd('/', '\\') + (os == TargetOs.Windows ? "\\" : "/") + cliId + "-not-installed",
        Command = [appBinDir.TrimEnd('/', '\\') + (os == TargetOs.Windows ? "\\" : "/") + cliId + "-not-installed"],
        ReadOnlyFolders = [],
        ConfigDenied = [],
        PathFolders = [],
        Placeholder = true,
    };

    /// <summary>
    /// Every config folder that exists is denied, unless a grant (an install folder, or a sign-in file) lies inside it:
    /// then each entry of it that is neither a grant nor on the way to one is denied, recursively along the way to the
    /// grant. Entries created after launch are not granted either: the sandbox denies by default.
    /// </summary>
    internal static IReadOnlyList<string> ConfigDenials(PolicyPaths p, IReadOnlyList<string> config, IReadOnlyList<string> readOnly, IHostFolders folders)
    {
        var denied = new List<string>();
        void Expand(string folder, IReadOnlyList<string> inside)
        {
            foreach (var child in folders.Children(folder))
            {
                var c = p.Normalize(child) ?? child;
                if (inside.Any(g => p.Same(g, c))) continue;          // the grant itself
                // A link (Codex's packages\standalone\current, a junction to the release it runs) resolves to its
                // target when the request is compiled: denying one that leads to a grant would deny the grant. Its
                // target is a real folder of the same config folder, reached and expanded on its own path.
                if (p.Resolve(c) is { } real && !p.Same(real, c) && (inside.Any(g => p.IsWithin(g, real)) || inside.Any(g => p.IsWithin(real, g))))
                    continue;
                var deeper = inside.Where(g => p.IsWithin(g, c)).ToList();
                if (deeper.Count > 0) Expand(c, deeper);              // on the way to a grant
                else denied.Add(c);
            }
        }
        foreach (var c in config)
        {
            if (!folders.Exists(c)) continue;
            var inside = readOnly.Where(g => p.IsWithin(g, c) && !p.Same(g, c)).ToList();
            if (inside.Count == 0) denied.Add(c);
            else Expand(c, inside);
        }
        return denied.Distinct(p.Comparer).ToList();
    }

    private static string? Parent(PolicyPaths p, string path)
    {
        var sep = p.Os == TargetOs.Windows ? '\\' : '/';
        var i = path.TrimEnd(sep).LastIndexOf(sep);
        if (i < 0) return null;
        return p.Normalize(i == 0 ? "/" : path[..(i + (p.Os == TargetOs.Windows && i == 2 ? 1 : 0))]);
    }
}
