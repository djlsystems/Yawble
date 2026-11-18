using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yawble.Core.Provenance;

namespace Yawble.Core.Logs;

/// <summary>
/// One log entry as logs.tail pushes it: when, which log (<see cref="Source"/>: "diagnostics", or the activity log's
/// source such as "Runs" or "Messages"), its level ("error", "warning" or "info"), the team and member it is about when
/// the log says so, what happened (<see cref="Event"/>) and its text. <see cref="Workflow"/>: a workflow event (an
/// instruction, hand-back, decision asked, completion). Facts only: no token, cost or usage field.
/// </summary>
public sealed record LogTailEntry(
    string At, string Source, string Level, string? Team, string? Member, string? Event, string? Text, bool Workflow = false,
    bool MemberInText = false)
{
    /// <summary>The entry as the event carries it, redacted the way logs.query redacts (<see cref="LogRedaction"/>).</summary>
    public JsonObject ToJson() => new()
    {
        ["at"] = At,
        ["source"] = Source,
        ["level"] = Level,
        ["team"] = Team,
        ["member"] = Member,
        ["event"] = LogRedaction.Text(Event),
        ["text"] = LogRedaction.Text(Text),
        ["workflow"] = Workflow,
    };
}

/// <summary>
/// What a tail lets through. <see cref="All"/> false (level "default"): warnings, errors and workflow events; true
/// ("all"): every level. <see cref="Team"/> a team name, <see cref="Member"/> "Team/Name" (the member wins over the team).
/// A member's own entries only: not its team's "&lt;Team&gt;" entries; a team's take those too (B101). <see cref="App"/>:
/// only the activity log's entries about no team and no member (team null, member "app").
/// </summary>
public sealed record LogTailFilter(bool All = false, string? Team = null, string? Member = null, bool App = false)
{
    public bool Matches(LogTailEntry e)
    {
        if (!All && e.Level is not (LogLevels.Error or LogLevels.Warning) && !e.Workflow) return false;
        if (App && !(e.Team is null && e.Member == "app" && !e.MemberInText)) return false;
        if (Member is { } member)
        {
            var slash = member.IndexOf('/');
            var (team, name) = (member[..slash], member[(slash + 1)..]);
            if (e.Member is not null)
                return string.Equals(e.Team, team, StringComparison.OrdinalIgnoreCase) && string.Equals(e.Member, name, StringComparison.OrdinalIgnoreCase);
            // A log that names members in passing (the Diagnostics log) matches the way logs.query matches it.
            return e.MemberInText && e.Text?.Contains(name, StringComparison.OrdinalIgnoreCase) == true;
        }
        if (Team is { } t)
        {
            if (e.Team is not null) return string.Equals(e.Team, t, StringComparison.OrdinalIgnoreCase);
            return e.MemberInText && e.Text?.Contains(t, StringComparison.OrdinalIgnoreCase) == true;
        }
        return true;
    }
}

/// <summary>The levels of a log entry, read from what the entry says (the logs keep no level of their own).</summary>
public static partial class LogLevels
{
    public const string Error = "error", Warning = "warning", Info = "info";

    [GeneratedRegex(@"(?i)\b(error|errors|failed|failure|exception|crash|crashed|fatal|unhandled|start_failed)\b")]
    private static partial Regex Errors();

    [GeneratedRegex(@"(?i)\b(warn|warning|denied|deny|refused|refuse|unauthorized|declined|blocked|could not|cannot|interrupted|not written|needsDecision)\b")]
    private static partial Regex Warnings();

    /// <summary>How a run ended, as the Runs log says it ("run failed", "run timeout"...).</summary>
    private static string? RunLevel(string? ev) => ev switch
    {
        "run failed" or "run timeout" or "run credential" or "run start_failed" => Error,
        "run interrupted" or "run idle" or "run stopped" or "run rate" => Warning,
        { } s when s.StartsWith("run ", StringComparison.Ordinal) => Info,
        _ => null,
    };

    /// <summary>A bus message's level by its type alone (its payload is the member's own words).</summary>
    public static string OfMessage(string? type) => type switch
    {
        Bus.MessageTypes.Failed => Error,
        Bus.MessageTypes.Blocked or Bus.MessageTypes.NeedsDecision => Warning,
        _ => Info,
    };

