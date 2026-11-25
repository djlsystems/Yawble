using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;
using Yawble.Core.Ipc;
using Yawble.Core.Scripts;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Teams;
using Xunit;

namespace Yawble.Core.Tests.Concierge;

/// <summary>
/// The Concierge's script tools through its dispatcher, onto a fake scripts.* / schemas.* RPC: every tool's
/// call and answer; the approvals (script_member_add and a script's first live run need the person's yes in a later turn,
/// building, checking and dry runs none); over the Cloud Link a member add and a live run are refused while a dry run passes.
/// </summary>
public sealed class ScriptToolsTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TeamsFixture _f = new();
    private BusEngine _bus = null!;
    private ConciergeStore _store = null!;
    private readonly ConfirmationBook _book = new();
    private readonly FakeScriptsRpc _rpc = new();
    private long _alpha;
    private long _adaId;

    public async ValueTask InitializeAsync()
    {
        var (_, ada, _) = _f.Team();
        _alpha = ada.TeamId;
        _adaId = ada.Id;
        _bus = new BusEngine(_f.Db.OpenStore(), new UnavailableRunner(), new BusOptions { IdleOffer = false, Repos = _f.Repos });
        await _bus.StartAsync(Ct);
        _store = new ConciergeStore(_f.Db.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await _bus.DisposeAsync();
        _f.Dispose();
    }

    /// <summary>A new turn of conversation c1 each call (the person's yes is a later turn than the list).</summary>
    private ConciergeTools Tools(bool remote = false, bool byPerson = true, bool withActions = true)
    {
        var turn = new ConciergeTurn("c1", byPerson, remote);
        var empty = new Func<string, JsonObject?, CancellationToken, Task<JsonNode?>>((_, _, _) => Task.FromResult<JsonNode?>(new JsonObject()));
        var git = new HostGit(Path.Combine(_f.Db.Dir, "empty.gitconfig"));
        var actions = withActions
            ? new ConciergeActions(_bus, empty, empty, new ConciergeGit(git, _f.Repos.Pusher!), (_, _) => Task.FromResult<IReadOnlyList<ActionRepo>>([]), _book, turn: turn)
            : null;
        return new ConciergeTools(_bus, _store, _rpc.CallAsync, () => null, "c1", null, actions, remote, turn);
    }

    private async Task<string> Ok(string tool, JsonObject args, ConciergeTools? tools = null)
    {
        var (text, isError) = await (tools ?? Tools()).CallToolAsync(tool, args, Ct);
        Assert.False(isError, text);
        return text;
    }

    private async Task<string> Refused(string tool, JsonObject args, ConciergeTools? tools = null)
    {
        var (text, isError) = await (tools ?? Tools()).CallToolAsync(tool, args, Ct);
        Assert.True(isError, text);
        return text;
    }

    private static string Token(string text) => Regex.Match(text, "confirm=\"([0-9a-f]+)\"").Groups[1].Value;

    private static JsonObject With(JsonObject args, string key, JsonNode? value)
    {
        var copy = args.DeepClone().AsObject();
        copy[key] = value;
        return copy;
    }

    [Fact]
    public async Task VerifyBuildLoop()
    {
        var doc = new JsonObject { ["id"] = "inbox-alert", ["field"] = "txt" };
        Assert.Contains("unknown_field", await Ok("script_check", new JsonObject { ["document"] = doc.DeepClone() }));
        doc["field"] = "text";
        _rpc.Clean = true;
        Assert.Contains("check: ok", await Ok("script_check", new JsonObject { ["document"] = doc.DeepClone() }));
        Assert.Contains("it passes its check", await Ok("script_save", new JsonObject { ["document"] = doc.DeepClone() }));
        Assert.Contains("dry run", await Ok("script_test_run", new JsonObject { ["script"] = "inbox-alert", ["team"] = "alpha", ["dry_run"] = true }));
    }

    // ------------------------------------------------------------------ the list

    [Fact]
    public async Task ToolsList_OffersEveryScriptTool_AndTheListsClassifyThem()
    {
        var body = Encoding.UTF8.GetBytes(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list" }.ToJsonString());
        var reply = await Tools().HandleAsync(body, Ct);
        var names = JsonNode.Parse(reply.Json!)!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        string[] expected = ["schemas_list", "schema_get", "schema_save", "scripts_list", "script_get", "script_save", "script_check",
            "script_test_run", "script_runs", "script_run", "script_member_add",
            // TriggerAndActionToolsTests covers these two.
            "script_trigger_set", "integration_action_allow"];
        foreach (var n in expected)
        {
            Assert.Contains(n, names);
            Assert.Contains(n, ConciergeTools.ToolNames);
        }
        Assert.Equal(expected, ScriptTools.Names);
        Assert.Equal(["schema_save", "script_save", "script_test_run", "script_member_add", "script_trigger_set", "integration_action_allow"], ScriptTools.Acting);
        foreach (var n in ScriptTools.Acting) Assert.Contains(n, ConciergeTools.Acting);
        Assert.Contains("script_member_add", ConciergeTools.LocalOnly);
        Assert.DoesNotContain("script_test_run", ConciergeTools.LocalOnly); // a dry run stays allowed; a live one is refused by its args
        Assert.Equal(ScriptToolApproval.PersonsYes, ScriptTools.ApprovalOf("script_member_add"));
        Assert.Equal(ScriptToolApproval.FirstLiveRun, ScriptTools.ApprovalOf("script_test_run"));
        Assert.Equal(ScriptToolApproval.None, ScriptTools.ApprovalOf("script_save"));
        var testRun = ScriptTools.Definitions().OfType<JsonObject>().Single(d => d["name"]!.GetValue<string>() == "script_test_run");
        Assert.Equal(["script", "team"], testRun["inputSchema"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.NotNull(testRun["inputSchema"]!["properties"]!["dry_run"]);
        Assert.NotNull(testRun["inputSchema"]!["properties"]!["confirm"]);
    }

    // ------------------------------------------------------------------ each tool

    [Fact]
    public async Task SchemaTools_ListGetAndSave_AndASaveThatBreaksIsNotSaved()
    {
        var list = await Ok("schemas_list", []);
        Assert.Contains("- EmailMessage v1 (built-in", list);
        Assert.Contains("- Alert v2: An alert", list);

        var got = await Ok("schema_get", new JsonObject { ["name"] = "Alert", ["version"] = 1 });
        Assert.Equal("Alert", _rpc.Last("schemas.get")["name"]!.GetValue<string>());
        Assert.Equal(1, _rpc.Last("schemas.get")["version"]!.GetValue<int>());
        Assert.Contains("\"schema\"", got);

        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } } };
        var saved = await Ok("schema_save", new JsonObject { ["name"] = "Alert", ["description"] = "An alert", ["schema"] = schema.DeepClone() });
        Assert.Equal("ok: saved schema Alert version 3; breaks: none.", saved);
        Assert.Equal(schema.ToJsonString(), _rpc.Last("schemas.save")["schema"]!.ToJsonString());

        _rpc.SchemaBreaks = [new JsonObject { ["scriptId"] = 1, ["script"] = "inbox-alert", ["version"] = 2, ["stepId"] = "send", ["fieldPath"] = "text" }];
        var broke = await Refused("schema_save", new JsonObject { ["name"] = "Alert", ["schema"] = "{\"type\":\"object\"}" });
        Assert.Contains("not saved", broke);
        Assert.Contains("- script inbox-alert version 2, step send, field text", broke);
        Assert.Equal(1, _rpc.Count("schemas.save")); // the breaking one never reached the save
    }

    [Fact]
    public async Task ScriptTools_ListGetSaveCheck_GiveIssuesWithStepCodeAndMessage()
    {
        var list = await Ok("scripts_list", []);
        Assert.Contains("- inbox-alert (Inbox alert): latest v2 (a draft: fails its check), runnable v1; used by Alpha/Poller.", list);

        var got = await Ok("script_get", new JsonObject { ["script"] = "inbox-alert" });
        Assert.StartsWith("inbox-alert version 2 (a draft: fails its check)", got);
        Assert.Contains("- step send: unknown_field: the field 'txt' is not in Alert", got);
        Assert.Contains("document: {", got);

        var doc = new JsonObject { ["format"] = 1, ["id"] = "inbox-alert", ["steps"] = new JsonArray() };
        var saved = await Ok("script_save", new JsonObject { ["document"] = doc.DeepClone() });
        Assert.StartsWith("saved inbox-alert version 3: a DRAFT", saved);
        Assert.Contains("1 issue(s)\n- step send: unknown_field: the field 'txt' is not in Alert", saved);
        Assert.Contains("\"stepId\":\"send\",\"code\":\"unknown_field\"", saved); // the check as JSON too
        Assert.Equal(doc.ToJsonString(), _rpc.Last("scripts.save")["document"]!.ToJsonString());

        var asText = await Ok("script_check", new JsonObject { ["document"] = doc.ToJsonString() });
        Assert.StartsWith("check: 1 issue(s)", asText);
        Assert.Equal(doc.ToJsonString(), _rpc.Last("scripts.check")["document"]!.GetValue<string>()); // JSON text passed on as given
        Assert.Equal(0, _rpc.Count("scripts.save") - 1); // checking saved nothing

        _rpc.Clean = true;
        Assert.StartsWith("check: ok, no issues", await Ok("script_check", new JsonObject { ["document"] = doc.DeepClone() }));
        Assert.Contains("it passes its check", await Ok("script_save", new JsonObject { ["document"] = doc.DeepClone() }));
    }

    [Fact]
    public async Task DryRun_IsTheDefault_NeedsNoYes_AndGivesStepsAndLog()
    {
        var args = new JsonObject
        {
            ["script"] = "inbox-alert", ["team"] = "alpha", ["version"] = 2, ["input"] = new JsonObject { ["subject"] = "hi" },
            ["settings"] = "{\"threshold\":3}", ["bindings"] = new JsonObject { ["mail"] = "Inbox" },
        };
        var text = await Ok("script_test_run", args);
        Assert.StartsWith("run 7 on Alpha (dry run: nothing was sent or written): succeeded", text);
        Assert.Contains("\"stepId\":\"fetch\"", text);
        Assert.Contains("\n  fetched 1 message", text);
        var p = _rpc.Last("scripts.testRun");
        Assert.True(p["dryRun"]!.GetValue<bool>());
        Assert.Equal(_alpha, p["teamId"]!.GetValue<long>());
        Assert.Equal(2, p["version"]!.GetValue<int>());
        Assert.Equal(3, p["settings"]!["threshold"]!.GetValue<int>()); // JSON text settings parsed to an object
        Assert.Equal("Inbox", p["bindings"]!["mail"]!.GetValue<string>());
        Assert.Equal("hi", p["input"]!["subject"]!.GetValue<string>());

        Assert.StartsWith("run 7", await Ok("script_test_run", With(args, "dry_run", true)));
        Assert.Equal(2, _rpc.Count("scripts.testRun"));
    }

    [Fact]
    public async Task Runs_ByTeamAndMemberName_AndOneRunInFull()
    {
        var all = await Ok("script_runs", new JsonObject { ["team"] = "Alpha" });
        Assert.Contains("- run 5: digest v1 failed by Poller, trigger trigger:4", all);
        Assert.Contains("; failed at step send: the integration refused", all);
        Assert.Contains("- run 6: inbox-alert v2 succeeded (test, dry run) by a test run", all);
        Assert.Equal(_alpha, _rpc.Last("scripts.runs")["teamId"]!.GetValue<long>());
        Assert.Null(_rpc.Last("scripts.runs")["memberId"]);
        Assert.Equal(20, _rpc.Last("scripts.runs")["limit"]!.GetValue<int>());

        await Ok("script_runs", new JsonObject { ["team"] = "Alpha", ["member"] = "ada", ["limit"] = 5 });
        Assert.Equal(_adaId, _rpc.Last("scripts.runs")["memberId"]!.GetValue<long>());
        Assert.Contains("has no member 'Nobody'", await Refused("script_runs", new JsonObject { ["team"] = "Alpha", ["member"] = "Nobody" }));
        Assert.Contains("there is no team 'Omega'", await Refused("script_runs", new JsonObject { ["team"] = "Omega" }));

        var one = await Ok("script_run", new JsonObject { ["id"] = 5 });
        Assert.Equal(5, _rpc.Last("scripts.run")["id"]!.GetValue<long>());
        Assert.Contains("\"log\":[{", one);
        Assert.Contains("refused (not_found)", await Refused("script_run", new JsonObject { ["id"] = 404 }));
    }

    // ------------------------------------------------------------------ approvals

    [Fact]
    public async Task MemberAdd_NeedsThePersonsYesInALaterTurn_ThenAddsOnce()
    {
        var args = new JsonObject
        {
            ["team"] = "Alpha", ["name"] = "Mailer", ["script"] = "inbox-alert",
            ["settings"] = new JsonObject { ["threshold"] = 3 }, ["bindings"] = new JsonObject { ["mail"] = "Inbox" },
        };
        var shown = await Ok("script_member_add", args);
        Assert.StartsWith("NOT DONE", shown);
        Assert.Contains("Add script member Mailer to team Alpha: it runs the script inbox-alert, the latest clean version (now 1", shown);
        Assert.Contains("Bindings (the team's integrations it uses): {\"mail\":\"Inbox\"}", shown);
        Assert.Equal(0, _rpc.Count("scripts.memberAdd"));

        var turn = Tools();
        var token = Token(shown);
        // The same turn that showed the list cannot say yes.
        var sameTurn = Tools();
        var first = await Ok("script_member_add", args, sameTurn);
        Assert.Contains("refused: the person has not said yes yet", await Refused("script_member_add", With(args, "confirm", Token(first)), sameTurn));
        Assert.Equal(0, _rpc.Count("scripts.memberAdd"));

        // A changed list voids the yes.
        var changed = await Ok("script_member_add", With(With(args, "name", "Other"), "confirm", token), turn);
        Assert.StartsWith("NOT DONE. THE LIST CHANGED", changed);
        Assert.Equal(0, _rpc.Count("scripts.memberAdd"));

        var again = await Ok("script_member_add", args);
        var done = await Ok("script_member_add", With(args, "confirm", Token(again)));
        Assert.StartsWith("ok: added script member Alpha/Mailer (member 99) running inbox-alert, the latest clean version (now 1).", done);
        Assert.Equal(1, _rpc.Count("scripts.memberAdd"));
        var p = _rpc.Last("scripts.memberAdd");
        Assert.Equal(_alpha, p["teamId"]!.GetValue<long>());
        Assert.Equal("Mailer", p["name"]!.GetValue<string>());
        Assert.Equal("Inbox", p["bindings"]!["mail"]!.GetValue<string>());

        // The token is used up.
        Assert.StartsWith("NOT DONE. That confirmation is unknown", await Ok("script_member_add", With(args, "confirm", Token(again))));
        Assert.Equal(1, _rpc.Count("scripts.memberAdd"));
    }

    [Fact]
    public async Task FirstLiveRun_NeedsTheYes_LaterLiveRunsAndDryRunsDoNot()
    {
        var live = new JsonObject { ["script"] = "inbox-alert", ["team"] = "Alpha", ["dry_run"] = false };
        var shown = await Ok("script_test_run", live);
        Assert.StartsWith("NOT DONE", shown);
        Assert.Contains("Run the script inbox-alert version 2 LIVE on team Alpha: the first live run of it there.", shown);
        Assert.Equal(0, _rpc.Count("scripts.testRun"));

        await Ok("script_test_run", With(live, "dry_run", null)); // a dry run meanwhile needs nothing
        Assert.Equal(1, _rpc.Count("scripts.testRun"));

        var done = await Ok("script_test_run", With(live, "confirm", Token(shown)));
        Assert.StartsWith("ok: run 7 on Alpha (LIVE): succeeded", done);
        Assert.False(_rpc.Last("scripts.testRun")["dryRun"]!.GetValue<bool>());

        // Now the script has had a live run on Alpha: the next live run needs no yes.
        var next = await Ok("script_test_run", With(live, "dry_run", "false"));
        Assert.StartsWith("run 7 on Alpha (LIVE)", next);
        Assert.Equal(3, _rpc.Count("scripts.testRun"));
    }

    [Fact]
    public async Task BuildingCheckingAndDryRuns_NeedNoYes_WhileAMemberAddWithoutActionsIsRefused()
    {
        var tools = Tools(withActions: false);
        Assert.StartsWith("saved", await Ok("script_save", new JsonObject { ["document"] = new JsonObject { ["id"] = "inbox-alert" } }, tools));
        Assert.StartsWith("check:", await Ok("script_check", new JsonObject { ["document"] = new JsonObject { ["id"] = "inbox-alert" } }, tools));
        Assert.StartsWith("run 7", await Ok("script_test_run", new JsonObject { ["script"] = "inbox-alert", ["team"] = "Alpha" }, tools));
        var refused = await Refused("script_member_add", new JsonObject { ["team"] = "Alpha", ["name"] = "Mailer", ["script"] = "inbox-alert" }, tools);
        Assert.Contains("needs the person's yes", refused);
        Assert.Equal(0, _rpc.Count("scripts.memberAdd"));
    }

    [Fact]
    public async Task AReviewTurn_ReadsButChangesNothing()
    {
        var review = Tools(byPerson: false);
        Assert.StartsWith("inbox-alert version 2", await Ok("script_get", new JsonObject { ["script"] = "inbox-alert" }, review));
        Assert.StartsWith("check:", await Ok("script_check", new JsonObject { ["document"] = new JsonObject() }, review));
        foreach (var (tool, args) in new (string, JsonObject)[]
                 {
                     ("script_save", new JsonObject { ["document"] = new JsonObject() }),
                     ("schema_save", new JsonObject { ["name"] = "Alert", ["schema"] = new JsonObject() }),
                     ("script_test_run", new JsonObject { ["script"] = "inbox-alert", ["team"] = "Alpha" }),
                     ("script_member_add", new JsonObject { ["team"] = "Alpha", ["name"] = "Mailer", ["script"] = "inbox-alert" }),
                 })
            Assert.Contains("was not started by a message from the person", await Refused(tool, args, review));
        Assert.Equal(0, _rpc.Count("scripts.save") + _rpc.Count("schemas.save") + _rpc.Count("scripts.testRun") + _rpc.Count("scripts.memberAdd"));
    }

    // ------------------------------------------------------------------ the Cloud Link

    [Fact]
    public async Task OverTheCloudLink_AMemberAddAndALiveRunAreRefused_ADryRunPasses()
    {
        var remote = Tools(remote: true);
        var add = new JsonObject { ["team"] = "Alpha", ["name"] = "Mailer", ["script"] = "inbox-alert" };
        Assert.Contains("refused: script_member_add is only for the person at this computer", await Refused("script_member_add", add, remote));

        // Even with a token the person made at the computer.
        var shown = await Ok("script_member_add", add);
        Assert.Contains("only for the person at this computer", await Refused("script_member_add", With(add, "confirm", Token(shown)), Tools(remote: true)));

        var live = new JsonObject { ["script"] = "inbox-alert", ["team"] = "Alpha", ["dry_run"] = false };
        Assert.Contains("refused: script_test_run with dry_run false (a live run) is only for the person at this computer", await Refused("script_test_run", live, remote));
        Assert.Equal(0, _rpc.Count("scripts.testRun"));
        Assert.Equal(0, _rpc.Count("scripts.memberAdd"));

        Assert.StartsWith("run 7 on Alpha (dry run", await Ok("script_test_run", With(live, "dry_run", true), remote));
        Assert.StartsWith("run 7 on Alpha (dry run", await Ok("script_test_run", With(live, "dry_run", null), remote));
        Assert.StartsWith("saved", await Ok("script_save", new JsonObject { ["document"] = new JsonObject() }, remote));
        Assert.True(ScriptTools.RemoteRefused("script_test_run", live));
        Assert.False(ScriptTools.RemoteRefused("script_test_run", With(live, "dry_run", true)));
    }

    /// <summary>A fake scripts.* / schemas.* RPC: canned shapes as scripts-rpc.md gives them, every call recorded.</summary>
    private sealed class FakeScriptsRpc
    {
        private readonly List<(string Method, JsonObject Params)> _calls = [];
        private readonly List<JsonObject> _liveRuns = [];
        public JsonArray SchemaBreaks { get; set; } = [];
        public bool Clean { get; set; }

        public int Count(string method) => _calls.Count(c => c.Method == method);
        public JsonObject Last(string method) => _calls.Last(c => c.Method == method).Params;

        private JsonObject Check() => Clean
            ? new JsonObject { ["ok"] = true, ["issues"] = new JsonArray(), ["schemaReferences"] = new JsonArray() }
            : new JsonObject
            {
                ["ok"] = false,
                ["issues"] = new JsonArray(new JsonObject { ["stepId"] = "send", ["code"] = "unknown_field", ["message"] = "the field 'txt' is not in Alert" }),
                ["schemaReferences"] = new JsonArray(),
            };

        public Task<JsonNode?> CallAsync(string method, JsonObject? p, CancellationToken ct)
        {
            p = (JsonObject?)p?.DeepClone() ?? [];
            _calls.Add((method, p));
            JsonNode? result = method switch
            {
                "schemas.list" => new JsonArray(
                    new JsonObject { ["name"] = "EmailMessage", ["description"] = "An email", ["latestVersion"] = 1, ["builtIn"] = true },
                    new JsonObject { ["name"] = "Alert", ["description"] = "An alert", ["latestVersion"] = 2, ["builtIn"] = false }),
                "schemas.get" => new JsonObject { ["name"] = p["name"]?.GetValue<string>(), ["version"] = 1, ["builtIn"] = false, ["schema"] = new JsonObject { ["type"] = "object" } },
                "schemas.check" => new JsonObject { ["ok"] = SchemaBreaks.Count == 0, ["breaks"] = SchemaBreaks.DeepClone() },
                "schemas.save" => new JsonObject { ["name"] = p["name"]?.GetValue<string>(), ["version"] = 3, ["breaks"] = new JsonArray() },
                "scripts.list" => new JsonArray(new JsonObject
                {
                    ["id"] = 1, ["key"] = "inbox-alert", ["name"] = "Inbox alert", ["description"] = "", ["latestVersion"] = 2, ["runnableVersion"] = 1, ["draft"] = true,
                    ["usedBy"] = new JsonArray(new JsonObject { ["teamId"] = 1, ["team"] = "Alpha", ["memberId"] = 9, ["member"] = "Poller", ["version"] = null }),
                }),
                "scripts.get" => p["script"]?.GetValue<string>() == "inbox-alert"
                    ? new JsonObject { ["id"] = 11, ["key"] = "inbox-alert", ["version"] = p["version"]?.GetValue<int>() ?? 2, ["ok"] = false, ["document"] = new JsonObject { ["id"] = "inbox-alert" }, ["check"] = Check() }
                    : throw new RpcException("not_found", "there is no script"),
                "scripts.save" => new JsonObject { ["id"] = 1, ["key"] = "inbox-alert", ["version"] = 3, ["ok"] = Clean, ["draft"] = !Clean, ["check"] = Check() },
                "scripts.check" => Check(),
                "scripts.testRun" => TestRun(p),
                "scripts.runs" => Runs(),
                "scripts.run" => p["id"]?.GetValue<long>() == 5
                    ? new JsonObject { ["id"] = 5, ["status"] = "failed", ["steps"] = new JsonArray(), ["log"] = new JsonArray(new JsonObject { ["at"] = "2026-11-23T10:00:00Z", ["line"] = "refused" }) }
                    : throw new RpcException("not_found", "there is no script run"),
                "scripts.memberAdd" => new JsonObject
                {
                    ["memberId"] = 99, ["teamId"] = p["teamId"]?.DeepClone(), ["team"] = "Alpha", ["name"] = p["name"]?.GetValue<string>(), ["scriptId"] = 1,
                    ["script"] = "inbox-alert", ["version"] = null, ["runsVersion"] = 1, ["settings"] = p["settings"]?.DeepClone(), ["bindings"] = p["bindings"]?.DeepClone(),
                },
                _ => throw new RpcException("unknown_method", method),
            };
            return Task.FromResult<JsonNode?>(result);
        }

        private JsonObject TestRun(JsonObject p)
        {
            var dry = p["dryRun"]?.GetValue<bool>() ?? true;
            if (!dry) _liveRuns.Add(new JsonObject { ["id"] = 7, ["script"] = "inbox-alert", ["version"] = 2, ["status"] = "succeeded", ["dryRun"] = false, ["test"] = true });
            return new JsonObject
            {
                ["runId"] = 7, ["status"] = "succeeded", ["failedStepId"] = null, ["reason"] = null, ["outputs"] = new JsonObject { ["sent"] = 0 },
                ["steps"] = new JsonArray(new JsonObject { ["seq"] = 1, ["stepId"] = "fetch", ["type"] = "email.list", ["status"] = "succeeded" }),
                ["log"] = new JsonArray("fetched 1 message"),
            };
        }

        private JsonArray Runs()
        {
            var list = new JsonArray(
                new JsonObject
                {
                    ["id"] = 5, ["script"] = "digest", ["version"] = 1, ["status"] = "failed", ["member"] = "Poller", ["trigger"] = "trigger:4", ["dryRun"] = false,
                    ["test"] = false, ["failedStepId"] = "send", ["reason"] = "the integration refused", ["startedAt"] = "2026-11-23T10:00:00Z",
                },
                new JsonObject
                {
                    ["id"] = 6, ["script"] = "inbox-alert", ["version"] = 2, ["status"] = "succeeded", ["member"] = null, ["trigger"] = "test", ["dryRun"] = true,
                    ["test"] = true, ["startedAt"] = "2026-11-23T11:00:00Z",
                });
            foreach (var r in _liveRuns) list.Add(r.DeepClone());
            return list;
        }
    }
}
