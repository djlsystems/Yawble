using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Companion;
using Yawble.Core.Concierge;
using Yawble.Core.Ipc;
using Yawble.Core.Provenance;
using Yawble.Core.Scripts;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Companion;

/// <summary>
/// Card 601 Part 2 (B135): the script tools offered to a paired companion client. Reads at read, saves and dry runs at act,
/// a member add and a live run at destructive; a member add and a script's first live run on a team become requests the
/// window approves (checked with request_status, bound to the call's key: a changed call is a new request); a later live
/// run, a save, a check and a dry run run at once. A fake scripts.* RPC; no credentials.
/// </summary>
public sealed class CompanionScriptToolsTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDb _db = new();
    private readonly FakeScripts _rpc = new();
    private BusEngine _bus = null!;
    private CompanionClientStore _clients = null!;
    private AuditLog _audit = null!;
    private CompanionService _service = null!;

    public async ValueTask InitializeAsync()
    {
        _bus = new BusEngine(_db.OpenStore(), new UnavailableRunner(), new BusOptions { IdleOffer = false });
        await _bus.StartAsync(Ct);
        await _bus.CreateTeamAsync("alpha", "Manager", "claude", null, Ct);
        _clients = new CompanionClientStore(_db.ConnectionString);
        _audit = new AuditLog(_db.ConnectionString);
        _service = new CompanionService(_bus, new ConciergeStore(_db.ConnectionString), _rpc.CallAsync, (_, _) => null, _clients, _audit, new AppEvents());
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        await _bus.DisposeAsync();
        _db.Dispose();
    }

    private CompanionClient Client(CompanionLevel level, string name = "cc") => _clients.Add(name, level, "tester", out _);

    private static JsonObject MemberAdd() => new()
    {
        ["team"] = "alpha", ["name"] = "Poller", ["script"] = "inbox-alert",
        ["settings"] = new JsonObject { ["folder"] = "INBOX" }, ["bindings"] = new JsonObject { ["mail"] = "Work mail" },
    };

    private static JsonObject TestRun(bool? dry) => dry is { } d
        ? new JsonObject { ["team"] = "alpha", ["script"] = "inbox-alert", ["dry_run"] = d }
        : new JsonObject { ["team"] = "alpha", ["script"] = "inbox-alert" };

    private async Task<ActionStatus> Decide(string id, bool approve)
    {
        ActionStatus s;
        using (Actor.Use(Actor.Window)) s = await _service.DecideActionAsync(id, approve, Ct);
        return s.State == ActionState.Running && _service.RunOf(id) is { } run ? await run.WaitAsync(TimeSpan.FromSeconds(30), Ct) : s;
    }

    private async Task<string> RequestStatus(CompanionClient c, string id)
    {
        var r = await _service.CallAsync(c, "request_status", new JsonObject { ["id"] = id }, Ct);
        Assert.False(r.IsError, r.Text);
        return r.Text;
    }

    [Fact]
    public async Task VerifyBuildLoop()
    {
        var cc = Client(CompanionLevel.Act);
        var doc = new JsonObject { ["id"] = "inbox-alert", ["field"] = "txt" };
        var bad = await _service.CallAsync(cc, ScriptTools.ScriptCheck, new JsonObject { ["document"] = doc.DeepClone() }, Ct);
        Assert.Contains("unknown_field", bad.Text);
        doc["field"] = "text";
        var good = await _service.CallAsync(cc, ScriptTools.ScriptCheck, new JsonObject { ["document"] = doc.DeepClone() }, Ct);
        Assert.Contains("check: ok", good.Text);
        var save = await _service.CallAsync(cc, ScriptTools.ScriptSave, new JsonObject { ["document"] = doc.DeepClone() }, Ct);
        Assert.False(save.IsError, save.Text);
        var run = await _service.CallAsync(cc, ScriptTools.ScriptTestRun, TestRun(true), Ct);
        Assert.False(run.IsError, run.Text);
        Assert.Contains("dry run", run.Text);
        Assert.Null(run.RequestId);
    }

    // ---------------------------------------------------------------- script_member_add

    [Fact]
    public async Task MemberAdd_IsARequest_PendingUntilApproved_ThenRunsOnceExactlyAsShown()
    {
        var cc = Client(CompanionLevel.Destructive);
        var r = await _service.CallAsync(cc, ScriptTools.ScriptMemberAdd, MemberAdd(), Ct);

        Assert.False(r.IsError, r.Text);
        Assert.NotNull(r.RequestId);
        Assert.StartsWith($"NOT DONE: waiting for the person's approval in the Yawble window (request {r.RequestId}): ", r.Text);
        Assert.Contains("Add script member Poller to team alpha", r.Text);
        Assert.Contains("Work mail", r.Text);
        Assert.Equal(0, _rpc.Count("scripts.memberAdd"));
        Assert.Equal(1, _service.PendingCount);
        Assert.Contains("pending", await RequestStatus(cc, r.RequestId!));

        var s = await Decide(r.RequestId!, approve: true);

        Assert.Equal(ActionState.Done, s.State);
        Assert.Equal(1, _rpc.Count("scripts.memberAdd"));
        var p = _rpc.Last("scripts.memberAdd");
        Assert.Equal("Poller", p["name"]!.GetValue<string>());
        Assert.Equal("INBOX", p["settings"]!["folder"]!.GetValue<string>());
        Assert.Equal("Work mail", p["bindings"]!["mail"]!.GetValue<string>());
        var status = await RequestStatus(cc, r.RequestId!);
        Assert.Contains("done", status);
        Assert.Contains("added script member alpha/Poller", status);
        Assert.Contains(_audit.Recent(100), a => a.Method == "companion.script_member_add" && a.Outcome.StartsWith("approved by", StringComparison.Ordinal) && a.Outcome.EndsWith("done", StringComparison.Ordinal));

        // A second Approve runs nothing.
        await Decide(r.RequestId!, approve: true);
        Assert.Equal(1, _rpc.Count("scripts.memberAdd"));
    }

    [Fact]
    public async Task MemberAdd_Denied_AddsNothing()
    {
        var cc = Client(CompanionLevel.Destructive);
        var r = await _service.CallAsync(cc, ScriptTools.ScriptMemberAdd, MemberAdd(), Ct);

        var s = await Decide(r.RequestId!, approve: false);

        Assert.Equal(ActionState.Denied, s.State);
        Assert.Equal(0, _rpc.Count("scripts.memberAdd"));
        Assert.Contains("denied", await RequestStatus(cc, r.RequestId!));
        Assert.Equal(0, _service.PendingCount);
    }

    [Fact]
    public async Task MemberAdd_WhatItWouldRunMovedBeforeApproval_IsANewRequest_NothingAdded()
    {
        var cc = Client(CompanionLevel.Destructive);
        var r = await _service.CallAsync(cc, ScriptTools.ScriptMemberAdd, MemberAdd(), Ct);
        _rpc.Runnable = 2; // a new clean version: "the latest clean version" now means another one

        var s = await Decide(r.RequestId!, approve: true);

        Assert.Equal(ActionState.Changed, s.State);
        Assert.Equal(0, _rpc.Count("scripts.memberAdd"));
        Assert.NotNull(s.NextId);
        Assert.Contains("now 2", (await _service.CallAsync(cc, "request_status", new JsonObject { ["id"] = s.NextId }, Ct)).Text);

        var again = await Decide(s.NextId!, approve: true);
        Assert.Equal(ActionState.Done, again.State);
        Assert.Equal(1, _rpc.Count("scripts.memberAdd"));
    }

    [Fact]
    public async Task MemberAdd_AClientsConfirmIsIgnored_StillARequest()
    {
        var cc = Client(CompanionLevel.Destructive);
        var args = MemberAdd();
        args["confirm"] = "0123456789abcdef";

        var r = await _service.CallAsync(cc, ScriptTools.ScriptMemberAdd, args, Ct);

        Assert.NotNull(r.RequestId);
        Assert.Equal(0, _rpc.Count("scripts.memberAdd"));
    }

    // ---------------------------------------------------------------- live runs

    [Fact]
    public async Task FirstLiveRun_IsARequest_Approved_RunsLive_ThenASecondLiveRunNeedsNone()
    {
        var cc = Client(CompanionLevel.Destructive);
        var first = await _service.CallAsync(cc, ScriptTools.ScriptTestRun, TestRun(false), Ct);

        Assert.False(first.IsError, first.Text);
        Assert.NotNull(first.RequestId);
        Assert.Contains("LIVE on team alpha", first.Text);
        Assert.Equal(0, _rpc.Count("scripts.testRun"));
        Assert.Contains("pending", await RequestStatus(cc, first.RequestId!));

        var s = await Decide(first.RequestId!, approve: true);

        Assert.Equal(ActionState.Done, s.State);
        Assert.Equal(1, _rpc.Count("scripts.testRun"));
        Assert.False(_rpc.Last("scripts.testRun")["dryRun"]!.GetValue<bool>());
        Assert.Contains("LIVE", await RequestStatus(cc, first.RequestId!));

        var second = await _service.CallAsync(cc, ScriptTools.ScriptTestRun, TestRun(false), Ct);

        Assert.False(second.IsError, second.Text);
        Assert.Null(second.RequestId);
        Assert.StartsWith("run 7 on alpha (LIVE)", second.Text);
        Assert.Equal(2, _rpc.Count("scripts.testRun"));
        Assert.Equal(0, _service.PendingCount);
        Assert.Contains(_audit.Recent(100), a => a.Method == "companion.script_test_run" && a.Outcome == "ok");
    }

    [Fact]
    public async Task FirstLiveRun_Denied_RunsNothing_AndTheNextLiveRunStillNeedsAYes()
    {
        var cc = Client(CompanionLevel.Destructive);
        var first = await _service.CallAsync(cc, ScriptTools.ScriptTestRun, TestRun(false), Ct);

        Assert.Equal(ActionState.Denied, (await Decide(first.RequestId!, approve: false)).State);
        Assert.Equal(0, _rpc.Count("scripts.testRun"));

        var again = await _service.CallAsync(cc, ScriptTools.ScriptTestRun, TestRun(false), Ct);
        Assert.NotNull(again.RequestId);
        Assert.NotEqual(first.RequestId, again.RequestId);
        Assert.Equal(0, _rpc.Count("scripts.testRun"));
    }

    [Fact]
    public async Task FirstLiveRun_AChangedCallIsANewRequest()
    {
        var cc = Client(CompanionLevel.Destructive);
        var a = await _service.CallAsync(cc, ScriptTools.ScriptTestRun, TestRun(false), Ct);
        var changedArgs = TestRun(false);
        changedArgs["input"] = new JsonObject { ["subject"] = "other" };
        var b = await _service.CallAsync(cc, ScriptTools.ScriptTestRun, changedArgs, Ct);

        Assert.NotNull(a.RequestId);
        Assert.NotNull(b.RequestId);
        Assert.NotEqual(a.RequestId, b.RequestId);
        Assert.Contains("\"subject\":\"other\"", b.Text);
        // The same tool and target (the script on the team): the newer request replaces the older one (B141).
        Assert.Equal(1, _service.PendingCount);
        var old = _service.Status(cc, a.RequestId!)!;
        Assert.Equal(ActionState.Changed, old.State);
        Assert.Equal($"replaced by {b.RequestId}", old.Result);
        Assert.Equal(b.RequestId, old.NextId);
    }

    // ---------------------------------------------------------------- no request

    [Fact]
    public async Task SaveCheckAndDryRun_NeedNoRequest()
    {
        var cc = Client(CompanionLevel.Act);
        var doc = new JsonObject { ["id"] = "inbox-alert", ["steps"] = new JsonArray() };

        var save = await _service.CallAsync(cc, ScriptTools.ScriptSave, new JsonObject { ["document"] = doc.DeepClone() }, Ct);
        var check = await _service.CallAsync(cc, ScriptTools.ScriptCheck, new JsonObject { ["document"] = doc.DeepClone() }, Ct);
        var dryDefault = await _service.CallAsync(cc, ScriptTools.ScriptTestRun, TestRun(null), Ct);
        var dry = await _service.CallAsync(cc, ScriptTools.ScriptTestRun, TestRun(true), Ct);

        foreach (var r in new[] { save, check, dryDefault, dry })
        {
            Assert.False(r.IsError, r.Text);
            Assert.Null(r.RequestId);
            Assert.DoesNotContain("NOT DONE", r.Text);
        }
        Assert.StartsWith("saved inbox-alert version 3", save.Text);
        Assert.StartsWith("check: ok", check.Text);
        Assert.StartsWith("run 7 on alpha (dry run", dry.Text);
        Assert.Equal(1, _rpc.Count("scripts.save"));
        Assert.Equal(1, _rpc.Count("scripts.check"));
        Assert.Equal(2, _rpc.Count("scripts.testRun"));
        Assert.All(_rpc.All("scripts.testRun"), p => Assert.True(p["dryRun"]!.GetValue<bool>()));
        Assert.Equal(0, _service.PendingCount);
        Assert.Contains(_audit.Recent(100), a => a.Method == "companion.script_save" && a.Outcome == "ok");
    }

    [Fact]
    public async Task ReadsRunAtTheReadLevel()
    {
        var cc = Client(CompanionLevel.Read);
        foreach (var (tool, args) in new (string, JsonObject)[]
                 {
                     (ScriptTools.SchemasList, []), (ScriptTools.SchemaGet, new() { ["name"] = "Alert" }), (ScriptTools.ScriptsList, []),
                     (ScriptTools.ScriptGet, new() { ["script"] = "inbox-alert" }), (ScriptTools.ScriptCheck, new() { ["document"] = new JsonObject() }),
                     (ScriptTools.ScriptRuns, new() { ["team"] = "alpha" }), (ScriptTools.ScriptRun, new() { ["id"] = 5 }),
                 })
        {
            var r = await _service.CallAsync(cc, tool, args, Ct);
            Assert.False(r.IsError, tool + ": " + r.Text);
            Assert.Null(r.RequestId);
        }
    }

    // ---------------------------------------------------------------- levels and definitions

    [Fact]
    public async Task Levels_SavesAndDryRunsNeedAct_LiveRunsAndMemberAddsNeedDestructive()
    {
        Assert.Equal(CompanionLevel.Read, CompanionTools.LevelOf(ScriptTools.ScriptCheck, []));
        Assert.Equal(CompanionLevel.Act, CompanionTools.LevelOf(ScriptTools.ScriptSave, []));
        Assert.Equal(CompanionLevel.Act, CompanionTools.LevelOf(ScriptTools.SchemaSave, []));
        Assert.Equal(CompanionLevel.Act, CompanionTools.LevelOf(ScriptTools.ScriptTestRun, TestRun(null)));
        Assert.Equal(CompanionLevel.Destructive, CompanionTools.LevelOf(ScriptTools.ScriptTestRun, TestRun(false)));
        Assert.Equal(CompanionLevel.Destructive, CompanionTools.LevelOf(ScriptTools.ScriptMemberAdd, MemberAdd()));
        foreach (var name in ScriptTools.Names) Assert.NotNull(CompanionTools.LevelOf(name, []));

        var reader = Client(CompanionLevel.Read, "reader");
        var save = await _service.CallAsync(reader, ScriptTools.ScriptSave, new JsonObject { ["document"] = new JsonObject() }, Ct);
        Assert.True(save.IsError);
        Assert.Contains("needs the act level", save.Text);

        var actor = Client(CompanionLevel.Act, "actor");
        var live = await _service.CallAsync(actor, ScriptTools.ScriptTestRun, TestRun(false), Ct);
        var add = await _service.CallAsync(actor, ScriptTools.ScriptMemberAdd, MemberAdd(), Ct);
        Assert.True(live.IsError);
        Assert.Contains("needs the destructive level", live.Text);
        Assert.True(add.IsError);
        Assert.Equal(0, _rpc.Count("scripts.save") + _rpc.Count("scripts.testRun") + _rpc.Count("scripts.memberAdd"));
        Assert.Equal(0, _service.PendingCount);
    }

    [Fact]
    public void Definitions_OfferTheElevenByLevel_NoConfirmAndNoTokenWording()
    {
        static HashSet<string> Names(JsonArray defs) => [.. defs.Select(d => d!["name"]!.GetValue<string>())];
        var read = CompanionTools.Definitions(CompanionLevel.Read);
        var act = CompanionTools.Definitions(CompanionLevel.Act);
        var destructive = CompanionTools.Definitions(CompanionLevel.Destructive);

        Assert.Equal(13, ScriptTools.Names.Count); // the 11 of B135 and B139's script_trigger_set and integration_action_allow (CompanionTriggerAndActionToolsTests)
        Assert.All(ScriptTools.Names, n => Assert.Contains(n, Names(destructive)));
        Assert.Contains(ScriptTools.ScriptCheck, Names(read));
        Assert.DoesNotContain(ScriptTools.ScriptSave, Names(read));
        Assert.Contains(ScriptTools.ScriptTestRun, Names(act));
        Assert.DoesNotContain(ScriptTools.ScriptMemberAdd, Names(act));

        foreach (var defs in new[] { act, destructive })
        foreach (var def in defs.Where(d => ScriptTools.Handles(d!["name"]!.GetValue<string>())))
        {
            var text = def!.ToJsonString();
            Assert.False(def["inputSchema"]!["properties"]!.AsObject().ContainsKey("confirm"), def["name"] + " offers confirm");
            Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("confirm", text, StringComparison.OrdinalIgnoreCase);
        }
        var liveRun = destructive.Single(d => d!["name"]!.GetValue<string>() == ScriptTools.ScriptTestRun)!["description"]!.GetValue<string>();
        Assert.Contains("request_status", liveRun);
        var dryOnly = act.Single(d => d!["name"]!.GetValue<string>() == ScriptTools.ScriptTestRun)!["description"]!.GetValue<string>();
        Assert.Contains("may only dry-run", dryOnly);
        Assert.Contains("request_status", destructive.Single(d => d!["name"]!.GetValue<string>() == ScriptTools.ScriptMemberAdd)!["description"]!.GetValue<string>());
    }

    /// <summary>A fake scripts.* / schemas.* RPC: canned shapes as scripts-rpc.md gives them; a live test run is listed in scripts.runs.</summary>
    private sealed class FakeScripts
    {
        private readonly List<(string Method, JsonObject Params)> _calls = [];
        private readonly List<JsonObject> _liveRuns = [];
        public int Runnable { get; set; } = 1;

        public int Count(string method) { lock (_calls) return _calls.Count(c => c.Method == method); }
        public JsonObject Last(string method) { lock (_calls) return _calls.Last(c => c.Method == method).Params; }
        public List<JsonObject> All(string method) { lock (_calls) return [.. _calls.Where(c => c.Method == method).Select(c => c.Params)]; }

        private static JsonObject Clean() => new() { ["ok"] = true, ["issues"] = new JsonArray(), ["schemaReferences"] = new JsonArray() };

        public Task<JsonNode?> CallAsync(string method, JsonObject? p, CancellationToken ct)
        {
            p = (JsonObject?)p?.DeepClone() ?? [];
            lock (_calls) _calls.Add((method, p));
            JsonNode? result = method switch
            {
                "schemas.list" => new JsonArray(new JsonObject { ["name"] = "Alert", ["description"] = "An alert", ["latestVersion"] = 1, ["builtIn"] = false }),
                "schemas.get" => new JsonObject { ["name"] = "Alert", ["version"] = 1, ["schema"] = new JsonObject { ["type"] = "object" } },
                "scripts.list" => new JsonArray(new JsonObject
                {
                    ["id"] = 1, ["key"] = "inbox-alert", ["name"] = "Inbox alert", ["latestVersion"] = 2, ["runnableVersion"] = Runnable, ["draft"] = false, ["usedBy"] = new JsonArray(),
                }),
                "scripts.get" => new JsonObject { ["id"] = 11, ["key"] = "inbox-alert", ["version"] = 2, ["ok"] = true, ["document"] = new JsonObject { ["id"] = "inbox-alert" }, ["check"] = Clean() },
                "scripts.save" => new JsonObject { ["id"] = 1, ["key"] = "inbox-alert", ["version"] = 3, ["ok"] = true, ["draft"] = false, ["check"] = Clean() },
                "scripts.check" => p["document"]?["field"]?.GetValue<string>() == "txt" ? new JsonObject { ["ok"] = false, ["issues"] = new JsonArray(new JsonObject { ["stepId"] = "send", ["code"] = "unknown_field", ["message"] = "unknown field txt" }), ["schemaReferences"] = new JsonArray() } : Clean(),
                "scripts.testRun" => TestRun(p),
                "scripts.runs" => Runs(),
                "scripts.run" => new JsonObject { ["id"] = 5, ["status"] = "succeeded", ["steps"] = new JsonArray(), ["log"] = new JsonArray() },
                "scripts.memberAdd" => new JsonObject
                {
                    ["memberId"] = 99, ["teamId"] = p["teamId"]?.DeepClone(), ["team"] = "alpha", ["name"] = p["name"]?.GetValue<string>(), ["scriptId"] = 1,
                    ["script"] = "inbox-alert", ["version"] = null, ["runsVersion"] = Runnable,
                },
                _ => throw new RpcException("unknown_method", method),
            };
            return Task.FromResult<JsonNode?>(result);
        }

        private JsonObject TestRun(JsonObject p)
        {
            var dry = p["dryRun"]?.GetValue<bool>() ?? true;
            if (!dry) lock (_liveRuns) _liveRuns.Add(new JsonObject { ["id"] = 7, ["script"] = "inbox-alert", ["version"] = 2, ["status"] = "succeeded", ["dryRun"] = false, ["test"] = true });
            return new JsonObject
            {
                ["runId"] = 7, ["status"] = "succeeded", ["outputs"] = new JsonObject(), ["steps"] = new JsonArray(), ["log"] = new JsonArray("fetched 1 message"),
            };
        }

        private JsonArray Runs()
        {
            var list = new JsonArray(new JsonObject { ["id"] = 6, ["script"] = "inbox-alert", ["version"] = 2, ["status"] = "succeeded", ["dryRun"] = true, ["test"] = true, ["trigger"] = "test" });
            lock (_liveRuns) foreach (var r in _liveRuns) list.Add(r.DeepClone());
            return list;
        }
    }
}
