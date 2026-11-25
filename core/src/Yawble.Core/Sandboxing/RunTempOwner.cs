using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Yawble.Core.Sandboxing;

/// <summary>
/// The core that made a run's temp folder (<see cref="RunTempFolder"/>), written in the marker beside it: a hash of the
/// core's data folder, its pid and the process's start time (UTC ticks; a pid alone can be reused). Two app instances
/// of the same Mac user share DARWIN_USER_TEMP_DIR, and this is how one core's sweep tells the other's folders apart.
/// </summary>
public sealed record RunTempOwner(string DataDirHash, int Pid, long StartTicks)
{
    private const string Version = "v1";

    /// <summary>A start time read twice for the same process may differ by rounding; more than this is another process.</summary>
    public static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

    /// <summary>This core process, for the data folder <paramref name="dataDir"/>.</summary>
    public static RunTempOwner ForThisProcess(string dataDir)
    {
        using var self = Process.GetCurrentProcess();
        return new RunTempOwner(HashDataDir(dataDir), self.Id, self.StartTime.ToUniversalTime().Ticks);
    }

    /// <summary>16 hex of the SHA-256 of the full data-folder path (no trailing separator).</summary>
    public static string HashDataDir(string dataDir) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))))[..16];

    /// <summary>Same data folder, pid and start time (within <see cref="StartTolerance"/>).</summary>
    public bool SameProcess(RunTempOwner other) =>
        DataDirHash == other.DataDirHash && Pid == other.Pid && SameStart(StartTicks, other.StartTicks);

    public string Serialize() => string.Join(' ', Version, DataDirHash, Pid.ToString(CultureInfo.InvariantCulture),
        StartTicks.ToString(CultureInfo.InvariantCulture)) + "\n";

    /// <summary>A marker's text; null when it is not exactly what <see cref="Serialize"/> writes.</summary>
    public static RunTempOwner? Parse(string text)
    {
        var parts = text.Trim().Split(' ');
        if (parts.Length != 4 || parts[0] != Version) return null;
        if (parts[1].Length != 16 || !parts[1].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) return null;
        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0) return null;
        if (!long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start <= 0) return null;
        return new RunTempOwner(parts[1], pid, start);
    }

    /// <summary>
    /// True when the owner's process is still running: its pid exists and started when the marker says. A process whose
    /// start time cannot be read counts as alive (its folders are kept).
    /// </summary>
    public static bool IsAlive(RunTempOwner owner)
    {
        Process process;
        try { process = Process.GetProcessById(owner.Pid); }
        catch (ArgumentException) { return false; } // no such pid
        catch (InvalidOperationException) { return false; }
        using (process)
        {
            try
            {
                if (process.HasExited) return false;
                return SameStart(process.StartTime.ToUniversalTime().Ticks, owner.StartTicks);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                return true;
            }
        }
    }

    private static bool SameStart(long a, long b) => Math.Abs(a - b) <= StartTolerance.Ticks;
}
