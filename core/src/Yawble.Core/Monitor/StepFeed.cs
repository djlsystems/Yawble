using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Monitor;

/// <summary>One step of a member's run as monitor.steps returns it. <see cref="Id"/> is the feed's cursor.</summary>
public sealed record Step(long Id, DateTimeOffset At, string Team, string Member, long Run, string Kind, string Tool, string? Target)
{
    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["at"] = At.ToString("O", CultureInfo.InvariantCulture), ["team"] = Team, ["member"] = Member, ["run"] = Run,
        ["kind"] = Kind, ["tool"] = Tool, ["target"] = Target,
    };
}

/// <summary>
/// THE MONITOR'S STEP FEED (monitor.steps): what the members do between their bus messages, read from the runs'
/// transcripts (TranscriptSteps says what a step is). The Monitor polls it every second or so, so each call is
/// cheap: only the transcripts of runs still going or ended in the window are looked at, each is read on from where
/// the last call stopped (its offset is remembered), a line is consumed only once it is whole, and a call reads at
/// most <see cref="BytesPerCall"/> (a first look at a day of runs takes a few calls; "pending" says more is there).
///
/// Most lines carry no time of their own, so a step is stamped by where its bytes sit between the last read of the
/// file (or the run's start) and the file's last write: polled live that is within a second of when it happened, and
/// a run read for the first time after it ended has its steps spread over its run, which is what a replay needs.
/// A line's own "timestamp" wins when it has one. The newest <see cref="Keep"/> steps are held, in memory only.
/// </summary>
public sealed class StepFeed(string connectionString, TimeProvider? clock = null)
{
    public const int Keep = 20_000;
    public const long BytesPerCall = 4L << 20;
    public const long BytesPerFile = 1L << 20;
    public const int MaxMinutes = 24 * 60;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly Dictionary<long, Cursor> _cursors = [];
    private readonly List<Step> _steps = [];
    private long _last;

    private sealed class Cursor(TranscriptContext context)
    {
        public TranscriptContext Context { get; } = context;
        public long Offset;
        /// <summary>When the bytes up to <see cref="Offset"/> were written, as far as the feed knows.</summary>
        public DateTimeOffset Stamp;
    }

