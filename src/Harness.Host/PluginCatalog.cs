using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE PLUGINS INSTALLED ON THIS HOST, read from <c>&lt;dataRoot&gt;/plugins</c>:
///
/// <code>
/// plugins/&lt;id&gt;/&lt;version&gt;/plugin.json   the manifest (PluginManifest)
/// plugins/&lt;id&gt;/&lt;version&gt;/...           its executable, skills, anything it ships
/// plugins/&lt;id&gt;/active                    one line: the version in use
/// </code>
///
/// With no <c>active</c> file, a plugin with exactly one version directory uses it.
///
/// IDENTITY IS THE MANIFEST'S <c>id</c>, NEVER THE PATH: the directory must agree with it, and the
/// version directory with <c>version</c>, but a member names <c>plugin:&lt;id&gt;</c> and nothing
/// else, so a new version is a new directory and a one-line <c>active</c> change.
///
/// REPLACED, NEVER EDITED IN PLACE. <see cref="Rescan"/> reads the whole tree and swaps the answer
/// in one reference assignment, the same pattern the Agent catalog's <c>Replace</c> uses, so a
/// plugin installed while the Host runs is registered with no restart. Nothing caches a resolved
/// plugin per member: the plugin runner asks <see cref="For"/> on every wake.
///
/// A manifest that is wrong is REFUSED BY NAME, with the field, in <see cref="Refused"/> - never
/// half-loaded.
/// </summary>
public sealed class PluginCatalog(string root)
{
    public const string ActiveFile = "active";

    private volatile PluginScan _scan = new([], []);

    /// <summary>Where plugins are installed on this Host.</summary>
    public string Root => root;

    public IReadOnlyList<InstalledPlugin> Plugins => _scan.Plugins;

    public IReadOnlyList<PluginRefused> Refused => _scan.Refused;

    /// <summary>The installed plugin <paramref name="id"/>, or null.</summary>
    public InstalledPlugin? For(string id) =>
        _scan.Plugins.FirstOrDefault(p => string.Equals(p.Manifest.Id, id, StringComparison.Ordinal));

    /// <summary>Reads the tree again and replaces the answer.</summary>
    public PluginScan Rescan()
    {
        var scan = Scan(root);
        _scan = scan;
        return scan;
    }

