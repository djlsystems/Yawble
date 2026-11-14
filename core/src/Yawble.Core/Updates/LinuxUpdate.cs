using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Yawble.Core.Updates;

/// <summary>
/// Linux: a downloaded release tarball may be installed when it holds one top folder with the app (yawble, install.sh,
/// apply-update.sh and the core), and no entry or link that leads outside that folder. There is no signature on
/// Linux: what makes it the publisher's is its SHA-256 in the release's SHA256SUMS-linux, checked before this.
/// </summary>
public sealed class LinuxTarballCheck : IInstallerCheck
{
    public static readonly string[] Required = ["yawble", "install.sh", "apply-update.sh", "yawble-launch.sh", "resources/core/yawble-core"];

    public bool ChecksPublisher => false;

    public string? Problem(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            string? root = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var entries = 0;
            while (tar.GetNextEntry() is { } entry)
            {
                if (++entries > 200_000) return "it has too many entries";
                var name = entry.Name.Replace('\\', '/');
                if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
                name = name.TrimEnd('/');
                if (name.Length == 0) continue;
                if (name.StartsWith('/') || name.Split('/').Any(p => p == ".." || p.Length == 0) || name.Contains('\0'))
                    return $"the entry '{entry.Name}' is not a plain relative path";
                var top = name.Split('/')[0];
                root ??= top;
                if (top != root) return $"it holds more than one top folder ({root}, {top})";
                if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                {
                    var link = entry.LinkName.Replace('\\', '/');
                    var target = entry.EntryType == TarEntryType.HardLink ? link : Resolve(Path.GetDirectoryName(name)!.Replace('\\', '/'), link);
                    if (link.StartsWith('/') || target is null || !(target == root || target.StartsWith(root + "/", StringComparison.Ordinal)))
                        return $"the link '{entry.Name}' points outside the app's folder";
                }
                else if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile
                    or TarEntryType.ContiguousFile or TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes))
                {
                    return $"the entry '{entry.Name}' is a {entry.EntryType}, not a file, folder or link";
                }
                seen.Add(name);
            }
            if (root is null) return "it is empty";
            var missing = Required.Where(r => !seen.Contains($"{root}/{r}")).ToList();
            return missing.Count == 0 ? null : $"it has no {string.Join(", ", missing)}";
        }
        catch (Exception e) when (e is InvalidDataException or IOException or FormatException or ArgumentException)
        {
            return $"it could not be read as a .tar.gz: {e.Message}";
        }
    }

    /// <summary>A link target relative to the folder holding the link, with "." and ".." applied; null when it climbs out.</summary>
    private static string? Resolve(string folder, string link)
    {
        var parts = new List<string>(folder.Length == 0 ? [] : folder.Split('/'));
        foreach (var p in link.Split('/'))
        {
            if (p.Length == 0 || p == ".") continue;
            if (p == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); }
            else parts.Add(p);
        }
        return string.Join('/', parts);
    }
}

/// <summary>
/// Linux: whether this app can replace itself. Only an install that install.sh made in a folder the person may change
/// (the default ~/.local/opt/yawble): the core runs from &lt;install&gt;/resources/core, and the install has yawble,
/// install.sh and apply-update.sh, and both it and the folder holding it are writable (install.sh replaces it). A
/// system-wide install in /opt, or a development run, is updated another way.
/// </summary>
public static class LinuxSelfUpdate
{
    /// <summary>The install folder for a core running from <paramref name="coreDir"/>, or null when it cannot update itself.</summary>
    public static string? InstallFolder(string coreDir, Func<string, bool>? writable = null)
    {
        writable ??= Writable;
        var core = Path.TrimEndingDirectorySeparator(Path.GetFullPath(coreDir));
        var resources = Path.GetDirectoryName(core);
        if (Path.GetFileName(core) != "core" || resources is null || Path.GetFileName(resources) != "resources") return null;
        var prefix = Path.GetDirectoryName(resources);
        var parent = prefix is null ? null : Path.GetDirectoryName(prefix);
        if (prefix is null || parent is null) return null;
        if (!File.Exists(Path.Combine(prefix, "yawble")) || !File.Exists(Path.Combine(prefix, "install.sh")) || !File.Exists(Path.Combine(prefix, "apply-update.sh")))
            return null;
        return writable(prefix) && writable(parent) ? prefix : null;
    }

    private static bool Writable(string folder) => OperatingSystem.IsLinux() && access(folder, 2 /* W_OK */) == 0;

    [DllImport("libc", SetLastError = true)]
    private static extern int access(string path, int mode);
}
