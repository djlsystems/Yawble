using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Scripts.Language;

namespace Yawble.Core.Tests.Scripts.Language;

public sealed class ScriptDocumentTests
{
    [Fact]
    public void TheExampleParses_WithEveryPart()
    {
        var doc = ScriptDocument.Parse(ScriptSamples.InboxSmsAlert);

        Assert.Equal("yawble-script/1", doc.Format);
        Assert.Equal("inbox-sms-alert", doc.Id);
        Assert.Equal("Inbox SMS alert", doc.Name);
        Assert.Equal(["every", "cron", "once"], doc.Trigger.Accepts);
        Assert.Equal(["recipients", "fromNumber", "folder"], doc.Settings.Select(s => s.Name));
        Assert.Equal("INBOX", (string?)doc.Settings[2].Default);
        Assert.Equal("email", doc.Requires[0].Kind);
        Assert.Equal(["POST /2010-04-01/Accounts/{AccountSid}/Messages.json"], doc.Requires[1].Actions);
        Assert.True(doc.Variables[0].IsSaved);
        Assert.Equal(0, (int?)doc.Variables[2].Initial);
        Assert.Equal("list", doc.Start);
        Assert.Equal(9, doc.Steps.Count);
        Assert.Equal("forEach", doc.Step("each")!.Type);
        Assert.Equal([("body", "text"), ("then", null)], doc.Step("phones")!.Successors().Select(s => (s.Property, s.Target)));
        Assert.Empty(doc.Step("done")!.Successors());
    }

    [Fact]
    public void SerializeThenParse_KeepsTheDocumentAndEveryStepProperty()
    {
        var doc = ScriptDocument.Parse(ScriptSamples.InboxSmsAlert);
        var again = ScriptDocument.Parse(doc.ToJsonString());

        Assert.True(JsonNode.DeepEquals(doc.ToJson(), again.ToJson()));
        var original = JsonNode.Parse(ScriptSamples.InboxSmsAlert)!["steps"]!;
        Assert.True(JsonNode.DeepEquals(original, again.ToJson()["steps"]));
        Assert.Equal("{settings.folder}", (string?)again.Step("list")!["args"]!["folder"]);
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("[1]", "a JSON object")]
    [InlineData("""{ "format": "yawble-script/2", "id": "x", "start": "a", "steps": [] }""", "is not 'yawble-script/1'")]
    [InlineData("""{ "id": "x", "start": "a", "steps": [] }""", "has no 'format'")]
    [InlineData("""{ "format": "yawble-script/1", "start": "a", "steps": [] }""", "has no 'id'")]
    [InlineData("""{ "format": "yawble-script/1", "id": "x", "steps": [] }""", "has no 'start'")]
    [InlineData("""{ "format": "yawble-script/1", "id": "x", "start": "a", "steps": [ { "type": "log" } ] }""", "steps[0] has no 'id'")]
    [InlineData("""{ "format": "yawble-script/1", "id": "x", "start": "a", "steps": [ { "id": "a" } ] }""", "steps[0] has no 'type'")]
    [InlineData("""{ "format": "yawble-script/1", "id": "x", "start": "a", "steps": {} }""", "'steps' is not a list")]
    [InlineData("""{ "format": "yawble-script/1", "id": "x", "start": "a", "settings": [ { "name": "s" } ], "steps": [] }""", "settings[0] has no 'type'")]
    [InlineData("""{ "format": "yawble-script/1", "id": "x", "start": "a", "trigger": { "accepts": [1] }, "steps": [] }""", "trigger.accepts[0] is not text")]
    public void AMalformedDocument_IsRefusedWithTheReason(string json, string reason)
    {
        var e = Assert.Throws<ScriptDocumentException>(() => ScriptDocument.Parse(json));
        Assert.Contains(reason, e.Message);
    }

    [Fact]
    public void NameDefaultsToTheId_AndOptionalPartsAreEmpty()
    {
        var doc = ScriptDocument.Parse("""{ "format": "yawble-script/1", "id": "x", "start": "a", "steps": [ { "id": "a", "type": "end" } ] }""");
        Assert.Equal("x", doc.Name);
        Assert.Empty(doc.Settings);
        Assert.Empty(doc.Requires);
        Assert.Empty(doc.Variables);
        Assert.Empty(doc.Trigger.Accepts);
    }
}

public sealed class ScriptTypeTests
{
    private static ScriptSchema? None(string _) => null;

