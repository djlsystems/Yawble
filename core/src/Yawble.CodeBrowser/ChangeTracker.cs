using System.Text;

namespace Yawble.CodeBrowser;

/// <summary>A changed line range, 1-based and inclusive. "removed" is a point: From = To = the line it follows (0: before the first).</summary>
public sealed record ChangeRange(int From, int To, string Kind);

/// <summary>One change the browser saw: a file written (or deleted) at a time, with its line ranges when known.</summary>
public sealed record ChangeEntry(string Path, DateTimeOffset At, IReadOnlyList<ChangeRange> Ranges, bool LinesUnknown, bool Deleted = false);

/// <summary>A line range of the file as it is now, marked by a recent change.</summary>
public sealed record LineMark(int From, int To, string Kind, DateTimeOffset At);

/// <summary>Lines removed recently: shown between lines, after <see cref="AfterLine"/> (0: before the first line).</summary>
public sealed record RemovedMark(int AfterLine, string Text, DateTimeOffset At);

/// <summary>The limits of change tracking (200 changes / 30 min, 20 MB per member, 60 s "live").</summary>
public sealed record TrackerOptions
{
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>A file written without pause is still processed this often.</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromSeconds(2);

    public int LogMax { get; init; } = 200;
    public TimeSpan Recent { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan Live { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The one memory budget of a member's tracking: the files' previous content (snapshots), their per-line marks and
    /// the removed-text notes, all counted together. Past it the least recently used files are forgotten (they keep
    /// only "changed at": their lines are unknown).
    /// </summary>
    public long SnapshotCapBytes { get; init; } = 20L * 1024 * 1024;

    /// <summary>Most files whose lines are tracked at once (least recently used forgotten first, as past the budget).</summary>
    public int MaxTrackedFiles { get; init; } = 2_000;

    /// <summary>Most files one debounce tick reads and diffs; the rest wait for the next tick.</summary>
    public int MaxPerTick { get; init; } = 200;

    /// <summary>Most paths waiting for the debounce; past it the clone is rescanned instead (as after a watcher overflow).</summary>
    public int MaxPending { get; init; } = 20_000;

    /// <summary>Most files a rescan looks at.</summary>
    public int RescanMaxFiles { get; init; } = 50_000;

    /// <summary>Most line ranges one change carries (the file's marks still cover every line).</summary>
    public int MaxRangesPerChange { get; init; } = 200;

    /// <summary>Files changed in the last <see cref="Recent"/> whose content is kept as a baseline when watching starts.</summary>
    public int SeedFiles { get; init; } = 300;

    /// <summary>Whether Start runs a FileSystemWatcher and the debounce timer (off in tests: they call Notify and Process).</summary>
    public bool Watch { get; init; } = true;

    /// <summary>Most removed-line markers kept per file.</summary>
    public int MaxRemovedPerFile { get; init; } = 200;

    /// <summary>Most removed text kept per marker (characters).</summary>
    public int MaxRemovedText { get; init; } = 4000;
}

/// <summary>
/// Watches one member's clone and keeps what changed: a bounded log, the previous content of recently touched files,
/// and, per file, which current lines were added or changed recently and where lines were removed, all within one
/// memory budget and a cap on files (past either, the least recently used file is forgotten and a change to it is
/// "lines unknown"). Changes come from file writes only (FileSystemWatcher, debounced, a bounded number per tick),
/// diffed in process against the last content seen: never from git. When the watcher loses events (its buffer
/// overflowed) the clone is rescanned by modified time and size. Noise folders and links are ignored; hidden files
/// (likely secrets) are marked changed but never read.
/// </summary>
public sealed class ChangeTracker : IDisposable
{
    private const byte KindNone = 0, KindAdded = 1, KindChanged = 2;

    /// <summary>What one tracked file costs besides its arrays and strings (the object, its dictionary entry, its path).</summary>
    private const long FileOverhead = 160;

    private sealed class FileState
    {
        public string[]? Snapshot;
        public long SnapshotBytes; // the file's size, as read
        public long SnapshotMemory; // what the kept lines cost in memory
        public long Cost; // everything this state costs, as counted in _trackedBytes
        public long LastUse;
        public byte[] Kinds = [];
        public long[] At = [];
        public List<RemovedMark> Removed = [];
        public DateTimeOffset? UnknownAt;
    }

    private readonly SafeTree _tree;
    private readonly TrackerOptions _options;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, FileState> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastChanged = new(StringComparer.Ordinal);
    private readonly LinkedList<ChangeEntry> _log = new();
    private readonly Dictionary<string, (DateTimeOffset First, DateTimeOffset Last, bool Created)> _pending = new(StringComparer.Ordinal);
    private long _snapshotBytes;
    private long _trackedBytes;
    private long _useCounter;
    private int _processing;
    private DateTimeOffset? _rescanSince;
    private DateTimeOffset? _lastNotify;
    private DateTimeOffset _startedAt;
    private FileSystemWatcher? _watcher;
    private ITimer? _timer;
    private bool _disposed;
    private bool _started;

    public ChangeTracker(SafeTree tree, TrackerOptions? options = null, TimeProvider? clock = null)
    {
        _tree = tree;
        _options = options ?? new TrackerOptions();
        _clock = clock ?? TimeProvider.System;
    }

    public SafeTree Tree => _tree;

    /// <summary>Raised (outside the lock) for each change processed.</summary>
    public event Action<ChangeEntry>? Changed;

    /// <summary>The start of the member's current run, when it is running: its changes count as recent for the whole run.</summary>
    public Func<DateTimeOffset?>? RunStartedAt { get; set; }

    /// <summary>Raised for each processed path (changed or not) so the branch view can re-check it.</summary>
    internal event Action<string>? Touched;

    /// <summary>The bytes of the files' kept previous content.</summary>
    public long SnapshotBytes { get { lock (_lock) return _snapshotBytes; } }

    /// <summary>Everything the tracking of files keeps (snapshots, line marks, removed text), as counted against the budget.</summary>
    public long TrackedBytes { get { lock (_lock) return _trackedBytes; } }

    /// <summary>How many files have tracked lines now.</summary>
    public int TrackedFiles { get { lock (_lock) return _files.Count; } }

    /// <summary>How many paths wait for the debounce.</summary>
    public int PendingCount { get { lock (_lock) return _pending.Count; } }

    /// <summary>Whether events were lost and the clone will be rescanned on the next tick.</summary>
    public bool NeedsRescan { get { lock (_lock) return _rescanSince is not null; } }

    /// <summary>Starts the FileSystemWatcher and the debounce timer, and keeps recently modified files as baselines.</summary>
    public void Start(bool seed = true)
    {
        lock (_lock)
        {
            if (_disposed || _started || !_tree.Exists) return;
            _started = true;
            _startedAt = _clock.GetUtcNow();
            if (!_options.Watch) return;
            var w = new FileSystemWatcher(_tree.Root)
            {
                IncludeSubdirectories = true,
                // 64 KB: the most a network share allows (larger buffers fail there), and the most worth pinning.
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            w.Changed += (_, e) => Notify(e.FullPath, false);
            w.Created += (_, e) => Notify(e.FullPath, true);
            w.Deleted += (_, e) => Notify(e.FullPath, false);
            w.Renamed += (_, e) =>
            {
                Notify(e.OldFullPath, false);
                Notify(e.FullPath, true);
            };
            w.Error += (_, e) => OnWatcherError(e.GetException());
            w.EnableRaisingEvents = true;
            _watcher = w;
            _timer = _clock.CreateTimer(_ => Tick(), null, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(150));
        }
        if (seed && _options.Watch) _ = Task.Run(Seed);
    }

    private void Tick()
    {
        // A timer callback must never throw (it would end the process): a file that cannot be read is skipped in Handle.
        // Process is single-flight: a tick that comes while the last one still runs does nothing.
        try { Process(_clock.GetUtcNow()); }
        catch (Exception) { }
    }

    private void OnWatcherError(Exception? error)
    {
        // The watcher lost events (its buffer overflowed) or stopped (the share went away): the markers would go stale,
        // so the clone is rescanned. A watcher that stopped is started again when it can be.
        Overflowed(_clock.GetUtcNow());
        if (error is InternalBufferOverflowException) return;
        FileSystemWatcher? w;
        lock (_lock) w = _disposed ? null : _watcher;
        if (w is null) return;
        try
        {
            // Outside the lock: the watcher's own event threads take it in Notify.
            w.EnableRaisingEvents = false;
            w.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or ObjectDisposedException or FileNotFoundException) { }
    }

    /// <summary>Events were lost (the watcher's buffer overflowed, or too many paths waited): the next tick rescans the clone.</summary>
    public void Overflowed(DateTimeOffset now)
    {
        lock (_lock) MarkRescan(now);
    }

    private void MarkRescan(DateTimeOffset now)
    {
        // What changed since the last event that did arrive (or since watching started) may have been lost.
        var since = _lastNotify ?? (_started ? _startedAt : now);
        if (since > now) since = now;
        if (_rescanSince is null || since < _rescanSince) _rescanSince = since;
    }

    public bool Watching { get { lock (_lock) return _started && !_disposed; } }

    private void Notify(string fullPath, bool created)
    {
        var full = Path.GetFullPath(fullPath);
        if (!SafeTree.IsUnder(full, _tree.Root) || full.Length <= _tree.Root.Length) return;
        var rel = full[(_tree.Root.Length + 1)..].Replace('\\', '/');
        Notify(rel, created, _clock.GetUtcNow());
    }

    /// <summary>A write the watcher saw (clone-relative path). Processed by <see cref="Process"/> once it has been quiet for the debounce.</summary>
    public void Notify(string relative, bool created, DateTimeOffset now)
    {
        if (relative.Length == 0 || SafeTree.IsNoise(relative)) return;
        lock (_lock)
        {
            if (_disposed) return;
            if (!Queue(relative, created, now)) return;
            _lastNotify = now;
        }
    }

    /// <summary>Puts a path in the debounce queue (under the lock). A full queue marks the clone for a rescan instead.</summary>
    private bool Queue(string relative, bool created, DateTimeOffset now)
    {
        if (_pending.TryGetValue(relative, out var p))
        {
            _pending[relative] = (p.First, now, p.Created || created);
            return true;
        }
        if (_pending.Count >= _options.MaxPending)
        {
            MarkRescan(now);
            return false;
        }
        _pending[relative] = (now, now, created);
        return true;
    }

    /// <summary>
    /// Processes the writes that have been quiet for the debounce (or waited the longest allowed), at most
    /// <see cref="TrackerOptions.MaxPerTick"/> (the longest waiting first; the rest stay for the next call), after a
    /// rescan when events were lost. Single-flight: a call while another runs returns 0. Returns how many it processed.
    /// </summary>
    public int Process(DateTimeOffset now)
    {
        if (Interlocked.CompareExchange(ref _processing, 1, 0) != 0) return 0;
        try
        {
            DateTimeOffset? rescan;
            lock (_lock)
            {
                rescan = _rescanSince;
                _rescanSince = null;
            }
            if (rescan is { } since) Rescan(since, now);

            List<(string Path, bool Created)> due;
            lock (_lock)
            {
                if (_pending.Count == 0) return 0;
                due = _pending.Where(p => now - p.Value.Last >= _options.Debounce || now - p.Value.First >= _options.MaxWait)
                    .OrderBy(p => p.Value.First)
                    .Take(Math.Max(1, _options.MaxPerTick))
                    .Select(p => (p.Key, p.Value.Created)).ToList();
                foreach (var d in due) _pending.Remove(d.Path);
            }
            foreach (var (path, created) in due) Handle(path, created, now);
            return due.Count;
        }
        finally
        {
            Volatile.Write(ref _processing, 0);
        }
    }

    /// <summary>
    /// After lost events: queues every file modified since <paramref name="since"/> (a little before, for coarse file
    /// times), every tracked file whose size is not its baseline's, and every marked file that is gone. Bounded by
    /// <see cref="TrackerOptions.RescanMaxFiles"/> and the queue's cap (a full queue rescans again on a later tick).
    /// </summary>
    private void Rescan(DateTimeOffset since, DateTimeOffset now)
    {
        var from = (since - TimeSpan.FromSeconds(2)).UtcDateTime;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var (rel, info) in _tree.WalkFiles(_options.RescanMaxFiles))
            {
                seen.Add(rel);
                if (SafeTree.IsNoise(rel)) continue;
                long size;
                DateTime mtime;
                try { size = info.Length; mtime = info.LastWriteTimeUtc; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                bool queue;
                lock (_lock)
                {
                    var state = _files.GetValueOrDefault(rel);
                    queue = mtime >= from || (state?.Snapshot is not null && state.SnapshotBytes != size);
                    if (queue && !Queue(rel, false, now)) return;
                }
            }
            lock (_lock)
            {
                // Deleted while events were lost: what is marked but not there any more.
                foreach (var path in _files.Keys.Concat(_lastChanged.Keys).Distinct().Where(p => !seen.Contains(p)).ToList())
                {
                    if (File.Exists(_tree.Full(path))) continue;
                    if (!Queue(path, false, now)) return;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            lock (_lock) MarkRescan(now);
        }
    }

    private void Handle(string path, bool created, DateTimeOffset now)
    {
        ChangeEntry? entry;
        try
        {
            entry = Compute(path, created, null, now);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CodeBrowserException)
        {
            entry = null;
        }
        Touched?.Invoke(path);
        if (entry is not null) Changed?.Invoke(entry);
    }

    /// <summary>
    /// The browser read a file: when that content differs from the baseline, the change is taken now (so the line
    /// marks match what the reader sees); with no baseline yet, it becomes the baseline.
    /// </summary>
    internal void Saw(string path, string[] lines, long bytes, DateTimeOffset now)
    {
        if (SafeTree.IsNoise(path)) return;
        ChangeEntry? entry = null;
        lock (_lock)
        {
            var state = _files.GetValueOrDefault(path);
            if (state?.Snapshot is null)
            {
                state ??= Track(path);
                state.Kinds = new byte[lines.Length];
                state.At = new long[lines.Length];
                Keep(state, lines, bytes);
                Account(state, now);
                return;
            }
            if (state.Snapshot.AsSpan().SequenceEqual(lines)) return;
            _pending.Remove(path);
            entry = Apply(path, state, lines, bytes, created: false, now);
        }
        if (entry is not null)
        {
            Touched?.Invoke(path);
            Changed?.Invoke(entry);
        }
    }

    private ChangeEntry? Compute(string path, bool created, string[]? known, DateTimeOffset now)
    {
        var full = _tree.Full(path);
        if (Directory.Exists(full)) return null;
        var info = new FileInfo(full);
        if (!info.Exists)
        {
            lock (_lock)
            {
                var had = Forget(path);
                if (!had && !_lastChanged.ContainsKey(path)) return null;
                _lastChanged.Remove(path);
                return Log(new ChangeEntry(path, now, [], LinesUnknown: true, Deleted: true), now);
            }
        }
        if (SafeTree.IsLink(info)) return null;
        if (_tree.IsHidden(path))
        {
            // Likely a secret: marked as changed, never read.
            lock (_lock)
            {
                _lastChanged[path] = now;
                return Log(new ChangeEntry(path, now, [], LinesUnknown: true), now);
            }
        }
        string[]? lines = known;
        long bytes = 0;
        if (lines is null)
        {
            var (stream, _) = _tree.OpenFile(path);
            using (stream)
            {
                var (content, size) = FileKinds.ReadBounded(stream);
                bytes = size;
                if (content is not null && !FileKinds.LooksBinary(content) && FileKinds.CountLines(content) <= FileKinds.MaxPreviewLines)
                    lines = FileKinds.Lines(FileKinds.Decode(content));
            }
        }
        lock (_lock)
        {
            if (_disposed) return null;
            var state = _files.GetValueOrDefault(path) ?? Track(path);
            if (lines is null)
            {
                // Binary or too large: the file is marked, its lines are not known.
                Unknown(state, now);
                Account(state, now);
                _lastChanged[path] = now;
                return Log(new ChangeEntry(path, now, [], LinesUnknown: true), now);
            }
            return Apply(path, state, lines, bytes, created, now);
        }
    }

    /// <summary>Takes a new content of a file into its state (under the lock): the diff against the baseline, or "unknown".</summary>
    private ChangeEntry? Apply(string path, FileState state, string[] lines, long bytes, bool created, DateTimeOffset now)
    {
        IReadOnlyList<DiffHunk> hunks;
        if (state.Snapshot is { } old)
        {
            hunks = LineDiff.Compute(old, lines);
            if (hunks.Count == 0)
            {
                state.LastUse = ++_useCounter;
                return null;
            }
            if (state.Kinds.Length != old.Length)
            {
                state.Kinds = new byte[old.Length];
                state.At = new long[old.Length];
            }
        }
        else if (created)
        {
            hunks = lines.Length == 0 ? [] : [new DiffHunk(0, 0, 0, lines.Length)];
            state.Kinds = [];
            state.At = [];
            state.Removed.Clear();
        }
        else
        {
            // Seen for the first time (or forgotten), already changed: which lines changed is not known (and this
            // content is the baseline now).
            state.Kinds = new byte[lines.Length];
            state.At = new long[lines.Length];
            state.Removed.Clear();
            state.UnknownAt = now;
            Keep(state, lines, bytes);
            Account(state, now);
            _lastChanged[path] = now;
            return Log(new ChangeEntry(path, now, [], LinesUnknown: true), now);
        }

        var ranges = new List<ChangeRange>();
        var kinds = new byte[lines.Length];
        var at = new long[lines.Length];
        int oi = 0, ni = 0;
        var removedNow = new List<RemovedMark>();
        foreach (var h in hunks)
        {
            while (oi < h.OldStart) { kinds[ni] = state.Kinds[oi]; at[ni] = state.At[oi]; oi++; ni++; }
            var kind = h.OldCount > 0 ? KindChanged : KindAdded;
            for (var t = 0; t < h.NewCount; t++) { kinds[ni + t] = kind; at[ni + t] = now.UtcTicks; }
            var full = ranges.Count >= _options.MaxRangesPerChange;
            if (h.NewCount > 0)
            {
                if (!full) ranges.Add(new ChangeRange(ni + 1, ni + h.NewCount, kind == KindAdded ? "added" : "changed"));
            }
            else
            {
                removedNow.Add(new RemovedMark(ni, RemovedText(state.Snapshot!, h.OldStart, h.OldCount), now));
                if (!full) ranges.Add(new ChangeRange(ni, ni, "removed"));
            }
            oi += h.OldCount;
            ni += h.NewCount;
        }
        while (oi < state.Kinds.Length && ni < kinds.Length) { kinds[ni] = state.Kinds[oi]; at[ni] = state.At[oi]; oi++; ni++; }

        // Earlier removal markers move with the lines around them; one inside a replaced block is gone.
        var moved = new List<RemovedMark>();
        foreach (var r in state.Removed)
        {
            var delta = 0;
            var inside = false;
            foreach (var h in hunks)
            {
                if (h.OldStart < r.AfterLine && r.AfterLine < h.OldStart + h.OldCount) { inside = true; break; }
                if (h.OldStart + h.OldCount <= r.AfterLine && !(h.OldCount == 0 && h.OldStart == r.AfterLine)) delta += h.NewCount - h.OldCount;
            }
            if (!inside) moved.Add(r with { AfterLine = r.AfterLine + delta });
        }
        moved.AddRange(removedNow);
        var keepFrom = now - _options.Recent;
        state.Removed = moved.Where(r => r.At >= keepFrom).TakeLast(_options.MaxRemovedPerFile).ToList();
        state.Kinds = kinds;
        state.At = at;
        Keep(state, lines, bytes);
        Account(state, now);
        _lastChanged[path] = now;
        return Log(new ChangeEntry(path, now, ranges, LinesUnknown: false), now);
    }

    /// <summary>The removed lines' text, at most <see cref="TrackerOptions.MaxRemovedText"/> characters (never built longer).</summary>
    private string RemovedText(string[] old, int start, int count)
    {
        var max = _options.MaxRemovedText;
        var sb = new StringBuilder();
        for (var i = start; i < start + count && sb.Length <= max; i++)
        {
            if (i > start) sb.Append('\n');
            var room = max + 1 - sb.Length;
            if (room > 0) sb.Append(old[i], 0, Math.Min(old[i].Length, room));
        }
        return sb.Length > max ? sb.ToString(0, max) + "…" : sb.ToString();
    }

    /// <summary>A new tracked file (under the lock).</summary>
    private FileState Track(string path)
    {
        var state = new FileState { LastUse = ++_useCounter, Cost = FileOverhead };
        _files[path] = state;
        _trackedBytes += FileOverhead;
        return state;
    }

    /// <summary>Stops tracking a file's lines (its "changed at" marker stays). Under the lock.</summary>
    private bool Forget(string path)
    {
        if (!_files.Remove(path, out var gone)) return false;
        _trackedBytes -= gone.Cost;
        _snapshotBytes -= gone.SnapshotBytes;
        return true;
    }

    /// <summary>The file's lines are not known any more: no baseline, no line marks.</summary>
    private void Unknown(FileState state, DateTimeOffset now)
    {
        Drop(state);
        state.Kinds = [];
        state.At = [];
        state.Removed = [];
        state.UnknownAt = now;
    }

    /// <summary>
    /// Counts a state's new cost against the budget (under the lock), then forgets the least recently used other files
    /// while over the budget or the file cap. A file alone over the budget keeps no baseline, then no line marks.
    /// </summary>
    private void Account(FileState state, DateTimeOffset now)
    {
        Recost(state);
        state.LastUse = ++_useCounter;
        while (_trackedBytes > _options.SnapshotCapBytes || _files.Count > _options.MaxTrackedFiles)
        {
            string? victim = null;
            long oldest = long.MaxValue;
            foreach (var (p, s) in _files)
                if (s != state && s.LastUse < oldest) { oldest = s.LastUse; victim = p; }
            if (victim is null) break;
            Forget(victim);
        }
        if (_trackedBytes > _options.SnapshotCapBytes && state.Snapshot is not null)
        {
            Drop(state);
            Recost(state);
        }
        if (_trackedBytes > _options.SnapshotCapBytes)
        {
            Unknown(state, now);
            Recost(state);
        }
    }

    private void Recost(FileState s)
    {
        long removed = 0;
        foreach (var r in s.Removed) removed += 64 + 2L * r.Text.Length;
        var cost = FileOverhead + s.SnapshotMemory + s.Kinds.LongLength + s.At.LongLength * sizeof(long) + removed;
        _trackedBytes += cost - s.Cost;
        s.Cost = cost;
    }

    /// <summary>Keeps a file's content as its baseline (counted by <see cref="Account"/>, which the caller runs next).</summary>
    private void Keep(FileState state, string[] lines, long bytes)
    {
        Drop(state);
        if (bytes <= 0) bytes = lines.Sum(l => (long)l.Length + 1);
        state.LastUse = ++_useCounter;
        long memory = 24 + 8L * lines.Length;
        foreach (var l in lines) memory += 24 + 2L * l.Length;
        if (memory + FileOverhead > _options.SnapshotCapBytes) return;
        state.Snapshot = lines;
        state.SnapshotBytes = bytes;
        state.SnapshotMemory = memory;
        _snapshotBytes += bytes;
    }

    private void Drop(FileState state)
    {
        if (state.Snapshot is null) return;
        _snapshotBytes -= state.SnapshotBytes;
        state.Snapshot = null;
        state.SnapshotBytes = 0;
        state.SnapshotMemory = 0;
    }

    private ChangeEntry Log(ChangeEntry entry, DateTimeOffset now)
    {
        _log.AddLast(entry);
        Prune(now);
        if (_lastChanged.Count > 20_000)
            foreach (var old in _lastChanged.OrderBy(p => p.Value).Take(_lastChanged.Count - 20_000).Select(p => p.Key).ToList())
                _lastChanged.Remove(old);
        return entry;
    }

    private void Prune(DateTimeOffset now)
    {
        var from = now - _options.Recent;
        while (_log.First is { } first && (first.Value.At < from || _log.Count > _options.LogMax)) _log.RemoveFirst();
    }

    /// <summary>The change log, oldest first: the last 200 changes or 30 minutes, whichever is smaller.</summary>
    public IReadOnlyList<ChangeEntry> Log(DateTimeOffset now)
    {
        lock (_lock)
        {
            Prune(now);
            return [.. _log];
        }
    }

    /// <summary>When a file was last seen changing (null: not since watching started, or long ago).</summary>
    public DateTimeOffset? LastChanged(string path)
    {
        lock (_lock) return _lastChanged.TryGetValue(path, out var at) ? at : null;
    }

    /// <summary>"live" (last 60 s), "recent" (last 30 min, or during the member's current run) or null, for a file; for a folder, the strongest inside.</summary>
    public string? Marker(string path, bool folder, DateTimeOffset now)
    {
        if (SafeTree.IsNoise(path)) return null;
        var recentFrom = now - _options.Recent;
        if (RunStartedAt?.Invoke() is { } run && run < recentFrom) recentFrom = run;
        DateTimeOffset? newest = null;
        lock (_lock)
        {
            if (!folder) newest = _lastChanged.TryGetValue(path, out var at) ? at : null;
            else
            {
                var prefix = path.Length == 0 ? "" : path + "/";
                foreach (var (p, at) in _lastChanged)
                    if (p.StartsWith(prefix, StringComparison.Ordinal) && (newest is null || at > newest)) newest = at;
            }
        }
        if (newest is null) return null;
        if (now - newest.Value <= _options.Live) return "live";
        return newest.Value >= recentFrom ? "recent" : null;
    }

    /// <summary>
    /// The recent line marks and removals of a file as it is now (the baseline's coordinates). A file that changed
    /// recently but whose lines were forgotten (past the budget) has none, and its lines are unknown.
    /// </summary>
    public (IReadOnlyList<LineMark> Lines, IReadOnlyList<RemovedMark> Removed, bool LinesUnknown) Marks(string path, DateTimeOffset now)
    {
        var from = now - _options.Recent;
        lock (_lock)
        {
            if (!_files.TryGetValue(path, out var s))
                return ([], [], _lastChanged.TryGetValue(path, out var changed) && changed >= from);
            var marks = new List<LineMark>();
            var i = 0;
            while (i < s.Kinds.Length)
            {
                if (s.Kinds[i] == KindNone || s.At[i] < from.UtcTicks) { i++; continue; }
                var j = i;
                while (j + 1 < s.Kinds.Length && s.Kinds[j + 1] == s.Kinds[i] && s.At[j + 1] == s.At[i]) j++;
                marks.Add(new LineMark(i + 1, j + 1, s.Kinds[i] == KindAdded ? "added" : "changed", new DateTimeOffset(s.At[i], TimeSpan.Zero)));
                i = j + 1;
            }
            return (marks, s.Removed.Where(r => r.At >= from).ToList(), s.UnknownAt is { } u && u >= from);
        }
    }

    /// <summary>
    /// The most recently edited file and its latest changed line: from the log (the newest change of a file that is
    /// still there and not hidden); before any change is seen, the newest file by modified time (line 1).
    /// </summary>
    public (string Path, int Line)? Latest(DateTimeOffset now)
    {
        List<ChangeEntry> log;
        lock (_lock)
        {
            Prune(now);
            log = [.. _log];
        }
        for (var i = log.Count - 1; i >= 0; i--)
        {
            var e = log[i];
            if (e.Deleted || _tree.IsHidden(e.Path) || !File.Exists(_tree.Full(e.Path))) continue;
            var first = e.Ranges.FirstOrDefault();
            return (e.Path, first is null ? 1 : Math.Max(1, first.From));
        }
        if (log.Count > 0) return null;
        (string Path, DateTime Mtime)? newest = null;
        foreach (var (rel, info) in _tree.WalkFiles())
        {
            DateTime m;
            try { m = info.LastWriteTimeUtc; } catch (IOException) { continue; }
            if (newest is null || m > newest.Value.Mtime) newest = (rel, m);
        }
        return newest is { } n ? (n.Path, 1) : null;
    }

    /// <summary>Keeps the content of files modified in the last 30 minutes as baselines (newest first), so their next change has lines.</summary>
    internal void Seed()
    {
        try
        {
            var from = _clock.GetUtcNow() - _options.Recent;
            var recent = _tree.WalkFiles()
                .Select(f => (f.Relative, f.Info, Mtime: SafeMtime(f.Info)))
                .Where(f => f.Mtime >= from.UtcDateTime && f.Info.Length <= FileKinds.MaxPreviewBytes)
                .OrderByDescending(f => f.Mtime)
                .Take(_options.SeedFiles)
                .ToList();
            long used = 0;
            foreach (var (rel, info, _) in recent)
            {
                // At most half the budget (a file's lines cost about twice its bytes and more).
                if (used + 2 * info.Length > _options.SnapshotCapBytes / 2) break;
                lock (_lock)
                {
                    if (_disposed) return;
                    if (_files.TryGetValue(rel, out var s) && s.Snapshot is not null) continue;
                }
                try
                {
                    var (stream, _) = _tree.OpenFile(rel);
                    using (stream)
                    {
                        var (bytes, size) = FileKinds.ReadBounded(stream);
                        if (bytes is null || FileKinds.LooksBinary(bytes) || FileKinds.CountLines(bytes) > FileKinds.MaxPreviewLines) continue;
                        var lines = FileKinds.Lines(FileKinds.Decode(bytes));
                        lock (_lock)
                        {
                            if (_disposed) return;
                            if (_files.TryGetValue(rel, out var s) && s.Snapshot is not null) continue;
                            if (_pending.ContainsKey(rel)) continue; // changing right now: its first change stays "unknown" rather than wrong
                            var state = s ?? Track(rel);
                            state.Kinds = new byte[lines.Length];
                            state.At = new long[lines.Length];
                            Keep(state, lines, size);
                            Account(state, _clock.GetUtcNow());
                        }
                        used += 2 * size;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or CodeBrowserException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static DateTime SafeMtime(FileInfo f)
    {
        try { return f.LastWriteTimeUtc; } catch (IOException) { return DateTime.MinValue; }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _watcher?.Dispose();
            _watcher = null;
            _timer?.Dispose();
            _timer = null;
            _pending.Clear();
        }
    }
}
