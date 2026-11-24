using System.Globalization;
using System.Text;

namespace Yawble.Core.Logs;

/// <summary>One structured Diagnostics line as <see cref="Diag"/> was given it (what a test's sink receives).</summary>
public sealed record DiagEntry(string Level, string Area, string Text, IReadOnlyList<(string Key, object? Value)> Fields)
{
    /// <summary>The value of <paramref name="key"/> as the fields column writes it, or null.</summary>
    public string? Field(string key) =>
        Fields.Where(f => f.Key == key).Select(f => DiagFields.Text(f.Value)).FirstOrDefault();
}

/// <summary>
/// The core's structured Diagnostics lines: a level (<see cref="LogLevels"/>), an area ("teams", "bus", "lease",
/// "logs"...), the text, and key=value fields with the ids the caller has (team, member, workflow, run, script, step,
/// action, integration, status, ms, attempt). Each line is written once to the core's Diagnostics log
/// (<see cref="Attach"/>; not through the standard-error tee, so never twice) and echoed to standard error. A test sets
/// <see cref="Sink"/> (or <see cref="Capture"/>, for its own flow only) and receives the entries instead. Never throws.
/// </summary>
public static class Diag
{
    private static DiagnosticLog? _log;
    private static TextWriter? _echo;
    private static readonly AsyncLocal<Action<DiagEntry>?> Local = new();

    /// <summary>Takes every entry instead of the log and standard error (a test's fake); null: the log.</summary>
    public static Action<DiagEntry>? Sink { get; set; }

    /// <summary>
    /// The core's Diagnostics log and the standard error it echoes to (the writer under the tee, so the line is not
    /// recorded a second time). Unattached (a test, a tool): standard error only.
    /// </summary>
    public static void Attach(DiagnosticLog? log, TextWriter? echo)
    {
        _log = log;
        _echo = echo;
    }

    /// <summary>
    /// Sends the entries written in this async flow (and the tasks it starts) to <paramref name="sink"/> until disposed;
    /// wins over <see cref="Sink"/>. Tests that run side by side each see only their own.
    /// </summary>
    public static IDisposable Capture(Action<DiagEntry> sink)
    {
        var before = Local.Value;
        Local.Value = sink;
        return new Restore(before);
    }

