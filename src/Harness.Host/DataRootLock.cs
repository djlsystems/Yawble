using System.Globalization;

namespace Harness.Host;

/// <summary>
/// The third clause of "one data root, one database, one process", which is the one nothing
/// enforced. A serving host holds this for its whole lifetime; another process that opens the same
/// root is refused, by name.
/// </summary>
/// <remarks>
/// <para>
/// It does two jobs. It stops two hosts opening one root — the collision that CORRUPTS rather than
/// complains, and the one <c>scripts\port-free.ps1</c> cannot see because the port is genuinely
/// free. And it is what lets <c>--restore</c> ask whether a host holds THE ROOT IT IS ABOUT TO
/// REPLACE, where the process scan it replaces could only ask whether any host was running anywhere
/// on the machine — so one instance's recovery was blocked by another instance merely being up.
/// </para>
/// <para>
/// THE HANDLE IS THE FACT, never the file's existence. A killed host leaves the file behind — the
/// operating system drops the handle, not the bytes — and a leftover that refused every later start
/// would turn a crash into an instance nobody can boot, recoverable only by knowing to delete a
/// file nothing mentions. The contents are a courtesy: a process id, so a refusal can name what to
/// stop.
/// </para>
/// <para>
/// TWO FILES, because one cannot be both exclusive and readable everywhere. On Windows a file held
/// open with <see cref="FileShare.Read"/> refuses every other writer at the kernel and still lets a
/// reader in, so <c>host.lock</c> alone was the lock AND the pid. On Unix .NET emulates sharing as
/// an advisory <c>flock</c>, and only two ways: <see cref="FileShare.None"/> takes an exclusive
/// lock, every other share mode takes a SHARED one. Two hosts each asking for <c>Read</c> therefore
/// both took shared locks, neither conflicted, and two hosts served one data root on macOS and
/// Linux — found the first night the suite ran there. Holding the pid file with <c>None</c> instead
/// would refuse the readers too, so a refusal could never say WHICH process to stop. So the pid
/// stays in <c>host.lock</c>, held exactly as before, and the exclusivity lives in
/// <c>host.lock.exclusive</c> beside it, opened with <c>None</c> — an exclusive advisory lock on
/// Unix and an exclusive handle on Windows, refused by a second opener on both. That second file is
/// the handle that IS the fact; it holds nothing and is never read.
/// </para>
/// </remarks>
public sealed class DataRootLock : IDisposable
{
    /// <summary>The pid file. Held open, shared for reading, for the life of the host.</summary>
    public const string FileName = "host.lock";

    /// <summary>The exclusive handle beside it. Empty on purpose; see the class remarks.</summary>
    public const string ExclusiveFileName = "host.lock.exclusive";

    public static string PathFor(string dataRoot) => Path.Combine(dataRoot, FileName);

    public static string ExclusivePathFor(string dataRoot) => Path.Combine(dataRoot, ExclusiveFileName);

    /// <summary>The two handles one process holds on one root: the exclusive one that is the
    /// fact, and the pid file that names the holder.</summary>
    private sealed class Entry(FileStream exclusive, FileStream pid)
    {
        public FileStream Exclusive { get; } = exclusive;

        public FileStream Pid { get; } = pid;

        public int Count { get; set; } = 1;

        public void Release()
        {
            // The pid file first, so the moment the root becomes free to another process is the
            // moment the exclusive handle goes - never a moment in which the exclusivity is gone
            // while this process still has the pid file open.
            Pid.Dispose();
            Exclusive.Dispose();
        }
    }

    /// <summary>
    /// What THIS process holds, and how many times.
    ///
    /// The lock is RE-ENTRANT within a process and exclusive between them, because the hazard is
    /// two host PROCESSES writing one messages.db. Two hosts inside one process is the test fixture
    /// standing a second host on the first's data root to prove something outlives a restart, and
    /// refusing that would refuse the specs rather than the danger.
    ///
    /// Counting, rather than opening twice, is also what makes re-entrancy WORK on Unix: an
    /// advisory lock belongs to an open file description, not to a process, so a second open inside
    /// this process would be refused by the first exactly as another process is. One process holds
    /// one exclusive handle per root and counts its leases against it.
    ///
    /// Keyed on <see cref="InstanceIdentity.For"/> rather than on the path as given: that function
    /// already answers "which data root is this" for one directory however its path is spelled, and
    /// two keys for one root would close the file while a second lease still believed it held it.
    /// </summary>
    private static readonly Dictionary<string, Entry> Mine = [];

    private readonly string _key;

    private bool _released;

    private DataRootLock(string key) => _key = key;

