using System.Runtime.InteropServices;

namespace Harness.Host;

/// <summary>What an install did, or why it did nothing. <paramref name="Status"/> is the HTTP status
/// the route answers: 200 written (and <paramref name="Installed"/> is the Host's verdict after the
/// rescan), 400 refused before anything was written, 409 that version is there and replacing was not
/// asked for.</summary>
public sealed record PluginInstallResult(
    int Status, string? Id, string? Version, bool Installed, bool Replaced, string? Reason);

/// <summary>
/// INSTALLS ONE BUILT PLUGIN VERSION FROM A FOLDER ALREADY INSIDE THE INSTANCE - a team's worktree, a
/// Concierge's workspace - with no copy out to the operator's computer. One implementation for both
/// ways a person asks: <c>POST /api/plugins/install</c> (the web app) and the <c>.install</c> request
/// file the operator CLI's <c>plugin install --from-instance</c> writes
/// (<see cref="PluginRescanRequests"/>).
///
/// <para>
/// EVERYTHING IS REFUSED BEFORE ANYTHING IS WRITTEN: a folder outside the data root, one reached
/// through a symlink that leaves it, a symlink inside it that leaves the folder, a manifest the
/// catalog would refuse (<see cref="PluginCatalog.Check"/>, the same code a rescan runs), and an
/// existing version unless replacing was asked for.
/// </para>
///
/// <para>
/// THE LAYOUT AND MODES ARE THE CLI's: <c>plugins/&lt;id&gt;/&lt;version&gt;/</c>, owned by the Host
/// user with the agent's group, directories 0750, files 0640, the manifest's executables 0750, then
/// <c>active</c> names the version and the catalog is rescanned. The copy is staged beside the target
/// and moved into place, so a half-copied version is never what the catalog reads.
/// </para>
/// </summary>
/// <param name="group">The group every installed file is given (the agent's, which runs plugins), or
/// -1 to leave the Host's own - development and the test suite.</param>
public sealed class PluginInstaller(PluginCatalog catalog, string dataRoot, int group = -1)
{
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    private const UnixFileMode ExecutableMode = FileMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<PluginInstallResult> InstallAsync(string? path, bool replace, CancellationToken ct = default) =>
        ExclusiveAsync(() => InstallLockedAsync(path, replace, ct), ct);

