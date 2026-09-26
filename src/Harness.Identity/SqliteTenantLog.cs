using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Harness.Contracts;

namespace Harness.Identity;

/// <summary>
/// The tenant log, over the same SQLite file the accounts and the message log use.
///
/// Mechanical sibling of <see cref="SqliteTeamStore"/> - same connection string, same
/// <c>Pooling=false</c>, same reliance on <c>AuthSchema</c>'s steps having been applied by
/// <c>SchemaMigrator</c> before this is constructed.
///
/// <b><c>tenant_events</c> HAS NO FOREIGN KEYS AT ALL, and that is deliberate rather than an
/// omission.</b> It must outlive the teams and accounts it names, so a reference would either block
/// the delete or take the record with it - and a record that vanishes when its subject does is not
/// an audit trail.
///
/// This connection nonetheless takes the shared <see cref="SqlitePragmas"/>, which turn foreign keys
/// ON. That is a NO-OP here: the pragma only does anything where a constraint is declared, and
/// none is. It takes them anyway so every store opens its connections the same way - the drift the
/// helper exists to end. Opting out would mean the next store to copy this file inherits the exception rather than the rule.
/// </summary>
public sealed class SqliteTenantLog : ITenantLog
{
    private readonly string _connectionString;

    /// <summary>Absence means the safe value. Only the test fixture asks for anything else.</summary>
    private readonly SqliteDurability _durability;

    public SqliteTenantLog(string databasePath, SqliteDurability durability = SqliteDurability.SurvivesPowerLoss)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();

        _durability = durability;
    }

    public async Task WriteAsync(
        string? actorId,
        string? actorEmail,
        string action,
        string? subject = null,
        string? subjectName = null,
        string? detail = null,
        CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO tenant_events
                (occurred_at, actor_id, actor_email, action, subject, subject_name, detail)
            VALUES ($at, $actorId, $actorEmail, $action, $subject, $subjectName, $detail)
            """;

        // Round-trip ("O"), matching every other timestamp this codebase stores. A local-format
        // string sorts wrong and parses differently on a machine with another culture.
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$actorId", (object?)actorId ?? DBNull.Value);
        command.Parameters.AddWithValue("$actorEmail", (object?)actorEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$subject", (object?)subject ?? DBNull.Value);
        command.Parameters.AddWithValue("$subjectName", (object?)subjectName ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<TenantEvent?> FindLatestAsync(
        string action,
        string subject,
        CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // ORDER BY seq DESC LIMIT 1, never a timestamp. `occurred_at` is a string this class writes
        // and two rows in the same tick sort arbitrarily by it; seq is the append order and is the
        // only thing that means "later" without argument.
        //
        // Subject matched ORDINALLY. Its parts are a team id, a commit and a suite name - the first
        // is already case-insensitive by the time it is composed here, and the other two are not
        // this log's to fold: `abc123` and `ABC123` are the same commit to git and a suite name
        // comes from a closed set, but nothing here should be deciding that.
        command.CommandText =
            """
            SELECT seq, occurred_at, actor_id, actor_email, action, subject, subject_name, detail
            FROM tenant_events
            WHERE action = $action AND subject = $subject
            ORDER BY seq DESC
            LIMIT 1
            """;

        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$subject", subject);

        await using var reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct)) return null;

        return ReadEvent(reader);
    }

    public async Task<IReadOnlyList<TenantEvent>> FindLatestBySubjectAsync(
        IReadOnlyCollection<string> actions,
        CancellationToken ct = default)
    {
        if (actions.Count == 0) return Array.Empty<TenantEvent>();

        var distinctActions = actions
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctActions.Length == 0) return Array.Empty<TenantEvent>();

        await using var connection = Open();
        await using var command = connection.CreateCommand();

        var inList = new StringBuilder();
        for (var i = 0; i < distinctActions.Length; i++)
        {
            if (i > 0) inList.Append(", ");

            var name = $"$action{i}";
            inList.Append(name);
            command.Parameters.AddWithValue(name, distinctActions[i]);
        }

        command.CommandText =
            $$"""
              WITH latest AS (
                  SELECT action, subject, MAX(seq) AS seq
                  FROM tenant_events
                  WHERE subject IS NOT NULL
                    AND action IN ({{inList}})
                  GROUP BY action, subject
              )
              SELECT e.seq, e.occurred_at, e.actor_id, e.actor_email, e.action, e.subject, e.subject_name, e.detail
              FROM tenant_events AS e
              INNER JOIN latest AS l
                      ON e.action = l.action
                     AND e.subject = l.subject
                     AND e.seq = l.seq
              ORDER BY e.seq DESC
              """;

        var events = new List<TenantEvent>(distinctActions.Length);
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            events.Add(ReadEvent(reader));
        }

        return events;
    }

    public async Task<TenantLogPage> ReadAsync(
        long? before = null, int take = 50, CancellationToken ct = default)
    {
        await using var connection = Open();

        // The total FIRST, and in the same connection: it is the screen's caption, and reading it
        // after the page would let a write land between the two and report a count the rows do not
        // add up to.
        long total;

        await using (var counting = connection.CreateCommand())
        {
            counting.CommandText = "SELECT COUNT(*) FROM tenant_events";
            total = (long)(await counting.ExecuteScalarAsync(ct) ?? 0L);
        }

        await using var command = connection.CreateCommand();

        // Newest first: an audit log is read from the top. The cursor narrows the page and never
        // the total, which counts the log rather than what is left of it.
        command.CommandText =
            "SELECT seq, occurred_at, actor_id, actor_email, action, subject, subject_name, detail "
            + "FROM tenant_events WHERE ($before IS NULL OR seq < $before) "
            + "ORDER BY seq DESC LIMIT $take";

        command.Parameters.AddWithValue("$before", before is { } seq ? seq : DBNull.Value);
        command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, ITenantLog.MaxTake));

        var events = new List<TenantEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            events.Add(ReadEvent(reader));
        }

        return new TenantLogPage(events, total);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        SqlitePragmas.Apply(connection, durability: _durability);

        return connection;
    }

    private static TenantEvent ReadEvent(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            DateTimeOffset.Parse(
                reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));
}
