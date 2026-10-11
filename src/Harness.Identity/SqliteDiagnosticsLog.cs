using System.Globalization;
using System.Text;
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
    /// <summary>
    /// How many writes pass between trims.
    ///
    /// A COUNTER RATHER THAN A CLOCK, because the cost being bounded is rows and a burst of ten
    /// thousand 500s in a minute is exactly when the bound matters. Two hundred is far below any
    /// retention bound - the floor is five thousand - so the table cannot overshoot meaningfully
    /// between trims, and the DELETE it runs is two indexed predicates.
    /// </summary>
    private const int WritesBetweenTrims = 200;

    private readonly string _connectionString;
    private readonly SqliteDurability _durability;
    private readonly DiagnosticRetention _retention;

    private int _writesSinceTrim;

    public SqliteDiagnosticsLog(
        string databasePath,
        DiagnosticRetention? retention = null,
        SqliteDurability durability = SqliteDurability.SurvivesPowerLoss)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();

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

                // Round-trip ("O"), matching every other timestamp this codebase stores. A
                // local-format string sorts wrong and parses differently on a machine with another
                // culture - and this column is the age half of the retention bound, so sorting
                // wrong would mean trimming the wrong rows.
                command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$severity", Name(severity));
                command.Parameters.AddWithValue("$kind", kind);
                command.Parameters.AddWithValue("$source", (object?)source ?? DBNull.Value);
                command.Parameters.AddWithValue("$route", (object?)route ?? DBNull.Value);
                command.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
                command.Parameters.AddWithValue(
                    "$exceptionType", (object?)exceptionType ?? DBNull.Value);

                // REDACTED HERE. These two are the fields nobody controls - an exception composes
                // its own message and routinely quotes the input that broke it.
                command.Parameters.AddWithValue(
                    "$message", (object?)DiagnosticRedaction.Redact(message) ?? DBNull.Value);
                command.Parameters.AddWithValue(
                    "$detail", (object?)DiagnosticRedaction.Redact(detail) ?? DBNull.Value);

                await command.ExecuteNonQueryAsync(ct);
            }

            if (Interlocked.Increment(ref _writesSinceTrim) < WritesBetweenTrims) return;

            Interlocked.Exchange(ref _writesSinceTrim, 0);

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

        var where = new StringBuilder();
        var parameters = new List<SqliteParameter>();

        Narrow(filter, where, parameters);

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
            foreach (var parameter in parameters) counting.Parameters.Add(Copy(parameter));

            total = (long)(await counting.ExecuteScalarAsync(ct) ?? 0L);
        }

        await using var command = connection.CreateCommand();

        // ORDER BY seq DESC, never occurred_at. That column is a string this class writes and two
        // rows in the same tick sort arbitrarily by it; seq is the append order and is the only
        // thing that means "later" without argument. Under a storm of identical failures that is
        // the difference between a readable page and an arbitrary one.
        //
        // THE CURSOR JOINS THE FILTER ON THE PAGE READ ONLY, never on the count above: the total
        // is what matches, and a caption that shrank as somebody scrolled would be counting down
        // rather than counting.
        var cursor = before is null
            ? clause
            : clause.Length == 0 ? " WHERE seq < $before" : $"{clause} AND seq < $before";

        command.CommandText =
            "SELECT seq, occurred_at, severity, kind, source, route, status, exception_type, "
            + $"message, detail FROM diagnostic_events{cursor} "
            + "ORDER BY seq DESC LIMIT $take";

        foreach (var parameter in parameters) command.Parameters.Add(Copy(parameter));

        if (before is { } seq) command.Parameters.AddWithValue("$before", seq);
        command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, IDiagnosticsLog.MaxTake));

        var events = new List<DiagnosticEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct)) events.Add(ReadEvent(reader));

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
                    "$cutoff", DateTimeOffset.UtcNow.Subtract(_retention.MaxAge).ToString("O"));

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
    /// Turns a filter into a WHERE clause and its parameters.
    ///
    /// Built by hand rather than with a query builder because there are five narrowings and each
    /// one's spelling is a decision: the `IN` lists are expanded as parameters so a kind cannot be
    /// injected, the time window is closed at both ends against the same round-trip string format
    /// the writer uses, and the search is a case-insensitive LIKE over four columns.
    /// </summary>
    private static void Narrow(
        DiagnosticsFilter filter, StringBuilder where, List<SqliteParameter> parameters)
    {
        if (filter.Kinds is { Count: > 0 } kinds)
        {
            var names = new List<string>(kinds.Count);

            foreach (var kind in kinds.Distinct(StringComparer.Ordinal))
            {
                var name = $"$kind{parameters.Count}";
                names.Add(name);
                parameters.Add(new SqliteParameter(name, kind));
            }

            Append(where, $"kind IN ({string.Join(", ", names)})");
        }

        if (filter.Severities is { Count: > 0 } severities)
        {
            var names = new List<string>(severities.Count);

            foreach (var severity in severities.Distinct())
            {
                var name = $"$severity{parameters.Count}";
                names.Add(name);
                parameters.Add(new SqliteParameter(name, Name(severity)));
            }

            Append(where, $"severity IN ({string.Join(", ", names)})");
        }

        if (filter.From is { } from)
        {
            parameters.Add(new SqliteParameter("$from", from.ToUniversalTime().ToString("O")));
            Append(where, "occurred_at >= $from");
        }

        if (filter.To is { } to)
        {
            parameters.Add(new SqliteParameter("$to", to.ToUniversalTime().ToString("O")));
            Append(where, "occurred_at <= $to");
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // The wildcards are added HERE rather than asked of the caller: a caller composing its
            // own pattern is a caller that can write one, and `%` typed into a search box should be
            // a percent sign. ESCAPE so it is - otherwise searching for a literal `%` or `_`
            // matches everything, silently.
            var pattern = "%"
                + filter.Search.Trim()
                    .Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("%", "\\%", StringComparison.Ordinal)
                    .Replace("_", "\\_", StringComparison.Ordinal)
                + "%";

            parameters.Add(new SqliteParameter("$search", pattern));

            // NOCASE, because a person searching for `Timeout` means `timeout`. Not over `kind` or
            // `severity`: both have typed filters of their own, and a free-text box that also
            // matched them would make the two disagree about the same word.
            Append(
                where,
                "(message LIKE $search ESCAPE '\\' COLLATE NOCASE "
                + "OR detail LIKE $search ESCAPE '\\' COLLATE NOCASE "
                + "OR route LIKE $search ESCAPE '\\' COLLATE NOCASE "
                + "OR exception_type LIKE $search ESCAPE '\\' COLLATE NOCASE)");
        }
    }

    private static void Append(StringBuilder where, string clause)
    {
        if (where.Length > 0) where.Append(" AND ");
        where.Append(clause);
    }

    /// <summary>A parameter belongs to one command, so the count query and the page query cannot
    /// share instances.</summary>
    private static SqliteParameter Copy(SqliteParameter parameter) =>
        new(parameter.ParameterName, parameter.Value);

    /// <summary>The stored spelling of a severity. Lowercase names rather than the enum's numbers,
    /// so the column stays readable when somebody renumbers - the rows outlive the enum.</summary>
    private static string Name(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => "error",
        DiagnosticSeverity.Warning => "warning",
        _ => "info",
    };

    private static DiagnosticSeverity Severity(string stored) => stored switch
    {
        "error" => DiagnosticSeverity.Error,
        "warning" => DiagnosticSeverity.Warning,
        _ => DiagnosticSeverity.Info,
    };

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        SqlitePragmas.Apply(connection, durability: _durability);

        return connection;
    }

    private static DiagnosticEvent ReadEvent(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            DateTimeOffset.Parse(
                reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Severity(reader.GetString(2)),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
}
