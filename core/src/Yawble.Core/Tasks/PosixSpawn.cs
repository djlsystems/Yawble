using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Yawble.Core.Tasks;

/// <summary>
/// Starts a program on Linux or macOS as the leader of its own process group, with posix_spawn and
/// POSIX_SPAWN_SETPGROUP (pgroup 0), so the whole tree can be signalled with kill(-pgid) without a setsid binary
/// (macOS has none, and on Linux it is util-linux's). stdin is /dev/null; stdout and stderr share one pipe; the
/// signals .NET ignores (SIGPIPE) are reset to their defaults and the signal mask is cleared, as an ordinary exec
/// from a shell would leave them.
/// </summary>
internal sealed class PosixSpawn : IDisposable
{
    // The same values in glibc, musl and macOS's spawn.h.
    private const short POSIX_SPAWN_SETPGROUP = 0x02;
    private const short POSIX_SPAWN_SETSIGDEF = 0x04;
    private const short POSIX_SPAWN_SETSIGMASK = 0x08;
    private const short POSIX_SPAWN_CLOEXEC_DEFAULT_MACOS = 0x4000; // macOS only
    private const int O_RDONLY = 0;
    private const int F_SETFD = 2;
    private const int FD_CLOEXEC = 1;
    private const int EINTR = 4;
    private const int ECHILD = 10;
    public const int SIGTERM = 15;
    public const int SIGKILL = 9;
    private static readonly int[] ResetToDefault = [1 /* HUP */, 2 /* INT */, 3 /* QUIT */, 13 /* PIPE */, 15 /* TERM */];

    // posix_spawnattr_t and posix_spawn_file_actions_t are structs on glibc (under 400 bytes) and pointers on macOS;
    // sigset_t is 128 bytes on glibc and 4 on macOS. Generous buffers fit every one.
    private const int OpaqueSize = 1024;
    private const int SigsetSize = 256;

    public int Pid { get; }
    /// <summary>The read end of the child's stdout+stderr pipe.</summary>
    public Stream Output { get; }

    private PosixSpawn(int pid, Stream output)
    {
        Pid = pid;
        Output = output;
    }

    /// <summary>
    /// Starts the program, or throws <see cref="EntryPointNotFoundException"/> / <see cref="DllNotFoundException"/>
    /// when this libc lacks what is needed (posix_spawn_file_actions_addchdir_np: glibc 2.29+, macOS 10.15+), so the
    /// caller can fall back, and <see cref="IOException"/> when the spawn itself fails.
    /// </summary>
    public static PosixSpawn Start(string program, IReadOnlyList<string> args, string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        var fds = new int[2];
        if (pipe(fds) != 0) throw new IOException($"pipe failed (errno {Marshal.GetLastPInvokeError()})");
        var (readFd, writeFd) = (fds[0], fds[1]);
        // Neither end leaks into the child's own descendants: the child gets the write end only as its 1 and 2. On Linux
        // both ends are close-on-exec (fcntl; Linux passes its variadic argument in a register as P/Invoke does); macOS
        // has no pipe2 and passes variadic arguments on the stack on arm64, so there POSIX_SPAWN_CLOEXEC_DEFAULT closes
        // every descriptor but 0, 1 and 2 in the child instead.
        if (!OperatingSystem.IsMacOS())
        {
            fcntl(readFd, F_SETFD, FD_CLOEXEC);
            fcntl(writeFd, F_SETFD, FD_CLOEXEC);
        }

        var attr = Marshal.AllocHGlobal(OpaqueSize);
        var actions = Marshal.AllocHGlobal(OpaqueSize);
        var sigdef = Marshal.AllocHGlobal(SigsetSize);
        var sigmask = Marshal.AllocHGlobal(SigsetSize);
        var argv = Strings([program, .. args]);
        var envp = Strings(environment.Select(kv => kv.Key + "=" + kv.Value));
        bool attrMade = false, actionsMade = false;
        try
        {
            Zero(attr, OpaqueSize); Zero(actions, OpaqueSize); Zero(sigdef, SigsetSize); Zero(sigmask, SigsetSize);
            Check(posix_spawnattr_init(attr), "posix_spawnattr_init");
            attrMade = true;
            Check(posix_spawn_file_actions_init(actions), "posix_spawn_file_actions_init");
            actionsMade = true;

            Check(sigemptyset(sigdef) == 0 ? 0 : -1, "sigemptyset");
            foreach (var s in ResetToDefault) sigaddset(sigdef, s);
            Check(sigemptyset(sigmask) == 0 ? 0 : -1, "sigemptyset");
            Check(posix_spawnattr_setsigdefault(attr, sigdef), "posix_spawnattr_setsigdefault");
            Check(posix_spawnattr_setsigmask(attr, sigmask), "posix_spawnattr_setsigmask");
            Check(posix_spawnattr_setpgroup(attr, 0), "posix_spawnattr_setpgroup");
            var flags = (short)(POSIX_SPAWN_SETPGROUP | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK | (OperatingSystem.IsMacOS() ? POSIX_SPAWN_CLOEXEC_DEFAULT_MACOS : 0));
            Check(posix_spawnattr_setflags(attr, flags), "posix_spawnattr_setflags");

            Check(posix_spawn_file_actions_addopen(actions, 0, "/dev/null", O_RDONLY, 0), "addopen /dev/null");
            Check(posix_spawn_file_actions_adddup2(actions, writeFd, 1), "adddup2 stdout");
            Check(posix_spawn_file_actions_adddup2(actions, writeFd, 2), "adddup2 stderr");
            // Throws EntryPointNotFoundException on an old libc: the caller falls back.
            Check(posix_spawn_file_actions_addchdir_np(actions, workingDirectory), "addchdir");

            int pid;
            var rc = program.Contains('/')
                ? posix_spawn(out pid, program, actions, attr, argv, envp)
                : posix_spawnp(out pid, program, actions, attr, argv, envp);
            if (rc != 0) throw new IOException($"could not start {program} (posix_spawn error {rc})");
            close(writeFd);
            writeFd = -1;
            var stream = new FileStream(new SafeFileHandle(readFd, ownsHandle: true), FileAccess.Read, 1);
            readFd = -1;
            return new PosixSpawn(pid, stream);
        }
        finally
        {
            if (actionsMade) posix_spawn_file_actions_destroy(actions);
            if (attrMade) posix_spawnattr_destroy(attr);
            Marshal.FreeHGlobal(attr);
            Marshal.FreeHGlobal(actions);
            Marshal.FreeHGlobal(sigdef);
            Marshal.FreeHGlobal(sigmask);
            Free(argv);
            Free(envp);
            if (writeFd >= 0) close(writeFd);
            if (readFd >= 0) close(readFd);
        }
    }

