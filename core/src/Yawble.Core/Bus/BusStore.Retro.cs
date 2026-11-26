namespace Yawble.Core.Bus;

/// <summary>The reads behind deliverable_digest and the retro item.</summary>
public sealed partial class BusStore
{
    /// <summary>The runs of one deliverable, oldest first.</summary>
    public IReadOnlyList<RunRow> RunsOf(long correlation)
    {
        using var cmd = Command(
            """
            SELECT id, member_id, queue_id, causation_seq, correlation_id, items, state, queued_at, started_at, ended_at, reason, failure_class
            FROM runs WHERE correlation_id = $c ORDER BY id;
            """, ("$c", correlation));
        using var r = cmd.ExecuteReader();
        var list = new List<RunRow>();
        while (r.Read())
        {
            list.Add(new RunRow(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt32(5),
                r.GetString(6), r.GetString(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : r.GetString(11)));
        }
        return list;
    }

    /// <summary>Whether a retro item was ever queued for this deliverable (an instruction whose payload's retroOf names it).</summary>
    public bool RetroQueued(long correlation) =>
        Scalar<long>(
            "SELECT count(*) FROM messages WHERE type LIKE 'agent.instruction.%' AND json_valid(payload) AND json_extract(payload, '$.retroOf') = $c;",
            ("$c", correlation)) > 0;

    /// <summary>The deliverable a retro item is for, when this message is one (its payload's retroOf); null otherwise.</summary>
    public static long? RetroOf(Message m)
    {
        if (!m.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal) || !m.Payload.Contains("\"retroOf\"", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(m.Payload);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && doc.RootElement.TryGetProperty("retroOf", out var v) && v.TryGetInt64(out var id) ? id : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
