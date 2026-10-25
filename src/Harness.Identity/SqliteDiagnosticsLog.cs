using System.Globalization;
using Microsoft.Data.Sqlite;
using Harness.Contracts;

namespace Harness.Identity;

/// <summary>
/// The diagnostics log, over the same SQLite file everything else here uses.
///
/// <para>
/// Mechanical sibling of <see cref="SqliteTenantLog"/> - same connection string, same
/// <c>Pooling=false</c>, same reliance on the migrator having run before this is constructed, same
/// shared <see cref="SqlitePragmas"/> on every open. That likeness is deliberate: this store must
/// survive the thing it observes, and the way to get that is to be the plainest table in the file,
/// not the cleverest.
/// </para>
///
/// <para>
/// <b>THREE THINGS ARE DIFFERENT FROM THE TENANT LOG, AND EACH HAS A REASON.</b>
/// </para>
///
/// <list type="number">
///   <item>
///     <b>THE WRITE SWALLOWS HERE, NOT IN A HOST HELPER.</b> `TenantLogging` gives the tenant log
///     its never-throws guarantee because every one of its call sites is a route with an
///     `HttpContext`. This store's call sites are not: the migrator is in `Harness.Messaging`,
///     the process runner is nowhere near a request, and a startup fact is written before the
///     pipeline exists. A guarantee that depends on every caller reaching for the right wrapper is
///     one that holds until somebody does not - so it lives on the implementation, where nothing
///     can route around it.
///   </item>
///   <item>
///     <b>REDACTION IS APPLIED HERE.</b> Same argument: at the write, in one place, so no call site
///     can forget. See <see cref="DiagnosticRedaction"/>.
///   </item>
///   <item>
///     <b>IT IS TRIMMED.</b> The tenant log is kept forever because an audit trail is; this is the
///     highest-volume store this product has and an unbounded one fills a disk. See
///     <see cref="DiagnosticRetention"/> for why the bound is derived rather than typed.
///   </item>
/// </list>
///
/// <para>
/// <b>NO TIMER, NO QUEUE, NO BACKGROUND FLUSHER.</b> The trim runs from the write path, throttled,
/// and once at startup. A moving part is a thing that can be dead while the table looks healthy,
/// and this store's whole claim is that what was written stays written when the host dies.
/// </para>
/// </summary>
public sealed class SqliteDiagnosticsLog : IDiagnosticsLog
{
    private readonly string _connectionString;
    private readonly SqliteDurability _durability;
    private readonly DiagnosticRetention _retention;

    private readonly DiagnosticTrimCount _trims = new();

    public SqliteDiagnosticsLog(
        string databasePath,
        DiagnosticRetention? retention = null,
        SqliteDurability durability = SqliteDurability.SurvivesPowerLoss)
    {
        _connectionString = SqliteOpen.ConnectionString(databasePath);

        _durability = durability;

        // An absent bound derives one from the volume this file sits on rather than meaning
        // "unbounded". The caller that knows better - Program.cs, reading an operator's declared
        // setting - passes one.
        _retention = retention ?? DiagnosticRetention.ForDataRoot(
            Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? databasePath);
    }

