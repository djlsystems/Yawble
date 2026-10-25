using System.Data.Common;
using System.Text;
using Harness.Contracts;

namespace Harness.Identity;

/// <summary>
/// WHAT A DIAGNOSTICS LOG WRITES INTO <c>diagnostic_events</c> AND HOW A READER NARROWS IT, whichever
/// database holds the rows: the column list, the stored spelling of a severity, the values a write
/// binds (redacted here, so no provider can forget), the WHERE clause a filter becomes and the row
/// reader. Both providers' diagnostics logs call these, so a row either writes reads the same on the
/// other and one filter means one thing; only the SQL each provider adds differs - the parameter
/// prefix, and the spelling of the case-insensitive search.
/// </summary>
public static class DiagnosticRows
{
    /// <summary>The columns <see cref="Read"/> reads, in its order.</summary>
    public const string Columns = "seq, occurred_at, severity, kind, source, route, status, exception_type, message, detail";

    /// <summary>
    /// How many writes pass between trims.
    ///
    /// A COUNTER RATHER THAN A CLOCK, because the cost being bounded is rows and a burst of ten
    /// thousand 500s in a minute is exactly when the bound matters. Two hundred is far below any
    /// retention bound - the floor is five thousand - so the table cannot overshoot meaningfully
    /// between trims, and the DELETE it runs is two indexed predicates.
    /// </summary>
    public const int WritesBetweenTrims = 200;

    /// <summary>
    /// The value of every column one write binds, by parameter name without its prefix.
    ///
    /// The time is round-trip ("O"), matching every other timestamp this codebase stores. A
    /// local-format string sorts wrong and parses differently on a machine with another culture - and
    /// this column is the age half of the retention bound, so sorting wrong would mean trimming the
    /// wrong rows. The message and the detail are REDACTED HERE: they are the fields nobody controls -
    /// an exception composes its own message and routinely quotes the input that broke it.
    /// </summary>
    public static IReadOnlyList<(string Name, object Value)> WriteValues(
        DiagnosticSeverity severity, string kind, string? source, string? route, int? status,
        string? exceptionType, string? message, string? detail) =>
    [
        ("at", Instant.Now()),
        ("severity", Name(severity)),
        ("kind", kind),
        ("source", (object?)source ?? DBNull.Value),
        ("route", (object?)route ?? DBNull.Value),
        ("status", (object?)status ?? DBNull.Value),
        ("exceptionType", (object?)exceptionType ?? DBNull.Value),
        ("message", (object?)DiagnosticRedaction.Redact(message) ?? DBNull.Value),
        ("detail", (object?)DiagnosticRedaction.Redact(detail) ?? DBNull.Value),
    ];

    /// <summary>
    /// Turns a filter into a WHERE clause (without the keyword; empty for no narrowing) and its
    /// parameters, named with <paramref name="prefix"/>.
    ///
    /// Built by hand rather than with a query builder because there are five narrowings and each
    /// one's spelling is a decision: the `IN` lists are expanded as parameters so a kind cannot be
    /// injected, the time window is closed at both ends against the same round-trip string format the
    /// writer uses, and the search is a case-insensitive LIKE over four columns.
    ///
    /// <paramref name="pattern"/> turns the trimmed search text into the LIKE pattern, its wildcards
    /// escaped for the provider's LIKE; <paramref name="search"/> is the provider's clause matching the
    /// parameter it is given across the four columns, case-insensitively.
    /// </summary>
    public static string Narrow(
        DiagnosticsFilter filter, string prefix, Func<string, string> pattern, Func<string, string> search,
        List<(string Name, object Value)> parameters)
    {
        var where = new StringBuilder();

        if (filter.Kinds is { Count: > 0 } kinds)
        {
            var names = new List<string>(kinds.Count);

            foreach (var kind in kinds.Distinct(StringComparer.Ordinal))
            {
                var name = $"{prefix}kind{parameters.Count}";
                names.Add(name);
                parameters.Add((name, kind));
            }

            Append(where, $"kind IN ({string.Join(", ", names)})");
        }

        if (filter.Severities is { Count: > 0 } severities)
        {
            var names = new List<string>(severities.Count);

            foreach (var severity in severities.Distinct())
            {
                var name = $"{prefix}severity{parameters.Count}";
                names.Add(name);
                parameters.Add((name, Name(severity)));
            }

            Append(where, $"severity IN ({string.Join(", ", names)})");
        }

        if (filter.From is { } from)
        {
            parameters.Add(($"{prefix}from", Instant.Stamp(from)));
            Append(where, $"occurred_at >= {prefix}from");
        }

        if (filter.To is { } to)
        {
            parameters.Add(($"{prefix}to", Instant.Stamp(to)));
            Append(where, $"occurred_at <= {prefix}to");
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // The wildcards are added by the provider's pattern rather than asked of the caller: a
            // caller composing its own pattern is a caller that can write one, and `%` typed into a
            // search box should be a percent sign.
            parameters.Add(($"{prefix}search", pattern(filter.Search.Trim())));

            // Case-insensitive, because a person searching for `Timeout` means `timeout`. Not over
            // `kind` or `severity`: both have typed filters of their own, and a free-text box that
            // also matched them would make the two disagree about the same word.
            Append(where, search($"{prefix}search"));
        }

        return where.ToString();
    }

    /// <summary>
    /// The page read's WHERE clause: <paramref name="clause"/> (the filter, from <see cref="Narrow"/>)
    /// with the cursor joined to it. THE CURSOR JOINS THE FILTER ON THE PAGE READ ONLY, never on the
    /// count: the total is what matches, and a caption that shrank as somebody scrolled would be
    /// counting down rather than counting.
    /// </summary>
    public static string Cursor(string clause, long? before, string prefix) =>
        before is null
            ? clause
            : clause.Length == 0 ? $" WHERE seq < {prefix}before" : $"{clause} AND seq < {prefix}before";

    /// <summary>A page's size, clamped to 1..<see cref="IDiagnosticsLog.MaxTake"/>.</summary>
    public static int Take(int take) => Math.Clamp(take, 1, IDiagnosticsLog.MaxTake);

    /// <summary>The stored spelling of a severity. Lowercase names rather than the enum's numbers,
    /// so the column stays readable when somebody renumbers - the rows outlive the enum.</summary>
    public static string Name(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => "error",
        DiagnosticSeverity.Warning => "warning",
        _ => "info",
    };

    public static DiagnosticSeverity Severity(string stored) => stored switch
    {
        "error" => DiagnosticSeverity.Error,
        "warning" => DiagnosticSeverity.Warning,
        _ => DiagnosticSeverity.Info,
    };

    public static DiagnosticEvent Read(DbDataReader reader) =>
        new(
            reader.GetInt64(0),
            Instant.Parse(
                reader.GetString(1)),
            Severity(reader.GetString(2)),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : checked((int)reader.GetInt64(6)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));

    private static void Append(StringBuilder where, string clause)
    {
        if (where.Length > 0) where.Append(" AND ");
        where.Append(clause);
    }
}

/// <summary>
/// THE WRITE COUNT BETWEEN TRIMS (<see cref="DiagnosticRows.WritesBetweenTrims"/>), one per store as
/// the store is one per process: <see cref="Counted"/> answers true on the write that is due a trim,
/// and starts counting again.
/// </summary>
public sealed class DiagnosticTrimCount
{
    private int _writesSinceTrim;

    public bool Counted()
    {
        if (Interlocked.Increment(ref _writesSinceTrim) < DiagnosticRows.WritesBetweenTrims) return false;

        Interlocked.Exchange(ref _writesSinceTrim, 0);
        return true;
    }
}
