using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Scripts.Language;
using Yawble.Core.Scripts.Library;
using Yawble.Core.Scripts.Running;

namespace Yawble.Core.Ui;

/// <summary>
/// The script library, schemas, script runs and script members over IPC (UiRpc hands every "scripts." and "schemas."
/// method here). The names and shapes are in the team documents' scripts-rpc.md. A Visitor (Cloud Link, read-only) may call
/// only the reads (<see cref="Reads"/>, listed in Link.RemoteAccess); everything else is refused there for a Visitor.
/// </summary>
public sealed class ScriptsRpc(BusEngine bus, ScriptLibrary library, ScriptRunStore store, ScriptRunner runner)
{
    public static readonly string[] Reads =
    [
        "scripts.list", "scripts.get", "scripts.check", "scripts.versions", "scripts.export", "scripts.runs", "scripts.run",
        "scripts.memberGet", "scripts.memberState", "scripts.memberFiles", "scripts.catalog", "scripts.useRealNames",
        "schemas.list", "schemas.get", "schemas.check", "schemas.versions", "schemas.export", "schemas.usage",
    ];

    public static readonly string[] Changes =
    [
        "scripts.save", "scripts.import", "scripts.delete", "scripts.testRun", "scripts.memberAdd", "scripts.memberUpdate", "scripts.memberResetVariable",
        "schemas.save", "schemas.import",
    ];

    public static bool Handles(string method) =>
        method.StartsWith("scripts.", StringComparison.Ordinal) || method.StartsWith("schemas.", StringComparison.Ordinal);

    public async Task<JsonNode?> DispatchAsync(string method, JsonObject? p, CancellationToken ct)
    {
        try
        {
            return method switch
            {
                "scripts.list" => new JsonArray(library.Scripts().Select(s => (JsonNode?)Summary(s)).ToArray()),
                "scripts.get" => Version(library.Get(ScriptOf(p), OptInt(p, "version"))),
                "scripts.save" => Saved(library.Save(DocumentText(p, "document"))),
                "scripts.check" => library.Check(DocumentText(p, "document")),
                "scripts.versions" => new JsonArray(library.Versions(ScriptOf(p)).Select(v => (JsonNode?)new JsonObject
                    { ["version"] = v.Version, ["ok"] = v.Ok, ["createdAt"] = v.CreatedAt }).ToArray()),
                "scripts.export" => File(library.Export(ScriptOf(p), OptInt(p, "version"))),
                "scripts.import" => Saved(library.Import(Str(p, "json"))),
                "scripts.delete" => Deleted(ScriptOf(p)),
                "scripts.catalog" => Catalog(OptLong(p, "teamId")),
                "scripts.useRealNames" => RealNames(p),

                "schemas.list" => new JsonArray(library.Schemas().Select(s => (JsonNode?)new JsonObject
                    { ["name"] = s.Name, ["description"] = s.Description, ["latestVersion"] = s.LatestVersion, ["builtIn"] = s.BuiltIn, ["updatedAt"] = s.UpdatedAt }).ToArray()),
                "schemas.get" => Schema(library.GetSchema(Str(p, "name"), OptInt(p, "version"))),
                "schemas.save" => SchemaSaved(library.SaveSchema(Str(p, "name"), OptStr(p, "description"), DocumentText(p, "schema"))),
                "schemas.check" => SchemaCheck(library.CheckSchema(Str(p, "name"), DocumentText(p, "schema"))),
                "schemas.versions" => new JsonArray(library.SchemaVersions(Str(p, "name")).Select(v => (JsonNode?)new JsonObject
                    { ["version"] = v.Version, ["createdAt"] = v.CreatedAt }).ToArray()),
                "schemas.export" => File(library.ExportSchema(Str(p, "name"), OptInt(p, "version"))),
                "schemas.import" => SchemaSaved(library.ImportSchema(Str(p, "json"))),
                "schemas.usage" => Usage(Str(p, "name")),

                "scripts.testRun" => TestRun(await runner.TestRunAsync(new ScriptTestRun(ScriptOf(p), OptInt(p, "version"), p?["input"]?.DeepClone(),
                    OptObject(p, "settings"), OptObject(p, "bindings"), Long(p, "teamId"), p?["dryRun"] is JsonValue d && d.TryGetValue<bool>(out var dry) ? dry : true), ct)),
                "scripts.runs" => new JsonArray(store.RecentRuns(OptLong(p, "teamId"), OptLong(p, "memberId"), (int)(OptLong(p, "limit") ?? 100))
                    .Select(r => (JsonNode?)Run(r)).ToArray()),
                "scripts.run" => RunInFull(Long(p, "id")),

                "scripts.memberAdd" => await MemberAddAsync(p, ct),
                "scripts.memberUpdate" => MemberUpdate(p),
                "scripts.memberGet" => Member(MemberOf(p)),
                "scripts.memberState" => State(MemberOf(p).MemberId),
                "scripts.memberFiles" => Files(MemberOf(p).MemberId, (int)Math.Clamp(OptLong(p, "lines") ?? 30, 1, 200)),
                "scripts.memberResetVariable" => new JsonObject { ["reset"] = store.ResetVariable(MemberOf(p).MemberId, Str(p, "name")) },
                _ => throw new RpcException("unknown_method", $"no method named '{method}'"),
            };
        }
        catch (ScriptLibraryException e)
        {
            throw new RpcException(e.Code is "schema_breaks" or "not_found" or "read_only" or "in_use" ? e.Code : "bad_script", e.Message);
        }
        catch (ScriptRunRefused e)
        {
            throw new RpcException(e.Code, e.Message);
        }
        catch (BusRefusal e)
        {
            throw new RpcException(e.Code, e.Message);
        }
    }