    [Theory]
    [InlineData("int", "int")]
    [InlineData("float", "float")]
    [InlineData("string", "string")]
    [InlineData("bool", "bool")]
    [InlineData("date", "date")]
    [InlineData("datetime", "datetime")]
    [InlineData("duration", "duration")]
    [InlineData("list<string>", "list<string>")]
    [InlineData("list<list<int>>", "list<list<int>>")]
    [InlineData("EmailMessage", "EmailMessage")]
    [InlineData("list<EmailMessage>", "list<EmailMessage>")]
    [InlineData("enum(order, proof, spam)", "enum(order, proof, spam)")]
    public void ATypeParses(string text, string shown)
    {
        var type = ScriptType.Parse(text, None, out var error);
        Assert.Null(error);
        Assert.Equal(shown, type!.ToString());
    }

    [Theory]
    [InlineData("", "no type")]
    [InlineData("Nope", "no schema named 'Nope'")]
    [InlineData("list<Nope>", "no schema named 'Nope'")]
    [InlineData("enum()", "has no values")]
    [InlineData("list<int", "is not a type")]
    public void ABadTypeGivesTheReason(string text, string reason)
    {
        Assert.Null(ScriptType.Parse(text, None, out var error));
        Assert.Contains(reason, error);
    }

    [Fact]
    public void ALibrarySchemaIsAnObject_AndAStringEnumSchemaIsANamedEnum()
    {
        var triage = ScriptSchema.Parse("Triage", """
            { "type": "object", "properties": { "emailId": { "type": "string" },
              "category": { "type": "string", "enum": ["order", "proof", "inquiry", "spam", "other"] },
              "escalate": { "type": "boolean" } }, "required": ["emailId"] }
            """);
        var kind = ScriptSchema.Parse("Kind", """{ "type": "string", "enum": ["a", "b"] }""");
        var lookup = new ScriptSchemaSet([triage, kind]);

        var t = ScriptType.Parse("Triage", lookup.Find, out _)!;
        Assert.Equal(ScriptTypeKind.Object, t.Kind);
        Assert.True(t.TryGetField("category", lookup.Find, out var category));
        Assert.Equal(ScriptTypeKind.Enum, category.Kind);
        Assert.Equal(5, category.EnumValues.Count);
        Assert.False(t.TryGetField("missing", lookup.Find, out _));
        var fields = triage.Fields(lookup.Find);
        Assert.Contains(fields, f => f.Field == "emailId" && f.Required && f.Type.Kind == ScriptTypeKind.String);
        Assert.Contains(fields, f => f.Field == "escalate" && !f.Required && f.Type.Kind == ScriptTypeKind.Bool);

        var k = ScriptType.Parse("Kind", lookup.Find, out _)!;
        Assert.Equal(ScriptTypeKind.Enum, k.Kind);
        Assert.Equal("Kind", k.ToString());
    }

    [Fact]
    public void BuiltInSchemaFieldsHaveTheirTypes_IncludingRefsAndFormats()
    {
        var m = ScriptType.FromSchema(BuiltInSchemas.EmailMessage);
        Assert.True(m.TryGetField("from", None, out var from));
        Assert.Equal("EmailAddress", from.Name);
        Assert.True(m.TryGetField("to", None, out var to));
        Assert.Equal("list<EmailAddress>", to.ToString());
        Assert.True(m.TryGetField("receivedAt", None, out var at));
        Assert.Equal(ScriptTypeKind.DateTime, at.Kind);
        Assert.True(m.TryGetField("threadId", None, out var thread));
        Assert.Equal(ScriptTypeKind.String, thread.Kind);
        Assert.True(ScriptType.FromSchema(BuiltInSchemas.TriggerFired).TryGetField("kind", None, out var kind));
        Assert.Equal(ScriptTypeKind.Enum, kind.Kind);
        Assert.True(ScriptType.FromSchema(BuiltInSchemas.ApiResponse).TryGetField("body", None, out var body));
        Assert.Equal(ScriptTypeKind.Any, body.Kind);
    }

    [Theory]
    [InlineData("float", "int", true)]
    [InlineData("int", "float", false)]
    [InlineData("string", "int", true)]
    [InlineData("int", "string", false)]
    [InlineData("datetime", "string", true)]
    [InlineData("datetime", "date", true)]
    [InlineData("date", "datetime", false)]
    [InlineData("datetime", "int", false)]
    [InlineData("list<string>", "list<string>", true)]
    [InlineData("list<string>", "string", false)]
    [InlineData("string", "list<string>", false)]
    [InlineData("list<float>", "list<int>", true)]
    [InlineData("EmailMessage", "EmailAddress", false)]
    [InlineData("EmailMessage", "EmailMessage", true)]
    [InlineData("enum(a, b)", "string", true)]
    [InlineData("enum(a, b)", "enum(a)", true)]
    [InlineData("enum(a)", "enum(a, b)", false)]
    [InlineData("bool", "int", false)]
    public void Assignability(string target, string source, bool ok) =>
        Assert.Equal(ok, ScriptType.IsAssignable(ScriptType.Parse(target, None, out _)!, ScriptType.Parse(source, None, out _)!));

