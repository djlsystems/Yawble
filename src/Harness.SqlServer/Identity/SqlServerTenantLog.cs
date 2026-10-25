using System.Data;
using System.Text;
using Harness.Contracts;
using Harness.Identity;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Identity;

/// <summary>
/// <see cref="ITenantLog"/> over <c>tenant_events</c> on SQL Server: SQLite's <c>SqliteTenantLog</c>
/// query for query, whose remarks are the argument (no foreign keys, latest by seq and never by time,
/// the subject compared exactly, the total counted once per append). A row's columns and its reading
/// are <see cref="TenantEventRows"/>', and the append is <see cref="SqlServerTenantAuditRow"/>, the one
/// insert every SQL Server store's tenant row goes through.
///
/// <para>
/// THE TABLE IS ONLY EVER APPENDED TO, so nothing here reads to decide a write and nothing takes a
/// lock. The latest row of each subject joins back on <c>seq</c> alone, which is the key and so names
/// the same row SQLite's join on action, subject and seq does, without comparing a subject of any length.
/// </para>
/// </summary>
public sealed class SqlServerTenantLog(string connectionString) : ITenantLog
{
    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    /// <summary>The last count taken, and the highest seq it was taken at.</summary>
    private readonly TenantLogTotal _counted = new();

    private Task<T> RunAsync<T>(Func<SqlConnection, CancellationToken, Task<T>> work, CancellationToken ct) =>
        SqlServerOpen.RunAsync(_connectionString, work, ct);

    public Task WriteAsync(
        string? actorId,
        string? actorEmail,
        string action,
        string? subject = null,
        string? subjectName = null,
        string? detail = null,
        CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await SqlServerTenantAuditRow.AppendAsync(
                    connection, null, new TriggerAudit(actorId, actorEmail, action, subject, subjectName, detail), token);
                return 0;
            },
            ct);

    public Task<TenantEvent?> FindLatestAsync(string action, string subject, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT TOP (1) {TenantEventRows.Columns} FROM tenant_events "
                    + "WHERE action = @action AND subject = @subject ORDER BY seq DESC";
                command.Parameters.AddWithValue("@action", action);
                command.Parameters.AddWithValue("@subject", subject);

                await using var reader = await command.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token) ? TenantEventRows.Read(reader) : null;
            },
            ct);

    public async Task<IReadOnlyList<TenantEvent>> FindLatestBySubjectAsync(
        IReadOnlyCollection<string> actions, CancellationToken ct = default)
    {
        var distinctActions = actions.Distinct(StringComparer.Ordinal).ToArray();
        if (distinctActions.Length == 0) return [];

        return await RunAsync<IReadOnlyList<TenantEvent>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();

                var inList = new StringBuilder();
                for (var i = 0; i < distinctActions.Length; i++)
                {
                    if (i > 0) inList.Append(", ");

                    var name = $"@action{i}";
                    inList.Append(name);
                    command.Parameters.AddWithValue(name, distinctActions[i]);
                }

                command.CommandText =
                    $"""
                     WITH latest AS (
                         SELECT action, subject, MAX(seq) AS seq
                         FROM tenant_events
                         WHERE subject IS NOT NULL
                           AND action IN ({inList})
                         GROUP BY action, subject
                     )
                     SELECT e.seq, e.occurred_at, e.actor_id, e.actor_email, e.action, e.subject, e.subject_name, e.detail
                     FROM tenant_events AS e
                     INNER JOIN latest AS l ON e.seq = l.seq
                     ORDER BY e.seq DESC
                     """;

                var events = new List<TenantEvent>(distinctActions.Length);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) events.Add(TenantEventRows.Read(reader));

                return events;
            },
            ct);
    }

    public Task<TenantLogPage> ReadAsync(long? before = null, int take = 50, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                // The total FIRST, on the same connection, as on SQLite: it is the screen's caption.
                long highest;
                await using (var top = connection.CreateCommand())
                {
                    top.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM tenant_events";
                    highest = (long)(await top.ExecuteScalarAsync(token) ?? 0L);
                }

                long total;
                if (_counted.At(highest) is { } counted)
                {
                    total = counted;
                }
                else
                {
                    await using var counting = connection.CreateCommand();
                    counting.CommandText = "SELECT COUNT_BIG(*) FROM tenant_events";
                    total = (long)(await counting.ExecuteScalarAsync(token) ?? 0L);
                    _counted.Counted(highest, total);
                }

                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT TOP (@take) {TenantEventRows.Columns} FROM tenant_events "
                    + "WHERE (@before IS NULL OR seq < @before) ORDER BY seq DESC";
                command.Parameters.Add("@before", SqlDbType.BigInt).Value = before is { } seq ? seq : DBNull.Value;
                command.Parameters.Add("@take", SqlDbType.Int).Value = Math.Clamp(take, 1, ITenantLog.MaxTake);

                var events = new List<TenantEvent>();
                await using (var reader = await command.ExecuteReaderAsync(token))
                {
                    while (await reader.ReadAsync(token)) events.Add(TenantEventRows.Read(reader));
                }

                return new TenantLogPage(events, total);
            },
            ct);
}