    private sealed record Run(long Id, string Team, string Member, string Path, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

    /// <summary>
    /// Reads on, then returns the steps after <paramref name="after"/> that happened in the last
    /// <paramref name="sinceMinutes"/>, oldest read first, at most <paramref name="limit"/>. End is the cursor for the
    /// next call; Pending is true when there is more to read or return now (call again without waiting).
    /// </summary>
    public (long End, IReadOnlyList<Step> Steps, bool Pending) Read(long after, int sinceMinutes = 30, int limit = 2000)
    {
        sinceMinutes = Math.Clamp(sinceMinutes, 1, MaxMinutes);
        limit = Math.Clamp(limit, 1, 5000);
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            var cutoff = now.AddMinutes(-sinceMinutes);
            var more = ReadOn(cutoff);
            var list = new List<Step>();
            var end = Math.Max(after, _last);
            // The held steps are in id order: find the first after the cursor, then take those in the window.
            var lo = 0;
            var hi = _steps.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (_steps[mid].Id <= after) lo = mid + 1; else hi = mid;
            }
            for (var i = lo; i < _steps.Count; i++)
            {
                if (list.Count == limit) { end = list[^1].Id; more = true; break; }
                if (_steps[i].At >= cutoff) list.Add(_steps[i]);
            }
            return (end, list, more);
        }
    }

    /// <summary>Reads every wanted transcript on from its offset; true when a byte budget left some unread.</summary>
    private bool ReadOn(DateTimeOffset cutoff)
    {
        long budget = BytesPerCall;
        var more = false;
        foreach (var run in Runs(cutoff))
        {
            if (budget <= 0) return true;
            FileInfo file;
            try { file = new FileInfo(run.Path); if (!file.Exists) continue; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
            if (run.EndedAt is null && file.LastWriteTimeUtc < cutoff.UtcDateTime) continue; // a run the core lost long ago
            if (!_cursors.TryGetValue(run.Id, out var cursor))
            {
                cursor = new Cursor(new TranscriptContext(run.Team, run.Member)) { Stamp = run.StartedAt };
                _cursors[run.Id] = cursor;
            }
            var length = file.Length;
            if (length <= cursor.Offset) continue;
            var lastWrite = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            var take = Math.Min(Math.Min(length - cursor.Offset, BytesPerFile), budget);
            var capped = take < length - cursor.Offset;
            byte[] bytes;
            try
            {
                using var stream = new FileStream(run.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(cursor.Offset, SeekOrigin.Begin);
                bytes = new byte[take];
                var got = 0;
                while (got < take && stream.Read(bytes, got, (int)(take - got)) is var n and > 0) got += n;
                if (got < take) Array.Resize(ref bytes, got);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            budget -= bytes.Length;
            // An ended run's bytes were written by its end (the file's write time may be a copy's).
            Consume(run, cursor, bytes, length, run.EndedAt ?? lastWrite, whole: run.EndedAt is not null && cursor.Offset + bytes.Length >= length);
            if (_steps.Count > Keep) _steps.RemoveRange(0, _steps.Count - Keep);
            // Capped by a budget: the rest is read on the next call, which the caller makes at once.
            more |= capped;
        }
        return more;
    }

    /// <summary>The lines in <paramref name="bytes"/> (read at the cursor) as steps; an unfinished last line waits.</summary>
    private void Consume(Run run, Cursor cursor, byte[] bytes, long length, DateTimeOffset written, bool whole)
    {
        var from = cursor.Offset;
        var span = Math.Max(1, length - from);
        var start = cursor.Stamp;
        var end = written < start ? start : written;
        DateTimeOffset StampAt(long offset) => start + TimeSpan.FromTicks((long)((end - start).Ticks * Math.Clamp((double)(offset - from) / span, 0, 1)));

        var used = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            Line(run, cursor, Encoding.UTF8.GetString(bytes, used, i - used), StampAt(from + i));
            used = i + 1;
        }
        if (whole && used < bytes.Length)
        {
            Line(run, cursor, Encoding.UTF8.GetString(bytes, used, bytes.Length - used), end);
            used = bytes.Length;
        }
        // A single line longer than a whole read (a huge tool result) is skipped rather than read for ever.
        if (used == 0 && bytes.Length >= BytesPerFile) used = bytes.Length;
        cursor.Offset = from + used;
        cursor.Stamp = StampAt(cursor.Offset);
    }

    private void Line(Run run, Cursor cursor, string line, DateTimeOffset stamp)
    {
        var steps = TranscriptSteps.Parse(line, cursor.Context, out var own);
        foreach (var s in steps)
            _steps.Add(new Step(++_last, own ?? stamp, run.Team, run.Member, run.Id, s.Kind, s.Tool, s.Target));
    }

    /// <summary>The runs worth reading: still going, or ended after <paramref name="cutoff"/>; the oldest first.</summary>
    private List<Run> Runs(DateTimeOffset cutoff)
    {
        var runs = new List<Run>();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        // The times are all written as UTC round-trip strings, so they compare as text.
        cmd.CommandText = """
            SELECT run_id, member, path, started_at, ended_at FROM transcripts
            WHERE ended_at IS NULL OR ended_at >= $c ORDER BY started_at, run_id;
            """;
        cmd.Parameters.AddWithValue("$c", cutoff.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var qualified = reader.GetString(1);
            var slash = qualified.IndexOf('/');
            if (slash <= 0) continue;
            var team = qualified[..slash];
            // The app's own runs (the Concierge, the agent test) are not a team's.
            if (team.StartsWith('_')) continue;
            if (!DateTimeOffset.TryParse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.None, out var started)) continue;
            DateTimeOffset? ended = !reader.IsDBNull(4) && DateTimeOffset.TryParse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.None, out var e) ? e : null;
            runs.Add(new Run(reader.GetInt64(0), team, qualified[(slash + 1)..], reader.GetString(2), started, ended));
        }
        return runs;
    }
}
