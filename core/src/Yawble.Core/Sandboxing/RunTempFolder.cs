using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Yawble.Core.Sandboxing;

/// <summary>
/// macOS: each run's own short temp folder, &lt;DARWIN_USER_TEMP_DIR&gt;yr-&lt;8 hex of the run id's SHA-256&gt;, made before the
/// run, granted read-write to that run only, and removed when the run ends; a sweep at core start removes those left by
/// a crash. TMPDIR and TMPPREFIX point into it. Unix-socket paths (sun_path) hold 104 bytes, and .NET puts a named pipe
/// at $TMPDIR/CoreFxPipe_&lt;name&gt;: the member's tmp folder under Application Support is too long for any pipe, the
/// per-user temp folder (/private/var/folders/xx/&lt;28&gt;/T/, 57 bytes) leaves room. A read-write grant also gives
/// network-bind for its subpath, so a pipe in this folder binds.
/// </summary>
public static partial class RunTempFolder
{
    public const string Prefix = "yr-";

    /// <summary>sun_path on macOS.</summary>
    public const int SocketPathMax = 104;

    /// <summary>What .NET puts between the temp folder and a pipe's name.</summary>
    public const string PipePrefix = "/CoreFxPipe_";

    /// <summary>
    /// The longest pipe name a run's folder must leave room for: 104 - 57 (the canonical per-user temp folder) - 11
    /// ("yr-" and 8 hex) - 12 ("/CoreFxPipe_") = 24, which holds MSBuild's node pipes (MSBuild&lt;pid&gt;).
    /// </summary>
    public const int PipeNameBudget = 24;