    private JsonObject Deleted(long scriptId)
    {
        library.Delete(scriptId);
        return new JsonObject { ["deleted"] = true };
    }

    // ------------------------------------------------------------------ script members

    private async Task<JsonNode?> MemberAddAsync(JsonObject? p, CancellationToken ct)
    {
        var teamId = Long(p, "teamId");
        var scriptId = ScriptOf(p);
        var version = OptInt(p, "version");
        var settings = OptObject(p, "settings") ?? [];
        var bindings = OptObject(p, "bindings") ?? [];
        var timeout = OptInt(p, "timeoutMinutes");
        CheckMember(scriptId, version, settings, bindings, timeout, teamId);
        var row = await bus.AddScriptMemberAsync(teamId, Str(p, "name"), ct);
        try
        {
            store.SetScriptMember(row.Id, scriptId, version, settings, bindings, timeout);
        }
        catch
        {
            await bus.RemoveMemberAsync(row.Id, ct);
            throw;
        }
        return Member(store.Member(row.Id)!);
    }

    private JsonNode? MemberUpdate(JsonObject? p)
    {
        var member = MemberOf(p);
        var version = p is not null && p.ContainsKey("version") ? OptInt(p, "version") : member.Version;
        var settings = p is not null && p.ContainsKey("settings") ? OptObject(p, "settings") ?? [] : member.Settings;
        var bindings = p is not null && p.ContainsKey("bindings") ? OptObject(p, "bindings") ?? [] : member.Bindings;
        var timeout = p is not null && p.ContainsKey("timeoutMinutes") ? OptInt(p, "timeoutMinutes") : member.TimeoutMinutes;
        CheckMember(member.ScriptId, version, settings, bindings, timeout, member.TeamId);
        store.SetScriptMember(member.MemberId, member.ScriptId, version, settings, bindings, timeout);
        return Member(store.Member(member.MemberId)!);
    }

