using System.Globalization;
using System.Text.Json.Nodes;

namespace Yawble.CodeBrowser;

/// <summary>Which member a code.open names: a team and a member, each by name or by id.</summary>
public sealed record MemberQuery(string? Team, long? TeamId, string? Member, long? MemberId);

/// <summary>A member as the code browser needs it: a stable key, its clone, and where its branch started.</summary>
public sealed record CodeMember(string Key, string Team, string Name, string ClonePath, BranchSource? Branch);

/// <summary>What the core gives the code browser: members, who is running, pushes, git, the policy's denied paths.</summary>
public interface ICodeHost
{
    Task<CodeMember?> FindAsync(MemberQuery query, CancellationToken ct);

    /// <summary>The members running now, with when their run started.</summary>
    Task<IReadOnlyList<(CodeMember Member, DateTimeOffset StartedAt)>> RunningAsync(CancellationToken ct);

    /// <summary>Pushes an event to the window (and, through the Cloud Link, to cloud viewers).</summary>
    void Publish(string name, JsonNode data);

    /// <summary>The hardened host git for "changed on branch"; null: no branch markers.</summary>
    IGitReader? Git { get; }

    /// <summary>Whether an absolute path is one the global guardrail policy denies.</summary>
    bool PolicyDenied(string fullPath);
}

public sealed record CodeBrowserOptions
{
    public TrackerOptions Tracker { get; init; } = new();

