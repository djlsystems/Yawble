using System.Runtime.InteropServices;
using Harness.Contracts;
using Harness.Host.Capacity;

namespace Harness.Host;

/// <summary>
/// THE RUNS A KILLED WORKER LEFT RUNNING. A run's child is started in a process group of its own, so
/// when its worker process is killed the child carries on, spending, with nothing left to stop it. A
/// worker therefore keeps, in a file of its own, every run's process group with its leader's start time
/// (<see cref="Record"/>), and a worker starting under the same name stops every group the file still
/// names (<see cref="StopLeftovers"/>) - only when the group's leader is the same process, by its start
/// time, so a number the system has since given to another process is never touched.
/// </summary>
public static class WorkerOrphans
{
    /// <summary>The file a worker named <paramref name="worker"/> keeps its runs' groups in.</summary>
    public static string FileFor(string stateDirectory, WorkerId worker) =>
        Path.Combine(stateDirectory, $"worker-{string.Concat(worker.Value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'))}.groups");

    /// <summary>Writes every registered group with its leader's start time, replacing the file whole.</summary>
    public static void Record(string file, RunProcessGroups groups, string proc = WorkerPaths.Proc)
    {
        var lines = groups.Snapshot().Keys
            .Select(group => StartTime(proc, group) is { } started ? $"{group} {started}" : null)
            .OfType<string>();

        var temp = file + ".tmp";
        File.WriteAllLines(temp, lines);
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>
    /// Stops every group the file names whose leader is still the process it named, then removes the
    /// file. Returns the groups stopped. Never throws.
    /// </summary>
    public static IReadOnlyList<int> StopLeftovers(string file, string proc = WorkerPaths.Proc)
    {
        var stopped = new List<int>();
        try
        {
            if (!File.Exists(file)) return stopped;

            foreach (var line in File.ReadAllLines(file))
            {
                if (line.Split(' ') is not [var g, var s] || !int.TryParse(g, out var group) || group <= 1
                    || !long.TryParse(s, out var started))
                {
                    continue;
                }

                if (StartTime(proc, group) == started && kill(-group, SigKill) == 0) stopped.Add(group);
            }

            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // What could not be read or removed is left: a leftover run outlives this worker as before.
        }

        return stopped;
    }

    /// <summary>A process's start time in clock ticks since boot, or null when it is gone.</summary>
    private static long? StartTime(string proc, int pid)
    {
        try
        {
            var stat = File.ReadAllText(Path.Combine(proc, pid.ToString(), "stat"));
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return fields.Length > 19 && long.TryParse(fields[19], out var ticks) ? ticks : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private const int SigKill = 9;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
