using System.Runtime.InteropServices;
using System.Text;

namespace Yawble.Core.Launch;

/// <summary>
/// The backstop: the processes that still hold a run folder (their working directory, or a file they have open, is in
/// it). A process that left the run's tree and group - reparented to launchd, a group of its own - is still found by the
/// files it holds, e.g. MSBuild's temp files under yr-*. Only finds them: the caller adopts them into the run's tree
/// (which applies its own start-time guard) and reaps.
/// </summary>
public interface IRunFolderHolders
{
    /// <summary>Pids whose cwd or any open vnode path is the folder or under it. Never the core itself, pid 0 or 1.</summary>
    IReadOnlyList<int> Under(string folder);
}

/// <summary>What <see cref="MacRunFolderHolders"/> reads per process, behind a seam so it is tested with a fake on any OS.</summary>
public interface IProcessPaths
{
    /// <summary>This (the core's) process id: never reported.</summary>
    int Self { get; }

    /// <summary>Every live pid. Throws <see cref="IOException"/> when the list cannot be read.</summary>
    IReadOnlyList<int> Pids();

    /// <summary>The process's working directory; null when it is gone or not ours to read (EPERM).</summary>
    string? Cwd(int pid);

    /// <summary>The paths of the vnodes (files, directories) it has open; empty when it is gone or not ours to read.</summary>
    IReadOnlyList<string> OpenFiles(int pid);

    /// <summary>realpath(3) of the path; the path itself when it cannot be resolved.</summary>
    string RealPath(string path);
}

/// <summary>
/// macOS: walks every process (libproc) and reports the ones whose cwd or open files are under the run folder. Paths are
/// compared as a prefix ending at a separator, against the folder as given and realpath'd (the kernel reports
/// /private/var/... for a folder the core knows as /var/...). A process that is gone or cannot be read is skipped quietly.
/// </summary>
public sealed class MacRunFolderHolders(IProcessPaths paths) : IRunFolderHolders
{
    public static MacRunFolderHolders Instance { get; } = new(LibprocPaths.Instance);

    public IReadOnlyList<int> Under(string folder)
    {
        var roots = Roots(folder);
        if (roots.Count == 0) return [];

        IReadOnlyList<int> pids;
        try { pids = paths.Pids(); }
        catch (Exception e) when (e is IOException or DllNotFoundException or EntryPointNotFoundException) { return []; }

        var found = new SortedSet<int>();
        foreach (var pid in pids)
        {
            if (pid <= 1 || pid == paths.Self || found.Contains(pid)) continue;
            try
            {
                if (IsUnder(paths.Cwd(pid), roots) || paths.OpenFiles(pid).Any(p => IsUnder(p, roots))) found.Add(pid);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Gone between the list and the read, or not ours to read: not a holder we can see.
            }
        }
        return [.. found];
    }

    private List<string> Roots(string folder)
    {
        var given = Trim(folder);
        if (given is null) return [];
        var roots = new List<string> { given };
        string? real;
        try { real = Trim(paths.RealPath(given)); }
        catch (Exception e) when (e is IOException or DllNotFoundException or EntryPointNotFoundException) { real = null; }
        if (real is not null && real != given) roots.Add(real);
        return roots;
    }

    /// <summary>The folder without trailing separators; null for an empty, relative or root folder (it would match everything).</summary>
    private static string? Trim(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/') return null;
        var trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static bool IsUnder(string? path, List<string> roots)
    {
        if (string.IsNullOrEmpty(path)) return false;
        foreach (var root in roots)
        {
            if (!path.StartsWith(root, StringComparison.Ordinal)) continue;
            if (path.Length == root.Length || path[root.Length] == '/') return true;
        }
        return false;
    }
}

/// <summary>macOS libproc: proc_listallpids, PROC_PIDVNODEPATHINFO (cwd), PROC_PIDLISTFDS + PROC_PIDFDVNODEPATHINFO (open files).</summary>
internal sealed class LibprocPaths : IProcessPaths
{
    public static LibprocPaths Instance { get; } = new();

    private const int PROC_PIDLISTFDS = 1;
    private const int PROC_PIDVNODEPATHINFO = 9;
    private const int PROC_PIDFDVNODEPATHINFO = 2;
    private const uint PROX_FDTYPE_VNODE = 1;

