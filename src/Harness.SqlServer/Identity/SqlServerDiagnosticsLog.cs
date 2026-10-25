using Harness.Contracts;
using Harness.Identity;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Identity;

/// <summary>
/// The diagnostics log over <c>diagnostic_events</c> on SQL Server: SQLite's <c>SqliteDiagnosticsLog</c>
/// write for write, whose remarks are the argument - a write that never throws, redaction at the write,
/// a trim from the write path and none on a timer. The values a write binds, the WHERE clause a filter
/// becomes and the row reader are <see cref="DiagnosticRows"/>', shared with it, so one filter means one
/// thing on both.
///
/// <para>
/// THE SEARCH IS SQLITE'S ASCII-CASE-INSENSITIVE <c>LIKE</c>: each column is matched with
/// <see cref="SqlServerSql.Like"/> (the case-insensitive collation on the comparison; the database
/// itself compares by code point), and the text is made literal with
/// <see cref="SqlServerSql.LikeLiteral"/>, which also escapes the <c>[</c> only T-SQL reads as a
/// pattern, so <c>%</c>, <c>_</c> and <c>[</c> typed into the box are those characters. The collation
/// folds every letter where SQLite folds ASCII only, so a search can match a non-ASCII letter of
/// another case here that it would not there.
/// </para>
///
/// <para>
/// A KIND AND A SEVERITY ARE COMPARED WITH <c>IN</c>, which ignores a trailing space: neither can carry
/// one, since a kind is refused unless it is one of <see cref="DiagnosticKinds.All"/> and a severity is
/// an enum the store spells itself.
/// </para>
///
/// <para>
/// NO READ-THEN-WRITE: a write is one insert and a trim two deletes, each one statement.
/// </para>
/// </summary>
public sealed class SqlServerDiagnosticsLog(string connectionString, DiagnosticRetention retention) : IDiagnosticsLog
{
    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    private readonly DiagnosticTrimCount _trims = new();

    /// <summary>The bound this store is applying, so a startup line can say what it is.</summary>
    public DiagnosticRetention Retention => retention;

    /// <inheritdoc />
    public async Task WriteAsync(
        DiagnosticSeverity severity,
        string kind,
        string? source = null,
        string? route = null,
        int? status = null,
        string? exceptionType = null,
        string? message = null,
        string? detail = null,
        CancellationToken ct = default)
    {
        try
        {
            var values = DiagnosticRows.WriteValues(severity, kind, source, route, status, exceptionType, message, detail);

            await SqlServerOpen.ExecuteAsync(
                _connectionString,
                """
                INSERT INTO diagnostic_events
                    (occurred_at, severity, kind, source, route, status, exception_type, message, detail)
                VALUES (@at, @severity, @kind, @source, @route, @status, @exceptionType, @message, @detail)
                """,
                command =>
                {
                    foreach (var (name, value) in values) command.Parameters.AddWithValue("@" + name, value);
                },
                ct);

            if (!_trims.Counted()) return;

            await TrimAsync(ct);
        }
        catch
        {
            // DELIBERATELY SWALLOWED, CANCELLATION INCLUDED, for the reason SQLite's store gives: a
            // failure to record must not fail the act being recorded.
        }
    }

    /// <inheritdoc />
    public Task<DiagnosticsPage> ReadAsync(
        DiagnosticsFilter? filter = null,
        long? before = null,
        int take = 50,
        CancellationToken ct = default)
    {
        filter ??= DiagnosticsFilter.None;

        var parameters = new List<(string Name, object Value)>();
        var where = DiagnosticRows.Narrow(filter, "@", Pattern, Search, parameters);
        var clause = where.Length == 0 ? "" : $" WHERE {where}";

        return SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                // The total first, on the same connection, and of the rows MATCHING THE FILTER: see
                // SQLite's store.
                long total;

                await using (var counting = connection.CreateCommand())
                {
                    counting.CommandText = $"SELECT COUNT_BIG(*) FROM diagnostic_events{clause}";
                    foreach (var (name, value) in parameters) counting.Parameters.AddWithValue(name, value);

                    total = (long)(await counting.ExecuteScalarAsync(token) ?? 0L);
                }

                await using var command = connection.CreateCommand();

                // ORDER BY seq DESC, never occurred_at, and the cursor on the page read only
                // (DiagnosticRows.Cursor).
                command.CommandText =
                    $"SELECT TOP (@take) {DiagnosticRows.Columns} FROM diagnostic_events{DiagnosticRows.Cursor(clause, before, "@")} "
                    + "ORDER BY seq DESC";

                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);

                if (before is { } seq) command.Parameters.AddWithValue("@before", seq);
                command.Parameters.AddWithValue("@take", DiagnosticRows.Take(take));

                var events = new List<DiagnosticEvent>();
                await using var reader = await command.ExecuteReaderAsync(token);

                while (await reader.ReadAsync(token)) events.Add(DiagnosticRows.Read(reader));

                return new DiagnosticsPage(events, total);
            },
            ct);
    }

    /// <inheritdoc />
    public async Task<bool?> HasAnyAsync(CancellationToken ct = default)
    {
        try
        {
            return await SqlServerOpen.RunAsync(
                _connectionString,
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT CASE WHEN EXISTS (SELECT 1 FROM diagnostic_events) THEN 1 ELSE 0 END";
                    return (int)(await command.ExecuteScalarAsync(token))! != 0;
                },
                ct);
        }
        catch
        {
            // NULL IS "I CANNOT TELL", a real answer rather than a failure.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task TrimAsync(CancellationToken ct = default)
    {
        try
        {
            // Age first, then size; both every time. The size bound keys on seq, the primary key, as
            // SQLite's does.
            await SqlServerOpen.ExecuteAsync(
                _connectionString,
                "DELETE FROM diagnostic_events WHERE occurred_at < @cutoff",
                command => command.Parameters.AddWithValue("@cutoff", Instant.Stamp(DateTimeOffset.UtcNow.Subtract(retention.MaxAge))),
                ct);

            await SqlServerOpen.ExecuteAsync(
                _connectionString,
                "DELETE FROM diagnostic_events WHERE seq <= (SELECT MAX(seq) FROM diagnostic_events) - @keep",
                command => command.Parameters.AddWithValue("@keep", retention.MaxRows),
                ct);
        }
        catch
        {
            // Swallowed for the reason the write is.
        }
    }

    /// <summary>The search text as a LIKE pattern, its wildcards and <c>[</c> made literal.</summary>
    private static string Pattern(string search) => "%" + SqlServerSql.LikeLiteral(search) + "%";

    /// <summary>SQLite's case-insensitive LIKE over the four searched columns.</summary>
    private static string Search(string parameter) =>
        $"(message LIKE {parameter} {SqlServerSql.Like} ESCAPE '\\' "
        + $"OR detail LIKE {parameter} {SqlServerSql.Like} ESCAPE '\\' "
        + $"OR route LIKE {parameter} {SqlServerSql.Like} ESCAPE '\\' "
        + $"OR exception_type LIKE {parameter} {SqlServerSql.Like} ESCAPE '\\')";
}