    /// <summary>
    /// A member runs only a version that passes its check; its settings and bindings must fit the script, and each integration it uses (the
    /// one the steps name, or the member's override) must be turned on for its team (an override also of the same kind).
    /// </summary>
    private void CheckMember(long scriptId, int? version, JsonObject settings, JsonObject bindings, int? timeout, long teamId)
    {
        if (timeout is < 1 or > 24 * 60) throw new RpcException("bad_script", "timeoutMinutes is 1 to 1440");
        var runs = version ?? library.RunnableVersion(scriptId)
            ?? throw new ScriptRunRefused("not_runnable", $"the script {library.Get(scriptId).Key} has no version that passes its check (only drafts)");
        var row = library.Get(scriptId, runs);
        if (!row.Ok) throw new ScriptRunRefused("not_runnable", $"version {runs} of {row.Key} is a draft: it does not pass its check");
        var team = store.TeamName(teamId) ?? $"team {teamId}";
        var problems = ScriptSettings.Problems(row.Document, settings, bindings, teamId, team, library.Integrations);
        if (problems.Count > 0) throw new RpcException("bad_script", string.Join("\n", problems));
    }

    // ------------------------------------------------------------------ the designer's catalog

    /// <summary>
    /// What a script can call and use: each of the person's integrations with its actions (Email: the fixed actions with their typed
    /// parameters; an API: its allowed and ask operations), turned on for the team or not (null without a team), and every function of the
    /// expression language.
    /// </summary>
    private JsonObject Catalog(long? teamId)
    {
        var integrations = new JsonArray();
        foreach (var i in library.Integrations.Integrations())
        {
            var actions = i.Kind == Scripts.Actions.EmailActions.Kind ? EmailCatalogActions() : ApiCatalogActions(i.Name, teamId);
            integrations.Add(new JsonObject
            {
                ["name"] = i.Name, ["kind"] = i.Kind, ["enabledForTeam"] = teamId is { } t ? i.IsOnFor(t) : null, ["actions"] = actions,
            });
        }
        return new JsonObject
        {
            ["integrations"] = integrations,
            ["functions"] = new JsonArray(ScriptFunctions.All.Select(f => (JsonNode?)new JsonObject
                { ["name"] = f.Name, ["signature"] = f.Signature, ["description"] = f.Description }).ToArray()),
        };
    }

    private static JsonArray EmailCatalogActions() => new(Scripts.Actions.EmailActions.All.Select(a =>
    {
        var props = a.Signature.Args["properties"] as JsonObject ?? [];
        var required = (a.Signature.Args["required"] as JsonArray ?? []).Select(r => (string?)r).ToHashSet();
        var parameters = new JsonArray(props.Select(pr => (JsonNode?)Param(pr.Key,
            Scripts.Language.ScriptType.FromJsonSchema(pr.Value, BuiltInFind).ToString(), required.Contains(pr.Key),
            pr.Value?["description"] is JsonValue d && d.TryGetValue<string>(out var text) ? text : null)).ToArray());
        return (JsonNode?)new JsonObject
        {
            ["name"] = a.Name, ["description"] = EmailActionText(a.Name), ["params"] = parameters, ["result"] = a.Signature.Result,
        };
    }).ToArray());

    private static Scripts.Language.ScriptSchema? BuiltInFind(string name) => Scripts.Language.BuiltInSchemas.Find(name);

    private static string EmailActionText(string action) => action switch
    {
        "list" => "Lists a folder's messages (for polling: after the last id you saved).",
        "get" => "Reads one message by its id.",
        "search" => "Searches a folder.",
        "thread" => "Reads a conversation's messages.",
        "markRead" => "Marks a message read (a change: a dry run does not do it).",
        "move" => "Moves a message to another folder (a change: a dry run does not do it).",
        _ => "",
    };

    private JsonArray ApiCatalogActions(string api, long? teamId) => new(library.Integrations.ApiOperations(api, teamId).Select(o =>
    {
        var action = new JsonObject
        {
            ["name"] = o.Action,
            ["description"] = o.Summary.Length > 0 ? o.Summary : $"{o.Method} {o.Route}",
            ["params"] = new JsonArray(o.Parameters.Select(x => (JsonNode?)Param(x.Name, ApiType(x.Type), x.Required, x.Description)).ToArray()),
            ["result"] = "ApiResponse",
        };
        if (o.Decision == "ask") action["ask"] = true;
        return (JsonNode?)action;
    }).ToArray());