    /// <summary>A clone neither running nor open in a Code dialog stops being watched after this.</summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>A session not used for this long is closed.</summary>
    public TimeSpan SessionIdle { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Most sessions one owner (the window, or each cloud viewer) has open; past it that owner's least recently used goes.</summary>
    public int MaxSessions { get; init; } = 64;

    /// <summary>A session gets at most one code.changed this often (the paths changed in between come together in the next).</summary>
    public TimeSpan CoalesceChanges { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Most paths one code.changed carries (the rest come in the next).</summary>
    public int MaxPathsPerEvent { get; init; } = 100;

    /// <summary>Most bytes of a file one code.download answer carries.</summary>
    public int DownloadChunkBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>How often watches are started for running members and stopped after the grace period.</summary>
    public TimeSpan SweepEvery { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long code.list waits for the first "changed on branch" comparison.</summary>
    public TimeSpan BranchWait { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// The code browser's RPCs (code-browser/src/types.ts mirrors them): code.open / list / read / download /
/// close, and the code.changed push. Each open member's clone is watched while a Code dialog is open on it or the member
/// runs, and for a 2-minute grace period after neither holds.
/// </summary>
public sealed class CodeBrowserService : IAsyncDisposable
{
    public const string ChangedEvent = "code.changed";

    /// <summary>The owner of a session a cloud viewer opened starts with this (the window's: "app:").</summary>
    public const string CloudOwnerPrefix = "cloud:";

    /// <summary>Whether a session's owner is a cloud viewer: its code.changed goes up the Cloud Link, and it is not told host paths.</summary>
    public static bool IsCloudOwner(string? owner) => owner?.StartsWith(CloudOwnerPrefix, StringComparison.Ordinal) == true;

    private sealed class Watch(CodeMember member, ChangeTracker tracker, BranchView? branch)
    {
        public CodeMember Member { get; set; } = member;
        public ChangeTracker Tracker { get; } = tracker;
        public BranchView? Branch { get; } = branch;
        public DateTimeOffset? RunStartedAt { get; set; }
        public DateTimeOffset? IdleSince { get; set; }
    }

    private sealed class Session(string id, string key, string? owner, DateTimeOffset at)
    {
        public string Id { get; } = id;
        public string Key { get; } = key;
        public string? Owner { get; } = owner;
        public DateTimeOffset LastUsed { get; set; } = at;

        /// <summary>The changes not pushed yet (latest per path), and when the last code.changed went.</summary>
        public Dictionary<string, ChangeEntry> Unsent { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset? LastPushed { get; set; }
    }

    private readonly ICodeHost _host;
    private readonly CodeBrowserOptions _options;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Watch> _watches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private ITimer? _sweep;
    private ITimer? _flush;
    private int _sweeping;

    public CodeBrowserService(ICodeHost host, CodeBrowserOptions? options = null, TimeProvider? clock = null)
    {
        _host = host;
        _options = options ?? new CodeBrowserOptions();
        _clock = clock ?? TimeProvider.System;
    }

    public static bool Handles(string method) => method.StartsWith("code.", StringComparison.Ordinal);

    /// <summary>Starts the periodic sweep (watches for running members, the grace period, idle sessions).</summary>
    public void Start()
    {
        _sweep ??= _clock.CreateTimer(_ => _ = SweepSafeAsync(), null, TimeSpan.FromSeconds(1), _options.SweepEvery);
    }

    private async Task SweepSafeAsync()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return;
        try { await SweepAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { }
        finally { Volatile.Write(ref _sweeping, 0); }
    }

    /// <param name="owner">Who opened the session (the window, or a cloud viewer): the other calls on it must come from the same.</param>
    public async Task<JsonNode?> DispatchAsync(string method, JsonObject? p, string? owner, CancellationToken ct) => method switch
    {
        "code.open" => await OpenAsync(p, owner, ct).ConfigureAwait(false),
        "code.list" => await ListAsync(p, owner).ConfigureAwait(false),
        "code.read" => await ReadAsync(p, owner, ct).ConfigureAwait(false),
        "code.download" => Download(p, owner),
        "code.close" => Close(p, owner),
        _ => throw new CodeBrowserException("unknown_method", $"no method named '{method}'"),
    };

    // ------------------------------------------------------------------ code.open

    private async Task<JsonNode?> OpenAsync(JsonObject? p, string? owner, CancellationToken ct)
    {
        var query = new MemberQuery(StrOrNull(p, "team"), LongOrNull(p, "team") ?? LongOrNull(p, "teamId"),
            StrOrNull(p, "member"), LongOrNull(p, "member") ?? LongOrNull(p, "memberId"));
        if (query.Team is null && query.TeamId is null && query.MemberId is null)
            throw new CodeBrowserException(CodeBrowserException.BadParams, "team and member are required");
        if (query.Member is null && query.MemberId is null)
            throw new CodeBrowserException(CodeBrowserException.BadParams, "member is required");
        var member = await _host.FindAsync(query, ct).ConfigureAwait(false)
                     ?? throw new CodeBrowserException("no_such_member", "there is no such member");
        var now = _clock.GetUtcNow();
        var watch = EnsureWatch(member, now);
        var id = "cb-" + Guid.NewGuid().ToString("N")[..16];
        lock (_lock)
        {
            ExpireSessions(now);
            // Per owner: a cloud viewer opening many sessions never closes the window's (or another viewer's).
            while (_sessions.Values.Count(s => s.Owner == owner) >= Math.Max(1, _options.MaxSessions))
                _sessions.Remove(_sessions.Values.Where(s => s.Owner == owner).MinBy(s => s.LastUsed)!.Id);
            _sessions[id] = new Session(id, member.Key, owner, now);
            watch.IdleSince = null;
        }
        var present = watch.Tracker.Tree.Exists;
        if (present) _ = watch.Branch?.RefreshAsync(CancellationToken.None);
        JsonObject? latest = null;
        if (present && watch.Tracker.Latest(now) is { } l) latest = new JsonObject { ["path"] = l.Path, ["line"] = l.Line };
        return new JsonObject
        {
            ["session"] = id,
            // The host's folder only to the window: a cloud viewer is told the member's name.
            ["root"] = IsCloudOwner(owner) ? member.Name : watch.Tracker.Tree.Root,
            ["latest"] = latest,
            ["clonePresent"] = present,
        };
    }

    private Watch EnsureWatch(CodeMember member, DateTimeOffset now)
    {
        Watch watch;
        lock (_lock)
        {
            if (_watches.TryGetValue(member.Key, out var known) && known.Tracker.Tree.Root == Path.TrimEndingDirectorySeparator(Path.GetFullPath(member.ClonePath)))
            {
                known.Member = member;
                watch = known;
            }
            else
            {
                if (known is not null) Stop(known);
                var tree = new SafeTree(member.ClonePath, _host.PolicyDenied);
                var tracker = new ChangeTracker(tree, _options.Tracker, _clock);
                var branch = _host.Git is { } git && member.Branch is { } source ? new BranchView(git, tree, source) : null;
                watch = new Watch(member, tracker, branch);
                tracker.RunStartedAt = () => watch.RunStartedAt;
                tracker.Changed += entry => OnChanged(watch, entry);
                if (branch is not null) tracker.Touched += branch.Touch;
                _watches[member.Key] = watch;
            }
        }
        // A clone that is not there yet is watched once it is (the next open, or the member's run).
        if (!watch.Tracker.Watching && watch.Tracker.Tree.Exists) watch.Tracker.Start();
        return watch;
    }

    /// <summary>A change in a watched clone: queued for each session open on it, and pushed at most every <see cref="CodeBrowserOptions.CoalesceChanges"/>.</summary>
    private void OnChanged(Watch watch, ChangeEntry entry)
    {
        lock (_lock)
        {
            foreach (var s in _sessions.Values)
                if (s.Key == watch.Member.Key) s.Unsent[entry.Path] = entry;
        }
        FlushChanges(_clock.GetUtcNow());
    }

    /// <summary>
    /// Pushes the queued changes of every session whose last code.changed is old enough: one event per session, at most
    /// <see cref="CodeBrowserOptions.MaxPathsPerEvent"/> paths, each path once (its latest change). What is left waits
    /// for a timer. The event's path, ranges and at are its newest change; changes lists them all. A window session's
    /// event is marked cloud: false and stays on this computer (the Cloud Link forwards only cloud viewers' sessions).
    /// </summary>
    internal void FlushChanges(DateTimeOffset now)
    {
        var send = new List<(Session Session, List<ChangeEntry> Changes)>();
        lock (_lock)
        {
            var wait = false;
            foreach (var s in _sessions.Values)
            {
                if (s.Unsent.Count == 0) continue;
                if (s.LastPushed is { } last && now - last < _options.CoalesceChanges) { wait = true; continue; }
                var take = s.Unsent.Values.OrderBy(e => e.At).Take(Math.Max(1, _options.MaxPathsPerEvent)).ToList();
                foreach (var e in take) s.Unsent.Remove(e.Path);
                s.LastPushed = now;
                if (s.Unsent.Count > 0) wait = true;
                send.Add((s, take));
            }
            if (wait && _flush is null)
                _flush = _clock.CreateTimer(_ => FlushLater(), null, _options.CoalesceChanges, Timeout.InfiniteTimeSpan);
        }
        foreach (var (session, changes) in send)
        {
            var newest = changes[^1];
            _host.Publish(ChangedEvent, new JsonObject
            {
                ["session"] = session.Id,
                ["path"] = newest.Path,
                ["ranges"] = Ranges(newest),
                ["at"] = Iso(newest.At),
                ["changes"] = new JsonArray(changes.Select(c => (JsonNode?)new JsonObject { ["path"] = c.Path, ["ranges"] = Ranges(c), ["at"] = Iso(c.At) }).ToArray()),
                ["cloud"] = IsCloudOwner(session.Owner),
            });
        }
    }

    private static JsonArray Ranges(ChangeEntry e) =>
        new(e.Ranges.Select(r => (JsonNode?)new JsonObject { ["from"] = r.From, ["to"] = r.To, ["kind"] = r.Kind }).ToArray());

    private void FlushLater()
    {
        lock (_lock)
        {
            _flush?.Dispose();
            _flush = null;
        }
        try { FlushChanges(_clock.GetUtcNow()); }
        catch (Exception) { } // a timer callback must never throw
    }

    // ------------------------------------------------------------------ code.list

    private async Task<JsonNode?> ListAsync(JsonObject? p, string? owner)
    {
        var watch = SessionWatch(p, owner);
        var now = _clock.GetUtcNow();
        watch.Tracker.Process(now);
        if (watch.Branch is { } branch) await branch.WaitAsync(_options.BranchWait).ConfigureAwait(false);
        var tree = watch.Tracker.Tree;
        var (_, relative) = tree.Resolve(StrOrNull(p, "path"));
        var entries = new JsonArray();
        foreach (var e in tree.List(relative))
        {
            var folder = e.Kind == "dir";
            string? marker = null;
            if (!e.Hidden && e.Kind != "link")
                marker = watch.Tracker.Marker(e.Path, folder, now) ?? (watch.Branch?.IsChanged(e.Path, folder) == true ? "branch" : null);
            else if (e.Hidden && e.Kind == "file")
                marker = watch.Tracker.Marker(e.Path, false, now); // a hidden file still shows that it changed, never what
            var o = new JsonObject
            {
                ["name"] = e.Name, ["path"] = e.Path, ["kind"] = e.Kind, ["size"] = e.Size, ["mtime"] = Iso(e.Mtime),
                ["marker"] = marker, ["hidden"] = e.Hidden,
            };
            if (e.Hidden) o["hiddenReason"] = "secrets";
            entries.Add(o);
        }
        return new JsonObject { ["path"] = relative, ["entries"] = entries };
    }

    // ------------------------------------------------------------------ code.read

    private async Task<JsonNode?> ReadAsync(JsonObject? p, string? owner, CancellationToken ct)
    {
        var watch = SessionWatch(p, owner);
        var now = _clock.GetUtcNow();
        var tree = watch.Tracker.Tree;
        var path = StrOrNull(p, "path") ?? throw new CodeBrowserException(CodeBrowserException.BadParams, "path is required");
        var (info, relative) = tree.Resolve(path, allowHidden: true);
        if (info is not FileInfo) throw CodeBrowserException.Refuse($"'{relative}' is not a file");
        var type = FileKinds.TypeOf(relative);
        var o = new JsonObject { ["path"] = relative, ["type"] = type };
        var language = FileKinds.LanguageOf(relative);
        if (language is not null && type is not "image") o["language"] = language;

        if (tree.IsHidden(relative))
        {
            o["type"] = "notShown";
            o["size"] = SafeLength(info);
            o["notShownReason"] = "secrets";
            return Finish(o, [], [], false);
        }

        var (stream, _) = tree.OpenFile(relative);
        byte[]? bytes;
        long size;
        using (stream) (bytes, size) = FileKinds.ReadBounded(stream);
        o["size"] = size;
        if (bytes is null) return NotShown(o, "tooLarge");
        if (type == "image")
        {
            // Bytes only (an SVG too): the shell shows it as an <img>, never inline.
            o["base64"] = Convert.ToBase64String(bytes);
            o["mime"] = FileKinds.ImageMime(relative);
            return Finish(o, [], [], false);
        }
        if (FileKinds.LooksBinary(bytes)) return NotShown(o, "binary");
        if (FileKinds.CountLines(bytes) > FileKinds.MaxPreviewLines) return NotShown(o, "tooLarge");
        var text = FileKinds.Decode(bytes);
        var lines = FileKinds.Lines(text);
        watch.Tracker.Saw(relative, lines, size, now);
        o["content"] = text;
        var (marks, removed, unknown) = watch.Tracker.Marks(relative, now);
        var all = marks.Select(m => (JsonNode?)new JsonObject { ["from"] = m.From, ["to"] = m.To, ["kind"] = m.Kind, ["at"] = Iso(m.At) }).ToList();
        if (watch.Branch is { } branch)
        {
            await branch.WaitAsync(_options.BranchWait).ConfigureAwait(false);
            foreach (var (from, to) in await branch.LinesAsync(relative, lines, ct).ConfigureAwait(false))
                all.Add(new JsonObject { ["from"] = from, ["to"] = to, ["kind"] = "branch" });
        }
        return Finish(o, all, removed.Select(r => (JsonNode?)new JsonObject { ["afterLine"] = r.AfterLine, ["text"] = r.Text, ["at"] = Iso(r.At) }).ToList(), unknown);
    }

    private static JsonObject NotShown(JsonObject o, string reason)
    {
        o["type"] = "notShown";
        o["notShownReason"] = reason;
        return Finish(o, [], [], false);
    }

    private static JsonObject Finish(JsonObject o, List<JsonNode?> lines, List<JsonNode?> removed, bool unknown)
    {
        o["lines"] = new JsonArray([.. lines]);
        o["removed"] = new JsonArray([.. removed]);
        o["linesUnknown"] = unknown;
        return o;
    }

    private static long SafeLength(FileSystemInfo info)
    {
        try { return info is FileInfo f ? f.Length : 0; } catch (IOException) { return 0; }
    }

    // ------------------------------------------------------------------ code.download

    private JsonNode? Download(JsonObject? p, string? owner)
    {
        var watch = SessionWatch(p, owner);
        var path = StrOrNull(p, "path") ?? throw new CodeBrowserException(CodeBrowserException.BadParams, "path is required");
        var offset = LongOrNull(p, "offset") ?? 0;
        if (offset < 0) throw new CodeBrowserException(CodeBrowserException.BadParams, "offset must be 0 or more");
        var asked = StrOrNull(p, "version");
        var (stream, relative) = watch.Tracker.Tree.OpenFile(path);
        using (stream)
        {
            var size = stream.Length;
            if (size > FileKinds.MaxDownloadBytes)
                throw CodeBrowserException.Refuse($"'{relative}' is {size / (1024.0 * 1024):0.0} MB; files over 50 MB are not downloaded");
            var version = Version(stream, size);
            if (asked is not null && asked != version)
                throw CodeBrowserException.Refuse("the file changed while it was downloading; download it again");
            if (offset > size) throw new CodeBrowserException(CodeBrowserException.BadParams, "offset is past the end of the file");
            stream.Seek(offset, SeekOrigin.Begin);
            var want = (int)Math.Min(_options.DownloadChunkBytes, size - offset);
            var buffer = new byte[want];
            var read = 0;
            while (read < want)
            {
                var n = stream.Read(buffer, read, want - read);
                if (n == 0) break;
                read += n;
            }
            return new JsonObject
            {
                ["name"] = Path.GetFileName(relative),
                ["base64"] = Convert.ToBase64String(buffer, 0, read),
                ["size"] = size,
                ["offset"] = offset,
                ["length"] = (long)read,
                ["version"] = version,
                ["done"] = offset + read >= size,
            };
        }
    }

    private static string Version(FileStream stream, long size)
    {
        var mtime = File.GetLastWriteTimeUtc(stream.Name).Ticks;
        return string.Create(CultureInfo.InvariantCulture, $"{size}-{mtime}");
    }

    // ------------------------------------------------------------------ code.close

    private JsonNode? Close(JsonObject? p, string? owner)
    {
        var id = StrOrNull(p, "session") ?? throw new CodeBrowserException(CodeBrowserException.BadParams, "session is required");
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var s) && s.Owner == owner)
            {
                _sessions.Remove(id);
                if (_watches.TryGetValue(s.Key, out var w) && _sessions.Values.All(x => x.Key != s.Key)) w.IdleSince ??= _clock.GetUtcNow();
            }
        }
        return new JsonObject();
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>
    /// Watches the clones of running members, stops watching a clone neither running nor open for the grace period,
    /// and closes sessions idle too long.
    /// </summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var running = await _host.RunningAsync(ct).ConfigureAwait(false);
        foreach (var (member, started) in running)
        {
            var w = EnsureWatch(member, now);
            lock (_lock)
            {
                w.RunStartedAt = started;
                w.IdleSince = null;
            }
        }
        var keys = running.Select(r => r.Member.Key).ToHashSet(StringComparer.Ordinal);
        lock (_lock)
        {
            ExpireSessions(now);
            foreach (var w in _watches.Values.ToList())
            {
                if (keys.Contains(w.Member.Key)) continue;
                w.RunStartedAt = null;
                if (_sessions.Values.Any(s => s.Key == w.Member.Key)) { w.IdleSince = null; continue; }
                w.IdleSince ??= now;
                if (now - w.IdleSince.Value >= _options.Grace) Stop(w);
            }
        }
    }

    /// <summary>Whether a member's clone is being watched now (tests, and the grace period).</summary>
    public bool IsWatching(string key)
    {
        lock (_lock) return _watches.TryGetValue(key, out var w) && w.Tracker.Watching;
    }

    /// <summary>The tracker of a member, while one exists (tests).</summary>
    internal ChangeTracker? TrackerOf(string key)
    {
        lock (_lock) return _watches.GetValueOrDefault(key)?.Tracker;
    }

    private void Stop(Watch w)
    {
        w.Tracker.Dispose();
        _watches.Remove(w.Member.Key);
    }

    private void ExpireSessions(DateTimeOffset now)
    {
        foreach (var s in _sessions.Values.Where(s => now - s.LastUsed >= _options.SessionIdle).ToList()) _sessions.Remove(s.Id);
    }

    private Watch SessionWatch(JsonObject? p, string? owner)
    {
        var id = StrOrNull(p, "session") ?? throw new CodeBrowserException(CodeBrowserException.BadParams, "session is required");
        var now = _clock.GetUtcNow();
        Watch? w;
        lock (_lock)
        {
            ExpireSessions(now);
            if (!_sessions.TryGetValue(id, out var s) || s.Owner != owner)
                throw CodeBrowserException.Refuse("the Code view was closed; open it again");
            s.LastUsed = now;
            w = _watches.GetValueOrDefault(s.Key);
        }
        if (w is null) throw CodeBrowserException.Refuse("the Code view was closed; open it again");
        if (!w.Tracker.Tree.Exists) throw new CodeBrowserException(CodeBrowserException.NoClone, "this member has no clone");
        if (!w.Tracker.Watching) w.Tracker.Start();
        return w;
    }

    private static string? StrOrNull(JsonObject? p, string name) =>
        p?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? LongOrNull(JsonObject? p, string name)
    {
        if (p?[name] is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && d is >= 0 and <= long.MaxValue) return (long)d;
        return null;
    }

    private static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public ValueTask DisposeAsync()
    {
        _sweep?.Dispose();
        lock (_lock)
        {
            _flush?.Dispose();
            _flush = null;
            foreach (var w in _watches.Values.ToList()) Stop(w);
            _sessions.Clear();
        }
        return ValueTask.CompletedTask;
    }
}