    [Fact]
    public void AnyAndNullGoAnywhere()
    {
        Assert.True(ScriptType.IsAssignable(ScriptType.Int, ScriptType.Any));
        Assert.True(ScriptType.IsAssignable(ScriptType.ListOf(ScriptType.Int), ScriptType.Null));
        Assert.True(ScriptType.IsAssignable(ScriptType.Any, ScriptType.ListOf(ScriptType.Int)));
    }
}

public sealed class JsonSchemaValidatorTests
{
    private static JsonObject S(string json) => JsonNode.Parse(json)!.AsObject();
    private static IReadOnlyList<string> V(string schema, string? value) =>
        JsonSchemaValidator.Validate(S(schema), value is null ? null : JsonNode.Parse(value));

    [Theory]
    [InlineData("""{ "type": "string" }""", "\"x\"", true)]
    [InlineData("""{ "type": "string" }""", "1", false)]
    [InlineData("""{ "type": "integer" }""", "3", true)]
    [InlineData("""{ "type": "integer" }""", "3.5", false)]
    [InlineData("""{ "type": "number" }""", "3.5", true)]
    [InlineData("""{ "type": "boolean" }""", "true", true)]
    [InlineData("""{ "type": "boolean" }""", "\"true\"", false)]
    [InlineData("""{ "type": "array" }""", "[]", true)]
    [InlineData("""{ "type": "object" }""", "[]", false)]
    [InlineData("""{ "type": ["string", "null"] }""", "null", true)]
    [InlineData("""{ "type": "string" }""", "null", false)]
    [InlineData("""{ }""", "{\"any\": 1}", true)]
    public void Type(string schema, string value, bool ok) => Assert.Equal(ok, V(schema, value).Count == 0);

