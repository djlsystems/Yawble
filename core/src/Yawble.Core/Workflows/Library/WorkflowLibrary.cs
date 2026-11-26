using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Yawble.Core.Workflows.Engine;
using Yawble.Core.Workflows.Language;

namespace Yawble.Core.Workflows.Library;

/// <summary>How a document is checked: <see cref="WorkflowChecker.Check"/> in the app; a fake in tests.</summary>
public delegate WorkflowCheckResult WorkflowCheck(WorkflowDocument document, IWorkflowSchemaLookup schemas, IWorkflowActionCatalog catalog);

/// <summary>A refused library call: a stable code (bad_workflow, bad_schema, not_found, schema_breaks, read_only) and the reason.</summary>
public sealed class WorkflowLibraryException(string code, string message, JsonObject? data = null) : Exception(message)
{
    public string Code { get; } = code;
    public JsonObject? Details { get; } = data;
}

public sealed record WorkflowUse(long TeamId, string Team, long MemberId, string Member, int? Version);

public sealed record WorkflowSummary(long Id, string Key, string Name, string Description, int LatestVersion, int? RunnableVersion, bool Draft,
    string UpdatedAt, IReadOnlyList<WorkflowUse> UsedBy);

public sealed record WorkflowVersionInfo(int Version, bool Ok, string CreatedAt);

/// <summary>One saved version of a workflow: the document and its check (JSON: ok, issues, schemaReferences).</summary>
public sealed record WorkflowVersionRow(long Id, string Key, string Name, string Description, int Version, bool Ok, JsonObject Document,
    JsonObject Check, string CreatedAt)
{
    public string DocumentText => Document.ToJsonString();
}

public sealed record WorkflowSaveResult(long Id, string Key, int Version, bool Ok, bool Draft, JsonObject Check);

public sealed record SchemaSummary(string Name, string Description, int LatestVersion, bool BuiltIn, string? UpdatedAt);

public sealed record SchemaVersionRow(string Name, string Description, int Version, bool BuiltIn, JsonObject Schema, string? CreatedAt);

public sealed record SchemaVersionInfo(int Version, string CreatedAt);

public sealed record SchemaSaveResult(string Name, int Version, IReadOnlyList<SchemaBreak> Breaks);

/// <summary>A saved workflow's step that a schema change would break: it uses a field the change removes or retypes.</summary>
public sealed record SchemaBreak(long WorkflowId, string Workflow, int Version, string? StepId, string FieldPath);

public sealed record ExportFile(string FileName, string Json);

/// <summary>
/// The global workflow and schema libraries (0056), on the Skills pattern: one row per workflow or schema and every save kept
/// as a new version. A workflow save is checked (<see cref="WorkflowCheck"/>) against the schemas and the action catalog; one
/// that fails is kept as a draft that no member runs (runnable_version stays on the newest clean version). A schema save
/// that would break a saved workflow's references is refused with the list of workflows and steps it breaks. Built-in
/// schemas are read-only.
/// </summary>
public sealed class WorkflowLibrary : IWorkflowSchemaLookup
{
    public const string Format = "yawble-workflow/1";

