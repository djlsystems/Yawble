using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Bus;

/// <summary>
/// The bus tables (0002_bus.sql) over one SQLite connection. Not thread-safe: the core opens one store and
/// <see cref="BusEngine"/> serialises every call through its gate, so the core stays the only writer.
/// Multi-statement changes go through <see cref="InTransaction{T}"/>, so a crash leaves either all or none.
/// </summary>
public sealed partial class BusStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TimeProvider _clock;
    private SqliteTransaction? _tx;

    public BusStore(string connectionString, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        // Per connection, so set here and not in a migration.
        Exec("PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
    }

    public void Dispose() => _connection.Dispose();

    public string Now() => _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    public TimeProvider Clock => _clock;

    public T InTransaction<T>(Func<T> work)
    {
        if (_tx is not null) return work();
        _tx = _connection.BeginTransaction();
        try
        {
            var result = work();
            _tx.Commit();
            return result;
        }
        catch
        {
            _tx.Rollback();
            throw;
        }
        finally
        {
            _tx.Dispose();
            _tx = null;
        }
    }

    public void InTransaction(Action work) => InTransaction(() => { work(); return 0; });

    // ---- teams and members ----

    public long CreateTeam(string name) =>
        Scalar<long>("INSERT INTO teams (name, created_at) VALUES ($n, $t) RETURNING id;", ("$n", name), ("$t", Now()));

    public MemberRef AddMember(long teamId, string name, MemberRole role, string cli = "claude")
    {
        var id = Scalar<long>(
            "INSERT INTO members (team_id, name, role, cli, created_at) VALUES ($team, $n, $r, $cli, $t) RETURNING id;",
            ("$team", teamId), ("$n", name), ("$r", role == MemberRole.Manager ? "manager" : "member"), ("$cli", cli), ("$t", Now()));
        return Member(id)!;
    }

    public MemberRef? Member(long id) => Members("WHERE m.id = $id", ("$id", id)).SingleOrDefault();

    public MemberRef? Member(string team, string name) =>
        Members("WHERE t.name = $t AND m.name = $n", ("$t", team), ("$n", name)).SingleOrDefault();

    public MemberRef? MemberBySubscriber(string qualified)
    {
        var slash = qualified.IndexOf('/');
        return slash <= 0 ? null : Member(qualified[..slash], qualified[(slash + 1)..]);
    }

    public IReadOnlyList<MemberRef> TeamMembers(long teamId) => Members("WHERE m.team_id = $t", ("$t", teamId));

    public IReadOnlyList<MemberRef> AllMembers() => Members("");

    private List<MemberRef> Members(string where, params (string, object?)[] args)
    {
        using var cmd = Command(
            $"SELECT m.id, m.team_id, t.name, m.name, m.role FROM members m JOIN teams t ON t.id = m.team_id {where} ORDER BY m.id;", args);
        using var r = cmd.ExecuteReader();
        var list = new List<MemberRef>();
        while (r.Read())
        {
            list.Add(new MemberRef(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
                r.GetString(4) == "manager" ? MemberRole.Manager : MemberRole.Member));
        }
        return list;
    }

    // ---- the log ----

    /// <summary>
    /// Appends a message. Correlation and depth come from the cause, read inside the same transaction;
    /// a root correlates to its own seq and has depth 0.
    /// </summary>
    public Message Append(NewMessage message) => InTransaction(() =>
    {
        long correlation = 0;
        var depth = 0;
        if (message.CausationSeq is { } cause)
        {
            var parent = Find(cause) ?? throw new BusRefusal("no_such_cause", $"message {cause} does not exist");
            correlation = parent.CorrelationId;
            depth = parent.Depth + 1;
        }
        var at = _clock.GetUtcNow();
        var (via, user) = Stamp(message);
        // The chain's origin: the cause's (rows from before 0015 have none, so the cause itself stands in), or this one.
        long? originSeq = null;
        string? originVia = via, originUser = user;
        if (message.CausationSeq is { } c)
        {
            using var cmd = Command("SELECT coalesce(origin_seq, seq), coalesce(origin_via, via), coalesce(origin_user, user_name) FROM messages WHERE seq = $c;", ("$c", c));
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                originSeq = r.GetInt64(0);
                originVia = r.IsDBNull(1) ? null : r.GetString(1);
                originUser = r.IsDBNull(2) ? null : r.GetString(2);
            }
        }
        var seq = Scalar<long>(
            """
            INSERT INTO messages (type, payload, source, correlation_id, causation_seq, depth, occurred_at, via, user_name, origin_seq, origin_via, origin_user)
            VALUES ($type, $payload, $source, $corr, $cause, $depth, $at, $via, $user, $oseq, $ovia, $ouser) RETURNING seq;
            """,
            ("$type", message.Type), ("$payload", message.Payload), ("$source", message.Source), ("$corr", correlation),
            ("$cause", message.CausationSeq), ("$depth", depth), ("$at", at.ToString("O", CultureInfo.InvariantCulture)),
            ("$via", via), ("$user", user), ("$oseq", originSeq), ("$ovia", originVia), ("$ouser", originUser));
        if (message.CausationSeq is null)
        {
            correlation = seq;
            Exec("UPDATE messages SET correlation_id = $s, origin_seq = $s WHERE seq = $s;", ("$s", seq));
        }
        return new Message(seq, message.Type, message.Payload, message.Source, correlation, message.CausationSeq, depth, at);
    });

    /// <summary>
    /// How a message came in and who is behind it (0015_provenance.sql). The person's own messages carry the call's
    /// actor (the window, a cloud viewer, the Concierge); a trigger's fire carries whoever pressed Run now, else whoever
    /// last saved the trigger; agents and the app carry no person (their chain's origin does).
    /// </summary>
    private (string Via, string? User) Stamp(NewMessage message)
    {
        if (message.Source == MessageTypes.PersonSource)
        {
            var actor = Provenance.Actor.Current ?? Provenance.Actor.Window;
            return (actor.Via, actor.User);
        }
        if (message.Source.StartsWith(BusEngine.TriggerSourcePrefix, StringComparison.Ordinal))
        {
            var savedBy = long.TryParse(message.Source[BusEngine.TriggerSourcePrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? Scalar<string?>("SELECT saved_by FROM triggers WHERE id = $id;", ("$id", id))
                : null;
            return (Provenance.Actor.Trigger, Provenance.Actor.Current?.User ?? savedBy);
        }
        return message.Source == MessageTypes.HostSource ? (Provenance.Actor.Host, null) : (Provenance.Actor.Agent, null);
    }

    /// <summary>Who and how for one message, and its chain's origin (null for a row from before 0015).</summary>
    public MessageProvenance? ProvenanceOf(long seq)
    {
        using var cmd = Command("SELECT via, user_name, origin_seq, origin_via, origin_user FROM messages WHERE seq = $s;", ("$s", seq));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new MessageProvenance(S(r, 0), S(r, 1), r.IsDBNull(2) ? null : r.GetInt64(2), S(r, 3), S(r, 4));
    }

    public Message? Find(long seq) => Messages("WHERE seq = $s", ("$s", seq)).SingleOrDefault();

    public IReadOnlyList<Message> Workflow(long correlation) => Messages("WHERE correlation_id = $c", ("$c", correlation));

    public IReadOnlyList<Message> AllMessages() => Messages("");

    /// <summary>
    /// A team's latest messages, oldest first: what its members published and what they were told (the Concierge's
    /// view of recent activity).
    /// </summary>
    public IReadOnlyList<Message> RecentTeamMessages(string team, int limit) =>
        Messages("WHERE seq IN (SELECT seq FROM messages WHERE source LIKE $src ESCAPE '!' OR type LIKE $instr ESCAPE '!' ORDER BY seq DESC LIMIT $n)",
            ("$src", Like(team) + "/%"), ("$instr", MessageTypes.InstructionPrefix + Like(team) + "/%"), ("$n", Math.Clamp(limit, 1, 200)));

    /// <summary>Each member's newest report in a team (progress, blocked, needs a decision, completed, handed back,
    /// failed), by member name: what the member last said about its work.</summary>
    public IReadOnlyDictionary<string, Message> LatestReports(string team)
    {
        string[] reports = [MessageTypes.Progress, MessageTypes.Blocked, MessageTypes.NeedsDecision, MessageTypes.Completed, MessageTypes.Handback, MessageTypes.Failed];
        var names = reports.Select((_, i) => "$r" + i).ToArray();
        var args = reports.Select((t, i) => (names[i], (object?)t)).Append(("$src", Like(team) + "/%")).ToArray();
        var list = Messages($"WHERE seq IN (SELECT max(seq) FROM messages WHERE source LIKE $src ESCAPE '!' AND type IN ({string.Join(", ", names)}) GROUP BY source)", args);
        return list.ToDictionary(m => m.Source[(m.Source.IndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The kinds of message that set a member's card status line (BusEngine's handback, blocked, report and declare).</summary>
    public static readonly IReadOnlyList<string> StatusLineTypes =
        [MessageTypes.Handback, MessageTypes.Blocked, MessageTypes.Progress, MessageTypes.NeedsDecision, MessageTypes.WorkflowCompleted];

    /// <summary>
    /// A member's newest message of the <see cref="StatusLineTypes"/> since the member was made (so a member removed
    /// and made again under the same name starts blank); null when it has said nothing. One row at most.
    /// </summary>
    public Message? LastStatusReport(MemberRef member)
    {
        var names = StatusLineTypes.Select((_, i) => "$t" + i).ToArray();
        var args = StatusLineTypes.Select((t, i) => (names[i], (object?)t)).Append(("$src", member.Qualified)).Append(("$id", member.Id)).ToArray();
        return Messages($"WHERE seq IN (SELECT seq FROM messages WHERE source = $src AND type IN ({string.Join(", ", names)}) "
            + "AND occurred_at >= (SELECT created_at FROM members WHERE id = $id) ORDER BY seq DESC LIMIT 1)", args).SingleOrDefault();
    }

    /// <summary>
    /// A page of the log for the Monitor: with <paramref name="after"/> 0, the newest <paramref name="limit"/> messages;
    /// otherwise the oldest <paramref name="limit"/> after it. Oldest first either way.
    /// </summary>
    public IReadOnlyList<Message> MessagesPage(long after, int limit)
    {
        var n = Math.Clamp(limit, 1, 500);
        return after <= 0
            ? Messages("WHERE seq IN (SELECT seq FROM messages ORDER BY seq DESC LIMIT $n)", ("$n", n))
            : Messages("WHERE seq IN (SELECT seq FROM messages WHERE seq > $a ORDER BY seq LIMIT $n)", ("$a", after), ("$n", n));
    }

    /// <summary>The oldest <paramref name="limit"/> messages after <paramref name="after"/>, oldest first (the board's projector).</summary>
    public IReadOnlyList<Message> MessagesFrom(long after, int limit) =>
        Messages("WHERE seq IN (SELECT seq FROM messages WHERE seq > $a ORDER BY seq LIMIT $n)", ("$a", after), ("$n", Math.Clamp(limit, 1, 1000)));

    /// <summary>A literal for LIKE ... ESCAPE '!'.</summary>
    private static string Like(string s) => s.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");

    public long MaxSeq() => Scalar<long?>("SELECT max(seq) FROM messages;") ?? 0;

    public IReadOnlyList<Message> MessagesAfter(long position, long upTo, IReadOnlyCollection<string> types)
    {
        if (types.Count == 0) return [];
        var names = types.Select((_, i) => "$ty" + i).ToArray();
        var args = new List<(string, object?)> { ("$p", position), ("$u", upTo) };
        args.AddRange(types.Select((t, i) => (names[i], (object?)t)));
        return Messages($"WHERE seq > $p AND seq <= $u AND type IN ({string.Join(",", names)})", [.. args]);
    }

    /// <summary>Declared complete or closed by a person: the workflow has ended (new activity reopens it).</summary>
    public bool WorkflowEnded(long correlation) =>
        Scalar<long>(
            """
            SELECT count(*) FROM messages e WHERE e.correlation_id = $c AND e.type IN ($done, $closed)
               AND NOT EXISTS (SELECT 1 FROM messages n WHERE n.correlation_id = $c AND n.seq > e.seq AND n.type LIKE 'agent.instruction.%');
            """,
            ("$c", correlation), ("$done", MessageTypes.WorkflowCompleted), ("$closed", MessageTypes.WorkflowClosed)) > 0;

    /// <summary>The workflows that touched a team, newest activity first: id, first and last time, message count.</summary>
    public IReadOnlyList<(long Id, DateTimeOffset First, DateTimeOffset Last, int Count)> WorkflowsOf(string team, int limit)
    {
        using var cmd = Command(
            """
            SELECT correlation_id, min(occurred_at), max(occurred_at), count(*) FROM messages
            WHERE correlation_id IN (SELECT DISTINCT correlation_id FROM messages
                                     WHERE source LIKE $src ESCAPE '!' OR type LIKE $ins ESCAPE '!')
            GROUP BY correlation_id ORDER BY max(seq) DESC LIMIT $n;
            """,
            ("$src", Like(team) + "/%"), ("$ins", Like(MessageTypes.InstructionPrefix + team) + "/%"), ("$n", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<(long, DateTimeOffset, DateTimeOffset, int)>();
        while (r.Read())
            list.Add((r.GetInt64(0), DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture), DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture), r.GetInt32(3)));
        return list;
    }

    /// <summary>Wakes asked for in this workflow that have not fired.</summary>
    public int OpenWakes(long correlation) =>
        (int)Scalar<long>("SELECT count(*) FROM wakes w JOIN messages m ON m.seq = w.causation_seq WHERE w.fired_seq IS NULL AND m.correlation_id = $c;", ("$c", correlation));

    /// <summary>Cancels the workflow's wakes that have not fired (fired_seq 0: cancelled, never fired).</summary>
    public int CancelWakes(long correlation)
    {
        using var cmd = Command("UPDATE wakes SET fired_seq = 0 WHERE fired_seq IS NULL AND causation_seq IN (SELECT seq FROM messages WHERE correlation_id = $c);", ("$c", correlation));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>The newest <paramref name="limit"/> messages of a type, oldest first.</summary>
    public IReadOnlyList<Message> RecentOfType(string type, int limit) =>
        Messages("WHERE seq IN (SELECT seq FROM messages WHERE type = $t ORDER BY seq DESC LIMIT $n)", ("$t", type), ("$n", limit));

    public bool WorkflowDeclared(long correlation) =>
        Scalar<long>("SELECT count(*) FROM messages WHERE correlation_id = $c AND type = $t;",
            ("$c", correlation), ("$t", MessageTypes.WorkflowCompleted)) > 0;

    private List<Message> Messages(string where, params (string, object?)[] args)
    {
        using var cmd = Command(
            $"SELECT seq, type, payload, source, correlation_id, causation_seq, depth, occurred_at FROM messages {where} ORDER BY seq;", args);
        using var r = cmd.ExecuteReader();
        var list = new List<Message>();
        while (r.Read())
        {
            list.Add(new Message(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4),
                r.IsDBNull(5) ? null : r.GetInt64(5), r.GetInt32(6),
                DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }
        return list;
    }

    // ---- subscriptions and cursors ----

    public void Subscribe(string subscriber, string type) =>
        Exec("INSERT OR IGNORE INTO subscriptions (subscriber, type) VALUES ($s, $t);", ("$s", subscriber), ("$t", type));

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Subscriptions()
    {
        using var cmd = Command("SELECT subscriber, type FROM subscriptions ORDER BY subscriber, type;");
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        while (r.Read())
        {
            if (!map.TryGetValue(r.GetString(0), out var list)) map[r.GetString(0)] = list = [];
            list.Add(r.GetString(1));
        }
        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A new subscriber starts at the current end of the log: it is not woken by history.</summary>
    public void InitCursor(string subscriber) =>
        Exec("INSERT OR IGNORE INTO cursors (subscriber, position) VALUES ($s, (SELECT coalesce(max(seq), 0) FROM messages));",
            ("$s", subscriber));

    public long Cursor(string subscriber) =>
        Scalar<long?>("SELECT position FROM cursors WHERE subscriber = $s;", ("$s", subscriber)) ?? 0;

    public void AdvanceCursor(string subscriber, long position) =>
        Exec("""
             INSERT INTO cursors (subscriber, position) VALUES ($s, $p)
             ON CONFLICT(subscriber) DO UPDATE SET position = max(position, excluded.position);
             """, ("$s", subscriber), ("$p", position));

    // ---- pending deliveries ----

    public void AddPending(string subscriber, long seq) =>
        Exec("INSERT OR IGNORE INTO pending_deliveries (subscriber, seq) VALUES ($s, $q);", ("$s", subscriber), ("$q", seq));

    public sealed record PendingRow(string Subscriber, long Seq, bool Started, long? RunId, long Correlation);

    public IReadOnlyList<PendingRow> Pending(string? subscriber = null, bool? started = null)
    {
        var where = new List<string>();
        if (subscriber is not null) where.Add("p.subscriber = $s");
        if (started is not null) where.Add("p.started = $st");
        using var cmd = Command(
            $"""
             SELECT p.subscriber, p.seq, p.started, p.run_id, m.correlation_id
             FROM pending_deliveries p JOIN messages m ON m.seq = p.seq
             {(where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where))}
             ORDER BY p.seq;
             """, ("$s", subscriber), ("$st", started is true ? 1 : 0));
        using var r = cmd.ExecuteReader();
        var list = new List<PendingRow>();
        while (r.Read())
        {
            list.Add(new PendingRow(r.GetString(0), r.GetInt64(1), r.GetInt64(2) != 0, r.IsDBNull(3) ? null : r.GetInt64(3), r.GetInt64(4)));
        }
        return list;
    }

    public void StartPending(string subscriber, long seq, long runId) =>
        Exec("UPDATE pending_deliveries SET started = 1, run_id = $r WHERE subscriber = $s AND seq = $q;",
            ("$r", runId), ("$s", subscriber), ("$q", seq));

    public void RemovePendingOfRun(long runId) =>
        Exec("DELETE FROM pending_deliveries WHERE run_id = $r;", ("$r", runId));

    // ---- runs ----

    public sealed record RunRow(
        long Id, long MemberId, long QueueId, long CausationSeq, long CorrelationId, int Items, string State,
        string QueuedAt, string StartedAt, string? EndedAt, string? Reason);

    public long InsertRun(long memberId, long queueId, long causation, long correlation, int items, string queuedAt) =>
        Scalar<long>(
            """
            INSERT INTO runs (member_id, queue_id, causation_seq, correlation_id, items, state, queued_at, started_at)
            VALUES ($m, $q, $c, $corr, $i, 'running', $qa, $sa) RETURNING id;
            """,
            ("$m", memberId), ("$q", queueId), ("$c", causation), ("$corr", correlation), ("$i", items), ("$qa", queuedAt), ("$sa", Now()));

    public void EndRun(long runId, string state, string? reason) =>
        Exec("UPDATE runs SET state = $st, ended_at = $t, reason = $r WHERE id = $id AND state = 'running';",
            ("$st", state), ("$t", Now()), ("$r", reason), ("$id", runId));

    public IReadOnlyList<RunRow> Runs(string? state = null)
    {
        using var cmd = Command(
            $"""
             SELECT id, member_id, queue_id, causation_seq, correlation_id, items, state, queued_at, started_at, ended_at, reason
             FROM runs {(state is null ? "" : "WHERE state = $st")} ORDER BY id;
             """, ("$st", state));
        using var r = cmd.ExecuteReader();
        var list = new List<RunRow>();
        while (r.Read())
        {
            list.Add(new RunRow(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt32(5),
                r.GetString(6), r.GetString(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10)));
        }
        return list;
    }

    public long RunningCount() => Scalar<long>("SELECT count(*) FROM runs WHERE state = 'running';");

    // ---- the global run queue ----

    /// <summary>Kind: "run" or "interactive" (0008): both wait in the one FIFO for a global slot.</summary>
    public sealed record QueueRow(long Id, long MemberId, string EnqueuedAt, string Kind = QueueKinds.Run);

    public void Enqueue(long memberId) =>
        Exec("INSERT OR IGNORE INTO run_queue (member_id, enqueued_at) VALUES ($m, $t);", ("$m", memberId), ("$t", Now()));

    public IReadOnlyList<QueueRow> Queue()
    {
        using var cmd = Command("SELECT id, member_id, enqueued_at, kind FROM run_queue ORDER BY id;");
        using var r = cmd.ExecuteReader();
        var list = new List<QueueRow>();
        while (r.Read()) list.Add(new QueueRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3)));
        return list;
    }

    public void Dequeue(long queueId) => Exec("DELETE FROM run_queue WHERE id = $id;", ("$id", queueId));

    // ---- plumbing ----

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            if (sql.Contains(name, StringComparison.Ordinal)) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return cmd;
    }

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var cmd = Command(sql, args);
        cmd.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string, object?)[] args)
    {
        using var cmd = Command(sql, args);
        var value = cmd.ExecuteScalar();
        if (value is null or DBNull) return default!;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }
}