    // sys/proc_info.h: struct vinfo_stat is 136 bytes; struct vnode_info (vinfo_stat, vi_type, vi_pad, fsid_t) 152;
    // struct vnode_info_path (vnode_info, char vip_path[MAXPATHLEN = 1024]) 1176.
    private const int VnodeInfoSize = 152;
    private const int VnodeInfoPathSize = VnodeInfoSize + 1024;
    // struct proc_vnodepathinfo { vnode_info_path pvi_cdir; vnode_info_path pvi_rdir; }: the cwd's path at 152.
    private const int VnodePathInfoSize = 2 * VnodeInfoPathSize;
    private const int CwdPathOffset = VnodeInfoSize;
    // struct vnode_fdinfowithpath { proc_fileinfo pfi (24 bytes); vnode_info_path pvip; }: the path at 24 + 152.
    private const int ProcFileInfoSize = 24;
    private const int FdPathInfoSize = ProcFileInfoSize + VnodeInfoPathSize;
    private const int FdPathOffset = ProcFileInfoSize + VnodeInfoSize;
    // struct proc_fdinfo { int32 proc_fd; uint32 proc_fdtype; }.
    private const int FdInfoSize = 8;

    public int Self { get; } = Environment.ProcessId;

    public IReadOnlyList<int> Pids()
    {
        var capacity = Math.Max(1024, proc_listallpids(null, 0) + 256);
        var pids = new int[capacity];
        var n = proc_listallpids(pids, capacity * sizeof(int));
        if (n < 0) throw new IOException($"proc_listallpids failed (errno {Marshal.GetLastPInvokeError()})");
        return [.. pids.Take(Math.Min(n, pids.Length)).Where(p => p > 0)];
    }

    public string? Cwd(int pid)
    {
        var info = new byte[VnodePathInfoSize];
        // 0 or less: gone (ESRCH) or not ours (EPERM).
        if (proc_pidinfo(pid, PROC_PIDVNODEPATHINFO, 0, info, VnodePathInfoSize) != VnodePathInfoSize) return null;
        return CString(info, CwdPathOffset, 1024);
    }

    public IReadOnlyList<string> OpenFiles(int pid)
    {
        var needed = proc_pidinfo(pid, PROC_PIDLISTFDS, 0, null, 0);
        if (needed <= 0) return [];
        // Room for a few opened since the size was asked.
        var buffer = new byte[needed + 32 * FdInfoSize];
        var n = proc_pidinfo(pid, PROC_PIDLISTFDS, 0, buffer, buffer.Length);
        if (n <= 0) return [];
        var files = new List<string>();
        var info = new byte[FdPathInfoSize];
        for (var off = 0; off + FdInfoSize <= Math.Min(n, buffer.Length); off += FdInfoSize)
        {
            if (BitConverter.ToUInt32(buffer, off + 4) != PROX_FDTYPE_VNODE) continue;
            var fd = BitConverter.ToInt32(buffer, off);
            // Closed since the list, or the process is gone: skipped.
            if (proc_pidfdinfo(pid, fd, PROC_PIDFDVNODEPATHINFO, info, FdPathInfoSize) != FdPathInfoSize) continue;
            var path = CString(info, FdPathOffset, 1024);
            if (path is not null) files.Add(path);
        }
        return files;
    }

    public string RealPath(string path)
    {
        var resolved = realpath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero) return path;
        try { return Marshal.PtrToStringUTF8(resolved) ?? path; }
        finally { free(resolved); }
    }

    private static string? CString(byte[] buffer, int offset, int max)
    {
        var end = Array.IndexOf(buffer, (byte)0, offset, max);
        var length = (end < 0 ? offset + max : end) - offset;
        return length == 0 ? null : Encoding.UTF8.GetString(buffer, offset, length);
    }

    [DllImport("libproc", SetLastError = true)]
    private static extern int proc_listallpids(int[]? buffer, int bufferSize);

    [DllImport("libproc", SetLastError = true)]
    private static extern int proc_pidinfo(int pid, int flavor, ulong arg, byte[]? buffer, int bufferSize);

    [DllImport("libproc", SetLastError = true)]
    private static extern int proc_pidfdinfo(int pid, int fd, int flavor, byte[] buffer, int bufferSize);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

    [DllImport("libc")]
    private static extern void free(IntPtr ptr);
}
