using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>One file a watch sees. <see cref="Path"/> is relative to the watch's root area.</summary>
public sealed record FolderEntry(string Path, long Size, DateTimeOffset ModifiedAt);

/// <summary>A watch's folder once resolved and checked.</summary>
/// <param name="Root">`documents` or `root:&lt;name&gt;`, as stored.</param>
/// <param name="Folder">The folder relative to the area, normalised. Empty is the area itself.</param>
/// <param name="Absolute">Where that is on disk.</param>
/// <param name="Display">What a person is shown: `documents/inbox`, `Share/in`.</param>
public sealed record WatchTarget(string Root, string Folder, string Absolute, string Display);

/// <summary>What one listing saw, or the sentence saying why it could not look.</summary>
public sealed record FolderListing(IReadOnlyList<FolderEntry> Entries, int ElapsedMs, string? Refusal);

/// <summary>A watchable area as the root picker offers it.</summary>
public sealed record WatchRootOption(string Value, string Label);

/// <summary>
/// THE FOLDER-CHANGE TRIGGER: polling, not a FileSystemWatcher.
///
/// <para>
/// The folders people drop files into are host folders and network shares mounted into the
/// container, and inotify sees neither a change made on the Windows side nor one made by another
/// machine on a share. A fingerprint listed on an interval works the same on all of them, so polling
/// is the design rather than the fallback.
/// </para>
///
/// <para>
/// IT NEVER DELIVERS. A change publishes `file.changed`; the row also carries that event type, so
/// the event-trigger arm of the delivery pump fires it. See <see cref="FolderWatchScope.Covers"/>.
/// </para>
///
/// <para>
/// <b>A PURE TOUCH DOES NOT FIRE.</b> The fingerprint is entry count, newest mtime and a hash of the
/// sorted names and sizes, and the whole of it decides when the quiet period starts and whether it
/// has held - so a file still being written keeps restarting the wait. What FIRES is the diff
/// against the last listing by name and size: a touch moves the mtime only, the diff is empty, and
/// the new fingerprint is taken as the baseline without a publish. The cost is stated rather than
/// hidden: a rewrite that keeps a file's exact size is not seen by a poll. The Documents dialog
/// announces its own writes, so that gap is only for writes made outside the platform.
/// </para>
///
/// <para>
/// <b>THE LOOP GUARD.</b> Changes made while ANY member of the same team is running - or that ran
/// since this watch last looked - are folded into the baseline rather than fired on, so a member
/// woken by a folder writing into it does not wake itself again. `min_interval_seconds` bounds how
/// soon a watch may publish twice, and `idle_only` holds a publish while the member is busy.
/// "Ran since this watch last looked" is a generation count bumped on every Running snapshot rather
/// than a timestamp, so it does not care which clock a caller passes.
/// </para>
/// </summary>
public sealed class FolderWatch
{
    /// <summary>A watch that sees more than this refuses rather than lists: a poll is not a crawl.</summary>
    public const int MaximumEntries = 10_000;

    public const int DefaultPollSeconds = 60;
    public const int MinimumPollSeconds = 15;
    public const int DefaultQuietSeconds = 30;
    public const int DefaultMinIntervalSeconds = 60;
    public const int DefaultMaximumWatches = 50;

    /// <summary>What "Test this folder" shows; the count is always whole.</summary>
    public const int TestEntriesShown = 200;

    public const string FileChangedEventType = MessageTypes.FileChanged;

    private readonly ITriggerStore _triggers;
    private readonly IMessageLog _log;
    private readonly ContainerHost _host;
    private readonly IPendingDeliveries? _pending;
    private readonly Func<string, string> _documentsRootFor;
    private readonly IReadOnlyList<FileBrowserRoot> _roots;
    private readonly string _dataRoot;
    private readonly ILogger? _logger;

    private readonly ConcurrentDictionary<string, long> _runGeneration = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _seenGeneration = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _announced = new(StringComparer.Ordinal);

