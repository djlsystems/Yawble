using Xunit;
using Yawble.Core.Scripts.Engine;
using Yawble.Core.Scripts.Language;

namespace Yawble.Core.Tests.Scripts.Engine;

/// <summary>
/// The while step in the engine: a condition false at first never runs the body, the loop runs until the condition is false,
/// passing max fails the run, the loop cap and step budget still apply, a null successor inside the body ends the pass, and a run
/// cut off mid-loop resumes from its checkpoint with the right pass count.
/// </summary>
public sealed class ScriptWhileEngineTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempFolder _temp = new();
    public void Dispose() => _temp.Dispose();

    private const string Vars = """
        [{ "name": "n", "type": "int", "initial": 0 }, { "name": "seen", "type": "string", "initial": "" }, { "name": "after", "type": "string", "initial": "" }]
        """;

    /// <summary>While n is below <paramref name="until"/>: add one to n (b), note it (c); then d notes the end.</summary>
    private static ScriptDocument Counter(int until, string max = "", int initial = 0) => ScriptSamples.Script($$"""
        [{ "id": "a", "type": "while", "when": { "left": "{vars.n}", "op": "lt", "right": {{until}} }, "body": "b", "then": "d" {{max}} },
         { "id": "b", "type": "set", "var": "n", "value": "{vars.n + 1}", "next": "c" },
         { "id": "c", "type": "set", "var": "seen", "value": "{vars.seen}{vars.n}", "next": null },
         { "id": "d", "type": "set", "var": "after", "value": "done at {vars.n}", "next": null }]
        """, Vars.Replace("\"initial\": 0", $"\"initial\": {initial}", StringComparison.Ordinal));

    private ScriptRunRequest Request(ScriptDocument doc) => new() { Document = doc, Actions = new FakeActions(), DataFolder = _temp.Sub("d") };

    private async Task<(ScriptRunResult Result, RecordingJournal Journal)> RunAsync(ScriptRunRequest request)
    {
        var journal = new RecordingJournal();
        return (await ScriptEngine.RunAsync(request, journal, Ct), journal);
    }

    [Fact]
    public async Task AConditionFalseAtFirst_NeverRunsTheBody_AndGoesToThen()
    {
        var (result, journal) = await RunAsync(Request(Counter(until: 3, initial: 5)));
        Assert.Equal(ScriptRunStatus.Succeeded, result.Status);
        Assert.Equal(["start", "a", "d"], journal.CompletedSteps);
        Assert.Equal("", journal.LastCheckpoint.Vars["seen"]!.GetValue<string>());
        Assert.Equal("done at 5", journal.LastCheckpoint.Vars["after"]!.GetValue<string>());
        Assert.Empty(journal.LastCheckpoint.Frames);
    }

    [Fact]
    public async Task TheLoop_RunsUntilTheConditionIsFalse_CheckingItBeforeEachPass()
    {
        var (result, journal) = await RunAsync(Request(Counter(until: 3)));
        Assert.Equal(ScriptRunStatus.Succeeded, result.Status);
        Assert.Equal("123", journal.LastCheckpoint.Vars["seen"]!.GetValue<string>());
        Assert.Equal("done at 3", journal.LastCheckpoint.Vars["after"]!.GetValue<string>());
        Assert.Equal(["start", "a", "b", "c", "a", "b", "c", "a", "b", "c", "a", "d"], journal.CompletedSteps);
        // Each check of the condition is a step of its own: its output says how many passes are done.
        var checks = journal.Completed.Where(c => c.Record.StepId == "a").Select(c => c.Record.Output!).ToList();
        Assert.Equal([0, 1, 2, 3], checks.Select(o => o["passes"]!.GetValue<int>()));
        Assert.Equal([true, true, true, false], checks.Select(o => o["result"]!.GetValue<bool>()));
        Assert.Empty(journal.LastCheckpoint.Frames);
    }

    [Fact]
    public async Task PassingMax_FailsTheRun_WithTheLoopsId()
    {
        var (result, journal) = await RunAsync(Request(Counter(until: 100, max: """, "max": 4""")));
        Assert.Equal(ScriptRunStatus.Failed, result.Status);
        Assert.Equal("a", result.FailedStepId);
        Assert.Equal("the loop 'a' ran 4 times (its max) and its condition still holds", result.Reason);
        Assert.Equal("1234", journal.LastCheckpoint.Vars["seen"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExactlyMaxPasses_ThenAFalseCondition_Succeeds()
    {
        var (result, journal) = await RunAsync(Request(Counter(until: 4, max: """, "max": 4""")));
        Assert.Equal(ScriptRunStatus.Succeeded, result.Status);
        Assert.Equal("done at 4", journal.LastCheckpoint.Vars["after"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheDefaultMax_Is100()
    {
        Assert.Equal(100, ScriptLoop.DefaultMax);
        var (result, _) = await RunAsync(Request(Counter(until: 1000)));
        Assert.Equal(ScriptRunStatus.Failed, result.Status);
        Assert.Contains("ran 100 times", result.Reason);
    }

    [Fact]
    public async Task TheLoopCap_AndTheStepBudget_StillApply()
    {
        var (capped, _) = await RunAsync(Request(Counter(until: 1000, max: """, "max": 500""")) with { LoopCap = 10 });
        Assert.Equal(ScriptRunStatus.Failed, capped.Status);
        Assert.Equal("the loop 'a' reached the loop cap of 10 passes", capped.Reason);

        var (budget, _) = await RunAsync(Request(Counter(until: 1000, max: """, "max": 500""")) with { StepBudget = 20 });
        Assert.Equal(ScriptRunStatus.Failed, budget.Status);
        Assert.Contains("step budget of 20", budget.Reason);
    }

    [Fact]
    public async Task AnEndStepInsideTheBody_EndsTheRun()
    {
        var doc = ScriptSamples.Script("""
            [{ "id": "a", "type": "while", "when": { "left": "{vars.n}", "op": "lt", "right": 10 }, "body": "b", "then": "d" },
             { "id": "b", "type": "set", "var": "n", "value": "{vars.n + 1}", "next": "e" },
             { "id": "e", "type": "if", "when": { "left": "{vars.n}", "op": "eq", "right": 2 }, "then": "x", "else": null },
             { "id": "x", "type": "end" },
             { "id": "d", "type": "set", "var": "after", "value": "never", "next": null }]
            """, Vars);
        var (result, journal) = await RunAsync(Request(doc));
        Assert.Equal(ScriptRunStatus.Succeeded, result.Status);
        Assert.Equal(2, journal.LastCheckpoint.Vars["n"]!.GetValue<int>());
        Assert.Equal("", journal.LastCheckpoint.Vars["after"]!.GetValue<string>());
    }

    [Fact]
    public async Task AWhileInsideAForEach_StartsAgainForEachItem()
    {
        var doc = ScriptSamples.Script("""
            [{ "id": "f", "type": "forEach", "items": ["x", "y"], "as": "item", "body": "z", "then": null },
             { "id": "z", "type": "set", "var": "n", "value": 0, "next": "a" },
             { "id": "a", "type": "while", "when": { "left": "{vars.n}", "op": "lt", "right": 2 }, "body": "b", "then": null },
             { "id": "b", "type": "set", "var": "n", "value": "{vars.n + 1}", "next": "c" },
             { "id": "c", "type": "set", "var": "seen", "value": "{vars.seen}{item}{vars.n}", "next": null }]
            """, Vars, start: "f");
        var (result, journal) = await RunAsync(Request(doc));
        Assert.Equal(ScriptRunStatus.Succeeded, result.Status);
        Assert.Equal("x1x2y1y2", journal.LastCheckpoint.Vars["seen"]!.GetValue<string>());
    }

    [Fact]
    public async Task ARunCutOffMidLoop_ResumesFromItsCheckpoint_WithTheRightPassCount()
    {
        // Cut off after the 6th completed step: start, a, b, c, a, b — mid way through the second pass.
        using var cancel = new CancellationTokenSource();
        var cut = new RecordingJournal { CancelAfterCompleted = 6, Cancel = cancel };
        var request = Request(Counter(until: 5, max: """, "max": 3"""));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScriptEngine.RunAsync(request, cut, cancel.Token));
        var cp = cut.LastCheckpoint;
        Assert.Equal("c", cp.Next);
        var frame = Assert.Single(cp.Frames);
        Assert.Equal(("a", true, 1), (frame.StepId, frame.IsWhile, frame.Index));

        // Resumed: the second pass finishes, a third runs, and the max of 3 is counted across the cut.
        var (resumed, journal) = await RunAsync(request with { ResumeFrom = ScriptRunCheckpoint.FromJson(cp.ToJson()) });
        Assert.Equal(ScriptRunStatus.Failed, resumed.Status);
        Assert.Equal("the loop 'a' ran 3 times (its max) and its condition still holds", resumed.Reason);
        Assert.Equal(["c", "a", "b", "c"], journal.CompletedSteps);
        Assert.Equal("123", journal.LastCheckpoint.Vars["seen"]!.GetValue<string>());
    }

    [Fact]
    public async Task ARunCutOffMidLoop_ThatEndsNormally_FinishesWithTheSameResultAsOneThatWasNot()
    {
        using var cancel = new CancellationTokenSource();
        var cut = new RecordingJournal { CancelAfterCompleted = 8, Cancel = cancel };
        var request = Request(Counter(until: 4));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScriptEngine.RunAsync(request, cut, cancel.Token));
        var (resumed, journal) = await RunAsync(request with { ResumeFrom = cut.LastCheckpoint });
        Assert.Equal(ScriptRunStatus.Succeeded, resumed.Status);
        Assert.Equal("1234", journal.LastCheckpoint.Vars["seen"]!.GetValue<string>());
        Assert.Equal("done at 4", journal.LastCheckpoint.Vars["after"]!.GetValue<string>());
        Assert.Empty(journal.LastCheckpoint.Frames);
    }

    [Fact]
    public void ALoopFrameFromBeforeWhile_StillReadsBack()
    {
        var cp = ScriptRunCheckpoint.FromJson("""{ "next": "b", "frames": [{ "stepId": "f", "as": "item", "items": [1, 2], "index": 1 }] }""");
        var frame = Assert.Single(cp.Frames);
        Assert.Equal(("f", "item", false, 1, 2), (frame.StepId, frame.As, frame.IsWhile, frame.Index, frame.Items.Count));
    }
}