    /// <summary>Waits for the child to end (blocking: run on its own thread) and returns its exit code, 128+signal when a signal ended it.</summary>
    public int WaitForExit()
    {
        while (true)
        {
            var r = waitpid(Pid, out var status, 0);
            if (r == Pid)
            {
                var sig = status & 0x7f;
                return sig == 0 ? (status >> 8) & 0xff : 128 + sig;
            }
            var errno = Marshal.GetLastPInvokeError();
            if (r == -1 && errno == EINTR) continue;
            // ECHILD: someone else reaped it (it has ended either way).
            if (r == -1 && errno == ECHILD) return -1;
            throw new IOException($"waitpid failed (errno {errno})");
        }
    }

    /// <summary>Signals the child's whole process group. Quiet: a group that is already gone (ESRCH) is not an error.</summary>
    public static void SignalGroup(int pgid, int signal)
    {
        if (pgid > 0) _ = kill(-pgid, signal);
    }

    public void Dispose() => Output.Dispose();

    private static void Check(int rc, string what)
    {
        if (rc != 0) throw new IOException($"{what} failed ({rc})");
    }

    private static void Zero(nint p, int size)
    {
        for (var i = 0; i < size; i += 8) Marshal.WriteInt64(p, i, 0);
    }

    /// <summary>A NULL-terminated array of NUL-terminated UTF-8 strings, as argv and envp.</summary>
    private static nint Strings(IEnumerable<string> values)
    {
        var list = values.ToList();
        var array = Marshal.AllocHGlobal(nint.Size * (list.Count + 1));
        for (var i = 0; i < list.Count; i++)
        {
            var bytes = Encoding.UTF8.GetBytes(list[i] + "\0");
            var s = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, s, bytes.Length);
            Marshal.WriteIntPtr(array, i * nint.Size, s);
        }
        Marshal.WriteIntPtr(array, list.Count * nint.Size, 0);
        return array;
    }

    private static void Free(nint array)
    {
        for (var i = 0; ; i++)
        {
            var s = Marshal.ReadIntPtr(array, i * nint.Size);
            if (s == 0) break;
            Marshal.FreeHGlobal(s);
        }
        Marshal.FreeHGlobal(array);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int pipe(int[] fds);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc")]
    private static extern int sigemptyset(nint set);

    [DllImport("libc")]
    private static extern int sigaddset(nint set, int signo);

    [DllImport("libc")]
    private static extern int posix_spawnattr_init(nint attr);

    [DllImport("libc")]
    private static extern int posix_spawnattr_destroy(nint attr);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setflags(nint attr, short flags);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setpgroup(nint attr, int pgroup);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setsigdefault(nint attr, nint set);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setsigmask(nint attr, nint set);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_init(nint actions);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_destroy(nint actions);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_addopen(nint actions, int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int oflag, int mode);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_adddup2(nint actions, int fd, int newfd);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_addchdir_np(nint actions, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport("libc")]
    private static extern int posix_spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint actions, nint attr, nint argv, nint envp);

    [DllImport("libc")]
    private static extern int posix_spawnp(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string file, nint actions, nint attr, nint argv, nint envp);
}
