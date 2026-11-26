using System.Globalization;

namespace Yawble.Core.Bus;

/// <summary>A deliverable's expiry bookkeeping (table deliverable_expiry): the person's last Extend and its one warning.</summary>
public sealed record DeliverableExpiryRow(long Deliverable, DateTimeOffset? ExtendedAt, DateTimeOffset? WarnedFor, DateTimeOffset? WarnedAt, long? WarnedQuestion);

/// <summary>Deliverable expiry: the team's setting, and the Extend and warning rows.</summary>
public sealed partial class BusStore
{
    /// <summary>The team's deliverableExpiryDays as stored; null when it was never set (the default applies).</summary>
    public int? DeliverableExpiryDays(long teamId) =>
        Scalar<long?>("SELECT deliverable_expiry_days FROM team_settings WHERE team_id = $t;", ("$t", teamId)) is { } d ? (int)d : null;

    /// <summary>Sets the team's deliverableExpiryDays; null goes back to the default.</summary>
    public void SetDeliverableExpiryDays(long teamId, int? days) =>
        Exec("""
            INSERT INTO team_settings (team_id, repo_path, deliverable_expiry_days, updated_at) VALUES ($t, NULL, $d, $at)
            ON CONFLICT (team_id) DO UPDATE SET deliverable_expiry_days = excluded.deliverable_expiry_days, updated_at = excluded.updated_at;
            """, ("$t", teamId), ("$d", days), ("$at", Now()));

    public DeliverableExpiryRow? DeliverableExpiry(long deliverable)
    {
        using var cmd = Command("SELECT extended_at, warned_for, warned_at, warned_question FROM deliverable_expiry WHERE deliverable = $w;", ("$w", deliverable));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        DateTimeOffset? T(int i) => r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return new DeliverableExpiryRow(deliverable, T(0), T(1), T(2), r.IsDBNull(3) ? null : r.GetInt64(3));
    }

    /// <summary>The person pressed Extend: the deliverable's idle clock starts again now.</summary>
    public void SetDeliverableExtended(long deliverable, DateTimeOffset at) =>
        Exec("""
            INSERT INTO deliverable_expiry (deliverable, extended_at) VALUES ($w, $at)
            ON CONFLICT (deliverable) DO UPDATE SET extended_at = excluded.extended_at;
            """, ("$w", deliverable), ("$at", Iso(at)));

    /// <summary>The one warning for the idle stretch that began at <paramref name="idleSince"/>.</summary>
    public void SetDeliverableWarned(long deliverable, DateTimeOffset idleSince, DateTimeOffset at, long question) =>
        Exec("""
            INSERT INTO deliverable_expiry (deliverable, warned_for, warned_at, warned_question) VALUES ($w, $for, $at, $q)
            ON CONFLICT (deliverable) DO UPDATE SET warned_for = excluded.warned_for, warned_at = excluded.warned_at, warned_question = excluded.warned_question;
            """, ("$w", deliverable), ("$for", Iso(idleSince)), ("$at", Iso(at)), ("$q", question));
}
