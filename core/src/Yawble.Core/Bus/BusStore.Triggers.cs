using System.Globalization;
using Microsoft.Data.Sqlite;
using Yawble.Core.Triggers;

namespace Yawble.Core.Bus;

/// <summary>A trigger as stored (0010_triggers).</summary>
public sealed record TriggerRow(
    long Id, long TeamId, long MemberId, string Name, TriggerSchedule Schedule, string Instruction, bool OnlyWhenIdle, bool Enabled,
    DateTimeOffset? NextDueAt, DateTimeOffset? LastFiredAt, string? LastOutcome, string? LastReason, long? LastSeq, int MissedCount,
    string WakeManager = WakeManagerPolicy.Always, FolderWatch? Watch = null, string? EventName = null);

/// <summary>A trigger as the person writes it (create when Id is null).</summary>
public sealed record TriggerDraft(
    long? Id, long MemberId, string Name, TriggerSchedule Schedule, string Instruction, bool OnlyWhenIdle, bool Enabled,
    string WakeManager = WakeManagerPolicy.OnHandbackOrFailure, FolderWatch? Watch = null, string? EventName = null);

public sealed record WakeRow(long Id, long MemberId, long CausationSeq, DateTimeOffset DueAt, string Note);

/// <summary>Triggers and wakes (0010_triggers).</summary>
public sealed partial class BusStore
{
    private const string TriggerColumns =
        "id, team_id, member_id, name, kind, interval_sec, cron, timezone, fire_at, instruction, only_when_idle, enabled, " +
        "next_due_at, last_fired_at, last_outcome, last_reason, last_seq, missed_count, wake_manager, watch_root, watch_path, watch_glob, quiet_sec, event_name";

    public IReadOnlyList<TriggerRow> Triggers(long? teamId = null, long? id = null) =>
        TriggerRows("WHERE ($t IS NULL OR team_id = $t) AND ($id IS NULL OR id = $id) ORDER BY id", ("$t", teamId), ("$id", id));

    public TriggerRow? Trigger(long id) => Triggers(id: id).SingleOrDefault();

    /// <summary>Enabled triggers whose next fire is at or before <paramref name="now"/>.</summary>
    public IReadOnlyList<TriggerRow> DueTriggers(DateTimeOffset now) =>
        TriggerRows("WHERE enabled = 1 AND next_due_at IS NOT NULL AND next_due_at <= $now ORDER BY next_due_at, id", ("$now", Iso(now)));

    public long SaveTrigger(TriggerDraft d, long teamId, DateTimeOffset? nextDueAt)
    {
        var s = d.Schedule;
        // Who saved it: its fires carry that person as their origin (0015_provenance.sql).
        var by = Provenance.Actor.Current ?? Provenance.Actor.Window;
        (string, object?)[] args =
        [
            ("$team", teamId), ("$member", d.MemberId), ("$name", d.Name), ("$kind", s.Kind), ("$iv", s.IntervalSec), ("$cron", s.Cron),
            ("$tz", s.Timezone), ("$at", s.FireAt is { } a ? Iso(a) : null), ("$ins", d.Instruction), ("$idle", d.OnlyWhenIdle ? 1 : 0),
            ("$en", d.Enabled ? 1 : 0), ("$next", nextDueAt is { } n ? Iso(n) : null), ("$now", Now()), ("$id", d.Id), ("$wake", d.WakeManager),
            ("$by", by.User), ("$byvia", by.Via),
            ("$ev", d.EventName), ("$wr", d.Watch?.Root), ("$wp", d.Watch?.Path), ("$wg", string.IsNullOrWhiteSpace(d.Watch?.Glob) ? null : d.Watch!.Glob!.Trim()), ("$wq", d.Watch?.QuietSec),
        ];
        if (d.Id is null)
        {
            return Scalar<long>(
                """
                INSERT INTO triggers (team_id, member_id, name, kind, interval_sec, cron, timezone, fire_at, instruction, only_when_idle,
                                      enabled, next_due_at, wake_manager, created_at, updated_at, saved_by, saved_via,
                                      watch_root, watch_path, watch_glob, quiet_sec, event_name)
                VALUES ($team, $member, $name, $kind, $iv, $cron, $tz, $at, $ins, $idle, $en, $next, $wake, $now, $now, $by, $byvia,
                        $wr, $wp, $wg, $wq, $ev) RETURNING id;
                """, args);
        }
        Exec(
            """
            UPDATE triggers SET member_id = $member, name = $name, kind = $kind, interval_sec = $iv, cron = $cron, timezone = $tz,
                fire_at = $at, instruction = $ins, only_when_idle = $idle, enabled = $en, next_due_at = $next, wake_manager = $wake,
                updated_at = $now, saved_by = $by, saved_via = $byvia,
                watch_root = $wr, watch_path = $wp, watch_glob = $wg, quiet_sec = $wq, watch_state = NULL, event_name = $ev
            WHERE id = $id;
            """, args);
        return d.Id.Value;
    }