    /// <summary>Runs <paramref name="work"/> while no install runs: <see cref="PluginRemover"/> takes the
    /// same gate, so a version is never removed from under an install of it.</summary>
    public async Task<T> ExclusiveAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);

        try
        {
            return await work();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PluginInstallResult> InstallLockedAsync(string? path, bool replace, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path.Trim()))
        {
            return Refused("Give the absolute path of one built plugin version folder (the folder holding plugin.json).");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        var asked = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

        if (!IsUnder(asked, root))
        {
            return Refused($"{asked} is outside the data root ({root}); only a folder inside the instance can be installed this way.");
        }

        var resolvedRoot = PluginCatalog.Resolved(root);
        var source = PluginCatalog.Resolved(asked);

        if (!IsUnder(source, resolvedRoot))
        {
            return Refused($"{asked} goes through a symlink that leaves the data root (it leads to {source}).");
        }

        if (!Directory.Exists(source)) return Refused($"{asked} is not a folder.");

        if (IsUnder(source, PluginCatalog.Resolved(catalog.Root)) || source == PluginCatalog.Resolved(catalog.Root))
        {
            return Refused($"{asked} is already under the plugins folder; choose the folder the plugin was built into.");
        }

        if (LinkLeaving(source) is { } leaving) return Refused(leaving);

        var manifestPath = Path.Combine(source, PluginManifest.FileName);
        if (!File.Exists(manifestPath)) return Refused($"{asked} holds no {PluginManifest.FileName}; choose one built version of a plugin.");

        string json;

        try
        {
            json = await File.ReadAllTextAsync(manifestPath, ct);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Refused($"{PluginManifest.FileName} could not be read: {exception.Message}");
        }

        var (manifest, refusal) = PluginManifest.Parse(json);
        if (manifest is null) return Refused($"The Host refuses this plugin: {refusal}");

        var (id, version) = (manifest.Id, manifest.Version);

        if (PluginCatalog.Check(source, version, manifest, catalog.RuntimeFound, requireExecutableBit: false) is { Reason: { } checkRefusal })
        {
            return Refused($"The Host refuses {id} {version}: {checkRefusal}", id, version);
        }

        var pluginDirectory = Path.Combine(catalog.Root, id);
        var target = Path.Combine(pluginDirectory, version);
        var exists = Directory.Exists(target) || File.Exists(target);

        if (exists && !replace)
        {
            return new PluginInstallResult(
                409, id, version, false, false,
                $"{id} {version} is already installed; choose Replace (the CLI's --force) to replace it.");
        }

        // WRITING FROM HERE ON.
        var stage = Path.Combine(pluginDirectory, $".incoming-{version}-{Guid.NewGuid():N}");
        string? outgoing = null;

        try
        {
            MakeDirectory(catalog.Root);
            MakeDirectory(pluginDirectory);

            Copy(source, stage);

            foreach (var executable in new[] { manifest.ExecutablePath }.Concat(manifest.Platforms.Values).Distinct(StringComparer.Ordinal))
            {
                var file = Path.Combine(stage, executable);
                if (File.Exists(file) && new FileInfo(file).LinkTarget is null) SetMode(file, ExecutableMode);
            }

            if (exists)
            {
                outgoing = Path.Combine(pluginDirectory, $".outgoing-{version}-{Guid.NewGuid():N}");
                Directory.Move(target, outgoing);
            }

            Directory.Move(stage, target);

            var active = Path.Combine(pluginDirectory, PluginCatalog.ActiveFile);
            var activeTemporary = Path.Combine(pluginDirectory, ".active.tmp");
            await File.WriteAllTextAsync(activeTemporary, version + "\n", ct);
            Own(activeTemporary, FileMode);
            File.Move(activeTemporary, active, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(stage);

            // Put the version that was there back, when it had been moved aside.
            if (outgoing is not null && !Directory.Exists(target))
            {
                try { Directory.Move(outgoing, target); outgoing = null; }
                catch (IOException) { }
            }

            return new PluginInstallResult(500, id, version, false, false, $"{id} {version} could not be written: {exception.Message}");
        }
        finally
        {
            if (outgoing is not null) TryDelete(outgoing);
        }

        await catalog.RescanAsync(ct);

        if (catalog.For(id) is { } loaded && loaded.Manifest.Version == version)
        {
            return new PluginInstallResult(200, id, version, true, exists, null);
        }

        var reason = catalog.Refused.FirstOrDefault(r => r.Id == id)?.Reason
            ?? $"the Host does not list {id} {version} after the rescan.";

        return new PluginInstallResult(200, id, version, false, exists, reason);
    }

    private static PluginInstallResult Refused(string reason, string? id = null, string? version = null) =>
        new(400, id, version, false, false, reason);

    private static bool IsUnder(string path, string directory) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>The first symlink inside <paramref name="source"/> that leads out of it, or is absolute
    /// (a copy of an absolute link would still point at the source, which its builder can change), as
    /// a sentence; null when there is none.</summary>
    private static string? LinkLeaving(string source)
    {
        var pending = new Stack<string>([source]);

        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var info = new FileInfo(entry);
                var relative = Path.GetRelativePath(source, entry);

                if (info.LinkTarget is { } link)
                {
                    if (Path.IsPathRooted(link))
                    {
                        return $"{relative} is a symlink to an absolute path ({link}); a plugin's links must be relative and stay inside its folder.";
                    }

                    var resolved = PluginCatalog.Resolved(entry);
                    if (!IsUnder(resolved, source) && resolved != source)
                    {
                        return $"{relative} is a symlink leading outside the folder ({link}).";
                    }

                    continue;
                }

                if (Directory.Exists(entry)) pending.Push(entry);
            }
        }

        return null;
    }

    /// <summary>Copies the tree, links as links (already checked relative and inside), with the Host's
    /// modes and the agent's group whatever the source had.</summary>
    private void Copy(string from, string to)
    {
        MakeDirectory(to);

        foreach (var entry in Directory.EnumerateFileSystemEntries(from))
        {
            var destination = Path.Combine(to, Path.GetFileName(entry));
            var info = new FileInfo(entry);

            if (info.LinkTarget is { } link)
            {
                File.CreateSymbolicLink(destination, link);
                if (!OperatingSystem.IsWindows() && group >= 0) _ = lchown(destination, -1, group);
            }
            else if (Directory.Exists(entry))
            {
                Copy(entry, destination);
            }
            else if (File.Exists(entry))
            {
                File.Copy(entry, destination);
                Own(destination, FileMode);
            }
        }
    }

    private void MakeDirectory(string directory)
    {
        if (Directory.Exists(directory)) return;

        Directory.CreateDirectory(directory);
        Own(directory, DirectoryMode);
    }

    private void Own(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows()) return;

        if (group >= 0) _ = lchown(path, -1, group);
        SetMode(path, mode);
    }

    private static void SetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int lchown(string path, int owner, int group);
}