    /// <summary>Why <c>plugin:&lt;id&gt;</c> cannot be hired here, or null when it can.</summary>
    public string? RefusalFor(string id)
    {
        if (For(id) is not null) return null;

        var refused = _scan.Refused.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));

        return refused is not null
            ? $"'{MemberRef.ForPlugin(id)}' is installed but was refused: {refused.Reason}"
            : $"'{MemberRef.ForPlugin(id)}' is not installed on this Host. Install it under {root}/{id}/<version>/ and rescan plugins.";
    }

    public static PluginScan Scan(string root)
    {
        var plugins = new List<InstalledPlugin>();
        var refused = new List<PluginRefused>();

        if (!Directory.Exists(root)) return new PluginScan(plugins, refused);

        foreach (var pluginDirectory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileName(pluginDirectory);

            if (!MemberRef.IsValidPluginId(id))
            {
                refused.Add(new PluginRefused(id, pluginDirectory, "the directory name is not a plugin id."));
                continue;
            }

            var (plugin, reason) = Load(pluginDirectory, id);

            if (plugin is not null) plugins.Add(plugin);
            else refused.Add(new PluginRefused(id, pluginDirectory, reason!));
        }

        return new PluginScan(plugins, refused);
    }

    private static (InstalledPlugin? Plugin, string? Reason) Load(string pluginDirectory, string id)
    {
        string version;
        var active = Path.Combine(pluginDirectory, ActiveFile);

        if (File.Exists(active))
        {
            version = File.ReadAllText(active).Trim();
        }
        else
        {
            var versions = Directory.EnumerateDirectories(pluginDirectory).Select(Path.GetFileName).ToList();

            if (versions.Count != 1)
            {
                return (null, versions.Count == 0
                    ? "it has no version directory."
                    : $"it has {versions.Count} versions and no `{ActiveFile}` file naming the one in use.");
            }

            version = versions[0]!;
        }

        if (version.Length == 0 || version.Contains('/') || version.Contains('\\') || version is "." or "..")
        {
            return (null, $"`{ActiveFile}` names '{version}', which is not a version directory.");
        }

        var directory = Path.Combine(pluginDirectory, version);
        var manifestPath = Path.Combine(directory, PluginManifest.FileName);

        if (!File.Exists(manifestPath)) return (null, $"{version}/{PluginManifest.FileName} does not exist.");

        string json;

        try
        {
            json = File.ReadAllText(manifestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (null, $"{version}/{PluginManifest.FileName} could not be read: {exception.Message}");
        }

        var (manifest, refusal) = PluginManifest.Parse(json);

        if (manifest is null) return (null, refusal);

        if (manifest.Id != id) return (null, $"the manifest's `id` is '{manifest.Id}' but its directory is '{id}'.");
        if (manifest.Version != version) return (null, $"the manifest's `version` is '{manifest.Version}' but its directory is '{version}'.");

        // INSIDE ITS OWN DIRECTORY AFTER SYMLINKS ARE RESOLVED. A manifest's relative path is
        // checked for `..` when it is parsed; a symlink is the other way out, and it is only
        // visible here.
        if (Inside(directory, manifest.ExecutableForThisPlatform) is not { } executable)
        {
            return (null, $"`{manifest.ExecutableForThisPlatform}` resolves outside {version}/.");
        }

        if (!File.Exists(executable)) return (null, $"its executable {manifest.ExecutableForThisPlatform} does not exist.");

        if (!OperatingSystem.IsWindows()
            && (File.GetUnixFileMode(executable) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
        {
            return (null, $"its executable {manifest.ExecutableForThisPlatform} is not executable (chmod +x it).");
        }

        foreach (var skill in manifest.Skills)
        {
            if (Inside(directory, skill) is not { } skillPath || !File.Exists(skillPath))
            {
                return (null, $"its skill {skill} does not exist inside {version}/.");
            }
        }

        return (new InstalledPlugin(manifest, directory, executable), null);
    }

    /// <summary>The full path of <paramref name="relative"/> under <paramref name="directory"/>,
    /// every symlink resolved, or null when it lands outside.</summary>
    internal static string? Inside(string directory, string relative)
    {
        var baseDirectory = Resolve(Path.GetFullPath(directory));
        var candidate = Resolve(Path.GetFullPath(Path.Combine(directory, relative)));
        var prefix = baseDirectory.EndsWith(Path.DirectorySeparatorChar) ? baseDirectory : baseDirectory + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, StringComparison.Ordinal) ? candidate : null;
    }

    private static string Resolve(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.LinkTarget is not null)
            {
                return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
            }

            var directoryInfo = new DirectoryInfo(path);
            if (directoryInfo.Exists && directoryInfo.LinkTarget is not null)
            {
                return directoryInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
            }

            // A symlinked PARENT is resolved by walking up.
            var parent = Path.GetDirectoryName(path);
            return parent is null || parent == path ? path : Path.Combine(Resolve(parent), Path.GetFileName(path));
        }
        catch (IOException)
        {
            return path;
        }
    }
}

/// <summary>One installed, valid plugin: its manifest, its version directory, and the executable
/// this machine runs, resolved and checked.</summary>
public sealed record InstalledPlugin(PluginManifest Manifest, string Directory, string Executable);

/// <summary>A plugin directory that was not loaded, and why, in one sentence.</summary>
public sealed record PluginRefused(string Id, string Path, string Reason);

public sealed record PluginScan(IReadOnlyList<InstalledPlugin> Plugins, IReadOnlyList<PluginRefused> Refused);