    /// <summary>The bound this store is applying, so a startup line can say what it is. An operator
    /// who cannot see the bound cannot tell a quiet instance from a trimmed one.</summary>
    public DiagnosticRetention Retention => _retention;

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
            await using (var connection = Open())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO diagnostic_events
                        (occurred_at, severity, kind, source, route, status, exception_type,
                         message, detail)
                    VALUES ($at, $severity, $kind, $source, $route, $status, $exceptionType,
                            $message, $detail)
                    """;

                // Round-trip time, the severity's stored name and the message and detail REDACTED:
                // DiagnosticRows.WriteValues, one place for both providers.
                foreach (var (name, value) in DiagnosticRows.WriteValues(
                    severity, kind, source, route, status, exceptionType, message, detail))
                {
                    command.Parameters.AddWithValue("$" + name, value);
                }

                await command.ExecuteNonQueryAsync(ct);
            }

            if (!_trims.Counted()) return;

            await TrimAsync(ct);
        }
        catch
        {
            // DELIBERATELY SWALLOWED, CANCELLATION INCLUDED. A failure to record must not fail the
            // act being recorded, and a cancelled diagnostics write surfacing as an
            // OperationCanceledException inside the request it was describing is the same failure
            // wearing a different type. The cost is that a broken store is silent, which is why it
            // is a plain table with no moving parts - and why `HasAnyAsync` exists, so a screen can
            // tell "nothing was captured" from "nothing went wrong".
        }
    }

    /// <inheritdoc />
    public async Task<DiagnosticsPage> ReadAsync(
        DiagnosticsFilter? filter = null,
        long? before = null,
        int take = 50,
        CancellationToken ct = default)
    {
        filter ??= DiagnosticsFilter.None;

        await using var connection = Open();

        var parameters = new List<(string Name, object Value)>();
        var where = DiagnosticRows.Narrow(filter, "$", Pattern, Search, parameters);

        var clause = where.Length == 0 ? "" : $" WHERE {where}";

        // The total FIRST, and in the same connection, for the reason the tenant log does it: a
        // grid showing "page 3 of 47" needs it, and reading it after the page would let a write
        // land between the two and report a count the rows do not add up to.
        //
        // IT IS THE COUNT OF ROWS MATCHING THE FILTER, not of rows in the table. A filtered grid
        // paging against an unfiltered total offers pages that are empty.
        long total;

        await using (var counting = connection.CreateCommand())
        {
            counting.CommandText = $"SELECT COUNT(*) FROM diagnostic_events{clause}";
            foreach (var (name, value) in parameters) counting.Parameters.AddWithValue(name, value);

            total = (long)(await counting.ExecuteScalarAsync(ct) ?? 0L);
        }

        await using var command = connection.CreateCommand();

        // ORDER BY seq DESC, never occurred_at. That column is a string this class writes and two
        // rows in the same tick sort arbitrarily by it; seq is the append order and is the only
        // thing that means "later" without argument. Under a storm of identical failures that is
        // the difference between a readable page and an arbitrary one.
        //
        // THE CURSOR JOINS THE FILTER ON THE PAGE READ ONLY, never on the count above
        // (DiagnosticRows.Cursor).
        var cursor = DiagnosticRows.Cursor(clause, before, "$");

        command.CommandText =
            "SELECT seq, occurred_at, severity, kind, source, route, status, exception_type, "
            + $"message, detail FROM diagnostic_events{cursor} "
            + "ORDER BY seq DESC LIMIT $take";

        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);

        if (before is { } seq) command.Parameters.AddWithValue("$before", seq);
        command.Parameters.AddWithValue("$take", DiagnosticRows.Take(take));

        var events = new List<DiagnosticEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct)) events.Add(DiagnosticRows.Read(reader));

        return new DiagnosticsPage(events, total);
    }

    /// <inheritdoc />
    public async Task<bool?> HasAnyAsync(CancellationToken ct = default)
    {
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();

            // EXISTS rather than COUNT(*): the question is whether there is a row, and on the
            // largest table in the file a count is a scan to answer a boolean.
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM diagnostic_events)";

            return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture)
                != 0;
        }
        catch
        {
            // NULL IS "I CANNOT TELL", and it is a real answer rather than a failure. A screen that
            // rendered an unreadable store as an empty one would be telling its reader that a
            // broken instance is a healthy one.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task TrimAsync(CancellationToken ct = default)
    {
        try
        {
            await using var connection = Open();

            // AGE FIRST, THEN SIZE. Age is the cheaper predicate and usually removes most of what
            // size would have had to, so running it first leaves the size delete less to do. Both
            // run every time: they answer different questions and either alone is not the bound.
            await using (var byAge = connection.CreateCommand())
            {
                byAge.CommandText =
                    "DELETE FROM diagnostic_events WHERE occurred_at < $cutoff";

                byAge.Parameters.AddWithValue(
                    "$cutoff", Instant.Stamp(DateTimeOffset.UtcNow.Subtract(_retention.MaxAge)));

                await byAge.ExecuteNonQueryAsync(ct);
            }

            await using (var bySize = connection.CreateCommand())
            {
                // NO `DELETE ... LIMIT`: SQLite only has it when compiled with
                // SQLITE_ENABLE_UPDATE_DELETE_LIMIT, which the bundled library is not, and a store
                // that depends on how its SQLite was built is one that works here and silently does
                // not somewhere else. Keyed on `seq` instead, which is the primary key and is
                // monotonic, so "everything below the newest N" is one indexed comparison.
                bySize.CommandText =
                    """
                    DELETE FROM diagnostic_events
                    WHERE seq <= (SELECT MAX(seq) FROM diagnostic_events) - $keep
                    """;

                bySize.Parameters.AddWithValue("$keep", _retention.MaxRows);

                await bySize.ExecuteNonQueryAsync(ct);
            }
        }
        catch
        {
            // Swallowed for the reason the write is. A trim that fails leaves a bigger table, which
            // is recoverable; a trim that threw would take down whatever it was attached to - and
            // it is attached to the write path.
        }
    }

    /// <summary>
    /// The search text as SQLite's LIKE pattern. ESCAPE so a `%` or `_` typed into a search box is a
    /// percent sign or an underscore - otherwise searching for a literal `%` or `_` matches everything,
    /// silently.
    /// </summary>
    private static string Pattern(string search) =>
        "%"
        + search
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
        + "%";

    /// <summary>NOCASE, because a person searching for `Timeout` means `timeout`.</summary>
    private static string Search(string parameter) =>
        $"(message LIKE {parameter} ESCAPE '\\' COLLATE NOCASE "
        + $"OR detail LIKE {parameter} ESCAPE '\\' COLLATE NOCASE "
        + $"OR route LIKE {parameter} ESCAPE '\\' COLLATE NOCASE "
        + $"OR exception_type LIKE {parameter} ESCAPE '\\' COLLATE NOCASE)";

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        SqlitePragmas.Apply(connection, durability: _durability);

        return connection;
    }
}