    public static void Write(string level, string area, string text, params (string Key, object? Value)[] fields)
    {
        level = LogLevels.Normal(level);
        var sink = Local.Value ?? Sink;
        if (sink is not null)
        {
            try { sink(new DiagEntry(level, area, text, fields)); }
            catch (Exception) { /* a test's sink: never the caller's problem */ }
            return;
        }
        _log?.Write(level, area, text, fields);
        try
        {
            var echo = _echo ?? (_log is null ? Console.Error : null);
            echo?.WriteLine(DiagFields.Echo(level, area, text, fields));
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public static void Debug(string area, string text, params (string Key, object? Value)[] fields) => Write(LogLevels.Debug, area, text, fields);
    public static void Info(string area, string text, params (string Key, object? Value)[] fields) => Write(LogLevels.Info, area, text, fields);
    public static void Warning(string area, string text, params (string Key, object? Value)[] fields) => Write(LogLevels.Warning, area, text, fields);
    public static void Error(string area, string text, params (string Key, object? Value)[] fields) => Write(LogLevels.Error, area, text, fields);

    private sealed class Restore(Action<DiagEntry>? before) : IDisposable
    {
        public void Dispose() => Local.Value = before;
    }
}

/// <summary>
/// The Diagnostics log's fourth column: space-separated key=value pairs, "level" and "area" first. A value with a space,
/// a quote, an equals sign or nothing in it is written in double quotes (\" and \\ inside); tabs and line breaks become
/// spaces. Null values are left out.
/// </summary>
public static class DiagFields
{
    public const string LevelKey = "level", AreaKey = "area", TeamKey = "team", MemberKey = "member";

    public static string Format(string level, string area, IReadOnlyList<(string Key, object? Value)>? fields)
    {
        var sb = new StringBuilder();
        Pair(sb, LevelKey, LogLevels.Normal(level));
        if (!string.IsNullOrWhiteSpace(area)) Pair(sb, AreaKey, area);
        foreach (var (key, value) in fields ?? [])
        {
            if (value is null || string.IsNullOrWhiteSpace(key) || key is LevelKey or AreaKey) continue;
            Pair(sb, key, Text(value));
        }
        return sb.ToString();
    }

    /// <summary>A value as the column writes it: invariant numbers, lower-case true/false, a time span in whole ms.</summary>
    public static string Text(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        TimeSpan t => ((long)t.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>The pairs of a fields column, in order (a later repeat of a key wins in <see cref="Get"/>). Never throws.</summary>
    public static IReadOnlyList<(string Key, string Value)> Parse(string? column)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrEmpty(column)) return list;
        var i = 0;
        while (i < column.Length)
        {
            while (i < column.Length && column[i] == ' ') i++;
            var eq = column.IndexOf('=', i);
            if (eq < 0) break;
            var key = column[i..eq];
            i = eq + 1;
            var value = new StringBuilder();
            if (i < column.Length && column[i] == '"')
            {
                i++;
                while (i < column.Length && column[i] != '"')
                {
                    if (column[i] == '\\' && i + 1 < column.Length) i++;
                    value.Append(column[i++]);
                }
                i++; // the closing quote
            }
            else
            {
                while (i < column.Length && column[i] != ' ') value.Append(column[i++]);
            }
            if (key.Length > 0 && !key.Contains(' ')) list.Add((key, value.ToString()));
        }
        return list;
    }

    public static string? Get(IReadOnlyList<(string Key, string Value)> pairs, string key)
    {
        string? found = null;
        foreach (var (k, v) in pairs) if (k == key) found = v;
        return found;
    }

    /// <summary>
    /// The team and member a line is about: "member" may be "Team/Name" (qualified) or a name with "team" beside it.
    /// </summary>
    public static (string? Team, string? Member) TeamMember(IReadOnlyList<(string Key, object? Value)>? fields)
    {
        string? team = null, member = null;
        foreach (var (k, v) in fields ?? [])
        {
            if (v is null) continue;
            if (k == TeamKey) team = Text(v);
            else if (k == MemberKey) member = Text(v);
        }
        return Split(team, member);
    }

    /// <summary><see cref="TeamMember(IReadOnlyList{ValueTuple{string, object}})"/> for a parsed column.</summary>
    public static (string? Team, string? Member) TeamMember(IReadOnlyList<(string Key, string Value)> pairs) =>
        Split(Get(pairs, TeamKey), Get(pairs, MemberKey));

    private static (string? Team, string? Member) Split(string? team, string? member)
    {
        if (member is { } m && m.IndexOf('/') is var slash and > 0 && slash < m.Length - 1)
            return (m[..slash], m[(slash + 1)..]); // the qualified name says the team
        return (string.IsNullOrWhiteSpace(team) ? null : team, string.IsNullOrWhiteSpace(member) ? null : member);
    }

    /// <summary>What goes to standard error: the text, then the pairs (level and area apart).</summary>
    public static string Echo(string level, string area, string text, IReadOnlyList<(string Key, object? Value)>? fields)
    {
        var sb = new StringBuilder(text);
        foreach (var (key, value) in fields ?? [])
        {
            if (value is null || string.IsNullOrWhiteSpace(key)) continue;
            Pair(sb, key, Text(value));
        }
        return sb.ToString().Replace("\r", " ").Replace('\n', ' ');
    }

    private static void Pair(StringBuilder sb, string key, string value)
    {
        if (sb.Length > 0) sb.Append(' ');
        sb.Append(key.Replace(' ', '_').Replace('=', '_').Replace('\t', '_')).Append('=');
        value = value.Replace('\t', ' ').Replace("\r", " ").Replace('\n', ' ');
        if (value.Length == 0 || value.IndexOfAny([' ', '"', '=', '\\']) >= 0)
            sb.Append('"').Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
        else sb.Append(value);
    }
}