    public bool DeleteTrigger(long id)
    {
        if (Trigger(id) is null) return false;
        Exec("DELETE FROM triggers WHERE id = $id;", ("$id", id));
        return true;
    }

    /// <summary>Records what one due fire did and arms the next.</summary>
    public void RecordTriggerOutcome(long id, DateTimeOffset? firedAt, DateTimeOffset? next, string outcome, string? reason, long? seq, int missedCount) =>
        Exec(
            """
            UPDATE triggers SET next_due_at = $next, last_outcome = $o, last_reason = $r, missed_count = $m,
                last_fired_at = coalesce($fired, last_fired_at), last_seq = coalesce($seq, last_seq), updated_at = $now
            WHERE id = $id;
            """,
            ("$next", next is { } n ? Iso(n) : null), ("$o", outcome), ("$r", reason), ("$m", missedCount),
            ("$fired", firedAt is { } f ? Iso(f) : null), ("$seq", seq), ("$now", Now()), ("$id", id));

    /// <summary>The folder watcher's memory of a trigger (its baseline listing, a change settling).</summary>
    public string? WatchState(long id) => Scalar<string?>("SELECT watch_state FROM triggers WHERE id = $id;", ("$id", id));

    /// <summary>After a look that did not fire: the watcher's memory and the next look; the last outcome stays.</summary>
    public void SetWatch(long id, string state, DateTimeOffset? next) =>
        Exec("UPDATE triggers SET watch_state = $s, next_due_at = $next WHERE id = $id;", ("$s", state), ("$next", next is { } n ? Iso(n) : null), ("$id", id));

    public void SetWatchState(long id, string state) => Exec("UPDATE triggers SET watch_state = $s WHERE id = $id;", ("$s", state), ("$id", id));

    public long AddWake(long memberId, long causationSeq, DateTimeOffset dueAt, string note) =>
        Scalar<long>("INSERT INTO wakes (member_id, causation_seq, due_at, note, created_at) VALUES ($m, $c, $d, $n, $now) RETURNING id;",
            ("$m", memberId), ("$c", causationSeq), ("$d", Iso(dueAt)), ("$n", note), ("$now", Now()));

    public IReadOnlyList<WakeRow> DueWakes(DateTimeOffset now)
    {
        using var cmd = Command("SELECT id, member_id, causation_seq, due_at, note FROM wakes WHERE fired_seq IS NULL AND due_at <= $now ORDER BY due_at, id;",
            ("$now", Iso(now)));
        using var r = cmd.ExecuteReader();
        var list = new List<WakeRow>();
        while (r.Read()) list.Add(new WakeRow(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), Parse(r.GetString(3))!.Value, r.GetString(4)));
        return list;
    }

    /// <summary>The member's next wake that has not fired (what it said it will come back to), or null.</summary>
    public WakeRow? NextWake(long memberId)
    {
        using var cmd = Command("SELECT id, member_id, causation_seq, due_at, note FROM wakes WHERE fired_seq IS NULL AND member_id = $m ORDER BY due_at, id LIMIT 1;",
            ("$m", memberId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new WakeRow(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), Parse(r.GetString(3))!.Value, r.GetString(4)) : null;
    }

    public void MarkWakeFired(long id, long seq) => Exec("UPDATE wakes SET fired_seq = $s WHERE id = $id;", ("$s", seq), ("$id", id));

    private List<TriggerRow> TriggerRows(string where, params (string, object?)[] args)
    {
        using var cmd = Command($"SELECT {TriggerColumns} FROM triggers {where};", args);
        using var r = cmd.ExecuteReader();
        var list = new List<TriggerRow>();
        while (r.Read())
        {
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            var schedule = new TriggerSchedule(r.GetString(4), r.IsDBNull(5) ? null : r.GetInt32(5), S(6), S(7), Parse(S(8)));
            list.Add(new TriggerRow(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), schedule, r.GetString(9), r.GetInt64(10) == 1,
                r.GetInt64(11) == 1, Parse(S(12)), Parse(S(13)), S(14), S(15), r.IsDBNull(16) ? null : r.GetInt64(16), r.GetInt32(17),
                r.GetString(18), S(19) is { } root ? new FolderWatch(root, S(20) ?? "", S(21), r.IsDBNull(22) ? FolderWatch.DefaultQuietSec : r.GetInt32(22)) : null, S(23)));
        }
        return list;
    }

    /// <summary>UTC, fixed width, so stored times compare as text.</summary>
    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Parse(string? s) =>
        s is null ? null : DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