    public FolderWatch(
        ITriggerStore triggers,
        IMessageLog log,
        ContainerHost host,
        IPendingDeliveries? pending,
        Func<string, string> documentsRootFor,
        IReadOnlyList<FileBrowserRoot> roots,
        string dataRoot,
        int maximumWatches = DefaultMaximumWatches,
        ILogger? logger = null)
    {
        _triggers = triggers;
        _log = log;
        _host = host;
        _pending = pending;
        _documentsRootFor = documentsRootFor;
        _roots = roots;
        _dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        MaximumWatches = maximumWatches;
        _logger = logger;

        _host.Changed += snapshot =>
        {
            if (snapshot.State == ContainerState.Running)
            {
                _runGeneration.AddOrUpdate(snapshot.Team, 1, (_, value) => value + 1);
            }
        };
    }

    public int MaximumWatches { get; }

    public static bool IsFolderKind(string? kind) =>
        string.Equals(kind?.Trim(), nameof(TriggerKind.FolderChange), StringComparison.OrdinalIgnoreCase);

    /// <summary>The areas a watch may name: the team's documents, then every root with allowWatch.</summary>
    public IReadOnlyList<WatchRootOption> RootOptions() =>
    [
        new(FolderWatchScope.DocumentsRoot, "Team documents"),
        .. _roots
            .Where(root => root.AllowWatch && !TouchesDataRoot(root.Path))
            .Select(root => new WatchRootOption(FolderWatchScope.RootPrefix + root.Name, root.Name)),
    ];

    /// <summary>Why these interval values cannot be stored, or null.</summary>
    public static string? IntervalRefusal(int? pollSeconds, int? quietSeconds, int? minIntervalSeconds)
    {
        if (pollSeconds is { } poll && poll < MinimumPollSeconds)
        {
            return $"pollSeconds must be at least {MinimumPollSeconds}.";
        }

        if (quietSeconds is < 0) return "quietSeconds must be 0 or more.";
        if (minIntervalSeconds is < 0) return "minIntervalSeconds must be 0 or more.";

        return null;
    }

