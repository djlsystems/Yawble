using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Yawble.Core.Scripts.Engine;
using Yawble.Core.Scripts.Language;

namespace Yawble.Core.Scripts.Library;

/// <summary>How a document is checked: <see cref="ScriptChecker.Check"/> in the app; a fake in tests.</summary>
public delegate ScriptCheckResult ScriptCheck(ScriptDocument document, IScriptSchemaLookup schemas, IScriptActionCatalog catalog);

/// <summary>A refused library call: a stable code (bad_script, bad_schema, not_found, schema_breaks, read_only) and the reason.</summary>
public sealed class ScriptLibraryException(string code, string message, JsonObject? data = null) : Exception(message)
{
    public string Code { get; } = code;
    public JsonObject? Details { get; } = data;
}

public sealed record ScriptUse(long TeamId, string Team, long MemberId, string Member, int? Version);

public sealed record ScriptSummary(long Id, string Key, string Name, string Description, int LatestVersion, int? RunnableVersion, bool Draft,
    string UpdatedAt, IReadOnlyList<ScriptUse> UsedBy);

public sealed record ScriptVersionInfo(int Version, bool Ok, string CreatedAt);

/// <summary>One saved version of a script: the document and its check (JSON: ok, issues, schemaReferences).</summary>
public sealed record ScriptVersionRow(long Id, string Key, string Name, string Description, int Version, bool Ok, JsonObject Document,
    JsonObject Check, string CreatedAt)
{
    public string DocumentText => Document.ToJsonString();
}

public sealed record ScriptSaveResult(long Id, string Key, int Version, bool Ok, bool Draft, JsonObject Check);

public sealed record SchemaSummary(string Name, string Description, int LatestVersion, bool BuiltIn, string? UpdatedAt);

public sealed record SchemaVersionRow(string Name, string Description, int Version, bool BuiltIn, JsonObject Schema, string? CreatedAt);

public sealed record SchemaVersionInfo(int Version, string CreatedAt);

public sealed record SchemaSaveResult(string Name, int Version, IReadOnlyList<SchemaBreak> Breaks);

/// <summary>A saved script's step that a schema change would break: it uses a field the change removes or retypes.</summary>
public sealed record SchemaBreak(long ScriptId, string Script, int Version, string? StepId, string FieldPath);

public sealed record ExportFile(string FileName, string Json);

/// <summary>
/// The global script and schema libraries (0056), on the Skills pattern: one row per script or schema and every save kept
/// as a new version. A script save is checked (<see cref="ScriptCheck"/>) against the schemas and the action catalog; one
/// that fails is kept as a draft that no member runs (runnable_version stays on the newest clean version). A schema save
/// that would break a saved script's references is refused with the list of scripts and steps it breaks. Built-in
/// schemas are read-only.
/// </summary>
public sealed class ScriptLibrary : IScriptSchemaLookup
{
    public const string Format = "yawble-script/1";

