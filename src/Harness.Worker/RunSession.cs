using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Harness.Host;

/// <summary>
/// HOW A RUN'S SESSION IS ENDED. A run's child, and the Concierge's terminal, start in a session of
/// their own (<c>setsid</c>, or the PTY's), so the session id is the leader's pid. A process the run
/// starts may move itself into ANOTHER PROCESS GROUP of that session (<c>setpgid</c>, a <c>set -m</c>
/// background job, <c>nohup</c> under job control), which a kill of the leader's group never reaches.
/// <see cref="End"/> therefore reads every process's session from <c>/proc/&lt;pid&gt;/stat</c>
/// (field 6) and SIGKILLs each process group whose session is the run's, then looks again until none
/// is left or <see cref="SweepBound"/> passes, so a process forked during the sweep is caught too.
///
/// <para>
/// WHAT IT CANNOT REACH: a process that calls <c>setsid</c> itself leaves the session, and no session
/// sweep finds it. Where the run has its own cgroup, <see cref="RunCgroup"/> kills that too
/// (<c>cgroup.kill</c>), which does reach it.
/// </para>
/// <para>
/// NEVER OUTSIDE THE SESSION. A process group cannot span sessions, so a group seen in the run's
/// session holds nothing else; the worker's own session is refused outright.
/// </para>
/// </summary>
public static class RunSession
{
    /// <summary>How long the sweep keeps looking for a process forked while it ran.</summary>
    public static readonly TimeSpan SweepBound = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Ends every process of session <paramref name="session"/>: its leader's group at once,
    /// then every other group the session holds, until none is left alive or
    /// <paramref name="bound"/> (<see cref="SweepBound"/>) passes. Returns the groups signalled.
    /// Never throws; the worker's own session, and ids 0 and 1, are never signalled.
    /// </summary>
    public static IReadOnlyCollection<int> End(int session, string proc = WorkerPaths.Proc, TimeSpan? bound = null)
    {
        var signalled = new HashSet<int>();
        if (session <= 1 || session == OwnSession()) return signalled;

        // The leader's own group first, whatever /proc says: never weaker than a group kill.
        if (kill(-session, SigKill) == 0) signalled.Add(session);

        var limit = bound ?? SweepBound;
        var clock = Stopwatch.StartNew();

        while (true)
        {
            var groups = Groups(session, proc);
            if (groups.Count == 0) break;

            foreach (var group in groups)
            {
                if (group > 1 && kill(-group, SigKill) == 0) signalled.Add(group);
            }

            if (clock.Elapsed >= limit) break;
            Thread.Sleep(Pause);
        }

        return signalled;
    }

    /// <summary>
    /// Whether <paramref name="pid"/> is a child of this process: read as a terminal starts, so its
    /// session is swept when it ends only when the pid is one this worker really started.
    /// </summary>
    public static bool StartedHere(int pid, string proc = WorkerPaths.Proc) =>
        pid > 1 && Stat(proc, pid) is { } stat && stat.Parent == Environment.ProcessId;

    /// <summary>
    /// The process group of every process in <paramref name="session"/> that is still running. A zombie
    /// is already dead and waits only to be reaped, so it does not count.
    /// </summary>
    public static HashSet<int> Groups(int session, string proc = WorkerPaths.Proc)
    {
        var groups = new HashSet<int>();
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateDirectories(proc);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return groups;
        }

        foreach (var entry in entries)
        {
            if (!int.TryParse(Path.GetFileName(entry), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
            if (Stat(proc, pid) is { } stat && stat.Session == session && stat.State != 'Z') groups.Add(stat.Group);
        }

        return groups;
    }

    /// <summary>
    /// State (field 3), parent (4), group (5) and session (6) of a <c>stat</c> line, or null when the
    /// process is gone or cannot be read. Counted from the LAST ")", since the command name may hold
    /// spaces and parentheses.
    /// </summary>
    internal static (char State, int Parent, int Group, int Session)? Stat(string proc, int pid)
    {
        string text;
        try
        {
            text = File.ReadAllText(Path.Combine(proc, pid.ToString(CultureInfo.InvariantCulture), "stat"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var close = text.LastIndexOf(')');
        if (close < 0) return null;

        var fields = text[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 3
            && fields[0].Length > 0
            && int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parent)
            && int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var group)
            && int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var session)
                ? (fields[0][0], parent, group, session)
                : null;
    }

    private static int OwnSession()
    {
        try
        {
            return getsid(0);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return -1;
        }
    }

    private const int SigKill = 9;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [DllImport("libc", SetLastError = true)]
    private static extern int getsid(int pid);
}