    /// <summary>The folder's name for this run: the same run id gives the same name, and no two live runs share one.</summary>
    public static string Name(string runId) =>
        Prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(runId)))[..8];

    /// <summary>The run's folder under <paramref name="userTempDir"/> (no trailing separator).</summary>
    public static string PathFor(string userTempDir, string runId) => userTempDir.TrimEnd('/') + "/" + Name(runId);

    /// <summary>True when a pipe of <see cref="PipeNameBudget"/> characters fits in sun_path inside <paramref name="folder"/>.</summary>
    public static bool Fits(string folder) =>
        Encoding.UTF8.GetByteCount(folder.TrimEnd('/') + PipePrefix) + PipeNameBudget <= SocketPathMax;

    /// <summary>A name this class makes (yr- and 8 lower-case hex), the only names the sweep removes.</summary>
    public static bool IsRunFolderName(string name) => NamePattern().IsMatch(name);

    /// <summary>What follows a run folder's name for its owner marker, the file beside it: yr-&lt;hex&gt;.owner.</summary>
    public const string MarkerSuffix = ".owner";

    /// <summary>
    /// How old a run folder with no readable owner marker must be before the sweep removes it (one made before markers
    /// existed, or whose marker was lost). A younger one may belong to a live core that is writing its marker.
    /// </summary>
    public static readonly TimeSpan UnownedAge = TimeSpan.FromHours(24);

    /// <summary>The owner marker beside <paramref name="folder"/> (never inside it: the run has read-write there).</summary>
    public static string MarkerPath(string folder) => folder.TrimEnd('/', '\\') + MarkerSuffix;

    /// <summary>
    /// Makes the run's folder, owner-only; one left by an earlier run of the same id is emptied first. With
    /// <paramref name="owner"/>, writes the owner marker beside it (owner-only) so another core's sweep keeps it.
    /// </summary>
    public static void Create(string folder, RunTempOwner? owner = null)
    {
        if (Directory.Exists(folder)) Remove(folder);
        if (owner is not null) WriteMarker(MarkerPath(folder), owner);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
        else Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Removes the run's folder and everything in it, then its owner marker; never throws (a leftover is the next sweep's).</summary>
    public static bool Remove(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        RemoveMarker(MarkerPath(folder));
        return true;
    }

    /// <summary>The owner a marker names; null when it is missing, a link, too long or not one this class writes.</summary>
    public static RunTempOwner? ReadMarker(string markerPath)
    {
        try
        {
            var info = new FileInfo(markerPath);
            if (!info.Exists || info.LinkTarget is not null || info.Length > 256) return null;
            return RunTempOwner.Parse(File.ReadAllText(markerPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// At core start: removes the run folders (<see cref="IsRunFolderName"/>) in <paramref name="userTempDir"/> that no live
    /// run of any core uses, and only those. A folder is removed when its marker names <paramref name="self"/> (this very
    /// core process) and its run is gone (<paramref name="isLive"/> false for its name); or its marker names another owner
    /// that <paramref name="ownerAlive"/> says is gone (its pid not running, or running with another start time); or it has
    /// no readable marker and was last written more than <see cref="UnownedAge"/> ago. A folder of another live core
    /// (a second app instance of the same user) is never touched, nor one whose run <paramref name="isLive"/> knows.
    /// A marker with no folder is removed. Links are never followed. Returns the folder names removed.
    /// </summary>
    public static IReadOnlyList<string> Sweep(string userTempDir, Func<string, bool> isLive, RunTempOwner? self = null,
        Func<RunTempOwner, bool>? ownerAlive = null, DateTime? nowUtc = null)
    {
        ownerAlive ??= RunTempOwner.IsAlive;
        var now = nowUtc ?? DateTime.UtcNow;
        var removed = new List<string>();
        List<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(userTempDir, Prefix + "*").ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return removed; }
        var names = entries.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            try
            {
                if (name.EndsWith(MarkerSuffix, StringComparison.Ordinal))
                {
                    // An orphan marker (its folder is gone): removed. A link of that name is removed itself, never followed.
                    var folderName = name[..^MarkerSuffix.Length];
                    if (IsRunFolderName(folderName) && !names.Contains(folderName) && !Directory.Exists(entry)) RemoveMarker(entry);
                    continue;
                }
                if (!IsRunFolderName(name)) continue;
                var dir = new DirectoryInfo(entry);
                // Only a real folder of that name: a link is never followed out of the temp folder, a file is left alone.
                if (!dir.Exists || dir.LinkTarget is not null || isLive(name)) continue;
                if (!ShouldRemove(dir, ReadMarker(MarkerPath(entry)), self, ownerAlive, now)) continue;
                if (Remove(entry)) removed.Add(name);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // One entry that cannot be read is left for the next sweep; the others are still looked at.
            }
        }
        return removed;
    }

    private static bool ShouldRemove(DirectoryInfo dir, RunTempOwner? owner, RunTempOwner? self, Func<RunTempOwner, bool> ownerAlive, DateTime now)
    {
        if (owner is null) return now - dir.LastWriteTimeUtc > UnownedAge;
        if (self is not null && owner.SameProcess(self)) return true; // this core's, and isLive said its run is gone
        bool alive;
        try { alive = ownerAlive(owner); }
        catch (Exception) { alive = true; } // cannot tell: kept
        return !alive;
    }

    private static void WriteMarker(string markerPath, RunTempOwner owner)
    {
        RemoveMarker(markerPath);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(markerPath, options);
        stream.Write(Encoding.UTF8.GetBytes(owner.Serialize()));
    }

    private static void RemoveMarker(string markerPath)
    {
        try
        {
            var info = new FileInfo(markerPath);
            if (info.Exists || info.LinkTarget is not null) info.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>macOS: the person's per-user temp folder (confstr _CS_DARWIN_USER_TEMP_DIR, what `getconf DARWIN_USER_TEMP_DIR`
    /// prints, e.g. /var/folders/xx/…/T/); null elsewhere or when it cannot be read.</summary>
    public static string? DarwinUserTempDir()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        const int CsDarwinUserTempDir = 65537;
        var buffer = new byte[1024];
        var n = (int)confstr(CsDarwinUserTempDir, buffer, (nuint)buffer.Length);
        if (n <= 1 || n > buffer.Length) return null;
        var path = Encoding.UTF8.GetString(buffer, 0, n - 1);
        return path.StartsWith('/') ? path : null;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern nuint confstr(int name, byte[] buf, nuint len);

    [GeneratedRegex("^yr-[0-9a-f]{8}$")]
    private static partial Regex NamePattern();
}