    private static readonly Regex KeyPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant);
    private static readonly Regex SchemaNamePattern = new(@"^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant);

    private readonly string _connectionString;
    private readonly Func<IScriptActionCatalog> _catalog;
    private readonly ScriptCheck _check;
    private readonly IScriptSchemaLookup _builtIns;
    private readonly IReadOnlyList<string> _builtInNames;
    private readonly TimeProvider _clock;

    /// <param name="catalog">The action catalog a script's requires are checked against (read at every check: SC-2/SC-3 fill it).</param>
    /// <param name="check">Null: <see cref="ScriptChecker.Check"/>.</param>
    /// <param name="builtIns">Null: <see cref="BuiltInSchemas.Lookup"/>; <paramref name="builtInNames"/> null: <see cref="BuiltInSchemas.Names"/>.</param>
    public ScriptLibrary(string connectionString, Func<IScriptActionCatalog> catalog, ScriptCheck? check = null,
        IScriptSchemaLookup? builtIns = null, IReadOnlyList<string>? builtInNames = null, TimeProvider? clock = null)
    {
        _connectionString = connectionString;
        _catalog = catalog;
        _check = check ?? ScriptChecker.Check;
        _builtIns = builtIns ?? BuiltInSchemas.Lookup;
        _builtInNames = builtInNames ?? BuiltInSchemas.Names;
        _clock = clock ?? TimeProvider.System;
    }

    // ------------------------------------------------------------------ scripts

    public IReadOnlyList<ScriptSummary> Scripts()
    {
        using var c = Open();
        var uses = Uses(c);
        var list = new List<ScriptSummary>();
        using var cmd = Cmd(c, "SELECT id, script_key, name, description, latest_version, runnable_version, updated_at FROM scripts ORDER BY name COLLATE NOCASE, id;");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetInt64(0);
            int? runnable = r.IsDBNull(5) ? null : r.GetInt32(5);
            var latest = r.GetInt32(4);
            list.Add(new ScriptSummary(id, r.GetString(1), r.GetString(2), r.GetString(3), latest, runnable, runnable != latest, r.GetString(6),
                uses.TryGetValue(id, out var u) ? u : []));
        }
        return list;
    }

    private static Dictionary<long, List<ScriptUse>> Uses(SqliteConnection c)
    {
        var map = new Dictionary<long, List<ScriptUse>>();
        using var cmd = Cmd(c, """
            SELECT m.script_id, t.id, t.name, m.id, m.name, m.script_version FROM members m JOIN teams t ON t.id = m.team_id
            WHERE m.kind = 'script' AND m.script_id IS NOT NULL ORDER BY t.name, m.name;
            """);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetInt64(0);
            if (!map.TryGetValue(id, out var l)) map[id] = l = [];
            l.Add(new ScriptUse(r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetInt32(5)));
        }
        return map;
    }

    /// <summary>A script by id (digits) or key.</summary>
    public long ScriptId(string scriptOrKey)
    {
        using var c = Open();
        var id = long.TryParse(scriptOrKey, out var n)
            ? Scalar(c, "SELECT id FROM scripts WHERE id = $v;", ("$v", n))
            : Scalar(c, "SELECT id FROM scripts WHERE script_key = $v;", ("$v", scriptOrKey.Trim()));
        return id is long l ? l : throw new ScriptLibraryException("not_found", $"there is no script '{scriptOrKey}'");
    }

    /// <summary>A version of a script (null: the latest saved).</summary>
    public ScriptVersionRow Get(long scriptId, int? version = null)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"""
            SELECT s.id, s.script_key, v.name, v.description, v.version, v.ok, v.document, v.check_result, v.created_at
            FROM script_versions v JOIN scripts s ON s.id = v.script_id
            WHERE v.script_id = $s AND v.version = {(version is null ? "s.latest_version" : "$v")};
            """, ("$s", scriptId), ("$v", (object?)version ?? DBNull.Value));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new ScriptLibraryException("not_found", version is null ? $"there is no script {scriptId}" : $"script {scriptId} has no version {version}");
        return new ScriptVersionRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt64(5) != 0,
            (JsonObject)JsonNode.Parse(r.GetString(6))!, (JsonObject)JsonNode.Parse(r.GetString(7))!, r.GetString(8));
    }

    /// <summary>The newest version that checked clean (what a member on "latest" runs); null when none did.</summary>
    public int? RunnableVersion(long scriptId)
    {
        using var c = Open();
        return Scalar(c, "SELECT runnable_version FROM scripts WHERE id = $s;", ("$s", scriptId)) is long v ? (int)v : null;
    }

    public IReadOnlyList<ScriptVersionInfo> Versions(long scriptId)
    {
        using var c = Open();
        if (Scalar(c, "SELECT id FROM scripts WHERE id = $s;", ("$s", scriptId)) is null) throw new ScriptLibraryException("not_found", $"there is no script {scriptId}");
        using var cmd = Cmd(c, "SELECT version, ok, created_at FROM script_versions WHERE script_id = $s ORDER BY version DESC;", ("$s", scriptId));
        using var r = cmd.ExecuteReader();
        var list = new List<ScriptVersionInfo>();
        while (r.Read()) list.Add(new ScriptVersionInfo(r.GetInt32(0), r.GetInt64(1) != 0, r.GetString(2)));
        return list;
    }

    /// <summary>Checks a document without saving it.</summary>
    public JsonObject Check(string documentText)
    {
        var (_, _, _, _, check) = Prepare(documentText);
        return check;
    }

    /// <summary>
    /// Saves a new version of the script the document names (its id). Always kept; one that fails its check is a draft.
    /// Throws bad_script when the text is not a script document at all (not JSON, no id or name).
    /// </summary>
    public ScriptSaveResult Save(string documentText)
    {
        var (json, key, name, description, check) = Prepare(documentText);
        var ok = check["ok"]?.GetValue<bool>() == true;
        var now = Now();
        using var c = Open();
        using var tx = c.BeginTransaction();
        var existing = Scalar(c, "SELECT id FROM scripts WHERE script_key = $k;", ("$k", key), tx) as long?;
        long id;
        int version;
        if (existing is { } e)
        {
            id = e;
            version = (int)(long)Scalar(c, "SELECT latest_version FROM scripts WHERE id = $s;", ("$s", id), tx)! + 1;
            Exec(c, tx, $"UPDATE scripts SET name = $n, description = $d, latest_version = $v, updated_at = $t{(ok ? ", runnable_version = $v" : "")} WHERE id = $s;",
                ("$n", name), ("$d", description), ("$v", version), ("$t", now), ("$s", id));
        }
        else
        {
            version = 1;
            id = (long)Scalar(c, """
                INSERT INTO scripts (script_key, name, description, latest_version, runnable_version, created_at, updated_at)
                VALUES ($k, $n, $d, 1, $r, $t, $t) RETURNING id;
                """, [("$k", key), ("$n", name), ("$d", description), ("$r", ok ? 1 : DBNull.Value), ("$t", now)], tx)!;
        }
        Exec(c, tx, """
            INSERT INTO script_versions (script_id, version, name, description, document, ok, check_result, created_at)
            VALUES ($s, $v, $n, $d, $doc, $ok, $chk, $t);
            """, ("$s", id), ("$v", version), ("$n", name), ("$d", description), ("$doc", json.ToJsonString()), ("$ok", ok ? 1 : 0),
            ("$chk", check.ToJsonString()), ("$t", now));
        tx.Commit();
        return new ScriptSaveResult(id, key, version, ok, !ok, check);
    }

    /// <summary>
    /// B134: deletes a script and every version (its runs go with it). Refused with in_use while any member uses it; the
    /// message names each team and member.
    /// </summary>
    public void Delete(long scriptId)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var key = Scalar(c, "SELECT script_key FROM scripts WHERE id = $s;", ("$s", scriptId), tx) as string
            ?? throw new ScriptLibraryException("not_found", $"there is no script {scriptId}");
        var users = new List<string>();
        using (var cmd = Cmd(c, """
            SELECT t.name, m.name FROM members m JOIN teams t ON t.id = m.team_id WHERE m.script_id = $s ORDER BY t.name, m.name;
            """, ("$s", scriptId)))
        {
            cmd.Transaction = tx;
            using var r = cmd.ExecuteReader();
            while (r.Read()) users.Add($"{r.GetString(0)}/{r.GetString(1)}");
        }
        if (users.Count > 0)
            throw new ScriptLibraryException("in_use", $"the script {key} is used by {string.Join(", ", users)}: remove or change those members first");
        Exec(c, tx, "DELETE FROM scripts WHERE id = $s;", ("$s", scriptId));
        tx.Commit();
    }

    public ExportFile Export(long scriptId, int? version = null)
    {
        var v = Get(scriptId, version);
        return new ExportFile(v.Key + ".json", v.Document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public ScriptSaveResult Import(string json) => Save(json);

    private (JsonObject Json, string Key, string Name, string Description, JsonObject Check) Prepare(string documentText)
    {
        JsonObject json;
        try { json = JsonNode.Parse(documentText) as JsonObject ?? throw new ScriptLibraryException("bad_script", "a script document is a JSON object"); }
        catch (JsonException e) { throw new ScriptLibraryException("bad_script", "the script is not JSON: " + e.Message); }
        var key = Text(json, "id");
        if (key is null || !KeyPattern.IsMatch(key))
            throw new ScriptLibraryException("bad_script", "a script needs an id (letters, digits, . _ -, at most 100), e.g. inbox-sms-alert");
        var name = Text(json, "name") ?? throw new ScriptLibraryException("bad_script", "a script needs a name");
        var description = Text(json, "description") ?? "";
        JsonObject check;
        if (Text(json, "format") != Format)
            check = CheckJson(false, [new ScriptCheckIssue(null, "bad_format", $"format must be {Format}")], []);
        else
        {
            try
            {
                var doc = ScriptDocument.Parse(json.ToJsonString());
                var result = _check(doc, this, _catalog());
                check = CheckJson(result.Ok, result.Issues, result.SchemaReferences);
            }
            catch (Exception e) when (e is not OutOfMemoryException and not ScriptLibraryException)
            {
                check = CheckJson(false, [new ScriptCheckIssue(null, "bad_document", e.Message)], []);
            }
        }
        return (json, key, name.Trim(), description.Trim(), check);
    }

    public static JsonObject CheckJson(bool ok, IEnumerable<ScriptCheckIssue> issues, IEnumerable<ScriptSchemaReference> references) => new()
    {
        ["ok"] = ok,
        ["issues"] = new JsonArray(issues.Select(i => (JsonNode?)new JsonObject { ["stepId"] = i.StepId, ["code"] = i.Code, ["message"] = i.Message }).ToArray()),
        ["schemaReferences"] = new JsonArray(references.Select(x => (JsonNode?)new JsonObject { ["schema"] = x.Schema, ["fieldPath"] = x.FieldPath, ["stepId"] = x.StepId }).ToArray()),
    };

    // ------------------------------------------------------------------ schemas

    public bool IsBuiltIn(string name) => _builtInNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The lookup a check uses: a built-in, else the newest saved version.</summary>
    public ScriptSchema? Find(string name)
    {
        if (_builtIns.Find(name) is { } builtIn) return builtIn;
        var row = StoredSchema(name, null);
        return row is null ? null : ScriptSchema.Parse(row.Name, row.Schema.ToJsonString());
    }

    public IReadOnlyList<SchemaSummary> Schemas()
    {
        var list = _builtInNames.Select(n => new SchemaSummary(n, "built-in", 1, true, null)).ToList();
        using var c = Open();
        using var cmd = Cmd(c, "SELECT name, description, latest_version, updated_at FROM schemas ORDER BY name COLLATE NOCASE;");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new SchemaSummary(r.GetString(0), r.GetString(1), r.GetInt32(2), false, r.GetString(3)));
        return list;
    }

    public SchemaVersionRow GetSchema(string name, int? version = null)
    {
        if (IsBuiltIn(name))
        {
            var builtIn = _builtIns.Find(_builtInNames.First(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                ?? throw new ScriptLibraryException("not_found", $"there is no schema '{name}'");
            return new SchemaVersionRow(builtIn.Name, "built-in", 1, true, SchemaJsonOf(builtIn), null);
        }
        return StoredSchema(name, version) ?? throw new ScriptLibraryException("not_found",
            version is null ? $"there is no schema '{name}'" : $"schema '{name}' has no version {version}");
    }

    /// <summary>A built-in schema's JSON: its Json property when it has one (read by reflection, so the seam stays small).</summary>
    private static JsonObject SchemaJsonOf(ScriptSchema schema)
    {
        var prop = schema.GetType().GetProperty("Json") ?? schema.GetType().GetProperty("Document");
        return prop?.GetValue(schema) switch
        {
            JsonObject o => (JsonObject)o.DeepClone(),
            string s when JsonNode.Parse(s) is JsonObject o => o,
            _ => new JsonObject { ["type"] = "object" },
        };
    }

    private SchemaVersionRow? StoredSchema(string name, int? version)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"""
            SELECT s.name, v.description, v.version, v.document, v.created_at FROM schema_versions v JOIN schemas s ON s.id = v.schema_id
            WHERE s.name = $n AND v.version = {(version is null ? "s.latest_version" : "$v")};
            """, ("$n", name.Trim()), ("$v", (object?)version ?? DBNull.Value));
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new SchemaVersionRow(r.GetString(0), r.GetString(1), r.GetInt32(2), false, (JsonObject)JsonNode.Parse(r.GetString(3))!, r.GetString(4))
            : null;
    }

    public IReadOnlyList<SchemaVersionInfo> SchemaVersions(string name)
    {
        if (IsBuiltIn(name)) return [new SchemaVersionInfo(1, "")];
        using var c = Open();
        using var cmd = Cmd(c, "SELECT v.version, v.created_at FROM schema_versions v JOIN schemas s ON s.id = v.schema_id WHERE s.name = $n ORDER BY v.version DESC;",
            ("$n", name.Trim()));
        using var r = cmd.ExecuteReader();
        var list = new List<SchemaVersionInfo>();
        while (r.Read()) list.Add(new SchemaVersionInfo(r.GetInt32(0), r.GetString(1)));
        if (list.Count == 0) throw new ScriptLibraryException("not_found", $"there is no schema '{name}'");
        return list;
    }

    /// <summary>The saved scripts' steps the new schema would break (nothing saved).</summary>
    public IReadOnlyList<SchemaBreak> CheckSchema(string name, string schemaText)
    {
        var (n, schema) = PrepareSchema(name, schemaText);
        return Breaks(n, schema);
    }

    /// <summary>Saves a new version of a schema; refused (schema_breaks) when it would break a saved script's references.</summary>
    public SchemaSaveResult SaveSchema(string name, string? description, string schemaText)
    {
        var (n, schema) = PrepareSchema(name, schemaText);
        var breaks = Breaks(n, schema);
        if (breaks.Count > 0)
        {
            var lines = breaks.Select(b => $"- {b.Script} v{b.Version}, step {b.StepId ?? "(document)"}: uses {n}.{b.FieldPath}");
            throw new ScriptLibraryException("schema_breaks",
                $"saving {n} would break {breaks.Count} reference(s) in saved scripts:\n" + string.Join("\n", lines),
                new JsonObject { ["breaks"] = JsonSerializer.SerializeToNode(breaks, Json.Web) });
        }
        var now = Now();
        var desc = (description ?? schema["description"]?.GetValue<string>() ?? "").Trim();
        using var c = Open();
        using var tx = c.BeginTransaction();
        var existing = Scalar(c, "SELECT id FROM schemas WHERE name = $n;", ("$n", n), tx) as long?;
        long id;
        int version;
        if (existing is { } e)
        {
            id = e;
            version = (int)(long)Scalar(c, "SELECT latest_version FROM schemas WHERE id = $s;", ("$s", id), tx)! + 1;
            Exec(c, tx, "UPDATE schemas SET description = $d, latest_version = $v, updated_at = $t WHERE id = $s;", ("$d", desc), ("$v", version), ("$t", now), ("$s", id));
        }
        else
        {
            version = 1;
            id = (long)Scalar(c, "INSERT INTO schemas (name, description, latest_version, created_at, updated_at) VALUES ($n, $d, 1, $t, $t) RETURNING id;",
                [("$n", n), ("$d", desc), ("$t", now)], tx)!;
        }
        Exec(c, tx, "INSERT INTO schema_versions (schema_id, version, description, document, created_at) VALUES ($s, $v, $d, $doc, $t);",
            ("$s", id), ("$v", version), ("$d", desc), ("$doc", schema.ToJsonString()), ("$t", now));
        tx.Commit();
        return new SchemaSaveResult(n, version, []);
    }

    public ExportFile ExportSchema(string name, int? version = null)
    {
        var v = GetSchema(name, version);
        var file = new JsonObject { ["name"] = v.Name, ["description"] = v.Description, ["schema"] = v.Schema.DeepClone() };
        return new ExportFile(v.Name + ".schema.json", file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>An exported schema file: { name, description?, schema }.</summary>
    public SchemaSaveResult ImportSchema(string json)
    {
        JsonObject o;
        try { o = JsonNode.Parse(json) as JsonObject ?? throw new ScriptLibraryException("bad_schema", "a schema file is a JSON object { name, description, schema }"); }
        catch (JsonException e) { throw new ScriptLibraryException("bad_schema", "the schema file is not JSON: " + e.Message); }
        var name = Text(o, "name") ?? throw new ScriptLibraryException("bad_schema", "a schema file needs a name");
        if (o["schema"] is not JsonObject schema) throw new ScriptLibraryException("bad_schema", "a schema file needs its schema (a JSON object)");
        return SaveSchema(name, Text(o, "description"), schema.ToJsonString());
    }

    private (string Name, JsonObject Schema) PrepareSchema(string name, string schemaText)
    {
        var n = (name ?? "").Trim();
        if (!SchemaNamePattern.IsMatch(n)) throw new ScriptLibraryException("bad_schema", "a schema's name starts with a letter and has letters, digits and _ (at most 64)");
        if (IsBuiltIn(n)) throw new ScriptLibraryException("read_only", $"{n} is a built-in schema and cannot be changed");
        JsonObject schema;
        try { schema = JsonNode.Parse(schemaText) as JsonObject ?? throw new ScriptLibraryException("bad_schema", "a schema is a JSON object"); }
        catch (JsonException e) { throw new ScriptLibraryException("bad_schema", "the schema is not JSON: " + e.Message); }
        if (Text(schema, "type") != "object") throw new ScriptLibraryException("bad_schema", "a schema is an object type: \"type\": \"object\"");
        try { ScriptSchema.Parse(n, schema.ToJsonString()); }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new ScriptLibraryException("bad_schema", e.Message); }
        return (n, schema);
    }

    /// <summary>
    /// Every saved script version a member could run (each script's latest, its runnable version and every version a member
    /// pins) whose check references a field of <paramref name="name"/> that the new schema removes or retypes.
    /// </summary>
    private List<SchemaBreak> Breaks(string name, JsonObject schema)
    {
        var old = StoredSchema(name, null);
        if (old is null) return [];
        var changed = SchemaDiff.RemovedOrRetyped(old.Schema, schema);
        if (changed.Count == 0) return [];
        var list = new List<SchemaBreak>();
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT s.id, s.script_key, v.version, v.check_result FROM script_versions v JOIN scripts s ON s.id = v.script_id
            WHERE v.version = s.latest_version OR v.version = s.runnable_version
               OR v.version IN (SELECT m.script_version FROM members m WHERE m.kind = 'script' AND m.script_id = s.id)
            ORDER BY s.script_key, v.version;
            """);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var check = JsonNode.Parse(r.GetString(3)) as JsonObject;
            foreach (var reference in check?["schemaReferences"] as JsonArray ?? [])
            {
                if (!string.Equals(reference?["schema"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase)) continue;
                var path = reference?["fieldPath"]?.GetValue<string>() ?? "";
                if (!changed.Any(p => SchemaDiff.Covers(p, path))) continue;
                list.Add(new SchemaBreak(r.GetInt64(0), r.GetString(1), r.GetInt32(2), reference?["stepId"]?.GetValue<string>(), path));
            }
        }
        return list;
    }

    // ------------------------------------------------------------------ plumbing

    private static string? Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private string Now() => _clock.GetUtcNow().ToString("O");

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var fk = c.CreateCommand();
        fk.CommandText = "PRAGMA foreign_keys=ON;";
        fk.ExecuteNonQuery();
        return c;
    }

    private static SqliteCommand Cmd(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    private static object? Scalar(SqliteConnection c, string sql, (string, object?) arg, SqliteTransaction? tx = null) => Scalar(c, sql, [arg], tx);

    private static object? Scalar(SqliteConnection c, string sql, (string, object?)[] args, SqliteTransaction? tx = null)
    {
        using var cmd = Cmd(c, sql, args);
        cmd.Transaction = tx;
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(c, sql, args);
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
