using System.Globalization;

namespace Yawble.Core.Bus;

/// <summary>A standing approval (0024): something the person approved for a team once and for all.</summary>
public sealed record TeamApproval(long Id, long TeamId, string Text, string CreatedAt, string? CreatedBy);

public sealed partial class BusStore
{
    public IReadOnlyList<TeamApproval> Approvals(long teamId)
    {
        using var cmd = Command("SELECT id, team_id, text, created_at, created_by FROM team_approvals WHERE team_id = $t ORDER BY id;", ("$t", teamId));
        using var r = cmd.ExecuteReader();
        var list = new List<TeamApproval>();
        while (r.Read()) list.Add(new TeamApproval(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }

    public long AddApproval(long teamId, string text, string? by) =>
        Scalar<long>("INSERT INTO team_approvals (team_id, text, created_at, created_by) VALUES ($t, $x, $at, $by) RETURNING id;",
            ("$t", teamId), ("$x", text), ("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)), ("$by", by));

    public void RemoveApproval(long teamId, long id) =>
        Exec("DELETE FROM team_approvals WHERE id = $id AND team_id = $t;", ("$id", id), ("$t", teamId));
}