    private static readonly Regex KeyPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant);
    private static readonly Regex SchemaNamePattern = new(@"^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant);

    private readonly string _connectionString;
    private readonly Func<IWorkflowActionCatalog> _catalog;
    private readonly WorkflowCheck _check;
    private readonly IWorkflowSchemaLookup _builtIns;
    private readonly IReadOnlyList<string> _builtInNames;
    private readonly TimeProvider _clock;

    /// <summary>The person's integrations: a step's <c>use</c> names one of them, and its requires entry is derived from it.</summary>
    public IWorkflowIntegrationDirectory Integrations { get; }

    /// <param name="catalog">The action catalog a workflow's requires are checked against (read at every check: SC-2/SC-3 fill it).</param>
    /// <param name="check">Null: <see cref="WorkflowChecker.Check"/>.</param>
    /// <param name="builtIns">Null: <see cref="BuiltInSchemas.Lookup"/>; <paramref name="builtInNames"/> null: <see cref="BuiltInSchemas.Names"/>.</param>
    /// <param name="integrations">Null: none (every name a step uses must then be in the document's requires).</param>
    public WorkflowLibrary(string connectionString, Func<IWorkflowActionCatalog> catalog, WorkflowCheck? check = null,
        IWorkflowSchemaLookup? builtIns = null, IReadOnlyList<string>? builtInNames = null, TimeProvider? clock = null,
        IWorkflowIntegrationDirectory? integrations = null)
    {
        Integrations = integrations ?? NoWorkflowIntegrations.Instance;
        _connectionString = connectionString;
        _catalog = catalog;
        _check = check ?? WorkflowChecker.Check;
        _builtIns = builtIns ?? BuiltInSchemas.Lookup;
        _builtInNames = builtInNames ?? BuiltInSchemas.Names;
        _clock = clock ?? TimeProvider.System;
    }

    // ------------------------------------------------------------------ workflows

    public IReadOnlyList<WorkflowSummary> Workflows()
    {
        using var c = Open();
        var uses = Uses(c);
        var list = new List<WorkflowSummary>();
        using var cmd = Cmd(c, "SELECT id, workflow_key, name, description, latest_version, runnable_version, updated_at FROM workflows ORDER BY name COLLATE NOCASE, id;");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetInt64(0);
            int? runnable = r.IsDBNull(5) ? null : r.GetInt32(5);
            var latest = r.GetInt32(4);
            list.Add(new WorkflowSummary(id, r.GetString(1), r.GetString(2), r.GetString(3), latest, runnable, runnable != latest, r.GetString(6),
                uses.TryGetValue(id, out var u) ? u : []));
        }
        return list;
    }

    private static Dictionary<long, List<WorkflowUse>> Uses(SqliteConnection c)
    {
        var map = new Dictionary<long, List<WorkflowUse>>();
        using var cmd = Cmd(c, """
            SELECT m.workflow_id, t.id, t.name, m.id, m.name, m.workflow_version FROM members m JOIN teams t ON t.id = m.team_id
            WHERE m.kind = 'workflow' AND m.workflow_id IS NOT NULL ORDER BY t.name, m.name;
            """);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetInt64(0);
            if (!map.TryGetValue(id, out var l)) map[id] = l = [];
            l.Add(new WorkflowUse(r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetInt32(5)));
        }
        return map;
    }

    /// <summary>A workflow by id (digits) or key.</summary>
    public long WorkflowId(string workflowOrKey)
    {
        using var c = Open();
        var id = long.TryParse(workflowOrKey, out var n)
            ? Scalar(c, "SELECT id FROM workflows WHERE id = $v;", ("$v", n))
            : Scalar(c, "SELECT id FROM workflows WHERE workflow_key = $v;", ("$v", workflowOrKey.Trim()));
        return id is long l ? l : throw new WorkflowLibraryException("not_found", $"there is no workflow '{workflowOrKey}'");
    }

    /// <summary>A version of a workflow (null: the latest saved).</summary>
    public WorkflowVersionRow Get(long workflowId, int? version = null)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"""
            SELECT s.id, s.workflow_key, v.name, v.description, v.version, v.ok, v.document, v.check_result, v.created_at
            FROM workflow_versions v JOIN workflows s ON s.id = v.workflow_id
            WHERE v.workflow_id = $s AND v.version = {(version is null ? "s.latest_version" : "$v")};
            """, ("$s", workflowId), ("$v", (object?)version ?? DBNull.Value));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new WorkflowLibraryException("not_found", version is null ? $"there is no workflow {workflowId}" : $"workflow {workflowId} has no version {version}");
        return new WorkflowVersionRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt64(5) != 0,
            (JsonObject)JsonNode.Parse(r.GetString(6))!, (JsonObject)JsonNode.Parse(r.GetString(7))!, r.GetString(8));
    }

    /// <summary>The newest version that checked clean (what a member on "latest" runs); null when none did.</summary>
    public int? RunnableVersion(long workflowId)
    {
        using var c = Open();
        return Scalar(c, "SELECT runnable_version FROM workflows WHERE id = $s;", ("$s", workflowId)) is long v ? (int)v : null;
    }

    public IReadOnlyList<WorkflowVersionInfo> Versions(long workflowId)
    {
        using var c = Open();
        if (Scalar(c, "SELECT id FROM workflows WHERE id = $s;", ("$s", workflowId)) is null) throw new WorkflowLibraryException("not_found", $"there is no workflow {workflowId}");
        using var cmd = Cmd(c, "SELECT version, ok, created_at FROM workflow_versions WHERE workflow_id = $s ORDER BY version DESC;", ("$s", workflowId));
        using var r = cmd.ExecuteReader();
        var list = new List<WorkflowVersionInfo>();
        while (r.Read()) list.Add(new WorkflowVersionInfo(r.GetInt32(0), r.GetInt64(1) != 0, r.GetString(2)));
        return list;
    }

    /// <summary>Checks a document without saving it.</summary>
    public JsonObject Check(string documentText)
    {
        var (_, _, _, _, check) = Prepare(documentText);
        return check;
    }

    /// <summary>
    /// Saves a new version of the workflow the document names (its id). Always kept; one that fails its check is a draft.
    /// Throws bad_workflow when the text is not a workflow document at all (not JSON, no id or name).
    /// </summary>
    public WorkflowSaveResult Save(string documentText)
    {
        var (json, key, name, description, check) = Prepare(documentText);
        var ok = check["ok"]?.GetValue<bool>() == true;
        var now = Now();
        using var c = Open();
        using var tx = c.BeginTransaction();
        var existing = Scalar(c, "SELECT id FROM workflows WHERE workflow_key = $k;", ("$k", key), tx) as long?;
        long id;
        int version;
        if (existing is { } e)
        {
            id = e;
            version = (int)(long)Scalar(c, "SELECT latest_version FROM workflows WHERE id = $s;", ("$s", id), tx)! + 1;
            Exec(c, tx, $"UPDATE workflows SET name = $n, description = $d, latest_version = $v, updated_at = $t{(ok ? ", runnable_version = $v" : "")} WHERE id = $s;",
                ("$n", name), ("$d", description), ("$v", version), ("$t", now), ("$s", id));
        }
        else
        {
            version = 1;
            id = (long)Scalar(c, """
                INSERT INTO workflows (workflow_key, name, description, latest_version, runnable_version, created_at, updated_at)
                VALUES ($k, $n, $d, 1, $r, $t, $t) RETURNING id;
                """, [("$k", key), ("$n", name), ("$d", description), ("$r", ok ? 1 : DBNull.Value), ("$t", now)], tx)!;
        }
        Exec(c, tx, """
            INSERT INTO workflow_versions (workflow_id, version, name, description, document, ok, check_result, created_at)
            VALUES ($s, $v, $n, $d, $doc, $ok, $chk, $t);
            """, ("$s", id), ("$v", version), ("$n", name), ("$d", description), ("$doc", json.ToJsonString()), ("$ok", ok ? 1 : 0),
            ("$chk", check.ToJsonString()), ("$t", now));
        tx.Commit();
        return new WorkflowSaveResult(id, key, version, ok, !ok, check);
    }

    /// <summary>
    /// Deletes a workflow and every version (its runs go with it). Refused with in_use while any member uses it; the
    /// message names each team and member.
    /// </summary>
    public void Delete(long workflowId)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var key = Scalar(c, "SELECT workflow_key FROM workflows WHERE id = $s;", ("$s", workflowId), tx) as string
            ?? throw new WorkflowLibraryException("not_found", $"there is no workflow {workflowId}");
        var users = new List<string>();
        using (var cmd = Cmd(c, """
            SELECT t.name, m.name FROM members m JOIN teams t ON t.id = m.team_id WHERE m.workflow_id = $s ORDER BY t.name, m.name;
            """, ("$s", workflowId)))
        {
            cmd.Transaction = tx;
            using var r = cmd.ExecuteReader();
            while (r.Read()) users.Add($"{r.GetString(0)}/{r.GetString(1)}");
        }
        if (users.Count > 0)
            throw new WorkflowLibraryException("in_use", $"the workflow {key} is used by {string.Join(", ", users)}: remove or change those members first");
        Exec(c, tx, "DELETE FROM workflows WHERE id = $s;", ("$s", workflowId));
        tx.Commit();
    }

    public ExportFile Export(long workflowId, int? version = null)
    {
        var v = Get(workflowId, version);
        return new ExportFile(v.Key + ".json", v.Document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public WorkflowSaveResult Import(string json) => Save(json);

    private (JsonObject Json, string Key, string Name, string Description, JsonObject Check) Prepare(string documentText)
    {
        JsonObject json;
        try { json = JsonNode.Parse(documentText) as JsonObject ?? throw new WorkflowLibraryException("bad_workflow", "a workflow document is a JSON object"); }
        catch (JsonException e) { throw new WorkflowLibraryException("bad_workflow", "the workflow is not JSON: " + e.Message); }
        var key = Text(json, "id");
        if (key is null || !KeyPattern.IsMatch(key))
            throw new WorkflowLibraryException("bad_workflow", "a workflow needs an id (letters, digits, . _ -, at most 100), e.g. inbox-sms-alert");
        var name = Text(json, "name") ?? throw new WorkflowLibraryException("bad_workflow", "a workflow needs a name");
        var description = Text(json, "description") ?? "";
        JsonObject check;
        if (Text(json, "format") != Format)
            check = CheckJson(false, [new WorkflowCheckIssue(null, "bad_format", $"format must be {Format}")], []);
        else
        {
            try
            {
                var warnings = DeriveRequires(json);
                var doc = WorkflowDocument.Parse(json.ToJsonString());
                var result = _check(doc, this, _catalog());
                check = CheckJson(result.Ok, result.Issues, result.SchemaReferences, warnings);
            }
            catch (Exception e) when (e is not OutOfMemoryException and not WorkflowLibraryException)
            {
                check = CheckJson(false, [new WorkflowCheckIssue(null, "bad_document", e.Message)], []);
            }
        }
        return (json, key, name.Trim(), description.Trim(), check);
    }

    /// <summary>The check's JSON: ok, issues, schemaReferences, and warnings (they never make a version a draft).</summary>
    public static JsonObject CheckJson(bool ok, IEnumerable<WorkflowCheckIssue> issues, IEnumerable<WorkflowSchemaReference> references,
        IEnumerable<WorkflowCheckIssue>? warnings = null) => new()
    {
        ["ok"] = ok,
        ["issues"] = Issues(issues),
        ["schemaReferences"] = new JsonArray(references.Select(x => (JsonNode?)new JsonObject { ["schema"] = x.Schema, ["fieldPath"] = x.FieldPath, ["stepId"] = x.StepId }).ToArray()),
        ["warnings"] = Issues(warnings ?? []),
    };

    private static JsonArray Issues(IEnumerable<WorkflowCheckIssue> issues) =>
        new(issues.Select(i => (JsonNode?)new JsonObject { ["stepId"] = i.StepId, ["code"] = i.Code, ["message"] = i.Message }).ToArray());

    /// <summary>The warning code of a document whose requires did not match its steps (the derived one replaced it).</summary>
    public const string StaleRequires = "stale-requires";

    /// <summary>
    /// Replaces the document's requires with the one its steps give (<see cref="WorkflowRequires.Derive"/>): one entry per integration the
    /// steps use, its kind and the actions they call. A requires that differed is a warning, never an issue.
    /// </summary>
    private List<WorkflowCheckIssue> DeriveRequires(JsonObject json)
    {
        if (json["steps"] is not JsonArray) return [];
        var derived = WorkflowRequires.Derive(json, Integrations);
        if (!derived.Stale) return [];
        var before = json["requires"] is JsonArray old ? Describe(old) : "none";
        json["requires"] = derived.Requires;
        return [new WorkflowCheckIssue(null, StaleRequires,
            $"requires did not match the steps ({before}); it is now what the steps use: {Describe(derived.Requires)}")];

        static string Describe(JsonArray requires) => requires.Count == 0 ? "none" : string.Join("; ", requires.OfType<JsonObject>().Select(r =>
            $"{r["name"]} ({r["kind"]}: {string.Join(", ", (r["actions"] as JsonArray ?? []).Select(a => a?.ToString()))})"));
    }

    /// <summary>
    /// The document with its aliases replaced by real integration names: each requires name that is not an integration becomes the one
    /// <paramref name="bindings"/> binds it to (or stays, listed in Unresolved, when there is none), in requires and in every step's use;
    /// requires is then derived again. Renamed maps each alias to its real name.
    /// </summary>
    public (JsonObject Document, IReadOnlyDictionary<string, string> Renamed, IReadOnlyList<string> Unresolved) UseRealNames(string documentText,
        IReadOnlyDictionary<string, string>? bindings)
    {
        JsonObject json;
        try { json = JsonNode.Parse(documentText) as JsonObject ?? throw new WorkflowLibraryException("bad_workflow", "a workflow document is a JSON object"); }
        catch (JsonException e) { throw new WorkflowLibraryException("bad_workflow", "the workflow is not JSON: " + e.Message); }
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        var unresolved = new List<string>();
        var uses = (json["steps"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(s => Text(s, "type") == "integration").Select(s => Text(s, "use")).OfType<string>();
        var names = (json["requires"] as JsonArray ?? []).OfType<JsonObject>().Select(r => Text(r, "name")).OfType<string>()
            .Concat(uses).Distinct(StringComparer.Ordinal).ToList();
        foreach (var name in names)
        {
            if (Integrations.Find(name) is not null) continue;
            var target = bindings is not null && bindings.TryGetValue(name, out var b) && !string.IsNullOrWhiteSpace(b) ? Integrations.Find(b)?.Name ?? b.Trim() : null;
            if (target is null) { unresolved.Add(name); continue; }
            renamed[name] = target;
        }
        foreach (var r in (json["requires"] as JsonArray ?? []).OfType<JsonObject>())
            if (Text(r, "name") is { } n && renamed.TryGetValue(n, out var real)) r["name"] = real;
        foreach (var s in (json["steps"] as JsonArray ?? []).OfType<JsonObject>())
            if (Text(s, "type") == "integration" && Text(s, "use") is { } u && renamed.TryGetValue(u, out var real)) s["use"] = real;
        if (json["steps"] is JsonArray) json["requires"] = WorkflowRequires.Derive(json, Integrations).Requires;
        return (json, renamed, unresolved);
    }

    // ------------------------------------------------------------------ schemas

    public bool IsBuiltIn(string name) => _builtInNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The lookup a check uses: a built-in, else the newest saved version.</summary>
    public WorkflowSchema? Find(string name)
    {
        if (_builtIns.Find(name) is { } builtIn) return builtIn;
        var row = StoredSchema(name, null);
        return row is null ? null : WorkflowSchema.Parse(row.Name, row.Schema.ToJsonString());
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
                ?? throw new WorkflowLibraryException("not_found", $"there is no schema '{name}'");
            var description = builtIn.Schema["description"] is JsonValue d && d.TryGetValue<string>(out var text) ? text : "built-in";
            return new SchemaVersionRow(builtIn.Name, description, 1, true, (JsonObject)builtIn.Schema.DeepClone(), null);
        }
        return StoredSchema(name, version) ?? throw new WorkflowLibraryException("not_found",
            version is null ? $"there is no schema '{name}'" : $"schema '{name}' has no version {version}");
    }

    /// <summary>A workflow that uses a schema: the variables typed with it and the steps that read its fields (its latest version).</summary>
    public sealed record SchemaUse(long Id, string Name, int Version, IReadOnlyList<string> Variables, IReadOnlyList<string> Steps);

    /// <summary>
    /// The workflows whose latest version uses the schema <paramref name="name"/>: each variable whose type names it (also in list&lt;...&gt;)
    /// and each step its check found reading it. A built-in or a saved schema; a name that is neither is not_found.
    /// </summary>
    public IReadOnlyList<SchemaUse> SchemaUsage(string name)
    {
        var n = (name ?? "").Trim();
        if (!IsBuiltIn(n) && StoredSchema(n, null) is null) throw new WorkflowLibraryException("not_found", $"there is no schema '{n}'");
        if (IsBuiltIn(n)) n = _builtInNames.First(b => string.Equals(b, n, StringComparison.OrdinalIgnoreCase));
        var typeName = new Regex($@"(?<![A-Za-z0-9_]){Regex.Escape(n)}(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);
        var list = new List<SchemaUse>();
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT s.id, v.name, v.version, v.document, v.check_result FROM workflow_versions v JOIN workflows s ON s.id = v.workflow_id
            WHERE v.version = s.latest_version ORDER BY v.name COLLATE NOCASE, s.id;
            """);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var doc = JsonNode.Parse(r.GetString(3)) as JsonObject;
            var check = JsonNode.Parse(r.GetString(4)) as JsonObject;
            var variables = (doc?["variables"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(v => Text(v, "type") is { } t && typeName.IsMatch(t)).Select(v => Text(v, "name")).OfType<string>().Distinct().ToList();
            var steps = (check?["schemaReferences"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(x => string.Equals(x["schema"]?.GetValue<string>(), n, StringComparison.Ordinal))
                .Select(x => x["stepId"] is JsonValue sv && sv.TryGetValue<string>(out var id) ? id : null).OfType<string>().Distinct().ToList();
            var usedByDeclaration = (check?["schemaReferences"] as JsonArray ?? []).OfType<JsonObject>()
                .Any(x => string.Equals(x["schema"]?.GetValue<string>(), n, StringComparison.Ordinal));
            if (variables.Count > 0 || steps.Count > 0 || usedByDeclaration) list.Add(new SchemaUse(r.GetInt64(0), r.GetString(1), r.GetInt32(2), variables, steps));
        }
        return list;
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
        if (list.Count == 0) throw new WorkflowLibraryException("not_found", $"there is no schema '{name}'");
        return list;
    }

    /// <summary>The saved workflows' steps the new schema would break (nothing saved).</summary>
    public IReadOnlyList<SchemaBreak> CheckSchema(string name, string schemaText)
    {
        var (n, schema) = PrepareSchema(name, schemaText);
        return Breaks(n, schema);
    }

    /// <summary>Saves a new version of a schema; refused (schema_breaks) when it would break a saved workflow's references.</summary>
    public SchemaSaveResult SaveSchema(string name, string? description, string schemaText)
    {
        var (n, schema) = PrepareSchema(name, schemaText);
        var breaks = Breaks(n, schema);
        if (breaks.Count > 0)
        {
            var lines = breaks.Select(b => $"- {b.Workflow} v{b.Version}, step {b.StepId ?? "(document)"}: uses {n}.{b.FieldPath}");
            throw new WorkflowLibraryException("schema_breaks",
                $"saving {n} would break {breaks.Count} reference(s) in saved workflows:\n" + string.Join("\n", lines),
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
        try { o = JsonNode.Parse(json) as JsonObject ?? throw new WorkflowLibraryException("bad_schema", "a schema file is a JSON object { name, description, schema }"); }
        catch (JsonException e) { throw new WorkflowLibraryException("bad_schema", "the schema file is not JSON: " + e.Message); }
        var name = Text(o, "name") ?? throw new WorkflowLibraryException("bad_schema", "a schema file needs a name");
        if (o["schema"] is not JsonObject schema) throw new WorkflowLibraryException("bad_schema", "a schema file needs its schema (a JSON object)");
        return SaveSchema(name, Text(o, "description"), schema.ToJsonString());
    }

    private (string Name, JsonObject Schema) PrepareSchema(string name, string schemaText)
    {
        var n = (name ?? "").Trim();
        if (!SchemaNamePattern.IsMatch(n)) throw new WorkflowLibraryException("bad_schema", "a schema's name starts with a letter and has letters, digits and _ (at most 64)");
        if (IsBuiltIn(n)) throw new WorkflowLibraryException("read_only", $"{n} is a built-in schema and cannot be changed");
        JsonObject schema;
        try { schema = JsonNode.Parse(schemaText) as JsonObject ?? throw new WorkflowLibraryException("bad_schema", "a schema is a JSON object"); }
        catch (JsonException e) { throw new WorkflowLibraryException("bad_schema", "the schema is not JSON: " + e.Message); }
        if (Text(schema, "type") != "object") throw new WorkflowLibraryException("bad_schema", "a schema is an object type: \"type\": \"object\"");
        try { WorkflowSchema.Parse(n, schema.ToJsonString()); }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new WorkflowLibraryException("bad_schema", e.Message); }
        return (n, schema);
    }

    /// <summary>
    /// Every saved workflow version a member could run (each workflow's latest, its runnable version and every version a member
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
            SELECT s.id, s.workflow_key, v.version, v.check_result FROM workflow_versions v JOIN workflows s ON s.id = v.workflow_id
            WHERE v.version = s.latest_version OR v.version = s.runnable_version
               OR v.version IN (SELECT m.workflow_version FROM members m WHERE m.kind = 'workflow' AND m.workflow_id = s.id)
            ORDER BY s.workflow_key, v.version;
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
        var c = Storage.Db.Open(_connectionString);
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
