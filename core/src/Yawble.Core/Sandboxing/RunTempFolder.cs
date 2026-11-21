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

    /// <summary>Makes the run's folder, owner-only; one left by an earlier run of the same id is emptied first.</summary>
    public static void Create(string folder)
    {
        if (Directory.Exists(folder)) Remove(folder);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
        else Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Removes the run's folder and everything in it; never throws (a leftover is the next sweep's).</summary>
    public static bool Remove(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// At core start: removes every run folder (<see cref="IsRunFolderName"/>) in <paramref name="userTempDir"/> whose run
    /// is gone (<paramref name="isLive"/> false for its name). Nothing else there is touched. Returns the names removed.
    /// </summary>
    public static IReadOnlyList<string> Sweep(string userTempDir, Func<string, bool> isLive)
    {
        var removed = new List<string>();
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(userTempDir, Prefix + "*").ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return removed; }
        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (!IsRunFolderName(name) || isLive(name)) continue;
            // A link is never followed out of the temp folder: only a real folder of that name is removed.
            if (new DirectoryInfo(dir).LinkTarget is not null) continue;
            if (Remove(dir)) removed.Add(name);
        }
        return removed;
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
