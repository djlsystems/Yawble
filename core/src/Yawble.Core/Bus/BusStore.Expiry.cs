using System.Globalization;

namespace Yawble.Core.Bus;

/// <summary>A workflow's expiry bookkeeping (0045_workflow_expiry.sql): the person's last Extend and its one warning.</summary>
public sealed record WorkflowExpiryRow(long Workflow, DateTimeOffset? ExtendedAt, DateTimeOffset? WarnedFor, DateTimeOffset? WarnedAt, long? WarnedQuestion);

/// <summary>Workflow expiry: the team's setting, and the Extend and warning rows.</summary>
public sealed partial class BusStore
{
    /// <summary>The team's workflowExpiryDays as stored; null when it was never set (the default applies).</summary>
    public int? WorkflowExpiryDays(long teamId) =>
        Scalar<long?>("SELECT workflow_expiry_days FROM team_settings WHERE team_id = $t;", ("$t", teamId)) is { } d ? (int)d : null;

    /// <summary>Sets the team's workflowExpiryDays; null goes back to the default.</summary>
    public void SetWorkflowExpiryDays(long teamId, int? days) =>
        Exec("""
            INSERT INTO team_settings (team_id, repo_path, workflow_expiry_days, updated_at) VALUES ($t, NULL, $d, $at)
            ON CONFLICT (team_id) DO UPDATE SET workflow_expiry_days = excluded.workflow_expiry_days, updated_at = excluded.updated_at;
            """, ("$t", teamId), ("$d", days), ("$at", Now()));

    public WorkflowExpiryRow? WorkflowExpiry(long workflow)
    {
        using var cmd = Command("SELECT extended_at, warned_for, warned_at, warned_question FROM workflow_expiry WHERE workflow = $w;", ("$w", workflow));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        DateTimeOffset? T(int i) => r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return new WorkflowExpiryRow(workflow, T(0), T(1), T(2), r.IsDBNull(3) ? null : r.GetInt64(3));
    }

    /// <summary>The person pressed Extend: the workflow's idle clock starts again now.</summary>
    public void SetWorkflowExtended(long workflow, DateTimeOffset at) =>
        Exec("""
            INSERT INTO workflow_expiry (workflow, extended_at) VALUES ($w, $at)
            ON CONFLICT (workflow) DO UPDATE SET extended_at = excluded.extended_at;
            """, ("$w", workflow), ("$at", Iso(at)));

    /// <summary>The one warning for the idle stretch that began at <paramref name="idleSince"/>.</summary>
    public void SetWorkflowWarned(long workflow, DateTimeOffset idleSince, DateTimeOffset at, long question) =>
        Exec("""
            INSERT INTO workflow_expiry (workflow, warned_for, warned_at, warned_question) VALUES ($w, $for, $at, $q)
            ON CONFLICT (workflow) DO UPDATE SET warned_for = excluded.warned_for, warned_at = excluded.warned_at, warned_question = excluded.warned_question;
            """, ("$w", workflow), ("$for", Iso(idleSince)), ("$at", Iso(at)), ("$q", question));
}
