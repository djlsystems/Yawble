using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Concierge;

public sealed record ConciergeToolUse(string Name, string? Detail);

public sealed record ConversationRow(string Id, string Title, string Cli, long? TeamId, string CreatedAt, string UpdatedAt, int Unread = 0);

public sealed record ConciergeMessageRow(
    long Id, string ConversationId, string Role, string Text, string At, string? State, string? Error,
    IReadOnlyList<ConciergeToolUse> Tools, string? Cli, string? RunKey, string? Team = null, string? Kind = null, long? Seq = null);

/// <summary>A follow-up the Concierge keeps in a conversation: a workflow it dispatched, or a whole team (0013).</summary>
public sealed record WatchRow(
    long Id, string ConversationId, string Team, long? Workflow, string What, bool FollowUp, long SinceSeq,
    string CreatedAt, string ExpiresAt, string State, string? EndedAt);

/// <summary>The Concierge's conversations, their messages, its notes (0012_concierge.sql) and its watches (0013).</summary>
public sealed class ConciergeStore(string connectionString, TimeProvider? clock = null)
{
    public string ConnectionString => connectionString;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string Now() => _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    public ConversationRow Create(string id, string title, string cli, long? teamId)
    {
        var now = Now();
        Exec("INSERT INTO concierge_conversations (id, title, cli, team_id, created_at, updated_at) VALUES ($id, $t, $c, $team, $now, $now);",
            ("$id", id), ("$t", title), ("$c", cli), ("$team", (object?)teamId ?? DBNull.Value), ("$now", now));
        return Get(id)!;
    }

    public IReadOnlyList<ConversationRow> List() => Conversations("ORDER BY updated_at DESC, rowid DESC");

    public ConversationRow? Get(string id) => Conversations("WHERE id = $id", ("$id", id)).SingleOrDefault();

    public void Rename(string id, string title) => Exec("UPDATE concierge_conversations SET title = $t WHERE id = $id;", ("$t", title), ("$id", id));

    public void SetTeam(string id, long? teamId) =>
        Exec("UPDATE concierge_conversations SET team_id = $team WHERE id = $id;", ("$team", (object?)teamId ?? DBNull.Value), ("$id", id));

    public void SetCli(string id, string cli) => Exec("UPDATE concierge_conversations SET cli = $c WHERE id = $id;", ("$c", cli), ("$id", id));

    public bool Delete(string id)
    {
        Exec("DELETE FROM concierge_watches WHERE conversation_id = $id;", ("$id", id));
        Exec("DELETE FROM concierge_messages WHERE conversation_id = $id;", ("$id", id));
        return Exec("DELETE FROM concierge_conversations WHERE id = $id;", ("$id", id)) > 0;
    }

    public long AddMessage(string conversationId, string role, string text, string? state = null, string? error = null,
        IReadOnlyList<ConciergeToolUse>? tools = null, string? cli = null, string? runKey = null)
    {
        var now = Now();
        Exec("UPDATE concierge_conversations SET updated_at = $now WHERE id = $id;", ("$now", now), ("$id", conversationId));
        return Scalar("""
            INSERT INTO concierge_messages (conversation_id, role, text, at, state, error, tools, cli, run_key)
            VALUES ($c, $r, $t, $at, $s, $e, $tools, $cli, $rk) RETURNING id;
            """,
            ("$c", conversationId), ("$r", role), ("$t", text), ("$at", now), ("$s", (object?)state ?? DBNull.Value),
            ("$e", (object?)error ?? DBNull.Value), ("$tools", JsonSerializer.Serialize(tools ?? [], Json)),
            ("$cli", (object?)cli ?? DBNull.Value), ("$rk", (object?)runKey ?? DBNull.Value));
    }

