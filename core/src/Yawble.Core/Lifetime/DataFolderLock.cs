namespace Yawble.Core.Lifetime;

/// <summary>
/// One core per data folder. <c>&lt;data&gt;/core.lock</c> is opened with FileShare.None and held for the core's whole life:
/// on Windows that is a share-mode lock, on Linux and macOS .NET takes flock(LOCK_EX | LOCK_NB) for it. The kernel drops
/// either when the process dies, so a crash never leaves the folder locked.
///
/// The holder writes its pid into core.lock (for a person reading the file) and into <c>&lt;data&gt;/core.pid</c>, which is
/// what a refused core reads: while the lock is held no .NET process can open core.lock at all (a reader's shared flock or
/// share mode is refused too). core.pid also carries a random nonce, so the removal watch can tell that the data folder at
/// this path is still the one this core locked (<see cref="StillOurs"/>). core.lock itself is never deleted: deleting a
/// lock file lets a second core lock a new file of the same name while a third still holds the old one.
/// </summary>
public sealed class DataFolderLock : IDisposable
{
    public const string LockFileName = "core.lock";
    public const string PidFileName = "core.pid";

    /// <summary>The core's exit code when another core holds the data folder.</summary>
    public const int InUseExitCode = 5;

    private readonly FileStream _held;
    private readonly string _pidPath;
    private readonly string _pidText;
    private int _disposed;

    private DataFolderLock(FileStream held, string pidPath, string pidText)
    {
        _held = held;
        _pidPath = pidPath;
        _pidText = pidText;
    }

    public int Pid { get; private init; }

    /// <summary>The line for stderr (after "fatal: ") and for the shell's dialog when the folder is in use.</summary>
    public static string InUseMessage(int? pid) =>
        $"another Yawble is using this data folder ({(pid is { } p ? $"pid {p}" : "pid unknown")})";

    /// <summary>Takes the lock on <paramref name="dataDir"/> (which must exist) or says who holds it.</summary>
    /// <remarks>
    /// A refused open is retried for about a second (<see cref="LockWait.Default"/>) before the folder counts as in use: on
    /// Windows a just-killed core's handle on core.lock can still be closing for a few ms after its WaitForExit returns, so an
    /// immediate restart would otherwise be refused. On Linux and macOS the flock goes with the process; the retry is
    /// harmless there.
    /// </remarks>
    public static LockAttempt TryAcquire(string dataDir, int pid) => TryAcquire(dataDir, pid, LockWait.Default, OpenLockFile);

    /// <summary>
    /// <see cref="TryAcquire(string, int)"/> with the wait and the open given: <paramref name="open"/> returns the held
    /// core.lock stream, or null when another process holds it. Tests pass a fake clock and a fake lock.
    /// </summary>
    internal static LockAttempt TryAcquire(string dataDir, int pid, LockWait wait, Func<string, FileStream?> open)
    {
        var lockPath = Path.Combine(dataDir, LockFileName);
        var pidPath = Path.Combine(dataDir, PidFileName);
        var started = wait.Now();
        FileStream? held;
        while ((held = open(lockPath)) is null)
        {
            var waited = wait.Now() - started;
            if (waited >= wait.Budget) return new LockAttempt(null, HeldBy(pidPath, wait));
            var left = wait.Budget - waited;
            wait.Delay(left < wait.Step ? left : wait.Step);
        }
        var text = $"{pid}\n{Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))}\n";
        try
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes($"{pid}\n");
            held.SetLength(0);
            held.Write(bytes);
            held.Flush(flushToDisk: false);
            File.WriteAllText(pidPath, text);
        }
        catch
        {
            held.Dispose();
            throw;
        }
        return new LockAttempt(new DataFolderLock(held, pidPath, text) { Pid = pid }, null);
    }

    /// <summary>
    /// Whether the data folder at the path this core locked is still that folder: its core.pid is still the one this core
    /// wrote. False when the folder (or the file) was removed, or the folder was replaced by another one.
    /// </summary>
    public bool StillOurs()
    {
        try
        {
            return File.ReadAllText(_pidPath) == _pidText;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes core.pid (when it is still ours) and lets the lock go.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try
        {
            if (StillOurs()) File.Delete(_pidPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        _held.Dispose();
    }

    /// <summary>The pid in core.pid; null when it is missing or unreadable (a core that is just starting has not written it yet).</summary>
    private static int? HeldBy(string pidPath, LockWait wait)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var first = File.ReadAllText(pidPath).Split('\n', 2)[0].Trim();
                if (int.TryParse(first, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) && pid > 0)
                    return pid;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (attempt < 2) wait.Delay(TimeSpan.FromMilliseconds(100));
        }
        return null;
    }

    /// <summary>The real open: core.lock with no sharing, or null when another process holds it.</summary>
    internal static FileStream? OpenLockFile(string lockPath)
    {
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException e) when (IsInUse(e))
        {
            return null;
        }
    }

    // Windows: ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION (33). Linux and macOS: .NET's flock refusal (EWOULDBLOCK),
    // an IOException that is not one of the not-found kinds.
    private static bool IsInUse(IOException e)
    {
        if (e is FileNotFoundException or DirectoryNotFoundException or PathTooLongException) return false;
        if (!OperatingSystem.IsWindows()) return true;
        var code = e.HResult & 0xFFFF;
        return code is 32 or 33;
    }
}

/// <summary>
/// How long <see cref="DataFolderLock.TryAcquire(string, int)"/> keeps retrying a held core.lock (<see cref="Budget"/>), how
/// long it waits between tries (<see cref="Step"/>), and the clock and delay it uses (fakes in tests).
/// </summary>
internal sealed record LockWait(TimeSpan Budget, TimeSpan Step, Func<TimeSpan> Now, Action<TimeSpan> Delay)
{
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultStep = TimeSpan.FromMilliseconds(50);

    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    public static LockWait Default { get; } = new(DefaultBudget, DefaultStep, () => Clock.Elapsed, Thread.Sleep);
}

/// <summary>A lock taken (<see cref="Lock"/>), or refused because another core holds it (<see cref="HeldByPid"/>, null when unknown).</summary>
public sealed record LockAttempt(DataFolderLock? Lock, int? HeldByPid)
{
    public bool Acquired => Lock is not null;
}
