using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;
using Yawble.Core.Bus;
using Yawble.Core.Scripts.Language;
using Yawble.Core.Scripts.Library;
using Yawble.Core.Scripts.Running;
using Yawble.Core.Skills;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;

namespace Yawble.Core.Tests.Scripts;

/// <summary>
/// Tester helper: a hand-written script with a code step and a while loop (paging by a text cursor that ends as null,
/// gathering every page's items with a second code step, then a summary), checked through scripts.check and run as a dry
/// and a real Test run; a loop that never ends fails at max; and the scripts skill's Loop and Code examples, verbatim,
/// checked with the real checker in scripts that define what they name.
/// </summary>
public sealed class CodeWhileHandWrittenTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-24T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private const string Page = """
        { "type": "object", "properties": { "items": { "type": "array", "items": { "type": "string" } }, "next": { "type": "string" } },
          "required": ["items"] }
        """;

    private const string Staleness = """
        { "type": "object", "properties": { "stale": { "type": "boolean" }, "age": { "type": "number" } }, "required": ["stale"] }
        """;

    /// <summary>Pages "a", "b", "c" (two items each); the last page has no next, so the cursor becomes null and the loop ends.</summary>
    private static string Paging(string id, string max = "20", string when = """{ "left": "{vars.cursor}", "op": "exists" }""") => $$"""
        { "format": "yawble-script/1", "id": "{{id}}", "name": "{{id}}", "trigger": { "accepts": ["once"] },
          "variables": [ { "name": "cursor", "type": "string", "initial": "a" }, { "name": "page", "type": "Page" },
                         { "name": "all", "type": "list<string>", "initial": [] }, { "name": "pages", "type": "int", "initial": 0 },
                         { "name": "summary", "type": "string", "initial": "" } ],
          "start": "loop",
          "steps": [
            { "id": "loop", "type": "while", "when": {{when}}, "body": "fetch", "then": "sum", "max": {{max}} },
            { "id": "fetch", "type": "code", "inputs": { "cursor": "{vars.cursor}" },
              "code": "const order = ['a', 'b', 'c'];\nconst i = order.indexOf(inputs.cursor);\nlog('page ' + inputs.cursor);\nconst page = { items: [inputs.cursor + '1', inputs.cursor + '2'] };\nif (i + 1 < order.length) page.next = order[i + 1];\nreturn page;",
              "returns": "Page", "into": "page", "next": "gather" },
            { "id": "gather", "type": "code", "inputs": { "all": "{vars.all}", "items": "{vars.page.items}" },
              "code": "return inputs.all.concat(inputs.items);", "returns": "list<string>", "into": "all", "next": "count" },
            { "id": "count", "type": "set", "var": "pages", "value": "{vars.pages + 1}", "next": "move" },
            { "id": "move", "type": "set", "var": "cursor", "value": "{vars.page.next}", "next": null },
            { "id": "sum", "type": "code", "inputs": { "all": "{vars.all}", "pages": "{vars.pages}" },
              "code": "return inputs.pages + ' pages: ' + inputs.all.join(',');", "returns": "string", "into": "summary", "next": "done" },
            { "id": "done", "type": "log", "text": "{vars.summary}", "next": null } ] }
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
            Library = new ScriptLibrary(Db.ConnectionString, () => FakeCatalog.MailAndSms());
            Library.SaveSchema("Page", "One page.", Page);
            Library.SaveSchema("Staleness", "How old a message is.", Staleness);
            Store = new ScriptRunStore(Db.ConnectionString);
            Runner = new ScriptRunner(Store, Library, NoScriptActions.Instance, SeamScriptEngine.Instance, Path.Combine(Db.Dir, "scripts"), clock: new FakeClock(T0));
            Bus.ScriptMembers = Runner;
            await Bus.StartAsync(Ct);
            return this;
        }

        public async Task<JsonNode> CheckAsync(string doc) =>
            (await new ScriptsRpc(Bus, Library, Store, Runner).DispatchAsync("scripts.check", new JsonObject { ["document"] = doc }, Ct))!;

        public async Task<ScriptRunOutcome> TestRunAsync(string doc, bool dryRun)
        {
            var saved = Library.Save(doc);
            Assert.True(saved.Ok, saved.Check.ToJsonString());
            return await Runner.TestRunAsync(new ScriptTestRun(saved.Id, null, ScriptPayloads.TriggerFired(1, "once", T0), null, null, Alpha, DryRun: dryRun), Ct);
        }

        public async ValueTask DisposeAsync()
        {
            await Runner.StopAsync();
            await Bus.DisposeAsync();
            Db.Dispose();
        }
    }

    private static string Logged(ScriptRunOutcome o) => string.Join("\n", o.Log.Select(l => l.Line));

    [Fact]
    public async Task TheHandWrittenPagingScript_ChecksClean()
    {
        await using var r = await new Rig().InitAsync();
        var check = await r.CheckAsync(Paging("hand-paging"));
        Assert.True(check["ok"]!.GetValue<bool>(), check.ToJsonString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheHandWrittenPagingScript_TestRun_PagesUntilTheCursorIsNull(bool dryRun)
    {
        await using var r = await new Rig().InitAsync();
        var outcome = await r.TestRunAsync(Paging("hand-paging-run"), dryRun);
        Assert.True(outcome.Succeeded, $"{outcome.Run.Status} at {outcome.Run.FailedStepId}: {outcome.Run.Reason}\n{Logged(outcome)}");
        var log = Logged(outcome);
        Assert.Contains("page a\npage b\npage c", log);
        Assert.Contains("3 pages: a1,a2,b1,b2,c1,c2", log);
        Assert.Equal(3, outcome.Steps.Count(s => s.StepId == "fetch"));
        Assert.Equal(4, outcome.Steps.Count(s => s.StepId == "loop"));
    }

    [Fact]
    public async Task ALoopThatNeverEnds_FailsAtMax()
    {
        await using var r = await new Rig().InitAsync();
        var doc = Paging("hand-paging-max", max: "5", when: """{ "left": "{vars.pages}", "op": "ge", "right": 0 }""")
            .Replace("if (i + 1 < order.length) page.next = order[i + 1];", "page.next = 'a';", StringComparison.Ordinal);
        var outcome = await r.TestRunAsync(doc, dryRun: true);
        Assert.Equal(ScriptRunStatus.Failed, outcome.Run.Status);
        Assert.Equal("loop", outcome.Run.FailedStepId);
        Assert.Equal("the loop 'loop' ran 5 times (its max) and its condition still holds", outcome.Run.Reason);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("10001")]
    public async Task AMaxOutsideOneTo10000_IsAProblem(string max)
    {
        await using var r = await new Rig().InitAsync();
        var check = await r.CheckAsync(Paging("hand-paging-bad-max", max: max));
        Assert.False(check["ok"]!.GetValue<bool>());
        Assert.Contains("it is from 1 to 10,000", check.ToJsonString());
    }

    [Fact]
    public async Task ASyntaxErrorInTheHandWrittenCode_IsOnItsStep_AtItsLineAndColumn()
    {
        await using var r = await new Rig().InitAsync();
        var doc = Paging("hand-paging-syntax").Replace("return inputs.all.concat(inputs.items);", "const x = 1;\\nreturn inputs.all.concat(inputs.items;", StringComparison.Ordinal);
        var check = await r.CheckAsync(doc);
        Assert.False(check["ok"]!.GetValue<bool>());
        var issue = check["issues"]!.AsArray().Single()!;
        Assert.Equal("gather", issue["stepId"]!.GetValue<string>());
        Assert.Contains("the code does not parse", issue["message"]!.GetValue<string>());
        Assert.Contains("(line 2, column", issue["message"]!.GetValue<string>());
    }

    // ---- the skill's examples, verbatim ----------------------------------------------------------------------------------

    private static string SkillExample(string heading)
    {
        var body = Assert.Single(SkillCatalog.BuiltIns, s => s.Name == "scripts").Body.Replace("\r\n", "\n", StringComparison.Ordinal);
        var m = Regex.Match(body, "## " + heading + @"\n.*?```json\n(?<json>.*?)\n```", RegexOptions.Singleline);
        Assert.True(m.Success, $"the skill has an example under ## {heading}");
        return m.Groups["json"].Value;
    }

    [Fact]
    public async Task TheSkillsLoopExample_ChecksClean()
    {
        var step = SkillExample("Loop");
        Assert.Contains("\"type\": \"while\"", step, StringComparison.Ordinal);
        var doc = """
            { "format": "yawble-script/1", "id": "loop-example", "name": "Loop example", "trigger": { "accepts": ["once"] },
              "variables": [ { "name": "cursor", "type": "string", "initial": "a" } ],
              "start": "page",
              "steps": [
            """ + step + """
            ,
                { "id": "fetch", "type": "set", "var": "cursor", "value": null, "next": null },
                { "id": "done", "type": "log", "text": "done", "next": null } ] }
            """;
        await using var r = await new Rig().InitAsync();
        var check = await r.CheckAsync(doc);
        Assert.True(check["ok"]!.GetValue<bool>(), check.ToJsonString());
        var outcome = await r.TestRunAsync(doc, dryRun: true);
        Assert.True(outcome.Succeeded, $"{outcome.Run.FailedStepId}: {outcome.Run.Reason}");
    }

    [Fact]
    public async Task TheSkillsCodeExample_ChecksClean()
    {
        var step = SkillExample("Code");
        Assert.Contains("\"type\": \"code\"", step, StringComparison.Ordinal);
        var doc = """
            { "format": "yawble-script/1", "id": "code-example", "name": "Code example", "trigger": { "accepts": ["once"] },
              "settings": [ { "name": "maxAge", "type": "int", "default": 24 } ],
              "requires": [ { "name": "mail", "kind": "email", "actions": ["list"] } ],
              "variables": [ { "name": "res", "type": "EmailList" }, { "name": "staleness", "type": "Staleness" } ],
              "start": "list",
              "steps": [
                { "id": "list", "type": "integration", "use": "mail", "action": "list", "args": { "folder": "INBOX" }, "into": "res", "next": "each" },
                { "id": "each", "type": "forEach", "items": "{vars.res.messages}", "as": "m", "body": "score", "then": null },
            """ + step + """
            ,
                { "id": "check", "type": "log", "text": "{vars.staleness.stale} {vars.staleness.age}", "next": null } ] }
            """;
        await using var r = await new Rig().InitAsync();
        var check = await r.CheckAsync(doc);
        Assert.True(check["ok"]!.GetValue<bool>(), check.ToJsonString());

        // The example's code itself runs on a message-shaped input.
        var code = JsonNode.Parse(step)!["code"]!.GetValue<string>();
        var run = ScriptCode.Run(code, new JsonObject { ["msg"] = new JsonObject { ["receivedAt"] = "2020-01-01T00:00:00Z" }, ["max"] = 24 },
            ScriptCodeLimits.Default, Ct);
        Assert.True(run.Result!["stale"]!.GetValue<bool>());
    }
}
