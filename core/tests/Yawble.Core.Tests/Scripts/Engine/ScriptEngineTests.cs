using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Scripts.Engine;
using Yawble.Core.Scripts.Language;

namespace Yawble.Core.Tests.Scripts.Engine;

public sealed class ScriptEngineTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 18, 0, 0, TimeSpan.Zero);
    private readonly TempFolder _temp = new();
    private readonly FakeClock _clock = new(T0);
    private readonly RecordingJournal _journal = new();

    public void Dispose() => _temp.Dispose();

    private string Data => _temp.Sub("data");

    private ScriptRunRequest Request(ScriptDocument doc, IScriptActions? actions = null) => new()
    {
        Document = doc,
        Actions = actions ?? new FakeActions(),
        DataFolder = Data,
        Clock = _clock,
        Input = JsonNode.Parse("""{ "triggerId": "t1", "kind": "once", "firedAt": "2026-11-23T17:59:00Z" }"""),
    };

    private Task<ScriptRunResult> Run(ScriptRunRequest request) =>
        ScriptEngine.RunAsync(request with { Clock = _clock }, _journal, TestContext.Current.CancellationToken);

    private Task<ScriptRunResult> Run(ScriptDocument doc, IScriptActions? actions = null) => Run(Request(doc, actions));

    private static void Succeeded(ScriptRunResult r) =>
        Assert.True(r.Status == ScriptRunStatus.Succeeded, $"failed at {r.FailedStepId}: {r.Reason}");

    // ---- start, set, log, end -----------------------------------------------------------------------------------------

    [Fact]
    public async Task StartBindsInputSettingsAndVariables_ThenStepsRunInOrder()
    {
        var doc = ScriptSamples.Script("""
            [ { "id": "a", "type": "set", "var": "n", "value": "{vars.n + settings.step}", "next": "b" },
              { "id": "b", "type": "log", "text": "{input.kind}: n={vars.n} {settings.name}", "next": "c" },
              { "id": "c", "type": "end", "outputs": { "n": "{vars.n}" } },
              { "id": "z", "type": "log", "text": "never", "next": null } ]
            """,
            variables: """[ { "name": "n", "type": "int", "initial": 40 } ]""",
            settings: """[ { "name": "step", "type": "int" }, { "name": "name", "type": "string", "default": "anon" } ]""");

        var r = await Run(Request(doc) with { Settings = new JsonObject { ["step"] = 2 } });

        Succeeded(r);
        Assert.Equal(["once: n=42 anon"], r.Log);
        Assert.Equal(42, (long?)r.Outputs!["n"]);
        Assert.Equal(["start", "a", "b", "c"], _journal.Started.Select(s => s.StepId));
        Assert.Equal(["start", "a", "b", "c"], _journal.CompletedSteps);
        Assert.Equal(3, r.StepsRun);
        Assert.Equal(["once: n=42 anon"], _journal.Lines);
        var set = _journal.Completed.Single(c => c.Record.StepId == "a").Record;
        Assert.Equal(42, (long?)set.Input!["value"]);
        Assert.Equal(42, (long?)set.Output);
        Assert.Equal("succeeded", set.Status);
    }

    [Fact]
    public async Task AMissingSettingFailsTheStart()
    {
        var doc = ScriptSamples.Script("""[ { "id": "a", "type": "end" } ]""", settings: """[ { "name": "to", "type": "list<string>" } ]""");
        var r = await Run(doc);
        Assert.Equal(ScriptRunStatus.Failed, r.Status);
        Assert.Equal("start", r.FailedStepId);
        Assert.Contains("setting 'to' is not set", r.Reason);
        Assert.Null(r.SavedVariables);
    }

    [Fact]
    public async Task ASettingOfTheWrongTypeFailsTheStart()
    {
        var doc = ScriptSamples.Script("""[ { "id": "a", "type": "end" } ]""", settings: """[ { "name": "to", "type": "list<string>" } ]""");
        var r = await Run(Request(doc) with { Settings = new JsonObject { ["to"] = "+1555" } });
        Assert.Equal("start", r.FailedStepId);
        Assert.Contains("setting 'to'", r.Reason);
    }

    [Fact]
    public async Task InputThatDoesNotMatchItsSchemaFailsTheStart()
    {
        var doc = ScriptSamples.Script("""[ { "id": "a", "type": "end" } ]""");
        var r = await Run(Request(doc) with { Input = JsonNode.Parse("""{ "kind": "once" }""") });
        Assert.Equal("start", r.FailedStepId);
        Assert.Contains("'triggerId' is required", r.Reason);
    }

    [Fact]
    public async Task SetConvertsToTheVariablesType_AndFailsWhenItCannot()
    {
        var doc = ScriptSamples.Script("""
            [ { "id": "a", "type": "set", "var": "at", "value": "{addHours(input.firedAt, 1)}", "next": "b" },
              { "id": "b", "type": "set", "var": "f", "value": "{3}", "next": "c" },
              { "id": "c", "type": "set", "var": "d", "value": "PT2H", "next": "e" },
              { "id": "e", "type": "set", "var": "n", "value": "{vars.f + 1}", "next": "g" },
              { "id": "g", "type": "set", "var": "n", "value": "{vars.f / 4}", "next": null } ]
            """, variables: """[ { "name": "at", "type": "datetime" }, { "name": "f", "type": "float" }, { "name": "d", "type": "duration" }, { "name": "n", "type": "int" } ]""");

        var r = await Run(doc);

        Assert.Equal("g", r.FailedStepId);
        Assert.Contains("variable 'n' (int): 0.75 is not a int", r.Reason);
        var cp = _journal.LastCheckpoint;
        Assert.Equal("2026-11-23T18:59:00Z", (string?)cp.Vars["at"]);
        Assert.Equal(3.0, (double?)cp.Vars["f"]);
        Assert.Equal("PT2H", (string?)cp.Vars["d"]);
        Assert.Equal(4, (long?)cp.Vars["n"]);
    }

    // ---- if, switch, forEach ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(5, "big")]
    [InlineData(1, "small")]
    public async Task IfTakesThenOrElse(int n, string expected)
    {
        var doc = ScriptSamples.Script("""
            [ { "id": "a", "type": "if", "when": { "all": [ { "left": "{vars.n}", "op": "gt", "right": 2 }, { "not": { "left": "{vars.n}", "op": "empty" } } ] },
                "then": "big", "else": "small" },
              { "id": "big", "type": "log", "text": "big", "next": null },
              { "id": "small", "type": "log", "text": "small", "next": null } ]
            """, variables: $$"""[ { "name": "n", "type": "int", "initial": {{n}} } ]""");
        var r = await Run(doc);
        Succeeded(r);
        Assert.Equal([expected], r.Log);
    }

    [Theory]
    [InlineData("order", "o")]
    [InlineData("spam", "")]
    [InlineData("proof", "default")]
    public async Task SwitchTakesTheCaseOrTheDefault(string category, string expected)
    {
        var doc = ScriptSamples.Script("""
            [ { "id": "a", "type": "switch", "on": "{vars.c}", "cases": { "order": "o", "spam": null }, "default": "d" },
              { "id": "o", "type": "log", "text": "o", "next": null },
              { "id": "d", "type": "log", "text": "default", "next": null } ]
            """, variables: $$"""[ { "name": "c", "type": "string", "initial": "{{category}}" } ]""");
        var r = await Run(doc);
        Succeeded(r);
        string[] want = expected.Length == 0 ? [] : [expected];
        Assert.Equal(want, r.Log);
    }

    [Fact]
    public async Task NestedLoopsRunEveryBodyOncePerItem_ThenGoOn()
    {
        var doc = ScriptSamples.Script("""
            [ { "id": "a", "type": "forEach", "items": "{settings.rows}", "as": "row", "body": "b", "then": "z" },
              { "id": "b", "type": "forEach", "items": "{settings.cols}", "as": "col", "body": "c", "then": "d" },
              { "id": "c", "type": "log", "text": "{row}{col}", "next": null },
              { "id": "d", "type": "log", "text": "end {row}", "next": null },
              { "id": "z", "type": "log", "text": "done", "next": null } ]
            """, settings: """[ { "name": "rows", "type": "list<string>" }, { "name": "cols", "type": "list<int>" } ]""");
        var r = await Run(Request(doc) with { Settings = JsonNode.Parse("""{ "rows": ["a", "b"], "cols": [1, 2, 3] }""")!.AsObject() });
        Succeeded(r);
        Assert.Equal(["a1", "a2", "a3", "end a", "b1", "b2", "b3", "end b", "done"], r.Log);
    }

    [Fact]
    public async Task AnEmptyLoopGoesStraightToThen()
    {
        var doc = ScriptSamples.Script("""
            [ { "id": "a", "type": "forEach", "items": "{vars.l}", "as": "x", "body": "b", "then": "c" },
              { "id": "b", "type": "log", "text": "body", "next": null },
              { "id": "c", "type": "log", "text": "after", "next": null } ]
            """, variables: """[ { "name": "l", "type": "list<int>", "initial": [] } ]""");
        var r = await Run(doc);
        Succeeded(r);
        Assert.Equal(["after"], r.Log);
    }

    [Fact]
    public async Task EndInsideALoopEndsTheRun()
    {
        var doc = ScriptSamples.Script("""
            [ { "id": "a", "type": "forEach", "items": "{vars.l}", "as": "x", "body": "b", "then": "z" },
              { "id": "b", "type": "if", "when": { "left": "{x}", "op": "eq", "right": 2 }, "then": "stop", "else": "c" },
              { "id": "c", "type": "log", "text": "{x}", "next": null },
              { "id": "stop", "type": "end", "outputs": { "stoppedAt": "{x}" } },
              { "id": "z", "type": "log", "text": "never", "next": null } ]
            """, variables: """[ { "name": "l", "type": "list<int>", "initial": [1, 2, 3] } ]""");
        var r = await Run(doc);
        Succeeded(r);
        Assert.Equal(["1"], r.Log);
        Assert.Equal(2, (long?)r.Outputs!["stoppedAt"]);
    }

    [Fact]
    public async Task ForEachOverSomethingNotAListFails()
    {
        var doc = ScriptSamples.Script("""
            [ { "id": "a", "type": "forEach", "items": "{vars.s}", "as": "x", "body": "b", "then": null },
              { "id": "b", "type": "log", "text": "x", "next": null } ]
            """, variables: """[ { "name": "s", "type": "string", "initial": "abc" } ]""");
        var r = await Run(doc);
        Assert.Equal("a", r.FailedStepId);
        Assert.Contains("is not a list", r.Reason);
    }

    // ---- integration --------------------------------------------------------------------------------------------------

    private const string Requires = """[ { "name": "api", "kind": "api", "actions": ["GET /items", "POST /items"] } ]""";

    [Fact]
    public async Task AnIntegrationStepCallsTheActionWithItsArgs_AndKeepsTheResult()
    {
        var actions = new FakeActions().On("api", "GET /items", args => JsonNode.Parse($$"""{ "status": 200, "body": { "q": "{{args["q"]}}", "items": [1, 2] } }"""));
        var doc = ScriptSamples.Script("""
            [ { "id": "a", "type": "integration", "use": "api", "action": "GET /items", "args": { "q": "{settings.q}", "n": 3 }, "into": "resp", "next": "b" },
              { "id": "b", "type": "log", "text": "{vars.resp.status} {steps.a.body.q} {count(steps.a.body.items)}", "next": null } ]
            """, variables: """[ { "name": "resp", "type": "ApiResponse" } ]""", settings: """[ { "name": "q", "type": "string" } ]""", requires: Requires);

        var r = await Run(Request(doc, actions) with { Settings = new JsonObject { ["q"] = "shoes" } });

        Succeeded(r);
        Assert.Equal(["200 shoes 2"], r.Log);
        var call = Assert.Single(actions.Calls);
        Assert.Equal(("api", "api", "GET /items", false), (call.Use, call.Kind, call.Action, call.DryRun));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "q": "shoes", "n": 3 }"""), call.Args));
    }

    [Fact]
    public async Task AFailedActionFailsTheStepWithItsMessage()
    {
        var actions = new FakeActions().On("api", "GET /items", _ => throw new InvalidOperationException("needs approval: allow GET /items for Team"));
        var doc = ScriptSamples.Script("""[ { "id": "a", "type": "integration", "use": "api", "action": "GET /items", "next": null } ]""", requires: Requires);
        var r = await Run(doc, actions);
        Assert.Equal("a", r.FailedStepId);
        Assert.Equal("api GET /items: needs approval: allow GET /items for Team", r.Reason);
        var failed = _journal.Completed[^1].Record;
        Assert.Equal(("a", "failed"), (failed.StepId, failed.Status));
        Assert.Equal(r.Reason, failed.Error);
        Assert.Equal("a", _journal.LastCheckpoint.Next);
        Assert.Contains("run failed at step 'a'", _journal.Lines[^1]);
    }

    [Fact]
    public async Task AResultThatDoesNotMatchTheVariablesTypeFailsTheStep()
    {
        var actions = new FakeActions().On("api", "GET /items", _ => JsonNode.Parse("""[ { "subject": "no id" } ]"""));
        var doc = ScriptSamples.Script("""[ { "id": "a", "type": "integration", "use": "api", "action": "GET /items", "into": "m", "next": null } ]""",
            variables: """[ { "name": "m", "type": "list<EmailMessage>" } ]""", requires: Requires);
        var r = await Run(doc, actions);
        Assert.Equal("a", r.FailedStepId);
        Assert.Contains("'id' is required", r.Reason);
    }

    // ---- files --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FileStepsWriteAppendReadCheckAndDelete_InTheDataFolder()
    {
        var doc = ScriptSamples.Script("""
            [ { "id": "a", "type": "file.writeAllText", "path": "out/notes.txt", "text": "one\n", "next": "b" },
              { "id": "b", "type": "file.appendLine", "path": "out/notes.txt", "text": "two", "next": "c" },
              { "id": "c", "type": "file.readAllText", "path": "out/notes.txt", "into": "text", "next": "d" },
              { "id": "d", "type": "file.readLines", "path": "out/notes.txt", "into": "lines", "next": "e" },
              { "id": "e", "type": "file.exists", "path": "out/notes.txt", "next": "f" },
              { "id": "f", "type": "file.delete", "path": "out/notes.txt", "next": "g" },
              { "id": "g", "type": "file.exists", "path": "out/notes.txt", "next": "h" },
              { "id": "h", "type": "log", "text": "{count(vars.lines)} {join(vars.lines, '|')} {steps.e} {steps.g}", "next": null } ]
            """, variables: """[ { "name": "text", "type": "string" }, { "name": "lines", "type": "list<string>" } ]""");

        var r = await Run(doc);

        Succeeded(r);
        Assert.Equal(["2 one|two true false"], r.Log);
        Assert.False(File.Exists(Path.Combine(Data, "out", "notes.txt")));
    }

    [Fact]
    public async Task ReadingAFileThatIsNotThereFails()
    {
        var r = await Run(ScriptSamples.Script("""[ { "id": "a", "type": "file.readAllText", "path": "missing.txt", "next": null } ]"""));
        Assert.Equal("a", r.FailedStepId);
        Assert.Contains("'missing.txt' does not exist", r.Reason);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("sub/../../escape.txt")]
    public async Task APathOutsideTheDataFolderFails(string path)
    {
        var r = await Run(ScriptSamples.Script($$"""[ { "id": "a", "type": "file.writeAllText", "path": "{{path}}", "text": "x", "next": null } ]"""));
        Assert.Equal("a", r.FailedStepId);
        Assert.Contains("outside the member's data folder and the team's folder grants", r.Reason);
        Assert.False(File.Exists(Path.Combine(_temp.Path, "escape.txt")));
    }

    [Fact]
    public async Task AGrantedFolderCanBeUsed_AReadOnlyOneOnlyRead()
    {
        var writable = _temp.Sub("granted-rw");
        var readOnly = _temp.Sub("granted-ro");
        File.WriteAllText(Path.Combine(readOnly, "in.txt"), "hello");
        // The absolute paths come from settings (a temp path may hold braces, which a template would read as an expression).
        var doc = ScriptSamples.Script("""
            [ { "id": "a", "type": "file.readAllText", "path": "{settings.ro}", "next": "b" },
              { "id": "b", "type": "file.writeAllText", "path": "{settings.w}", "text": "{steps.a} world", "next": "c" },
              { "id": "c", "type": "file.appendLine", "path": "{settings.ro}", "text": "no", "next": null } ]
            """, settings: """[ { "name": "ro", "type": "string" }, { "name": "w", "type": "string" } ]""");
        var settings = new JsonObject { ["ro"] = Path.Combine(readOnly, "in.txt"), ["w"] = Path.Combine(writable, "out.txt") };
        var r = await Run(Request(doc) with { Settings = settings, AllowedFolders = [new(writable, true), new(readOnly, false)] });

        Assert.True(r.FailedStepId == "c", r.Reason);
        Assert.Contains("granted read-only", r.Reason);
        Assert.Equal("hello world", File.ReadAllText(Path.Combine(writable, "out.txt")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(readOnly, "in.txt")));
    }

    // ---- limits -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheStepBudgetFailsARunThatRunsTooManySteps()
    {
        var doc = ScriptSamples.Script("""
            [ { "id": "a", "type": "forEach", "items": "{vars.l}", "as": "x", "body": "b", "then": null },
              { "id": "b", "type": "log", "text": "{x}", "next": null } ]
            """, variables: """[ { "name": "l", "type": "list<int>", "initial": [1, 2, 3, 4, 5, 6] } ]""");
        var r = await Run(Request(doc) with { StepBudget = 4 });
        Assert.Equal(ScriptRunStatus.Failed, r.Status);
        Assert.Equal("b", r.FailedStepId);
        Assert.Contains("the step budget of 4 steps is used up", r.Reason);
        Assert.Equal(["1", "2", "3"], r.Log.Take(3));
        Assert.Equal(4, r.StepsRun);
    }

    [Fact]
    public async Task TheLoopCapFailsALoopOverTooManyItems()
    {
        var doc = ScriptSamples.Script("""
            [ { "id": "a", "type": "forEach", "items": "{vars.l}", "as": "x", "body": "b", "then": null },
              { "id": "b", "type": "log", "text": "{x}", "next": null } ]
            """, variables: """[ { "name": "l", "type": "list<int>", "initial": [1, 2, 3] } ]""");
        var r = await Run(Request(doc) with { LoopCap = 2 });
        Assert.Equal("a", r.FailedStepId);
        Assert.Contains("forEach has 3 items, over the loop cap of 2", r.Reason);
        Assert.DoesNotContain(r.Log, l => !l.StartsWith("run failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheTimeoutFailsARunWhoseTimeIsUp_BetweenSteps()
    {
        var actions = new FakeActions().On("api", "GET /items", _ => { _clock.Now += TimeSpan.FromMinutes(11); return null; });
        var doc = ScriptSamples.Script("""
            [ { "id": "a", "type": "integration", "use": "api", "action": "GET /items", "next": "b" },
              { "id": "b", "type": "log", "text": "late", "next": null } ]
            """, requires: Requires);
        var r = await Run(doc, actions);
        Assert.Equal("b", r.FailedStepId);
        Assert.Equal("the run timed out after 10m", r.Reason);
    }

    [Fact]
    public async Task TheTimeoutFailsARunStuckInAnAction()
    {
        var actions = new FakeActions { Before = (_, ct) => Task.Delay(Timeout.Infinite, ct) }.On("api", "GET /items", _ => null);
        var doc = ScriptSamples.Script("""[ { "id": "a", "type": "integration", "use": "api", "action": "GET /items", "next": null } ]""", requires: Requires);
        var r = await ScriptEngine.RunAsync(Request(doc, actions) with { Timeout = TimeSpan.FromMilliseconds(200), Clock = null }, _journal, TestContext.Current.CancellationToken);
        Assert.Equal("a", r.FailedStepId);
        Assert.StartsWith("the run timed out", r.Reason);
    }

    [Fact]
    public async Task TheCallersCancellationThrows_LeavingTheRunToResume()
    {
        using var cts = new CancellationTokenSource();
        var actions = new FakeActions { Before = (_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; } }
            .On("api", "GET /items", _ => null);
        var doc = ScriptSamples.Script("""[ { "id": "a", "type": "integration", "use": "api", "action": "GET /items", "next": null } ]""", requires: Requires);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ScriptEngine.RunAsync(Request(doc, actions), _journal, cts.Token));
        Assert.Equal("a", _journal.LastCheckpoint.Next);
    }

    // ---- saved variables ----------------------------------------------------------------------------------------------

    private static ScriptDocument Counter(bool fail) => ScriptSamples.Script($$"""
        [ { "id": "a", "type": "set", "var": "runs", "value": "{vars.runs + 1}", "next": "b" },
          { "id": "b", "type": "set", "var": "scratch", "value": "x", "next": "c" },
          { "id": "c", "type": "{{(fail ? "file.readAllText" : "log")}}", "path": "missing.txt", "text": "ok", "next": null } ]
        """, variables: """[ { "name": "runs", "type": "int", "scope": "saved", "initial": 0 }, { "name": "scratch", "type": "string" } ]""");

    [Fact]
    public async Task SavedVariablesComeBackFromASuccessfulRun_OnlySavedOnes()
    {
        var r = await Run(Request(Counter(false)) with { SavedVariables = new JsonObject { ["runs"] = 4 } });
        Succeeded(r);
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["runs"] = 5 }, r.SavedVariables));
    }

    [Fact]
    public async Task AFailedRunReturnsNoSavedVariables()
    {
        var r = await Run(Request(Counter(true)) with { SavedVariables = new JsonObject { ["runs"] = 4 } });
        Assert.Equal(ScriptRunStatus.Failed, r.Status);
        Assert.Null(r.SavedVariables);
        Assert.Null(r.Outputs);
    }

    [Fact]
    public async Task ASavedVariableNotSavedYetStartsFromItsInitialValue()
    {
        var r = await Run(Counter(false));
        Assert.Equal(1, (long?)r.SavedVariables!["runs"]);
    }

    // ---- dry run ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADryRunDoesNotCallChangingActions_ReadsAreReal_WritesGoToTheDryRunFolder()
    {
        File.WriteAllText(Path.Combine(Data, "seen.txt"), "old\n");
        var catalog = new FakeCatalog().Read("api", "GET /items").Change("api", "POST /items");
        var actions = new FakeActions()
            .On("api", "GET /items", _ => JsonNode.Parse("""{ "status": 200, "body": ["x"] }"""))
            .On("api", "POST /items", _ => throw new InvalidOperationException("must not be called"));
        var doc = ScriptSamples.Script("""
            [ { "id": "a", "type": "integration", "use": "api", "action": "GET /items", "next": "b" },
              { "id": "b", "type": "integration", "use": "api", "action": "POST /items", "args": { "name": "{steps.a.body[0]}" }, "next": "c" },
              { "id": "c", "type": "file.appendLine", "path": "seen.txt", "text": "new", "next": "d" },
              { "id": "d", "type": "file.readLines", "path": "seen.txt", "into": "lines", "next": "e" },
              { "id": "e", "type": "file.delete", "path": "seen.txt", "next": "f" },
              { "id": "f", "type": "file.exists", "path": "seen.txt", "next": "g" },
              { "id": "g", "type": "log", "text": "{join(vars.lines, ',')} {steps.f}", "next": null } ]
            """, variables: """[ { "name": "lines", "type": "list<string>" } ]""", requires: Requires);
        var dry = Path.Combine(_temp.Path, "dry");

        var r = await Run(Request(doc, actions) with { DryRun = true, DryRunFolder = dry, Catalog = catalog });

        Succeeded(r);
        Assert.Equal(["GET /items"], actions.Calls.Select(c => c.Action));
        Assert.Contains("""would POST /items on api with {"name":"x"}""", r.Log);
        Assert.Contains(r.Log, l => l.StartsWith("would append a line to ", StringComparison.Ordinal) && l.Contains(dry, StringComparison.Ordinal));
        Assert.Contains(r.Log, l => l.StartsWith("would delete ", StringComparison.Ordinal));
        Assert.Equal("old,new false", r.Log[^1]);
        Assert.Equal("old\n", File.ReadAllText(Path.Combine(Data, "seen.txt")));
    }

    [Fact]
    public async Task ADryRunWithoutACatalogLeavesTheDecisionToTheActions()
    {
        var actions = new FakeActions().On("api", "POST /items", _ => null);
        var doc = ScriptSamples.Script("""[ { "id": "a", "type": "integration", "use": "api", "action": "POST /items", "next": null } ]""", requires: Requires);
        Succeeded(await Run(Request(doc, actions) with { DryRun = true, DryRunFolder = Path.Combine(_temp.Path, "dry") }));
        Assert.True(Assert.Single(actions.Calls).DryRun);
    }

    // ---- journal and resume -------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryCompletedStepIsJournaledWithAJsonCheckpoint()
    {
        var doc = ScriptSamples.Script("""
            [ { "id": "a", "type": "forEach", "items": "{vars.l}", "as": "x", "body": "b", "then": null },
              { "id": "b", "type": "set", "var": "sum", "value": "{vars.sum + x}", "next": null } ]
            """, variables: """[ { "name": "l", "type": "list<int>", "initial": [1, 2] }, { "name": "sum", "type": "int", "initial": 0 } ]""");
        Succeeded(await Run(doc));

        Assert.Equal(["start", "a", "b", "b"], _journal.Completed.Select(c => c.Record.StepId));
        var afterLoop = ScriptRunCheckpoint.FromJson(_journal.Completed[1].Checkpoint);
        Assert.Equal("b", afterLoop.Next);
        Assert.Equal(0, Assert.Single(afterLoop.Frames).Index);
        var afterFirst = ScriptRunCheckpoint.FromJson(_journal.Completed[2].Checkpoint);
        Assert.Equal(("b", 1, 1L), (afterFirst.Next, afterFirst.Frames[0].Index, (long)afterFirst.Vars["sum"]!));
        Assert.True(_journal.LastCheckpoint.IsDone);
        Assert.All(_journal.Completed, c => Assert.True(c.Record.EndedAt >= c.Record.StartedAt));
    }

    [Fact]
    public async Task ARunCutOffMidLoopResumesFromItsLastCheckpoint_AndDoesNotRepeatCompletedSteps()
    {
        var sends = new List<string>();
        JsonNode? Send(JsonObject args) { sends.Add((string)args["to"]!); return null; }

        var doc = ScriptSamples.Script("""
            [ { "id": "a", "type": "forEach", "items": "{settings.to}", "as": "to", "body": "send", "then": "done" },
              { "id": "send", "type": "integration", "use": "api", "action": "POST /items", "args": { "to": "{to}" }, "next": "count" },
              { "id": "count", "type": "set", "var": "sent", "value": "{vars.sent + 1}", "next": null },
              { "id": "done", "type": "end", "outputs": { "sent": "{vars.sent}" } } ]
            """, variables: """[ { "name": "sent", "type": "int", "initial": 0 }, { "name": "last", "type": "string", "scope": "saved" } ]""",
            settings: """[ { "name": "to", "type": "list<string>" } ]""", requires: Requires);
        var settings = JsonNode.Parse("""{ "to": ["+1", "+2", "+3"] }""")!.AsObject();

        // Cut off after start, a, send(+1), count, send(+2): the app closed.
        using var cts = new CancellationTokenSource();
        var first = new RecordingJournal { CancelAfterCompleted = 5, Cancel = cts };
        var actions = new FakeActions().On("api", "POST /items", Send);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ScriptEngine.RunAsync(Request(doc, actions) with { Settings = settings }, first, cts.Token));
        Assert.Equal(["+1", "+2"], sends);

        var checkpoint = first.LastCheckpoint;
        Assert.Equal("count", checkpoint.Next);
        var resumed = await ScriptEngine.RunAsync(
            Request(doc, actions) with { Settings = settings, ResumeFrom = checkpoint }, _journal, TestContext.Current.CancellationToken);

        Succeeded(resumed);
        Assert.Equal(["+1", "+2", "+3"], sends);
        Assert.Equal(3, (long?)resumed.Outputs!["sent"]);
        Assert.Equal(["count", "send", "count", "done"], _journal.Started.Select(s => s.StepId));
        Assert.Equal(8, resumed.StepsRun);
        Assert.True(resumed.SavedVariables!.ContainsKey("last"));
    }

    [Fact]
    public async Task ResumingAFailedRunRunsTheFailedStepAgain()
    {
        var fail = true;
        var actions = new FakeActions().On("api", "GET /items", _ => fail ? throw new InvalidOperationException("down") : JsonValue.Create("up"));
        var doc = ScriptSamples.Script("""
            [ { "id": "a", "type": "log", "text": "before", "next": "b" },
              { "id": "b", "type": "integration", "use": "api", "action": "GET /items", "next": "c" },
              { "id": "c", "type": "log", "text": "{steps.b}", "next": null } ]
            """, requires: Requires);
        var r1 = await Run(doc, actions);
        Assert.Equal("b", r1.FailedStepId);

        fail = false;
        var journal = new RecordingJournal();
        var r2 = await ScriptEngine.RunAsync(Request(doc, actions) with { ResumeFrom = _journal.LastCheckpoint }, journal, TestContext.Current.CancellationToken);
        Succeeded(r2);
        Assert.Equal(["up"], r2.Log);
        Assert.Equal(["b", "c"], journal.Started.Select(s => s.StepId));
    }

    [Fact]
    public async Task ResumingAFinishedRunJustSucceeds()
    {
        var doc = ScriptSamples.Script("""[ { "id": "a", "type": "end", "outputs": { "x": 1 } } ]""");
        Succeeded(await Run(doc));
        var again = await ScriptEngine.RunAsync(Request(doc) with { ResumeFrom = _journal.LastCheckpoint }, new RecordingJournal(), TestContext.Current.CancellationToken);
        Succeeded(again);
        Assert.Equal(1, (long?)again.Outputs!["x"]);
    }
}