    /// <summary>
    /// Resolves and checks a watch's folder: inside the team's documents or an allowWatch root, never
    /// the data root, no symbolic link on the way down, and there. Null with a sentence otherwise.
    /// </summary>
    public WatchTarget? Resolve(string team, string? watchRoot, string? watchPath, out string? refusal)
    {
        refusal = null;
        var root = (watchRoot ?? "").Trim();
        var raw = (watchPath ?? "").Replace('\\', '/').Trim();
        var folder = FolderWatchScope.Normalise(raw);

        string area;
        string display;
        string where;
        string leaves;

        if (string.Equals(root, FolderWatchScope.DocumentsRoot, StringComparison.OrdinalIgnoreCase))
        {
            root = FolderWatchScope.DocumentsRoot;
            area = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_documentsRootFor(team)));
            display = FolderWatchScope.DocumentsRoot;
            where = "the team's documents";
            leaves = "That path leaves the team's documents folder.";
        }
        else if (root.StartsWith(FolderWatchScope.RootPrefix, StringComparison.OrdinalIgnoreCase)
                 && root.Length > FolderWatchScope.RootPrefix.Length)
        {
            var name = root[FolderWatchScope.RootPrefix.Length..];
            var configured = _roots.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

            if (configured is null)
            {
                refusal = $"No file-browser root is named \"{name}\".";
                return null;
            }

            if (TouchesDataRoot(configured.Path))
            {
                refusal = "The platform's own data folder cannot be watched.";
                return null;
            }

            if (!configured.AllowWatch)
            {
                refusal = $"The file-browser root \"{configured.Name}\" does not allow watching. "
                    + "Set \"allowWatch\": true on it under FileBrowser:Roots.";
                return null;
            }

            root = FolderWatchScope.RootPrefix + configured.Name;
            area = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured.Path));
            display = configured.Name;
            where = $"the root \"{configured.Name}\"";
            leaves = $"That path leaves the file-browser root \"{configured.Name}\".";
        }
        else
        {
            refusal = "A folder trigger needs a root: \"documents\" or \"root:<name>\".";
            return null;
        }

        // Resolved and then COMPARED, the TeamDocuments.Resolve rule: a check that looks for ".."
        // is defeated by spelling, one that compares the resolved result is not.
        if (raw.StartsWith('/') || raw.Contains(':') || Path.IsPathRooted(raw))
        {
            refusal = leaves;
            return null;
        }

        string absolute;

        try
        {
            absolute = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(area, folder)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            refusal = leaves;
            return null;
        }

        if (!Within(absolute, area))
        {
            refusal = leaves;
            return null;
        }

        if (TouchesDataRoot(absolute) && root != FolderWatchScope.DocumentsRoot)
        {
            refusal = "The platform's own data folder cannot be watched.";
            return null;
        }

        folder = FolderWatchScope.Normalise(Path.GetRelativePath(area, absolute).Replace('\\', '/'));
        if (folder == ".") folder = "";

        // SYMBOLIC LINKS ARE REFUSED AFTER RESOLVING, segment by segment. Path.GetFullPath never
        // touches the disk, so a link inside the area pointing outside it passes the comparison
        // above; this is the check that sees it.
        var cursor = area;

        foreach (var segment in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, segment);

            FileSystemInfo info = Directory.Exists(cursor) ? new DirectoryInfo(cursor) : new FileInfo(cursor);

            if (info.Exists && info.LinkTarget is not null)
            {
                refusal = $"\"{FolderWatchScope.Normalise(Path.GetRelativePath(area, cursor).Replace('\\', '/'))}\" "
                    + "is a symbolic link, and a watched folder may not be one.";
                return null;
            }
        }

        if (!Directory.Exists(absolute))
        {
            refusal = $"There is no folder at \"{(folder.Length == 0 ? "/" : folder)}\" in {where}.";
            return null;
        }

        return new WatchTarget(root, folder, absolute, folder.Length == 0 ? display : $"{display}/{folder}");
    }

    /// <summary>
    /// Every file under the target that the watch counts, sorted by path, timed. Ignored names are
    /// never descended into, symbolic links are never followed, and more than
    /// <see cref="MaximumEntries"/> entries is a refusal rather than a slow poll.
    /// </summary>
    public static FolderListing List(WatchTarget target, string? glob)
    {
        var clock = Stopwatch.StartNew();
        var entries = new List<FolderEntry>();
        var seen = 0;
        var pending = new Stack<(DirectoryInfo Directory, string Relative)>();
        pending.Push((new DirectoryInfo(target.Absolute), ""));

        try
        {
            while (pending.Count > 0)
            {
                var (directory, relative) = pending.Pop();

                IEnumerable<FileSystemInfo> children;

                try
                {
                    children = directory.EnumerateFileSystemInfos();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // An unreadable SUBfolder is skipped; the watched folder itself is not.
                    if (relative.Length == 0) throw;
                    continue;
                }

                foreach (var child in children)
                {
                    if (FolderWatchScope.IsIgnored(child.Name)) continue;
                    if (child.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                    if (++seen > MaximumEntries)
                    {
                        return new FolderListing(
                            [], Elapsed(clock),
                            $"The folder holds more than {MaximumEntries.ToString("N0", CultureInfo.InvariantCulture)} entries. "
                            + "Watch a smaller folder.");
                    }

                    var childRelative = relative.Length == 0 ? child.Name : $"{relative}/{child.Name}";

                    if (child is DirectoryInfo sub)
                    {
                        pending.Push((sub, childRelative));
                        continue;
                    }

                    if (child is not FileInfo file) continue;
                    if (!FolderWatchScope.GlobMatches(glob, childRelative)) continue;

                    entries.Add(new FolderEntry(
                        target.Folder.Length == 0 ? childRelative : $"{target.Folder}/{childRelative}",
                        file.Length,
                        file.LastWriteTimeUtc));
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new FolderListing([], Elapsed(clock), $"The folder \"{target.Display}\" could not be read: {ex.Message}");
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new FolderListing(entries, Elapsed(clock), null);
    }

    /// <summary>Entry count, newest mtime, and a hash of the sorted names and sizes.</summary>
    public static string Fingerprint(IReadOnlyList<FolderEntry> entries)
    {
        var newest = entries.Count == 0 ? 0 : entries.Max(e => e.ModifiedAt.UtcTicks);

        return $"{entries.Count}:{newest}:{ContentHash(entries)}";
    }

    /// <summary>"Test this folder": what a watch there would see, and how long it took to look.</summary>
    public object Test(string team, string? watchRoot, string? watchPath, string? watchGlob)
    {
        if (Resolve(team, watchRoot, watchPath, out var refusal) is not { } target)
        {
            return new { ok = false, refusal, folder = (string?)null, count = 0, truncated = false, elapsedMs = 0, entries = Array.Empty<object>() };
        }

        var listing = List(target, Glob(watchGlob));

        if (listing.Refusal is not null)
        {
            return new { ok = false, refusal = listing.Refusal, folder = (string?)target.Display, count = 0, truncated = false, elapsedMs = listing.ElapsedMs, entries = Array.Empty<object>() };
        }

        return new
        {
            ok = true,
            refusal = (string?)null,
            folder = (string?)target.Display,
            count = listing.Entries.Count,
            truncated = listing.Entries.Count > TestEntriesShown,
            elapsedMs = listing.ElapsedMs,
            entries = listing.Entries.Take(TestEntriesShown)
                .Select(e => (object)new { path = e.Path, size = e.Size, modifiedAt = e.ModifiedAt })
                .ToArray(),
        };
    }

    /// <summary>
    /// Why a folder trigger with these values cannot be saved, or null: the intervals, the folder,
    /// and a listing within the entry cap. <paramref name="counting"/> adds the per-instance cap,
    /// for a row that is becoming a folder trigger rather than one that already is.
    /// </summary>
    public async Task<string?> RefusalForAsync(
        string team, string? watchRoot, string? watchPath, string? watchGlob,
        int? pollSeconds, int? quietSeconds, int? minIntervalSeconds,
        bool counting, CancellationToken ct)
    {
        if (IntervalRefusal(pollSeconds, quietSeconds, minIntervalSeconds) is { } interval) return interval;

        if (Resolve(team, watchRoot, watchPath, out var refusal) is not { } target) return refusal;

        if (List(target, Glob(watchGlob)).Refusal is { } listing) return listing;

        if (counting && await _triggers.CountKindAsync(nameof(TriggerKind.FolderChange), ct) >= MaximumWatches)
        {
            return $"This instance already watches {MaximumWatches} folders, the most it allows.";
        }

        return null;
    }

    public static string? Glob(string? glob) => string.IsNullOrWhiteSpace(glob) ? null : glob.Trim();

    /// <summary>
    /// ONE POLL of one folder trigger, called by <see cref="TriggerSweep"/> when the row is due.
    /// Records the poll's time, duration and outcome on the row, and arms the next.
    /// </summary>
    public async Task PollAsync(TriggerRow row, DateTimeOffset now, CancellationToken ct = default)
    {
        var poll = row.PollSeconds ?? DefaultPollSeconds;
        var quiet = row.QuietSeconds ?? DefaultQuietSeconds;
        var minInterval = row.MinIntervalSeconds ?? DefaultMinIntervalSeconds;
        var nextPoll = now.AddSeconds(poll);

        // Read before the listing, so a run that starts while the folder is being listed still
        // counts as "ran since this watch last looked" on the next poll.
        var generation = _runGeneration.GetValueOrDefault(row.Team);
        var ranSinceLastPoll = _seenGeneration.TryGetValue(row.Id, out var seen) && seen != generation;
        _seenGeneration[row.Id] = generation;

        var clock = Stopwatch.StartNew();

        if (Resolve(row.Team, row.WatchRoot, row.WatchPath, out var refusal) is not { } target)
        {
            await RecordAsync(row, now, Elapsed(clock), null, refusal, nextPoll, ct: ct);
            return;
        }

        var listing = List(target, row.WatchGlob);

        if (listing.Refusal is not null)
        {
            await RecordAsync(row, now, listing.ElapsedMs, null, listing.Refusal, nextPoll, ct: ct);
            return;
        }

        var elapsed = Elapsed(clock);
        var entries = listing.Entries;
        var fingerprint = Fingerprint(entries);
        var current = entries.ToDictionary(e => e.Path, e => e.Size, StringComparer.Ordinal);
        var serialized = JsonSerializer.Serialize(current);
        var state = await _triggers.WatchStateAsync(row.Id, ct);

        // THE FIRST LOOK IS THE BASELINE. Whatever was there when the watch was made is not news.
        if (row.LastFingerprint is null || state.Listing is null)
        {
            await BaselineAsync(row, now, elapsed, entries.Count, nextPoll, fingerprint, serialized, ct);
            return;
        }

        var previous = JsonSerializer.Deserialize<Dictionary<string, long>>(state.Listing)
            ?? new Dictionary<string, long>(StringComparer.Ordinal);

        var announced = _announced.GetValueOrDefault(row.Id);
        var changed = Diff(previous, current)
            .Where(path => announced is null || !announced.ContainsKey(path))
            .ToList();

        // Nothing moved by name or size (a touch), or only what the platform already announced.
        if (changed.Count == 0)
        {
            await BaselineAsync(row, now, elapsed, entries.Count, nextPoll, fingerprint, serialized, ct);
            return;
        }

        // THE LOOP GUARD: changed while the team was working, so folded rather than fired.
        if (ranSinceLastPoll || TeamIsRunning(row.Team))
        {
            await BaselineAsync(row, now, elapsed, entries.Count, nextPoll, fingerprint, serialized, ct);
            return;
        }

        // A NEW SHAPE STARTS (OR RESTARTS) THE QUIET PERIOD. A half-written file changes its size
        // or mtime on the next look and so keeps pushing the publish back.
        if (quiet > 0 && !string.Equals(state.PendingFingerprint, fingerprint, StringComparison.Ordinal))
        {
            await RecordAsync(row, now, elapsed, entries.Count, null, Earlier(nextPoll, now.AddSeconds(quiet)),
                pendingFingerprint: fingerprint, pendingSince: now, ct: ct);
            return;
        }

        var since = state.PendingSince ?? now;

        if (quiet > 0 && since.AddSeconds(quiet) > now)
        {
            await RecordAsync(row, now, elapsed, entries.Count, null, Earlier(nextPoll, since.AddSeconds(quiet)),
                pendingFingerprint: fingerprint, pendingSince: since, ct: ct);
            return;
        }

        if (row.LastChangeAt is { } last && last.AddSeconds(minInterval) > now)
        {
            await RecordAsync(row, now, elapsed, entries.Count, null, Earlier(nextPoll, last.AddSeconds(minInterval)),
                pendingFingerprint: fingerprint, pendingSince: since, ct: ct);
            return;
        }

        if (row.IdleOnly && await MemberIsBusyAsync(row, ct))
        {
            await RecordAsync(row, now, elapsed, entries.Count, null, nextPoll,
                pendingFingerprint: fingerprint, pendingSince: since, ct: ct);
            return;
        }

        await PublishAsync(row.Team, target.Root, target.Folder, changed, $"{FolderWatchScope.TriggerSourcePrefix}{row.Id}", ct);

        _announced.TryRemove(row.Id, out _);
        await RecordAsync(row, now, elapsed, entries.Count, null, nextPoll,
            fingerprint: fingerprint, listing: serialized, changeAt: now, ct: ct);
    }

    /// <summary>
    /// THE PLATFORM ANNOUNCES ITS OWN WRITES. A Documents-dialog upload or delete publishes
    /// `file.changed` at once, and every folder trigger it covers is told to leave those files out of
    /// its next diff - or the next poll would fire a second time for the same file.
    /// </summary>
    public async Task AnnounceAsync(
        string team, string folder, IReadOnlyList<string> changed, string source, CancellationToken ct = default)
    {
        var normalisedFolder = FolderWatchScope.Normalise(folder);
        var paths = changed.Select(FolderWatchScope.Normalise).Where(p => p.Length > 0).ToList();
        if (paths.Count == 0) return;

        var payload = await PublishAsync(team, FolderWatchScope.DocumentsRoot, normalisedFolder, paths, source, ct);

        foreach (var trigger in await _triggers.ListForTeamAsync(team, ct))
        {
            if (!trigger.Enabled || !IsFolderKind(trigger.Kind)) continue;
            if (!FolderWatchScope.Covers(trigger, payload, source)) continue;

            var set = _announced.GetOrAdd(trigger.Id, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            foreach (var path in paths) set[path] = 0;
        }
    }

    private async Task<string> PublishAsync(
        string team, string root, string folder, IReadOnlyList<string> changed, string source, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [PayloadFields.Team] = team,
            [PayloadFields.Root] = root,
            [PayloadFields.Path] = folder,
            [PayloadFields.Changed] = changed.Take(FolderWatchScope.MaximumChangedListed).ToArray(),
            [PayloadFields.Count] = changed.Count,
        });

        await _log.AppendAsync(new NewMessage(MessageTypes.FileChanged, payload, source), ct);
        return payload;
    }

    private Task BaselineAsync(
        TriggerRow row, DateTimeOffset now, int elapsed, int entries, DateTimeOffset next,
        string fingerprint, string listing, CancellationToken ct)
    {
        _announced.TryRemove(row.Id, out _);
        return RecordAsync(row, now, elapsed, entries, null, next, fingerprint: fingerprint, listing: listing, ct: ct);
    }

    private Task RecordAsync(
        TriggerRow row, DateTimeOffset now, int elapsed, int? entries, string? error, DateTimeOffset next,
        string? fingerprint = null, string? listing = null,
        string? pendingFingerprint = null, DateTimeOffset? pendingSince = null,
        DateTimeOffset? changeAt = null, CancellationToken ct = default)
    {
        if (error is not null)
        {
            _logger?.LogInformation("Folder trigger {TriggerId} could not poll: {Refusal}", row.Id, error);
        }

        return _triggers.RecordPollAsync(
            row.Id,
            new FolderPollRecord(now, elapsed, entries, error, next, fingerprint, listing, pendingFingerprint, pendingSince, changeAt),
            ct);
    }

    private bool TeamIsRunning(string team) =>
        _host.Snapshots().Any(s =>
            string.Equals(s.Team, team, StringComparison.OrdinalIgnoreCase) && s.State == ContainerState.Running);

    private async Task<bool> MemberIsBusyAsync(TriggerRow row, CancellationToken ct)
    {
        if (_host.Find(new ContainerId(row.Team, row.Container)) is not { } container) return false;

        IReadOnlyCollection<PendingDelivery> accepted = _pending is null
            ? []
            : await _pending.ForAsync(container.Id, ct);

        return ContainerBusy.IsBusy(container, accepted);
    }

    private static IEnumerable<string> Diff(IReadOnlyDictionary<string, long> before, IReadOnlyDictionary<string, long> after)
    {
        foreach (var (path, size) in after)
        {
            if (!before.TryGetValue(path, out var was) || was != size) yield return path;
        }

        foreach (var path in before.Keys)
        {
            if (!after.ContainsKey(path)) yield return path;
        }
    }

    private static string ContentHash(IReadOnlyList<FolderEntry> entries)
    {
        var builder = new StringBuilder();

        foreach (var entry in entries.OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            builder.Append(entry.Path).Append('\0').Append(entry.Size.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..32];
    }

    private bool TouchesDataRoot(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Within(full, _dataRoot) || Within(_dataRoot, full)) return true;

        // THE STRINGS ALONE ARE NOT ENOUGH: a root configured as a symlink to, or into,
        // the data folder passes the lexical test above. Compare where both really lead as well.
        var real = RealPath(full);
        var realData = RealPath(_dataRoot);

        return Within(real, realData) || Within(realData, real);
    }

    /// <summary>
    /// The path with every symlink on it resolved, segment by segment, so a linked ancestor counts
    /// as well as a linked last segment. A segment that does not exist or cannot be read is kept as
    /// written: this is only ever used to REFUSE more, never to allow.
    /// </summary>
    private static string RealPath(string path, int depth = 0)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var current = Path.GetPathRoot(full) ?? string.Empty;

        foreach (var segment in full[current.Length..].Split(
            Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);

            try
            {
                var info = new DirectoryInfo(current);

                if (info.LinkTarget is not null
                    && info.ResolveLinkTarget(returnFinalTarget: true) is { } target
                    && depth < 32)
                {
                    // The target's own ancestors may be links too.
                    current = RealPath(target.FullName, depth + 1);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    private static bool Within(string candidate, string root) =>
        candidate.Equals(root, StringComparison.Ordinal)
        || candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static DateTimeOffset Earlier(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static int Elapsed(Stopwatch clock) => (int)Math.Min(int.MaxValue, clock.ElapsedMilliseconds);
}
