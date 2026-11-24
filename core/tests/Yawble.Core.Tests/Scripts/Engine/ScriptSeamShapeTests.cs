using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Scripts.Engine;
using Yawble.Core.Scripts.Language;

namespace Yawble.Core.Tests.Scripts.Engine;

/// <summary>The seam shapes card 584 codes against: static entry points, the built-in lookup, the checkpoint as plain JSON.</summary>
public sealed class ScriptSeamShapeTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void BuiltInSchemas_LookupFindsEveryBuiltIn_AndNamesListsThem()
    {
        Assert.Equal(["TriggerFired", "FolderChange", "EventItem", "EmailAddress", "EmailMessage", "EmailList", "ApiResponse"], BuiltInSchemas.Names);
        foreach (var name in BuiltInSchemas.Names)
            Assert.True(BuiltInSchemas.Lookup.Find(name) is { BuiltIn: true } s && s.Name == name, name);
        Assert.Null(BuiltInSchemas.Lookup.Find("Nope"));
    }

    [Fact]
    public void ScriptSchemaParse_TakesTheNameAndTheJsonSchemaText()
    {
        var s = ScriptSchema.Parse("Order", """{ "type": "object", "properties": { "id": { "type": "string" } }, "required": ["id"] }""");
        Assert.Equal(("Order", false), (s.Name, s.BuiltIn));
        Assert.Empty(s.Validate(new JsonObject { ["id"] = "1" }));
        Assert.Throws<FormatException>(() => ScriptSchema.Parse("Bad", "[]"));
    }

    [Fact]
    public void ABadDocumentThrowsAFormatException()
    {
        Assert.ThrowsAny<FormatException>(() => ScriptDocument.Parse("""{ "format": "not-a-script" }"""));
    }

    [Fact]
    public async Task StaticRunAsync_ACheckpointRoundTripsThroughDefaultJsonSerializer_AndResumes()
    {
        var doc = ScriptSamples.Script("""
            [ { "id": "a", "type": "forEach", "items": "{vars.l}", "as": "x", "body": "b", "then": null },
              { "id": "b", "type": "set", "var": "sum", "value": "{vars.sum + x}", "next": null } ]
            """, variables: """[ { "name": "l", "type": "list<int>", "initial": [1, 2, 3] }, { "name": "sum", "type": "int", "initial": 0 } ]""");
        var journal = new RecordingJournal();
        var request = new ScriptRunRequest { Document = doc, Actions = new FakeActions(), DataFolder = _temp.Sub("d") };
        var first = await ScriptEngine.RunAsync(request, journal, TestContext.Current.CancellationToken);
        Assert.Equal("succeeded", first.Status.ToString(), ignoreCase: true);

        // The checkpoint after the first loop item, stored the way card 584 stores it (System.Text.Json defaults).
        var cp = ScriptRunCheckpoint.FromJson(journal.Completed[2].Checkpoint);
        var stored = JsonSerializer.Serialize(cp);
        var back = JsonSerializer.Deserialize<ScriptRunCheckpoint>(stored)!;
        Assert.Equal(("b", 1, 1L), (back.Next, back.Frames[0].Index, (long)back.Vars["sum"]!));

        var second = new RecordingJournal();
        var resumed = await ScriptEngine.RunAsync(request with { ResumeFrom = back }, second, TestContext.Current.CancellationToken);
        Assert.Equal(ScriptRunStatus.Succeeded, resumed.Status);
        Assert.Equal(["b", "b"], second.Completed.Select(c => c.Record.StepId));
        Assert.Equal(6L, (long)second.LastCheckpoint.Vars["sum"]!);
    }
}