    [Fact]
    public void PropertiesRequiredItemsAndEnum()
    {
        const string schema = """
            { "type": "object", "required": ["id", "tags"],
              "properties": { "id": { "type": "string" }, "kind": { "enum": ["a", "b"] },
                "tags": { "type": "array", "items": { "type": "string" } } } }
            """;
        Assert.Empty(V(schema, """{ "id": "1", "kind": "a", "tags": ["x"], "extra": 5 }"""));
        Assert.Contains("$: 'tags' is required", V(schema, """{ "id": "1" }"""));
        Assert.Contains(V(schema, """{ "id": 1, "tags": [] }"""), e => e.StartsWith("$.id: expected string", StringComparison.Ordinal));
        Assert.Contains(V(schema, """{ "id": "1", "tags": ["x", 2] }"""), e => e.StartsWith("$.tags[1]: expected string", StringComparison.Ordinal));
        Assert.Contains(V(schema, """{ "id": "1", "tags": [], "kind": "c" }"""), e => e.StartsWith("$.kind: '\"c\"' is not one of", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("date-time", "2026-11-23T18:00:00Z", true)]
    [InlineData("date-time", "2026-11-23T18:00:00+02:00", true)]
    [InlineData("date-time", "2026-11-23", false)]
    [InlineData("date-time", "yesterday", false)]
    [InlineData("date", "2026-11-23", true)]
    [InlineData("date", "2026-02-30", false)]
    [InlineData("date", "23/11/2026", false)]
    public void Format(string format, string value, bool ok) =>
        Assert.Equal(ok, V($$"""{ "type": "string", "format": "{{format}}" }""", $"\"{value}\"").Count == 0);

    [Fact]
    public void ARefNamesAnotherSchema()
    {
        var good = ScriptSamples.Email("1", "Hi", "a@example.com", "2026-11-23T10:00:00Z");
        Assert.Empty(BuiltInSchemas.EmailMessage.Validate(good));

        var bad = (JsonObject)good.DeepClone();
        bad["from"] = new JsonObject { ["name"] = "No address" };
        bad["receivedAt"] = "soon";
        var errors = BuiltInSchemas.EmailMessage.Validate(bad);
        Assert.Contains("$.from: 'address' is required", errors);
        Assert.Contains("$.receivedAt: 'soon' is not a date-time", errors);

        Assert.Contains(V("""{ "$ref": "Missing" }""", "{}"), e => e.Contains("no schema named 'Missing'"));
    }

    [Fact]
    public void EveryBuiltInSchemaIsThere_ReadOnly()
    {
        Assert.Equal(["TriggerFired", "FolderChange", "EventItem", "EmailAddress", "EmailMessage", "EmailList", "ApiResponse"], BuiltInSchemas.All.Select(s => s.Name));
        Assert.All(BuiltInSchemas.All, s => Assert.True(s.BuiltIn));
        Assert.Empty(BuiltInSchemas.TriggerFired.Validate(JsonNode.Parse("""{ "triggerId": "t1", "kind": "every", "firedAt": "2026-11-23T10:00:00Z" }""")));
        Assert.NotEmpty(BuiltInSchemas.TriggerFired.Validate(JsonNode.Parse("""{ "triggerId": "t1", "kind": "folder", "firedAt": "2026-11-23T10:00:00Z" }""")));
        Assert.Empty(BuiltInSchemas.FolderChange.Validate(JsonNode.Parse("""{ "root": "C:/in", "added": ["a.txt"], "removed": [], "changed": [] }""")));
        Assert.Empty(BuiltInSchemas.EventItem.Validate(JsonNode.Parse("""{ "event": "order", "item": { "nested": [1, 2] } }""")));
        Assert.Empty(BuiltInSchemas.ApiResponse.Validate(JsonNode.Parse("""{ "status": 201, "body": "text" }""")));
        Assert.NotEmpty(BuiltInSchemas.ApiResponse.Validate(JsonNode.Parse("""{ "status": "ok" }""")));
    }
}

/// <summary>The designer's <c>layout</c> section and other top-level members the format does not name.</summary>
public sealed class ScriptDocumentLayoutTests
{

    [Fact]
    public void Layout_AndUnknownTopLevelMembers_SurviveParseThenToJson()
    {
        var text = ScriptSamples.WithLayout();
        var original = JsonNode.Parse(text)!.AsObject();
        var doc = ScriptDocument.Parse(text);

        Assert.NotNull(doc.Layout);
        Assert.Equal(original["layout"]!.ToJsonString(), doc.Layout!.ToJsonString());
        Assert.Equal(["x-editor", "comment"], doc.Extensions.Select(e => e.Key));

        var saved = doc.ToJson();
        // The layout section is written exactly as given (same members, order, numbers and text).
        Assert.Equal(original["layout"]!.ToJsonString(), saved["layout"]!.ToJsonString());
        Assert.Equal(original["x-editor"]!.ToJsonString(), saved["x-editor"]!.ToJsonString());
        Assert.Equal("kept too", (string?)saved["comment"]);
        // And again through text: a second save is the same as the first.
        var again = ScriptDocument.Parse(doc.ToJsonString());
        Assert.Equal(doc.ToJsonString(), again.ToJsonString());
        Assert.Equal(original["layout"]!.ToJsonString(), again.ToJson()["layout"]!.ToJsonString());
        // The whole document still equals what was given, member for member.
        foreach (var (name, value) in original)
            Assert.True(JsonNode.DeepEquals(value, saved[name]), $"'{name}' changed on save");
    }

    [Fact]
    public void ToJson_WritesCopies_SoChangingTheSavedJsonLeavesTheDocument()
    {
        var doc = ScriptDocument.Parse(ScriptSamples.WithLayout());
        var before = doc.ToJsonString();

        var saved = doc.ToJson();
        saved["layout"]!["nodes"]!["list"]!["x"] = 999;
        saved["x-editor"]!["tabs"]!.AsArray().Add("more");

        Assert.Equal(before, doc.ToJsonString());
    }

    [Fact]
    public void ALayoutThatIsNotAnObject_IsKeptAsAnUnknownMember()
    {
        var text = ScriptSamples.InboxSmsAlert.TrimEnd().TrimEnd('}') + """, "layout": [1, 2] }""";
        var doc = ScriptDocument.Parse(text);

        Assert.Null(doc.Layout);
        Assert.Equal("[1,2]", doc.ToJson()["layout"]!.ToJsonString());
    }

    [Fact]
    public void ADocumentWithoutLayout_SavesWithoutOne()
    {
        var doc = ScriptDocument.Parse(ScriptSamples.InboxSmsAlert);
        Assert.Null(doc.Layout);
        Assert.Empty(doc.Extensions);
        Assert.False(doc.ToJson().ContainsKey("layout"));
    }

    [Fact]
    public void TheCheckerIgnoresLayoutAndUnknownMembers()
    {
        var plain = ScriptChecker.Check(ScriptDocument.Parse(ScriptSamples.InboxSmsAlert), ScriptSchemaSet.Empty, FakeCatalog.MailAndSms());
        var withLayout = ScriptChecker.Check(ScriptDocument.Parse(ScriptSamples.WithLayout()), ScriptSchemaSet.Empty, FakeCatalog.MailAndSms());

        Assert.True(withLayout.Ok, string.Join("; ", withLayout.Issues.Select(i => i.Message)));
        Assert.Equal(plain.Issues, withLayout.Issues);
        Assert.Equal(plain.SchemaReferences, withLayout.SchemaReferences);
    }
}