    /// <summary>Whether an activity-log message row is a workflow event: workflow.*, instructions, hand-backs, completion, failure, blocked, decisions.</summary>
    public static bool IsWorkflowEvent(string? type) =>
        type is not null && type != Bus.MessageTypes.Progress
        && (type.StartsWith("workflow.", StringComparison.Ordinal) || type.StartsWith("agent.", StringComparison.Ordinal)
            || type.StartsWith("instruction ", StringComparison.Ordinal));

    /// <summary>The level of an entry by its event and text.</summary>
    public static string Of(string? ev, string? text)
    {
        if (RunLevel(ev) is { } run) return run;
        var said = $"{ev} {text}";
        if (Errors().IsMatch(said)) return Error;
        if (Warnings().IsMatch(said)) return Warning;
        return Info;
    }
}

/// <summary>
/// How log entries are redacted before anyone reads them, in logs.query and logs.tail alike: anything shaped like a
/// token or a credential (<see cref="Integrations.CliRunner.Redact"/>, which also scrubs the known secrets) becomes
/// "[redacted]".
/// </summary>
public static class LogRedaction
{
    public static string? Text(string? text) => string.IsNullOrEmpty(text) ? text : Integrations.CliRunner.Redact(text);

    /// <summary>Redacts every text value of a logs.query row in place.</summary>
    public static JsonObject Row(JsonObject row)
    {
        foreach (var key in row.Select(kv => kv.Key).ToList())
        {
            if (key == "_id") continue;
            if (row[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) row[key] = Integrations.CliRunner.Redact(s);
        }
        return row;
    }
}

/// <summary>
/// logs.tail: while a viewer tails, each new log entry that passes its filter is pushed as the event
/// <see cref="EntryEvent"/> {tail, entry}, one entry each. The filtering is done here, on this computer; nothing is
/// buffered (an entry nobody's filter takes is dropped). Entries come from the Diagnostics log as it is written
/// (<see cref="Write"/>) and, while any tail is open, from the activity log read every <see cref="Interval"/> (model usage
/// left out). A tail ends with logs.untail, or when its cloud viewer is no longer viewing or the Cloud Link drops
/// (read from <see cref="Link.CloudLink.StateEvent"/>). With no tail open nothing is read or watched.
/// </summary>
public sealed class LogTail : IDisposable
{
    public const string EntryEvent = "logs.tailEntry";

    /// <summary>Tails one viewer may keep open: a further one ends that viewer's oldest (a page reloaded without untail).</summary>
    public const int MaxPerViewer = 4;

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private sealed record Tail(string Id, string? Via, string? User, LogTailFilter Filter);

    private readonly Ui.AppEvents _events;
    private readonly Func<string, IReadOnlyList<(string Id, LogTailEntry Entry)>>? _readSince;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;
    private readonly Lock _lock = new();
    private Tail[] _tails = [];
    private IDisposable? _linkWatch;
    private CancellationTokenSource? _poll;
    private string _since = "";
    private HashSet<string> _seenAtSince = new(StringComparer.Ordinal);
    private long _next;

    /// <param name="readSince">New activity-log entries at or after an ISO time, oldest first (LogService.TailSince); null: the Diagnostics log only.</param>
    public LogTail(Ui.AppEvents events, Func<string, IReadOnlyList<(string Id, LogTailEntry Entry)>>? readSince = null,
        TimeProvider? clock = null, TimeSpan? interval = null)
    {
        _events = events;
        _readSince = readSince;
        _clock = clock ?? TimeProvider.System;
        _interval = interval ?? Interval;
    }

    /// <summary>The tails open now.</summary>
    public int Count => Volatile.Read(ref _tails).Length;

    /// <summary>Opens a tail for <paramref name="owner"/> (the caller: a cloud viewer or the window); returns its id.</summary>
    public string Start(LogTailFilter filter, Actor? owner)
    {
        var tail = new Tail($"tail-{Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture)}", owner?.Via, owner?.User, filter);
        lock (_lock)
        {
            var list = _tails.ToList();
            var mine = list.Where(t => Same(t, owner)).ToList();
            foreach (var old in mine.Take(Math.Max(0, mine.Count - MaxPerViewer + 1))) list.Remove(old);
            list.Add(tail);
            Changed(list);
        }
        return tail.Id;
    }

