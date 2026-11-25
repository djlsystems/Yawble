using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Bus;
using Yawble.Core.Scripts.Library;
using Yawble.Core.Scripts.Running;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;

namespace Yawble.Core.Tests.Scripts;

/// <summary>
/// The code and while steps through the library, the runner and the scripts RPC with the real checker and engine: scripts.check
/// and scripts.save (which back the Concierge's script_check and script_save) accept both steps and report a code syntax error
/// with its line and column; a Test run (dry or not) and a member's run run the code for real and loop with while.
/// </summary>
public class ScriptCodeWhileRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-24T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private const string Page = """
        { "type": "object", "properties": { "items": { "type": "array", "items": { "type": "string" } }, "cursor": { "type": "integer" } },
          "required": ["items"] }
        """;

    /// <summary>
    /// Pages through a made-up list with a while and a code step: each pass the code gives the next page and cursor, until the cursor
    /// reaches 3; then logs what it gathered.
    /// </summary>
    private static string PagingScript(string id, string code = "log('page ' + inputs.cursor);\nreturn { items: ['p' + inputs.cursor], cursor: inputs.cursor + 1 };") => $$"""
        { "format": "yawble-script/1", "id": "{{id}}", "name": "{{id}}", "trigger": { "accepts": ["once"] },
          "variables": [ { "name": "cursor", "type": "int", "initial": 0 }, { "name": "page", "type": "Page" }, { "name": "seen", "type": "string", "initial": "" } ],
          "start": "loop",
          "steps": [
            { "id": "loop", "type": "while", "when": { "left": "{vars.cursor}", "op": "lt", "right": 3 }, "body": "fetch", "then": "done", "max": 10 },
            { "id": "fetch", "type": "code", "inputs": { "cursor": "{vars.cursor}" }, "code": {{System.Text.Json.JsonSerializer.Serialize(code)}},
              "returns": "Page", "into": "page", "next": "keep" },
            { "id": "keep", "type": "set", "var": "seen", "value": "{vars.seen}{first(vars.page.items)}", "next": "move" },
            { "id": "move", "type": "set", "var": "cursor", "value": "{vars.page.cursor}", "next": null },
            { "id": "done", "type": "log", "text": "gathered {vars.seen}", "next": null } ] }
        """;

    private sealed class Rig : IAsyncDisposable
    {
        public readonly TempDb Db = new();
        public BusEngine Bus = null!;
        public ScriptLibrary Library = null!;
        public ScriptRunStore Store = null!;
        public ScriptRunner Runner = null!;
        public long Alpha;

        public async Task<Rig> InitAsync()
        {
            Bus = new BusEngine(Db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false });
            Alpha = await Bus.CreateTeamAsync("Alpha", ct: Ct);
            Library = new ScriptLibrary(Db.ConnectionString, () => FakeCatalog.MailAndSms()); // the real checker
            Library.SaveSchema("Page", "One page of a list.", Page);
            Store = new ScriptRunStore(Db.ConnectionString);
            Runner = new ScriptRunner(Store, Library, NoScriptActions.Instance, SeamScriptEngine.Instance, Path.Combine(Db.Dir, "scripts"), clock: new FakeClock(T0));
            Bus.ScriptMembers = Runner;
            await Bus.StartAsync(Ct);
            return this;
        }

        public ScriptsRpc Rpc() => new(Bus, Library, Store, Runner);

        public async ValueTask DisposeAsync()
        {
            await Runner.StopAsync();
            await Bus.DisposeAsync();
            Db.Dispose();
        }
    }

    private static string Why(ScriptRunRow run) => $"{run.Status} at {run.FailedStepId}: {run.Reason}";
    private static string Logged(ScriptRunOutcome o) => string.Join("\n", o.Log.Select(l => l.Line));

    [Fact]
    public async Task ScriptCheckAndSave_AcceptTheCodeAndWhileSteps()
    {
        await using var r = await new Rig().InitAsync();
        var rpc = r.Rpc();
        var check = (await rpc.DispatchAsync("scripts.check", new JsonObject { ["document"] = PagingScript("paging-check") }, Ct))!;
        Assert.True(check["ok"]!.GetValue<bool>(), check.ToJsonString());
        var saved = (await rpc.DispatchAsync("scripts.save", new JsonObject { ["document"] = PagingScript("paging-check") }, Ct))!;
        Assert.True(saved["ok"]!.GetValue<bool>(), saved.ToJsonString());
    }

    [Fact]
    public async Task ScriptCheck_ReportsACodeSyntaxError_WithLineAndColumn_OnTheStep()
    {
        await using var r = await new Rig().InitAsync();
        var bad = PagingScript("paging-bad", code: "const a = 1;\nreturn { items: [a, };");
        var check = (await r.Rpc().DispatchAsync("scripts.check", new JsonObject { ["document"] = bad }, Ct))!;
        Assert.False(check["ok"]!.GetValue<bool>());
        var issue = check["issues"]!.AsArray().Single()!;
        Assert.Equal("fetch", issue["stepId"]!.GetValue<string>());
        Assert.Contains("the code does not parse", issue["message"]!.GetValue<string>());
        Assert.Contains("(line 2, column", issue["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task ScriptCheck_RefusesAWhileMaxOver10000()
    {
        await using var r = await new Rig().InitAsync();
        var bad = PagingScript("paging-max").Replace("\"max\": 10", "\"max\": 10001", StringComparison.Ordinal);
        var check = (await r.Rpc().DispatchAsync("scripts.check", new JsonObject { ["document"] = bad }, Ct))!;
        Assert.False(check["ok"]!.GetValue<bool>());
        Assert.Contains("it is from 1 to 10,000", check.ToJsonString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATestRun_RunsTheCodeForReal_AndLoopsUntilTheConditionIsFalse(bool dryRun)
    {
        await using var r = await new Rig().InitAsync();
        var saved = r.Library.Save(PagingScript("paging"));
        Assert.True(saved.Ok, saved.Check.ToJsonString());

        var outcome = await r.Runner.TestRunAsync(new ScriptTestRun(saved.Id, null, ScriptPayloads.TriggerFired(1, "once", T0), null, null, r.Alpha, DryRun: dryRun), Ct);

        Assert.True(outcome.Succeeded, Why(outcome.Run));
        var log = Logged(outcome);
        Assert.Contains("page 0\npage 1\npage 2", log);
        Assert.Contains("gathered p0p1p2", log);
        Assert.Equal(3, outcome.Steps.Count(s => s.StepId == "fetch"));
        Assert.Equal(4, outcome.Steps.Count(s => s.StepId == "loop"));
    }

    [Fact]
    public async Task ACodeStepThatHitsALimit_FailsTheRun_WithTheReason()
    {
        await using var r = await new Rig().InitAsync();
        var saved = r.Library.Save(PagingScript("paging-deep", code: "function f(n) { return f(n + 1); }\nreturn f(0);"));
        Assert.True(saved.Ok, saved.Check.ToJsonString());
        var outcome = await r.Runner.TestRunAsync(new ScriptTestRun(saved.Id, null, ScriptPayloads.TriggerFired(1, "once", T0), null, null, r.Alpha), Ct);
        Assert.Equal(ScriptRunStatus.Failed, outcome.Run.Status);
        Assert.Equal("fetch", outcome.Run.FailedStepId);
        Assert.Contains("nested calls", outcome.Run.Reason);
    }
}
