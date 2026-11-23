using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Scripts.Library;
using Yawble.Core.Scripts.Running;

namespace Yawble.Core.Ui;

/// <summary>
/// B131: the script library, schemas, script runs and script members over IPC (UiRpc hands every "scripts." and "schemas."
/// method here). The names and shapes are in the team documents' scripts-rpc.md. A Visitor (Cloud Link, read-only) may call
/// only the reads (<see cref="Reads"/>, listed in Link.RemoteAccess); everything else is refused there for a Visitor.
/// </summary>
public sealed class ScriptsRpc(BusEngine bus, ScriptLibrary library, ScriptRunStore store, ScriptRunner runner)
{
    public static readonly string[] Reads =
    [
        "scripts.list", "scripts.get", "scripts.check", "scripts.versions", "scripts.export", "scripts.runs", "scripts.run",
        "scripts.memberGet", "scripts.memberState",
        "schemas.list", "schemas.get", "schemas.check", "schemas.versions", "schemas.export",
    ];

    public static readonly string[] Changes =
    [
        "scripts.save", "scripts.import", "scripts.testRun", "scripts.memberAdd", "scripts.memberUpdate", "scripts.memberResetVariable",
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

                "schemas.list" => new JsonArray(library.Schemas().Select(s => (JsonNode?)new JsonObject
                    { ["name"] = s.Name, ["description"] = s.Description, ["latestVersion"] = s.LatestVersion, ["builtIn"] = s.BuiltIn, ["updatedAt"] = s.UpdatedAt }).ToArray()),
                "schemas.get" => Schema(library.GetSchema(Str(p, "name"), OptInt(p, "version"))),
                "schemas.save" => SchemaSaved(library.SaveSchema(Str(p, "name"), OptStr(p, "description"), DocumentText(p, "schema"))),
                "schemas.check" => SchemaCheck(library.CheckSchema(Str(p, "name"), DocumentText(p, "schema"))),
                "schemas.versions" => new JsonArray(library.SchemaVersions(Str(p, "name")).Select(v => (JsonNode?)new JsonObject
                    { ["version"] = v.Version, ["createdAt"] = v.CreatedAt }).ToArray()),
                "schemas.export" => File(library.ExportSchema(Str(p, "name"), OptInt(p, "version"))),
                "schemas.import" => SchemaSaved(library.ImportSchema(Str(p, "json"))),

                "scripts.testRun" => TestRun(await runner.TestRunAsync(new ScriptTestRun(ScriptOf(p), OptInt(p, "version"), p?["input"]?.DeepClone(),
                    OptObject(p, "settings"), OptObject(p, "bindings"), Long(p, "teamId"), p?["dryRun"] is JsonValue d && d.TryGetValue<bool>(out var dry) ? dry : true), ct)),
                "scripts.runs" => new JsonArray(store.RecentRuns(OptLong(p, "teamId"), OptLong(p, "memberId"), (int)(OptLong(p, "limit") ?? 100))
                    .Select(r => (JsonNode?)Run(r)).ToArray()),
                "scripts.run" => RunInFull(Long(p, "id")),

                "scripts.memberAdd" => await MemberAddAsync(p, ct),
                "scripts.memberUpdate" => MemberUpdate(p),
                "scripts.memberGet" => Member(MemberOf(p)),
                "scripts.memberState" => State(MemberOf(p).MemberId),
                "scripts.memberResetVariable" => new JsonObject { ["reset"] = store.ResetVariable(MemberOf(p).MemberId, Str(p, "name")) },
                _ => throw new RpcException("unknown_method", $"no method named '{method}'"),
            };
        }
        catch (ScriptLibraryException e)
        {
            throw new RpcException(e.Code is "schema_breaks" or "not_found" or "read_only" ? e.Code : "bad_script", e.Message);
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

    // ------------------------------------------------------------------ script members

    private async Task<JsonNode?> MemberAddAsync(JsonObject? p, CancellationToken ct)
    {
        var teamId = Long(p, "teamId");
        var scriptId = ScriptOf(p);
        var version = OptInt(p, "version");
        var settings = OptObject(p, "settings") ?? [];
        var bindings = OptObject(p, "bindings") ?? [];
        var timeout = OptInt(p, "timeoutMinutes");
        CheckMember(scriptId, version, settings, bindings, timeout);
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
        CheckMember(member.ScriptId, version, settings, bindings, timeout);
        store.SetScriptMember(member.MemberId, member.ScriptId, version, settings, bindings, timeout);
        return Member(store.Member(member.MemberId)!);
    }

    /// <summary>A member runs only a version that passes its check; its settings and bindings must fit the script.</summary>
    private void CheckMember(long scriptId, int? version, JsonObject settings, JsonObject bindings, int? timeout)
    {
        if (timeout is < 1 or > 24 * 60) throw new RpcException("bad_script", "timeoutMinutes is 1 to 1440");
        var runs = version ?? library.RunnableVersion(scriptId)
            ?? throw new ScriptRunRefused("not_runnable", $"the script {library.Get(scriptId).Key} has no version that passes its check (only drafts)");
        var row = library.Get(scriptId, runs);
        if (!row.Ok) throw new ScriptRunRefused("not_runnable", $"version {runs} of {row.Key} is a draft: it does not pass its check");
        var problems = ScriptSettings.Problems(row.Document, settings, bindings);
        if (problems.Count > 0) throw new RpcException("bad_script", string.Join("\n", problems));
    }

    private ScriptMemberRow MemberOf(JsonObject? p) =>
        store.Member(Long(p, "memberId")) ?? throw new RpcException("not_found", "that member is not a script member");

    private JsonObject Member(ScriptMemberRow m)
    {
        var state = store.State(m.MemberId);
        var last = store.RecentRuns(null, m.MemberId, 1).FirstOrDefault();
        return new JsonObject
        {
            ["memberId"] = m.MemberId,
            ["teamId"] = m.TeamId,
            ["team"] = m.Team,
            ["name"] = m.Name,
            ["scriptId"] = m.ScriptId,
            ["script"] = library.Get(m.ScriptId).Key,
            ["version"] = m.Version,
            ["runsVersion"] = m.Version ?? library.RunnableVersion(m.ScriptId),
            ["settings"] = m.Settings.DeepClone(),
            ["bindings"] = m.Bindings.DeepClone(),
            ["timeoutMinutes"] = m.TimeoutMinutes,
            ["consecutiveFailures"] = state.ConsecutiveFailures,
            ["running"] = runner.IsRunning(m.MemberId),
            ["lastRun"] = last is null ? null : Run(last),
        };
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
