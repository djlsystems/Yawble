namespace Harness.SqlServer;

/// <summary>
/// THE T-SQL SPELLINGS OF WHAT THE SQLITE STORES SAY IN SQLITE, in one place so every SQL Server
/// store translates a SQLite idiom the same way.
/// </summary>
public static class SqlServerSql
{
    /// <summary>
    /// WHAT SQLITE'S <c>COLLATE NOCASE</c> BECOMES. The database itself compares by code point
    /// (<see cref="SqlServerMigrator.RequiredCollation"/>), so a column or a comparison SQLite makes
    /// case-insensitive names this collation explicitly: on a column, so its key and its index are
    /// case-insensitive as SQLite's are; in a query, after the expression it governs.
    ///
    /// <para>
    /// SQLite folds ASCII letters only, and this folds every letter (and treats a sharp s as
    /// <c>ss</c>). The two agree on every container id, team and member name, which are ASCII
    /// letters, digits, <c>-</c> and <c>_</c> (<c>ContainerId.IsLegalName</c>); they can differ only
    /// on free text nobody compares this way.
    /// </para>
    /// </summary>
    public const string NoCase = "Latin1_General_100_CI_AS_SC_UTF8";

    /// <summary>
    /// SQLite's <c>LIKE</c> IS CASE-INSENSITIVE FOR ASCII by default, so every <c>LIKE</c> a SQL
    /// Server store writes is followed by this, or a prefix match that SQLite answers would miss on a
    /// differently cased row here.
    /// </summary>
    public const string Like = "COLLATE " + NoCase;

    /// <summary>
    /// ORDERING A NOCASE COLUMN AS SQLITE DOES: by its ASCII-lower-cased code points, which is what
    /// SQLite's NOCASE compares. The case-insensitive collation above sorts punctuation by word rules
    /// (a hyphen nearly ignored), so ordering by it would put <c>a-b</c> and <c>a_b</c> elsewhere.
    /// </summary>
    public static string NoCaseOrder(string expression) =>
        $"LOWER({expression}) COLLATE {SqlServerMigrator.RequiredCollation}";

    /// <summary>
    /// SQLITE'S <c>json_extract(column, '$.field') IS NOT NULL</c>: the payload has the field, with a
    /// value that is not JSON null. Not <c>JSON_VALUE ... IS NOT NULL</c>, which also answers null
    /// for an object, an array or a string longer than 4,000 characters. <paramref name="field"/> is
    /// a constant from the code (a <c>PayloadFields</c> name), never a caller's text.
    /// </summary>
    public static string Present(string column, string field) =>
        $"EXISTS (SELECT 1 FROM OPENJSON({column}) WHERE [key] = N'{field}' AND [type] <> 0)";

    /// <summary>SQLite's <c>json_extract(column, '$.field') IS NULL</c>; see <see cref="Present"/>.</summary>
    public static string Absent(string column, string field) => $"NOT {Present(column, field)}";

    /// <summary>
    /// <paramref name="text"/> made literal inside a <c>LIKE</c> pattern written with
    /// <c>ESCAPE '\'</c>: <c>\</c>, <c>%</c> and <c>_</c> as SQLite's are escaped, and <c>[</c>, which
    /// T-SQL alone reads as the start of a character class.
    /// </summary>
    public static string LikeLiteral(string text) =>
        text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal);
}