    /// <summary>Takes the root, or refuses naming the process that has it.</summary>
    public static DataRootLock Acquire(string dataRoot)
    {
        var key = InstanceIdentity.For(dataRoot);

        lock (Mine)
        {
            if (Mine.TryGetValue(key, out var entry)) entry.Count++;
            else Mine[key] = Open(dataRoot);

            return new DataRootLock(key);
        }
    }

    private static Entry Open(string dataRoot)
    {
        FileStream? exclusive = null;

        try
        {
            // THE FACT, taken first. FileShare.None is an exclusive handle on Windows and an
            // exclusive advisory lock on Unix, and a second opener is refused on both - which is
            // the whole of what this file is for. It is opened for reading and writing rather than
            // writing alone so that no share mode a reader could ask for gets past it on Windows.
            exclusive = new FileStream(
                ExclusivePathFor(dataRoot), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            // FileShare.Read rather than None, deliberately: None would stop anything reading the
            // process id back, so a refusal could say a host holds this root but never which one.
            // On Windows Read is itself enough to refuse a second WRITER, which is what a host is;
            // on Unix it is a shared advisory lock that refuses nothing, and the file above is what
            // refuses. Held open for the life of the host on both, so the pid file cannot be
            // edited or deleted from under a running host any more than it could before.
            var pid = new FileStream(
                PathFor(dataRoot), FileMode.Create, FileAccess.Write, FileShare.Read);

            // AutoFlush, because the whole value of the contents is being readable by another
            // process while this one is still running, and a buffered write reaches the file
            // whenever the stream feels like it. The writer is deliberately not disposed - doing so
            // would close the very handle this method exists to keep open.
            new StreamWriter(pid) { AutoFlush = true }
                .WriteLine(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

            return new Entry(exclusive, pid);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // If the exclusive handle was taken and the pid file then refused, let it go: a refusal
            // that kept the root would leave the next start - and the host that refused this one -
            // meeting a lock nobody is serving behind.
            exclusive?.Dispose();

            var holder = HeldBy(dataRoot);

            throw new DataRootInUseException(
                $"""
                 Another host is already using this data root.

                   root:    {dataRoot}
                   held by: {(holder is null ? "another process" : $"process {holder}")}

                 One data root, one database, one process - two hosts sharing one messages.db is
                 the collision that corrupts rather than complains. Stop that host, or start this
                 one on its own instance with --Instance.
                 """);
        }
    }

    /// <summary>Whether a live process holds this root. The question <c>--restore</c> asks, and it
    /// is about the HANDLE - a lock file left behind by a killed host answers false.</summary>
    /// <remarks>
    /// Both files are probed. The exclusive one is the fact on every platform. The pid file is
    /// probed as well because a host built before the exclusive file existed holds only that one,
    /// and a <c>--restore</c> from a newer build running beside such a host must still refuse; the
    /// probe is the same exclusive open a starting host of that build would have made, and it
    /// conflicts with a shared holder on Windows and on Unix alike.
    /// </remarks>
    public static bool IsHeld(string dataRoot) =>
        IsHeldAt(ExclusivePathFor(dataRoot)) || IsHeldAt(PathFor(dataRoot));

    private static bool IsHeldAt(string path)
    {
        if (!File.Exists(path)) return false;

        try
        {
            // Opening for WRITE with no sharing asks exactly the question a starting host asks,
            // rather than an adjacent one.
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // A read-only file, or one this account cannot open for writing. Neither is a host
            // holding the root, and reporting it as one would refuse a restore forever with a
            // message naming a process that is not there.
            return false;
        }
    }

    /// <summary>The process id in a HELD lock, or null - which means either that nothing holds this
    /// root or that the id could not be read. It only ever makes a refusal more specific, so the
    /// two are one answer here; <see cref="IsHeld"/> is what decides anything.</summary>
    public static int? HeldBy(string dataRoot)
    {
        if (!IsHeld(dataRoot)) return null;

        try
        {
            using var reader = new StreamReader(new FileStream(
                PathFor(dataRoot), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));

            return int.TryParse(
                reader.ReadLine(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                ? id
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Releases this lease, and the root itself once the last one goes.
    ///
    /// Idempotent, because a lease disposed twice must not decrement a count twice — that would
    /// close the file while another lease still held it, and the next process to start would be let
    /// straight in. The FILES are left behind on purpose: deleting them is a second thing that can
    /// fail during shutdown and it buys nothing, since every reader here asks whether the handle is
    /// held.
    /// </summary>
    public void Dispose()
    {
        lock (Mine)
        {
            if (_released) return;

            _released = true;

            if (!Mine.TryGetValue(_key, out var entry)) return;
            if (--entry.Count > 0) return;

            entry.Release();
            Mine.Remove(_key);
        }
    }
}

/// <summary>Refuses a start rather than letting two hosts write one database. See
/// <see cref="DataRootLock"/>.</summary>
public sealed class DataRootInUseException(string message) : Exception(message);
