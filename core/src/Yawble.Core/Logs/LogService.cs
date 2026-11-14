using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Logs;

/// <summary>
/// What the Logs dialog asks for. <paramref name="Member"/> is "Team/Name". <paramref name="From"/> and
/// <paramref name="To"/> are ISO times (inclusive, compared as text). <paramref name="AsOf"/>: the time the first page
/// was read, so later pages are of the same list while new entries arrive.
/// </summary>
public sealed record LogQuery(
    string Source, string? Team = null, string? Member = null, string? From = null, string? To = null, string? Text = null,
    IReadOnlyDictionary<string, string>? Filters = null, string? Sort = null, bool Descending = true, int Offset = 0,
    int Limit = 100, string? AsOf = null);

/// <summary>What pruning a source before a cutoff would delete.</summary>
public sealed record LogPrunePreview(string Source, string Title, string Cutoff, long Count, string? Oldest, string? Newest, string Loss, long Files);

/// <summary>
/// Every log the app keeps, for viewing and upkeep: the database logs (<see cref="LogSources"/>) and the log files
/// (<see cref="FileLogs"/>). Pages of entries, filtered, searched and sorted; what pruning would delete, and pruning it;
/// how long each log is kept, and the daily sweep that applies it.
/// </summary>
public sealed class LogService(string dataDir, string connectionString, TimeProvider? clock = null, Action<string>? log = null)
{
    public const int MaxPage = 200;
    public const string RetentionPrefix = "logs.retention.";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string DataDir => dataDir;

    // ------------------------------------------------------------------ the sources

    public JsonArray Sources()
    {
        var list = new JsonArray();
        foreach (var s in LogSources.All)
        {
            list.Add(Describe(s.Id, s.Title, s.Group, s.About, s.Columns, s.Prune is not null, s.Prune?.Loss, s.KeptBecause, s.Prune?.DefaultDays,
                team: s.TeamWhere is not null, member: s.MemberWhere is not null));
        }
        foreach (var f in FileLogs.All)
            list.Add(Describe(f.Id, f.Title, f.Group, f.About, f.Columns, true, f.Loss, null, f.DefaultDays, f.MemberInText, f.MemberInText));
        return list;
    }

    private JsonObject Describe(string id, string title, string group, string about, IReadOnlyList<LogColumn> columns, bool prunable,
        string? loss, string? kept, int? defaultDays, bool team, bool member) => new()
    {
        ["id"] = id,
        ["title"] = title,
        ["group"] = group,
        ["about"] = about,
        ["columns"] = new JsonArray(columns.Select(c => (JsonNode)new JsonObject
        {
            ["key"] = c.Key, ["label"] = c.Label, ["kind"] = c.Kind, ["filter"] = c.Filter,
        }).ToArray()),
        ["prunable"] = prunable,
        ["loss"] = loss,
        ["keptBecause"] = kept,
        ["defaultDays"] = defaultDays,
        ["retentionDays"] = prunable ? RetentionDays(id, defaultDays) : null,
        ["team"] = team,
        ["member"] = member,
    };

    private static bool Known(string id) => LogSources.Find(id) is not null || FileLogs.Find(id) is not null;

    // ------------------------------------------------------------------ reading

