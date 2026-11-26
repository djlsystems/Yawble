using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Workflows.Engine;
using Yawble.Core.Workflows.Language;

namespace Yawble.Core.Tests.Workflows.Engine;

/// <summary>
/// The code step in the engine: inputs reach the code as plain JSON, the returned value is checked against returns and kept in
/// into, log lines go to the run log, each limit (time, memory, statements, call depth) fails the step with its reason, nothing
/// outside the engine is reachable (require, import, fetch, timers, .NET types), and a dry run runs the code.
/// </summary>
public sealed class WorkflowCodeEngineTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempFolder _temp = new();
    public void Dispose() => _temp.Dispose();

    private static readonly WorkflowSchema Staleness = WorkflowSchema.Parse("Staleness", """
        { "type": "object", "properties": { "stale": { "type": "boolean" }, "age": { "type": "number" } }, "required": ["stale"] }
        """);

    private const string Vars = """
        [{ "name": "r", "type": "any" }, { "name": "s", "type": "Staleness" }, { "name": "n", "type": "int", "initial": 0 },
         { "name": "items", "type": "list<string>", "initial": ["a", "b"] }]
        """;

    private static WorkflowDocument Doc(string code, string extra = """, "into": "r" """) => WorkflowSamples.Workflow($$"""
        [{ "id": "a", "type": "code", "code": {{JsonSerializer.Serialize(code)}} {{extra}}, "next": null }]
        """, Vars, settings: """[{ "name": "maxAge", "type": "int", "default": 24 }]""");

    private WorkflowRunRequest Request(WorkflowDocument doc, WorkflowCodeLimits? limits = null, bool dryRun = false) => new()
    {
        Document = doc, Actions = new FakeActions(), DataFolder = _temp.Sub("d"), Schemas = new WorkflowSchemaSet([Staleness]),
        CodeLimits = limits ?? WorkflowCodeLimits.Default, DryRun = dryRun,
    };

    private async Task<(WorkflowRunResult Result, RecordingJournal Journal)> RunAsync(WorkflowDocument doc, WorkflowCodeLimits? limits = null, bool dryRun = false)
    {
        var journal = new RecordingJournal();
        var result = await WorkflowEngine.RunAsync(Request(doc, limits, dryRun), journal, Ct);
        return (result, journal);
    }

    private async Task<JsonNode?> ReturnsAsync(string code)
    {
        var (result, journal) = await RunAsync(Doc(code));
        Assert.True(result.Status == WorkflowRunStatus.Succeeded, result.Reason);
        return journal.LastCheckpoint.Vars["r"];
    }

    private async Task<string> FailsAsync(string code, WorkflowCodeLimits? limits = null, string extra = """, "into": "r" """)
    {
        var (result, _) = await RunAsync(Doc(code, extra), limits);
        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        Assert.Equal("a", result.FailedStepId);
        return result.Reason!;
    }

    // ---- inputs, returns, into, log ------------------------------------------------------------------------------------

    [Fact]
    public async Task Inputs_ReachTheCodeAsPlainJson_AndTheResultGoesIntoInto()
    {
        var doc = Doc("""
            return { sum: inputs.n + inputs.fixed, first: inputs.list[0], count: inputs.list.length, nested: inputs.obj.k,
                     max: inputs.max, kind: typeof inputs.obj, isArray: Array.isArray(inputs.list), none: inputs.none };
            """, """, "inputs": { "n": "{vars.n + 2}", "fixed": 40, "list": "{vars.items}", "obj": { "k": "v{vars.n}" }, "max": "{settings.maxAge}", "none": null }, "into": "r" """);
        var (result, journal) = await RunAsync(doc);
        Assert.Equal(WorkflowRunStatus.Succeeded, result.Status);
        var r = journal.LastCheckpoint.Vars["r"]!;
        Assert.Equal(42L, r["sum"]!.GetValue<long>());
        Assert.Equal(("a", 2L, "v0", 24L, "object", true), (r["first"]!.GetValue<string>(), r["count"]!.GetValue<long>(), r["nested"]!.GetValue<string>(),
            r["max"]!.GetValue<long>(), r["kind"]!.GetValue<string>(), r["isArray"]!.GetValue<bool>()));
        Assert.Null(r["none"]);
        // The step's record shows the evaluated inputs, and steps.a keeps the result.
        var record = journal.Completed.Single(c => c.Record.StepId == "a").Record;
        Assert.Equal(2L, record.Input!["inputs"]!["n"]!.GetValue<long>());
        Assert.Equal(42L, journal.LastCheckpoint.Steps["a"]!["sum"]!.GetValue<long>());
    }

    [Fact]
    public async Task NoReturn_GivesNull_AndUndefinedToo()
    {
        Assert.Null(await ReturnsAsync("const x = 1;"));
        Assert.Null(await ReturnsAsync("return undefined;"));
        Assert.Null(await ReturnsAsync("return null;"));
        Assert.Equal("text", (await ReturnsAsync("return 'text';"))!.GetValue<string>());
        Assert.Equal(new[] { 1, 2 }, (await ReturnsAsync("return [1, 2];"))!.AsArray().Select(n => n!.GetValue<int>()));
    }

    [Fact]
    public async Task Returns_ASchema_ThatMatches_IsKeptTyped()
    {
        var (result, journal) = await RunAsync(Doc("return { stale: inputs.age > 3, age: inputs.age };", """, "inputs": { "age": 5 }, "returns": "Staleness", "into": "s" """));
        Assert.True(result.Status == WorkflowRunStatus.Succeeded, result.Reason);
        Assert.True(journal.LastCheckpoint.Vars["s"]!["stale"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("return { age: 3 };", "Staleness")]
    [InlineData("return 'many';", "int")]
    [InlineData("return [1, 'x'];", "list<int>")]
    public async Task AReturnedValueThatDoesNotMatchReturns_FailsTheStep(string code, string returns)
    {
        var reason = await FailsAsync(code, extra: $$""", "returns": "{{returns}}" """);
        Assert.Contains("the code returned", reason);
        Assert.Contains(returns, reason);
    }

    [Fact]
    public async Task LogLines_GoToTheRunLog_InOrder_EvenWhenTheCodeThenFails()
    {
        var (ok, journal) = await RunAsync(Doc("log('one'); log('n is ' + inputs.n); log({ a: 1 }); return 1;", """, "inputs": { "n": 7 }, "into": "r" """));
        Assert.Equal(WorkflowRunStatus.Succeeded, ok.Status);
        Assert.Equal(["one", "n is 7", """{"a":1}"""], journal.Lines);
        Assert.Equal(["one", "n is 7", """{"a":1}"""], ok.Log);

        var (failed, failedJournal) = await RunAsync(Doc("log('before'); throw new Error('boom');"));
        Assert.Equal(WorkflowRunStatus.Failed, failed.Status);
        Assert.Equal("before", failedJournal.Lines[0]);
        Assert.Contains("boom", failed.Reason);
    }

    [Fact]
    public async Task AThrow_FailsTheStep_WithTheMessageAndLine()
    {
        var reason = await FailsAsync("const a = 1;\nthrow new TypeError('not a number');");
        Assert.Contains("the code threw", reason);
        Assert.Contains("not a number", reason);
        Assert.Contains("(line 2)", reason);
        Assert.Contains("is not defined", await FailsAsync("return nope + 1;"));
    }

    [Fact]
    public async Task StrictMode_IsOn()
    {
        // Assigning an undeclared name throws only in strict mode.
        Assert.Contains("the code threw", await FailsAsync("undeclared = 5; return undeclared;"));
    }

    // ---- limits --------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheLimits_AreTwoSeconds64MBAStatementCountAndACallDepth()
    {
        var d = WorkflowCodeLimits.Default;
        Assert.Equal(TimeSpan.FromSeconds(2), d.Timeout);
        Assert.Equal(64L * 1024 * 1024, d.MemoryBytes);
        Assert.True(d.MaxStatements > 0);
        Assert.True(d.MaxRecursion > 0);
    }

    [Fact]
    public async Task ALoopThatRunsTooLong_FailsWithTheTimeout()
    {
        var reason = await FailsAsync("while (true) {}", WorkflowCodeLimits.Default with { MaxStatements = int.MaxValue });
        Assert.Equal("the code ran longer than 2 s", reason);
    }

    [Fact]
    public async Task UsingTooMuchMemory_FailsWithTheMemoryLimit()
    {
        var reason = await FailsAsync("const a = []; while (true) { a.push(new Array(100000).fill(1)); }",
            WorkflowCodeLimits.Default with { Timeout = TimeSpan.FromSeconds(20), MaxStatements = int.MaxValue });
        Assert.Equal("the code used more than 64 MB of memory", reason);
    }

    [Fact]
    public async Task TooManyStatements_FailsWithTheStatementCount()
    {
        var reason = await FailsAsync("let i = 0; while (true) { i++; }", WorkflowCodeLimits.Default with { Timeout = TimeSpan.FromSeconds(20) });
        Assert.Equal("the code ran more than 1,000,000 statements", reason);
    }

    [Fact]
    public async Task DeepRecursion_FailsWithTheCallDepth()
    {
        var reason = await FailsAsync("function down(n) { return down(n + 1); }\nreturn down(0);");
        Assert.Equal("the code went deeper than 256 nested calls", reason);
    }

    [Fact]
    public async Task TheRunTimeout_StillAppliesToACodeStep()
    {
        var doc = Doc("while (true) {}");
        var journal = new RecordingJournal();
        var result = await WorkflowEngine.RunAsync(Request(doc, WorkflowCodeLimits.Default with { Timeout = TimeSpan.FromSeconds(30), MaxStatements = int.MaxValue })
            with { Timeout = TimeSpan.FromMilliseconds(300) }, journal, Ct);
        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        Assert.StartsWith("the run timed out", result.Reason);
    }

    // ---- nothing outside -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("require")]
    [InlineData("fetch")]
    [InlineData("setTimeout")]
    [InlineData("setInterval")]
    [InlineData("XMLHttpRequest")]
    [InlineData("importNamespace")]
    [InlineData("System")]
    [InlineData("clr")]
    [InlineData("process")]
    [InlineData("host")]
    [InlineData("console")]
    [InlineData("Deno")]
    public async Task HostNames_AreUndefined(string name)
    {
        Assert.Equal("undefined", (await ReturnsAsync($"return typeof {name};"))!.GetValue<string>());
    }

    [Theory]
    [InlineData("return require('fs');")]
    [InlineData("return fetch('https://example.com');")]
    [InlineData("setTimeout(() => {}, 1); return 1;")]
    [InlineData("return importNamespace('System.IO');")]
    [InlineData("return System.IO.File.ReadAllText('x');")]
    public async Task CallingThem_Fails(string code)
    {
        var reason = await FailsAsync(code);
        Assert.Contains("is not defined", reason);
    }

    [Fact]
    public async Task ImportStatements_DoNotParse_AndDynamicImportFails()
    {
        Assert.Contains("the code does not parse", await FailsAsync("import fs from 'fs';\nreturn 1;"));
        var dynamicImport = await FailsAsync("return import('fs');");
        Assert.StartsWith("the code failed: Module loading has been disabled", dynamicImport);
    }

    [Fact]
    public async Task TheLogFunction_IsAPlainFunction_ThatReachesNoHostObject()
    {
        Assert.Equal("undefined", (await ReturnsAsync("return typeof log.constructor.constructor('return globalThis')().System;"))!.GetValue<string>());
        Assert.Equal("undefined", (await ReturnsAsync("return typeof Object.getPrototypeOf(log).GetType;"))!.GetValue<string>());
        Assert.Equal("undefined", (await ReturnsAsync("return typeof log.GetType;"))!.GetValue<string>());
    }

    [Fact]
    public async Task OnlyInputsAndLog_AreAdded_ToTheGlobals()
    {
        var names = (await ReturnsAsync("return Object.getOwnPropertyNames(globalThis);"))!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet();
        Assert.Contains("inputs", names);
        Assert.Contains("log", names);
        foreach (var host in new[] { "require", "fetch", "setTimeout", "importNamespace", "System", "process" })
            Assert.DoesNotContain(host, names);
    }

    // ---- dry run --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADryRun_RunsTheCodeForReal()
    {
        var (result, journal) = await RunAsync(Doc("log('ran'); return inputs.x * 2;", """, "inputs": { "x": 21 }, "returns": "int", "into": "n" """), dryRun: true);
        Assert.Equal(WorkflowRunStatus.Succeeded, result.Status);
        Assert.Equal(42, journal.LastCheckpoint.Vars["n"]!.GetValue<int>());
        Assert.Contains("ran", journal.Lines);
    }

    // ---- WorkflowCode directly ---------------------------------------------------------------------------------------------

    [Fact]
    public void Parse_GivesLineAndColumnInTheCode_NotInTheWrapper()
    {
        Assert.Null(WorkflowCode.Parse("return 1;"));
        var e = WorkflowCode.Parse("let a = 1;\nlet b = ;");
        Assert.NotNull(e);
        Assert.Equal((2, 9), (e.Line, e.Column));
    }

    [Fact]
    public void Run_GivesBackTheResultAndTheLog()
    {
        var outcome = WorkflowCode.Run("log('hi', 2); return inputs.a;", new JsonObject { ["a"] = "x" }, WorkflowCodeLimits.Default, Ct);
        Assert.Equal("x", outcome.Result!.GetValue<string>());
        Assert.Equal(["hi 2"], outcome.Log);
    }
}