    /// <summary>An API parameter's JSON type as a script type.</summary>
    private static string ApiType(string? jsonType) => jsonType switch
    {
        "integer" => "int",
        "number" => "float",
        "boolean" => "bool",
        "array" => "list<any>",
        "object" => "object",
        _ => "string",
    };

    private static JsonObject Param(string name, string type, bool required, string? description)
    {
        var o = new JsonObject { ["name"] = name, ["type"] = type, ["required"] = required };
        if (!string.IsNullOrWhiteSpace(description)) o["description"] = description;
        return o;
    }

    private JsonObject RealNames(JsonObject? p)
    {
        var bindings = OptObject(p, "bindings")?.Where(kv => kv.Value is JsonValue v && v.TryGetValue<string>(out _))
            .ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>(), StringComparer.Ordinal);
        var (document, renamed, unresolved) = library.UseRealNames(DocumentText(p, "document"), bindings);
        var map = new JsonObject();
        foreach (var (alias, real) in renamed) map[alias] = real;
        return new JsonObject
        {
            ["document"] = document, ["renamed"] = map, ["unresolved"] = new JsonArray(unresolved.Select(u => (JsonNode?)u).ToArray()),
        };
    }

    private JsonObject Usage(string name) => new()
    {
        ["scripts"] = new JsonArray(library.SchemaUsage(name).Select(u => (JsonNode?)new JsonObject
        {
            ["id"] = u.Id, ["name"] = u.Name, ["version"] = u.Version,
            ["variables"] = new JsonArray(u.Variables.Select(v => (JsonNode?)v).ToArray()),
            ["steps"] = new JsonArray(u.Steps.Select(s => (JsonNode?)s).ToArray()),
        }).ToArray()),
    };

    private ScriptMemberRow MemberOf(JsonObject? p) =>
        store.Member(Long(p, "memberId")) ?? throw new RpcException("not_found", "that member is not a script member");

    private JsonObject Member(ScriptMemberRow m)
    {
        var state = store.State(m.MemberId);
        var last = store.RecentRuns(null, m.MemberId, 1).FirstOrDefault();
        var runsVersion = m.Version ?? library.RunnableVersion(m.ScriptId);
        var document = library.Get(m.ScriptId, runsVersion).Document;
        var integrations = new JsonArray(ScriptSettings.MemberIntegrations(document, m.Bindings, library.Integrations).Select(i => (JsonNode?)new JsonObject
            { ["name"] = i.Name, ["kind"] = i.Kind, ["uses"] = i.Uses, ["overridden"] = i.Overridden }).ToArray());
        return new JsonObject
        {
            ["memberId"] = m.MemberId,
            ["teamId"] = m.TeamId,
            ["team"] = m.Team,
            ["name"] = m.Name,
            ["scriptId"] = m.ScriptId,
            ["script"] = library.Get(m.ScriptId).Key,
            ["version"] = m.Version,
            ["runsVersion"] = runsVersion,
            ["settings"] = m.Settings.DeepClone(),
            ["bindings"] = m.Bindings.DeepClone(),
            ["integrations"] = integrations,
            ["timeoutMinutes"] = m.TimeoutMinutes,
            ["consecutiveFailures"] = state.ConsecutiveFailures,
            ["running"] = runner.IsRunning(m.MemberId),
            ["lastRun"] = last is null ? null : Run(last),
            ["nextTriggerAt"] = store.NextTriggerAt(m.MemberId),
        };
    }

    private static readonly HashSet<string> TextFiles = new(StringComparer.OrdinalIgnoreCase) { ".log", ".txt", ".csv", ".md", ".jsonl", ".json" };
    private const int MaxListedFiles = 50, MaxTailBytes = 16 * 1024;

    /// <summary>
    /// The files a script member wrote in its data folder (newest first, at most 50): path inside the folder, size and time, and
    /// for a text file (.log, .txt, .csv, .md, .json, .jsonl) its last lines, read from at most its last 16 KB. Reads only.
    /// </summary>
    private JsonObject Files(long memberId, int lines)
    {
        var root = runner.MemberFolder(memberId);
        var list = new JsonArray();
        if (Directory.Exists(root))
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxListedFiles))
            {
                var rel = Path.GetRelativePath(root, f.FullName).Replace(Path.DirectorySeparatorChar, '/');
                var item = new JsonObject { ["path"] = rel, ["bytes"] = f.Length, ["modifiedAt"] = f.LastWriteTimeUtc.ToString("O") };
                if (TextFiles.Contains(f.Extension)) item["tail"] = new JsonArray(Tail(f.FullName, lines).Select(l => (JsonNode?)l).ToArray());
                list.Add(item);
            }
        return new JsonObject { ["memberId"] = memberId, ["files"] = list };
    }

    private static List<string> Tail(string path, int lines)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, fs.Length - MaxTailBytes);
            fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
            var all = reader.ReadToEnd().ReplaceLineEndings("\n").Split('\n').ToList();
            if (start > 0 && all.Count > 0) all.RemoveAt(0); // a cut first line
            while (all.Count > 0 && all[^1].Length == 0) all.RemoveAt(all.Count - 1);
            return all.Skip(Math.Max(0, all.Count - lines)).ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private JsonObject State(long memberId)
    {
        var s = store.State(memberId);
        return new JsonObject
        {
            ["memberId"] = memberId, ["variables"] = s.Variables.DeepClone(), ["consecutiveFailures"] = s.ConsecutiveFailures, ["updatedAt"] = s.UpdatedAt,
        };
    }

    // ------------------------------------------------------------------ shapes

    private static JsonObject Summary(ScriptSummary s) => new()
    {
        ["id"] = s.Id, ["key"] = s.Key, ["name"] = s.Name, ["description"] = s.Description, ["latestVersion"] = s.LatestVersion,
        ["runnableVersion"] = s.RunnableVersion, ["draft"] = s.Draft, ["updatedAt"] = s.UpdatedAt,
        ["usedBy"] = new JsonArray(s.UsedBy.Select(u => (JsonNode?)new JsonObject
            { ["teamId"] = u.TeamId, ["team"] = u.Team, ["memberId"] = u.MemberId, ["member"] = u.Member, ["version"] = u.Version }).ToArray()),
    };

    private static JsonObject Version(ScriptVersionRow v) => new()
    {
        ["id"] = v.Id, ["key"] = v.Key, ["name"] = v.Name, ["description"] = v.Description, ["version"] = v.Version, ["ok"] = v.Ok,
        ["document"] = v.Document.DeepClone(), ["check"] = v.Check.DeepClone(), ["createdAt"] = v.CreatedAt,
    };

    private static JsonObject Saved(ScriptSaveResult s) => new()
    {
        ["id"] = s.Id, ["key"] = s.Key, ["version"] = s.Version, ["ok"] = s.Ok, ["draft"] = s.Draft, ["check"] = s.Check.DeepClone(),
    };

    private static JsonObject Schema(SchemaVersionRow s) => new()
    {
        ["name"] = s.Name, ["description"] = s.Description, ["version"] = s.Version, ["builtIn"] = s.BuiltIn, ["schema"] = s.Schema.DeepClone(),
        ["createdAt"] = s.CreatedAt,
    };

    private static JsonObject SchemaSaved(SchemaSaveResult s) => new() { ["name"] = s.Name, ["version"] = s.Version, ["breaks"] = Breaks(s.Breaks) };

    private static JsonObject SchemaCheck(IReadOnlyList<SchemaBreak> breaks) => new() { ["ok"] = breaks.Count == 0, ["breaks"] = Breaks(breaks) };

    private static JsonArray Breaks(IReadOnlyList<SchemaBreak> breaks) => new(breaks.Select(b => (JsonNode?)new JsonObject
        { ["scriptId"] = b.ScriptId, ["script"] = b.Script, ["version"] = b.Version, ["stepId"] = b.StepId, ["fieldPath"] = b.FieldPath }).ToArray());

    private static JsonObject File(ExportFile f) => new() { ["fileName"] = f.FileName, ["json"] = f.Json };

    public static JsonObject Run(ScriptRunRow r) => new()
    {
        ["id"] = r.Id, ["teamId"] = r.TeamId, ["team"] = r.Team, ["memberId"] = r.MemberId, ["member"] = r.Member, ["scriptId"] = r.ScriptId,
        ["script"] = r.Script, ["version"] = r.Version, ["trigger"] = r.Trigger, ["status"] = r.Status, ["dryRun"] = r.DryRun, ["test"] = r.Test,
        ["input"] = r.Input?.DeepClone(), ["outputs"] = r.Outputs?.DeepClone(), ["failedStepId"] = r.FailedStepId, ["reason"] = r.Reason,
        ["startedAt"] = r.StartedAt, ["endedAt"] = r.EndedAt,
    };

    private static JsonArray Steps(IReadOnlyList<ScriptStepRow> steps) => new(steps.Select(s => (JsonNode?)new JsonObject
    {
        ["seq"] = s.Seq, ["stepId"] = s.StepId, ["type"] = s.Type, ["input"] = s.Input?.DeepClone(), ["output"] = s.Output?.DeepClone(),
        ["status"] = s.Status, ["truncated"] = s.Truncated, ["startedAt"] = s.StartedAt, ["endedAt"] = s.EndedAt,
    }).ToArray());

    private static JsonObject TestRun(ScriptRunOutcome o) => new()
    {
        ["runId"] = o.Run.Id, ["status"] = o.Run.Status, ["failedStepId"] = o.Run.FailedStepId, ["reason"] = o.Run.Reason,
        ["outputs"] = o.Run.Outputs?.DeepClone(), ["steps"] = Steps(o.Steps),
        ["log"] = new JsonArray(o.Log.Select(l => (JsonNode?)JsonValue.Create(l.Line)).ToArray()),
    };

    private JsonObject RunInFull(long id)
    {
        var run = store.Run(id) ?? throw new RpcException("not_found", $"there is no script run {id}");
        var o = Run(run);
        o["steps"] = Steps(store.Steps(id));
        o["log"] = new JsonArray(store.LogLines(id).Select(l => (JsonNode?)new JsonObject { ["at"] = l.At, ["line"] = l.Line }).ToArray());
        return o;
    }

    // ------------------------------------------------------------------ params

    private long ScriptOf(JsonObject? p) => p?["script"] switch
    {
        JsonValue v when UiRpc.WholeNumber(v, out var id) => library.ScriptId(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 0 => library.ScriptId(s),
        _ => throw new RpcException("bad_params", "script (its id or key) is required"),
    };

    /// <summary>A document given as an object or as JSON text.</summary>
    private static string DocumentText(JsonObject? p, string key) => p?[key] switch
    {
        JsonObject o => o.ToJsonString(),
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => throw new RpcException("bad_params", $"{key} is required (an object or JSON text)"),
    };

    private static JsonObject? OptObject(JsonObject? p, string key) => p?[key] switch
    {
        null => null,
        JsonObject o => (JsonObject)o.DeepClone(),
        _ => throw new RpcException("bad_params", $"{key} must be an object"),
    };

    private static string Str(JsonObject? p, string key) =>
        OptStr(p, key) ?? throw new RpcException("bad_params", $"{key} is required");

    private static string? OptStr(JsonObject? p, string key) => p?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? OptLong(JsonObject? p, string key) => p?[key] is JsonValue v && UiRpc.WholeNumber(v, out var l) ? l : null;

    private static int? OptInt(JsonObject? p, string key) => OptLong(p, key) is { } l ? checked((int)l) : null;

    private static long Long(JsonObject? p, string key) =>
        OptLong(p, key) ?? throw new RpcException("bad_params", $"{key} must be a number");
}