    public IReadOnlyList<ConciergeMessageRow> Messages(string conversationId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, conversation_id, role, text, at, state, error, tools, cli, run_key, team, kind, seq FROM concierge_messages WHERE conversation_id = $c ORDER BY id;";
        cmd.Parameters.AddWithValue("$c", conversationId);
        using var r = cmd.ExecuteReader();
        var list = new List<ConciergeMessageRow>();
        while (r.Read())
        {
            list.Add(new ConciergeMessageRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                JsonSerializer.Deserialize<List<ConciergeToolUse>>(r.GetString(7), Json) ?? [],
                r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9),
                r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetInt64(12)));
        }
        return list;
    }

    /// <summary>An update about a team, posted by the core (no agent ran): counts as unread until the person opens the chat.</summary>
    public long AddUpdate(string conversationId, string text, string team, string kind, long? seq)
    {
        var now = Now();
        Exec("UPDATE concierge_conversations SET updated_at = $now, unread = unread + 1 WHERE id = $id;", ("$now", now), ("$id", conversationId));
        return Scalar("""
            INSERT INTO concierge_messages (conversation_id, role, text, at, team, kind, seq)
            VALUES ($c, 'update', $t, $at, $team, $kind, $seq) RETURNING id;
            """,
            ("$c", conversationId), ("$t", text), ("$at", now), ("$team", team), ("$kind", kind), ("$seq", (object?)seq ?? DBNull.Value));
    }

    public ConciergeMessageRow? Message(long id) =>
        Messages(Scalar<string>("SELECT conversation_id FROM concierge_messages WHERE id = $id;", ("$id", id)) ?? "").FirstOrDefault(m => m.Id == id);

    public void MarkRead(string id) => Exec("UPDATE concierge_conversations SET unread = 0 WHERE id = $id;", ("$id", id));

    // ---------------------------------------------------------------- watches

    public WatchRow AddWatch(string conversationId, string team, long? workflow, string what, bool followUp, long sinceSeq, DateTimeOffset expires) =>
        Watches("WHERE id = $id", ("$id", Scalar("""
            INSERT INTO concierge_watches (conversation_id, team, workflow, what, follow_up, since_seq, created_at, expires_at, state)
            VALUES ($c, $team, $wf, $what, $fu, $since, $now, $exp, 'active') RETURNING id;
            """,
            ("$c", conversationId), ("$team", team), ("$wf", (object?)workflow ?? DBNull.Value), ("$what", what), ("$fu", followUp ? 1 : 0),
            ("$since", sinceSeq), ("$now", Now()), ("$exp", expires.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))))).Single();

    public WatchRow? Watch(long id) => Watches("WHERE id = $id", ("$id", id)).SingleOrDefault();

    /// <summary>The active watches of one conversation, or of every conversation.</summary>
    public IReadOnlyList<WatchRow> ActiveWatches(string? conversationId = null) => conversationId is null
        ? Watches("WHERE state = 'active' ORDER BY id")
        : Watches("WHERE state = 'active' AND conversation_id = $c ORDER BY id", ("$c", conversationId));

    public bool EndWatch(long id, string state) =>
        Exec("UPDATE concierge_watches SET state = $s, ended_at = $now WHERE id = $id AND state = 'active';", ("$s", state), ("$now", Now()), ("$id", id)) > 0;

    public void SetWatchFollowUp(long id, bool followUp) =>
        Exec("UPDATE concierge_watches SET follow_up = $fu WHERE id = $id;", ("$fu", followUp ? 1 : 0), ("$id", id));

    /// <summary>Every active watch has seen the bus up to <paramref name="seq"/>.</summary>
    public void AdvanceWatches(long seq) =>
        Exec("UPDATE concierge_watches SET since_seq = $s WHERE state = 'active' AND since_seq < $s;", ("$s", seq));

    private IReadOnlyList<WatchRow> Watches(string where, params (string, object)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT id, conversation_id, team, workflow, what, follow_up, since_seq, created_at, expires_at, state, ended_at FROM concierge_watches {where};";
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        using var r = cmd.ExecuteReader();
        var list = new List<WatchRow>();
        while (r.Read())
        {
            list.Add(new WatchRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.GetString(4),
                r.GetInt64(5) != 0, r.GetInt64(6), r.GetString(7), r.GetString(8), r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10)));
        }
        return list;
    }

    public string Notes()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT text FROM concierge_notes WHERE id = 1;";
        return cmd.ExecuteScalar() as string ?? "";
    }

    public void SaveNotes(string text) =>
        Exec("INSERT INTO concierge_notes (id, text, updated_at) VALUES (1, $t, $now) ON CONFLICT(id) DO UPDATE SET text = $t, updated_at = $now;",
            ("$t", text), ("$now", Now()));

    private IReadOnlyList<ConversationRow> Conversations(string where, params (string, object)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT id, title, cli, team_id, created_at, updated_at, unread FROM concierge_conversations {where};";
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        using var r = cmd.ExecuteReader();
        var list = new List<ConversationRow>();
        while (r.Read())
            list.Add(new ConversationRow(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.GetString(4), r.GetString(5), r.GetInt32(6)));
        return list;
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        return c;
    }

    private int Exec(string sql, params (string, object)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return cmd.ExecuteNonQuery();
    }

    private T? Scalar<T>(string sql, params (string, object)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return cmd.ExecuteScalar() is T t ? t : default;
    }

    private long Scalar(string sql, params (string, object)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