    public JsonObject Query(LogQuery q)
    {
        var limit = Math.Clamp(q.Limit, 1, MaxPage);
        var offset = Math.Max(0, q.Offset);
        var asOf = q.AsOf ?? _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        if (FileLogs.Find(q.Source) is { } file) return QueryFile(file, q, asOf, offset, limit);
        var s = LogSources.Find(q.Source) ?? throw new ArgumentException($"no log named '{q.Source}'");

        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = Where(s, q, asOf, cmd, c);
        var sort = s.Columns.FirstOrDefault(col => col.Key == q.Sort) ?? s.Columns[0];
        var dir = q.Descending ? "DESC" : "ASC";
        var select = string.Join(", ", s.Columns.Select(col => $"{col.Sql} AS \"{col.Key}\""));
        cmd.CommandText = $"SELECT {s.RowId} AS _id, {select} FROM {s.From} WHERE {where} ORDER BY {sort.Sql} {dir}, {s.RowId} {dir} LIMIT @limit OFFSET @offset;";
        cmd.Parameters.AddWithValue("@limit", limit + 1);
        cmd.Parameters.AddWithValue("@offset", offset);
        var rows = new JsonArray();
        var more = false;
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (rows.Count == limit) { more = true; break; }
                var row = new JsonObject { ["_id"] = Value(r, 0)?.ToString() };
                for (var i = 0; i < s.Columns.Count; i++) row[s.Columns[i].Key] = Value(r, i + 1);
                rows.Add(row);
            }
        }
        long? total = null;
        if (offset == 0)
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM {s.From} WHERE {where};";
            total = (long)cmd.ExecuteScalar()!;
        }
        return Page(rows, total, asOf, more ? offset + limit : null);
    }

    private static JsonObject Page(JsonArray rows, long? total, string asOf, int? next) =>
        new() { ["rows"] = rows, ["total"] = total, ["asOf"] = asOf, ["next"] = next };

    /// <summary>The WHERE clause for a query, its parameters added to <paramref name="cmd"/>.</summary>
    private static string Where(LogSource s, LogQuery q, string asOf, SqliteCommand cmd, SqliteConnection c)
    {
        var parts = new List<string> { $"{s.Time} <= @asOf" };
        cmd.Parameters.AddWithValue("@asOf", asOf);
        var (team, teamId, member, name, memberId) = Identify(c, q.Team, q.Member);
        cmd.Parameters.AddWithValue("@team", (object?)team ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@teamId", (object?)teamId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@member", (object?)member ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@name", (object?)name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@memberId", (object?)memberId ?? DBNull.Value);
        if (member is not null)
        {
            if (s.MemberWhere is null) parts.Add("0");
            else parts.Add($"({s.MemberWhere})");
        }
        else if (team is not null)
        {
            if (s.TeamWhere is null) parts.Add("0");
            else parts.Add($"({s.TeamWhere})");
        }
        if (!string.IsNullOrWhiteSpace(q.From)) { parts.Add($"{s.Time} >= @from"); cmd.Parameters.AddWithValue("@from", q.From); }
        if (!string.IsNullOrWhiteSpace(q.To)) { parts.Add($"{s.Time} <= @to"); cmd.Parameters.AddWithValue("@to", q.To); }
        var n = 0;
        foreach (var (key, value) in q.Filters ?? new Dictionary<string, string>())
        {
            var col = s.Columns.FirstOrDefault(x => x.Key == key && x.Filter);
            if (col is null) continue;
            parts.Add($"COALESCE(CAST({col.Sql} AS TEXT), '') = @f{n}");
            cmd.Parameters.AddWithValue($"@f{n}", value);
            n++;
        }
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            parts.Add("(" + string.Join(" OR ", s.Columns.Select(col => $"CAST({col.Sql} AS TEXT) LIKE @q ESCAPE '\\'")) + ")");
            cmd.Parameters.AddWithValue("@q", "%" + Escape(q.Text.Trim()) + "%");
        }
        return string.Join(" AND ", parts);
    }

    /// <summary>A team name, and a member "Team/Name", resolved to what the sources' predicates use.</summary>
    private static (string? Team, long? TeamId, string? Member, string? Name, long? MemberId) Identify(SqliteConnection c, string? team, string? member)
    {
        string? name = null;
        if (!string.IsNullOrWhiteSpace(member))
        {
            var slash = member.IndexOf('/');
            if (slash <= 0) throw new ArgumentException("member is \"Team/Name\"");
            team = member[..slash];
            name = member[(slash + 1)..];
        }
        else member = null;
        if (string.IsNullOrWhiteSpace(team)) return (null, null, null, null, null);
        long? teamId = null, memberId = null;
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM teams WHERE name = @t COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("@t", team);
        if (cmd.ExecuteScalar() is long t) teamId = t;
        if (name is not null && teamId is not null)
        {
            cmd.CommandText = "SELECT id FROM members WHERE team_id = @id AND name = @n COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("@id", teamId);
            cmd.Parameters.AddWithValue("@n", name);
            if (cmd.ExecuteScalar() is long m) memberId = m;
        }
        return (team, teamId, member, name, memberId);
    }

    /// <summary>The values a filter column has (most used first, at most 200), within the team or member chosen.</summary>
    public JsonArray Values(string source, string column, string? team = null, string? member = null)
    {
        if (FileLogs.Find(source) is { } file)
        {
            var index = file.Columns.ToList().FindIndex(c => c.Key == column && c.Filter);
            if (index < 0) throw new ArgumentException($"'{column}' is not a filter of {source}");
            return new JsonArray(file.Read(dataDir).Select(e => e.Fields[index] ?? "").Where(v => v.Length > 0)
                .GroupBy(v => v).OrderByDescending(g => g.Count()).Take(200).Select(g => (JsonNode)g.Key).ToArray());
        }
        var s = LogSources.Find(source) ?? throw new ArgumentException($"no log named '{source}'");
        var col = s.Columns.FirstOrDefault(x => x.Key == column && x.Filter) ?? throw new ArgumentException($"'{column}' is not a filter of {source}");
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = Where(s, new LogQuery(source, team, member), _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture), cmd, c);
        cmd.CommandText = $"SELECT CAST({col.Sql} AS TEXT) AS v, COUNT(*) AS n FROM {s.From} WHERE {where} GROUP BY v HAVING v IS NOT NULL AND v <> '' ORDER BY n DESC, v LIMIT 200;";
        var list = new JsonArray();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    private JsonObject QueryFile(FileLogSource f, LogQuery q, string asOf, int offset, int limit)
    {
        IEnumerable<string[]> rows = f.Read(dataDir).Select(e => e.Fields).Where(e => string.CompareOrdinal(e[0] ?? "", asOf) <= 0);
        if (!string.IsNullOrWhiteSpace(q.Member))
        {
            var name = q.Member[(q.Member.IndexOf('/') + 1)..];
            rows = f.MemberInText ? rows.Where(e => e.Any(v => v?.Contains(name, StringComparison.OrdinalIgnoreCase) == true)) : [];
        }
        else if (!string.IsNullOrWhiteSpace(q.Team))
            rows = f.MemberInText ? rows.Where(e => e.Any(v => v?.Contains(q.Team, StringComparison.OrdinalIgnoreCase) == true)) : [];
        if (!string.IsNullOrWhiteSpace(q.From)) rows = rows.Where(e => string.CompareOrdinal(e[0] ?? "", q.From) >= 0);
        if (!string.IsNullOrWhiteSpace(q.To)) rows = rows.Where(e => string.CompareOrdinal(e[0] ?? "", q.To) <= 0);
        foreach (var (key, value) in q.Filters ?? new Dictionary<string, string>())
        {
            var i = f.Columns.ToList().FindIndex(c => c.Key == key && c.Filter);
            if (i >= 0) rows = rows.Where(e => (e[i] ?? "") == value);
        }
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var text = q.Text.Trim();
            rows = rows.Where(e => e.Any(v => v?.Contains(text, StringComparison.OrdinalIgnoreCase) == true));
        }
        var sortIndex = Math.Max(0, f.Columns.ToList().FindIndex(c => c.Key == q.Sort));
        var list = rows.ToList();
        list.Sort((a, b) => string.CompareOrdinal(a[sortIndex] ?? "", b[sortIndex] ?? ""));
        if (q.Descending) list.Reverse();
        var page = new JsonArray();
        foreach (var (e, i) in list.Skip(offset).Take(limit).Select((e, i) => (e, i)))
        {
            var row = new JsonObject { ["_id"] = $"{offset + i}" };
            for (var k = 0; k < f.Columns.Count; k++) row[f.Columns[k].Key] = e[k];
            page.Add(row);
        }
        return Page(page, offset == 0 ? list.Count : null, asOf, offset + limit < list.Count ? offset + limit : null);
    }

    /// <summary>The teams and their members ("Team/Name"), archived ones too, for the team and member filters.</summary>
    public JsonObject Who()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT t.name, m.name FROM teams t LEFT JOIN members m ON m.team_id = t.id ORDER BY t.name COLLATE NOCASE, CASE m.role WHEN 'manager' THEN 0 ELSE 1 END, m.name COLLATE NOCASE;";
        var teams = new JsonArray();
        var members = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var team = r.GetString(0);
            if (seen.Add(team)) teams.Add(team);
            if (!r.IsDBNull(1)) members.Add(new JsonObject { ["team"] = team, ["name"] = r.GetString(1), ["key"] = $"{team}/{r.GetString(1)}" });
        }
        return new JsonObject { ["teams"] = teams, ["members"] = members };
    }

    // ------------------------------------------------------------------ pruning

    /// <summary>What pruning <paramref name="source"/> before <paramref name="cutoff"/> (ISO) would delete.</summary>
    public LogPrunePreview PrunePreview(string source, string cutoff)
    {
        if (FileLogs.Find(source) is { } f)
        {
            var old = f.Read(dataDir).Select(e => e.Fields[0] ?? "").Where(t => string.CompareOrdinal(t, cutoff) < 0).Order(StringComparer.Ordinal).ToList();
            return new(f.Id, f.Title, cutoff, old.Count, old.FirstOrDefault(), old.LastOrDefault(), f.Loss, 0);
        }
        var s = LogSources.Find(source) ?? throw new ArgumentException($"no log named '{source}'");
        var p = s.Prune ?? throw new ArgumentException($"{s.Title} is kept: {s.KeptBecause}");
        var quietSince = QuietSince();
        using var c = Open();
        long count = 0, files = 0;
        string? oldest = null, newest = null;
        foreach (var t in p.Tables)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*), MIN({t.TimeColumn}), MAX({t.TimeColumn}) {(t.FileColumn is null ? "" : $", COUNT({t.FileColumn})")} FROM {t.Table} WHERE {PruneWhere(t)};";
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            cmd.Parameters.AddWithValue("@quietSince", quietSince);
            using var r = cmd.ExecuteReader();
            r.Read();
            count += r.GetInt64(0);
            if (!r.IsDBNull(1) && (oldest is null || string.CompareOrdinal(r.GetString(1), oldest) < 0)) oldest = r.GetString(1);
            if (!r.IsDBNull(2) && (newest is null || string.CompareOrdinal(r.GetString(2), newest) > 0)) newest = r.GetString(2);
            if (t.FileColumn is not null) files += r.GetInt64(3);
        }
        return new(s.Id, s.Title, cutoff, count, oldest, newest, p.Loss, files);
    }

    /// <summary>Answers newer than this are kept whatever the cutoff: a network denial and a tool's Not now hold the agent off
    /// for <see cref="LogSources.AnswerQuiet"/>, and pruning them would lift it.</summary>
    private string QuietSince() => (_clock.GetUtcNow() - LogSources.AnswerQuiet).ToString("O", CultureInfo.InvariantCulture);

    private static string PruneWhere(LogPrune p) => $"{p.TimeColumn} < @cutoff" + (p.Where is null ? "" : $" AND ({p.Where})");

    /// <summary>Deletes the entries of <paramref name="source"/> before <paramref name="cutoff"/>; how many went.</summary>
    public long Prune(string source, string cutoff)
    {
        if (FileLogs.Find(source) is { } f) return f.Prune(dataDir, cutoff);
        var s = LogSources.Find(source) ?? throw new ArgumentException($"no log named '{source}'");
        var p = s.Prune ?? throw new ArgumentException($"{s.Title} is kept: {s.KeptBecause}");
        var quietSince = QuietSince();
        using var c = Open();
        long deleted = 0;
        foreach (var t in p.Tables)
        {
            if (t.FileColumn is not null)
            {
                using var files = c.CreateCommand();
                files.CommandText = $"SELECT {t.FileColumn} FROM {t.Table} WHERE {PruneWhere(t)} AND {t.FileColumn} IS NOT NULL;";
                files.Parameters.AddWithValue("@cutoff", cutoff);
                files.Parameters.AddWithValue("@quietSince", quietSince);
                using var r = files.ExecuteReader();
                while (r.Read()) DeleteFile(r.GetString(0));
            }
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"DELETE FROM {t.Table} WHERE {PruneWhere(t)};";
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            cmd.Parameters.AddWithValue("@quietSince", quietSince);
            deleted += cmd.ExecuteNonQuery();
        }
        return deleted;
    }

    /// <summary>Deletes a file the app wrote under its data folder; nothing outside it.</summary>
    private void DeleteFile(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
            File.Delete(full);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
    }

    // ------------------------------------------------------------------ retention

    /// <summary>How many days <paramref name="source"/> is kept; 0 is forever.</summary>
    public int RetentionDays(string source)
    {
        var d = LogSources.Find(source)?.Prune?.DefaultDays ?? FileLogs.Find(source)?.DefaultDays;
        return RetentionDays(source, d);
    }

    private int RetentionDays(string source, int? defaultDays)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_meta WHERE key = @k;";
        cmd.Parameters.AddWithValue("@k", RetentionPrefix + source);
        return cmd.ExecuteScalar() is string v && int.TryParse(v, CultureInfo.InvariantCulture, out var days) ? days : defaultDays ?? 0;
    }

    /// <summary>Keeps <paramref name="source"/> for <paramref name="days"/> days (0: forever; at least 1 otherwise).</summary>
    public void SetRetention(string source, int days)
    {
        if (!Known(source)) throw new ArgumentException($"no log named '{source}'");
        if (LogSources.Find(source) is { Prune: null } kept) throw new ArgumentException($"{kept.Title} is kept: {kept.KeptBecause}");
        if (days < 0 || days > 3650) throw new ArgumentException("days is 0 (keep forever) to 3650");
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO app_meta (key, value) VALUES (@k, @v) ON CONFLICT(key) DO UPDATE SET value = @v;";
        cmd.Parameters.AddWithValue("@k", RetentionPrefix + source);
        cmd.Parameters.AddWithValue("@v", days.ToString(CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    /// <summary>The cutoff for keeping <paramref name="days"/> days, as of now.</summary>
    public string CutoffFor(int days) => (_clock.GetUtcNow() - TimeSpan.FromDays(days)).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Applies every retention: each prunable log loses what is older than its days. Also forgets transcripts whose
    /// file is gone. Returns how many entries went, by source.
    /// </summary>
    public IReadOnlyDictionary<string, long> Sweep()
    {
        var done = new Dictionary<string, long>();
        foreach (var id in LogSources.All.Where(s => s.Prune is not null).Select(s => s.Id).Concat(FileLogs.All.Select(f => f.Id)))
        {
            var days = RetentionDays(id);
            if (days <= 0) continue;
            try
            {
                var n = Prune(id, CutoffFor(days));
                if (n > 0) done[id] = n;
            }
            catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"Logs: keeping {id} to {days} days failed: {e.Message}");
            }
        }
        var orphans = ForgetMissingTranscripts();
        if (orphans > 0) done["transcripts"] = done.GetValueOrDefault("transcripts") + orphans;
        foreach (var (id, n) in done) log?.Invoke($"Logs: pruned {n} {id} entr{(n == 1 ? "y" : "ies")} past their retention");
        return done;
    }

    /// <summary>Removes the rows of finished transcripts whose file no longer exists.</summary>
    public int ForgetMissingTranscripts()
    {
        using var c = Open();
        var gone = new List<long>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT run_id, path FROM transcripts WHERE ended_at IS NOT NULL;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (r.IsDBNull(1) || !File.Exists(r.GetString(1))) gone.Add(r.GetInt64(0));
        }
        foreach (var id in gone)
        {
            using var del = c.CreateCommand();
            del.CommandText = "DELETE FROM transcripts WHERE run_id = @id;";
            del.Parameters.AddWithValue("@id", id);
            del.ExecuteNonQuery();
        }
        return gone.Count;
    }

    /// <summary>Sweeps now, then once a day, until <paramref name="ct"/> ends.</summary>
    public async Task RunDailyAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Sweep(); }
            catch (Exception e) when (e is SqliteException or IOException) { log?.Invoke($"Logs: the daily sweep failed: {e.Message}"); }
            try { await Task.Delay(TimeSpan.FromDays(1), _clock, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ------------------------------------------------------------------ helpers

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        return c;
    }

    private static JsonNode? Value(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        return r.GetValue(i) switch
        {
            long l => JsonValue.Create(l),
            double d => JsonValue.Create(d),
            string s => JsonValue.Create(s),
            byte[] => null,
            var o => JsonValue.Create(Convert.ToString(o, CultureInfo.InvariantCulture)),
        };
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