    /// <summary>Ends <paramref name="id"/>, or every tail of <paramref name="owner"/> when null; only the owner's own. Returns how many ended.</summary>
    public int Stop(string? id, Actor? owner) =>
        Remove(t => Same(t, owner) && (id is null || t.Id == id));

    /// <summary>Ends every tail of the cloud viewer <paramref name="user"/>.</summary>
    public int StopViewer(string user) => Remove(t => t.Via == Actor.Cloud && t.User == user);

    public void Dispose()
    {
        lock (_lock) Changed([]);
    }

    /// <summary>
    /// A log entry just written. With no tail open this returns at once; otherwise each tail whose filter takes it gets
    /// it, redacted, as one <see cref="EntryEvent"/>.
    /// </summary>
    public void Write(LogTailEntry entry)
    {
        var tails = Volatile.Read(ref _tails);
        if (tails.Length == 0) return;
        JsonObject? json = null;
        foreach (var t in tails)
        {
            if (!t.Filter.Matches(entry)) continue;
            json ??= entry.ToJson();
            _events.Publish(EntryEvent, new JsonObject { ["tail"] = t.Id, ["entry"] = json.DeepClone() });
        }
    }

    /// <summary>Reads the activity log once for entries since the last read and pushes them (the poll's one step).</summary>
    internal void PollOnce()
    {
        if (_readSince is null || Count == 0) return;
        string since;
        HashSet<string> seen;
        lock (_lock) (since, seen) = (_since, _seenAtSince);
        IReadOnlyList<(string Id, LogTailEntry Entry)> rows;
        try { rows = _readSince(since); }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or IOException) { return; }
        foreach (var (id, entry) in rows)
        {
            var cmp = string.CompareOrdinal(entry.At, since);
            if (cmp < 0 || (cmp == 0 && seen.Contains(id))) continue;
            if (cmp > 0) { since = entry.At; seen = new HashSet<string>(StringComparer.Ordinal); }
            seen.Add(id);
            Write(entry);
        }
        lock (_lock) (_since, _seenAtSince) = (since, seen);
    }

    private static bool Same(Tail t, Actor? owner) => t.Via == owner?.Via && t.User == owner?.User;

    private int Remove(Func<Tail, bool> which)
    {
        lock (_lock)
        {
            var keep = _tails.Where(t => !which(t)).ToList();
            var gone = _tails.Length - keep.Count;
            if (gone > 0) Changed(keep);
            return gone;
        }
    }

    /// <summary>Sets the open tails, and starts or stops the link watch and the activity read to match (under the lock).</summary>
    private void Changed(List<Tail> tails)
    {
        Volatile.Write(ref _tails, [.. tails]);
        var cloud = tails.Any(t => t.Via == Actor.Cloud);
        if (cloud && _linkWatch is null) _linkWatch = _events.Subscribe(OnEventAsync);
        else if (!cloud && _linkWatch is not null) { _linkWatch.Dispose(); _linkWatch = null; }

        if (tails.Count > 0 && _poll is null && _readSince is not null)
        {
            _since = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
            _seenAtSince = new HashSet<string>(StringComparer.Ordinal);
            _poll = new CancellationTokenSource();
            _ = PollAsync(_poll.Token);
        }
        else if (tails.Count == 0 && _poll is not null)
        {
            _poll.Cancel();
            _poll.Dispose();
            _poll = null;
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_interval, _clock, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            PollOnce();
        }
    }

    /// <summary>The Cloud Link's state: a viewer gone from its viewers, or the link not connected, ends their tails.</summary>
    private Task OnEventAsync(JsonObject message)
    {
        if (message["event"]?.GetValue<string>() != Link.CloudLink.StateEvent || message["data"] is not JsonObject data) return Task.CompletedTask;
        OnLinkState(data);
        return Task.CompletedTask;
    }

    internal void OnLinkState(JsonObject status)
    {
        if (status["state"]?.GetValue<string>() != "connected")
        {
            Remove(t => t.Via == Actor.Cloud);
            return;
        }
        var viewers = (status["viewers"] as JsonArray ?? []).Select(v => v?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Remove(t => t.Via == Actor.Cloud && (t.User is null || !viewers.Contains(t.User)));
    }
}
