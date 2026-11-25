using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Xunit;
using Yawble.Core.Scripts.Language;
using Yawble.Core.Scripts.Library;
using Yawble.Core.Storage;
using Yawble.Core.Tests.Bus;

namespace Yawble.Core.Tests.Scripts;

/// <summary>Migration 0056, the script library (save, draft, versions, export/import) and schemas (break refusal).</summary>
public class ScriptLibraryTests
{
    private static ScriptLibrary Library(TempDb db, FakeActionCatalog? catalog = null) =>
        new(db.ConnectionString, () => catalog ?? new FakeActionCatalog(), FakeChecker.Check);

    private static object? Scalar(TempDb db, string sql)
    {
        using var c = new SqliteConnection(db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void Exec(TempDb db, string sql)
    {
        using var c = new SqliteConnection(db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ migration

    [Fact]
    public void Migration0056_AddsTheTables_MembersKindDefaultsToAgent_AndRunRecordsTakeScript()
    {
        using var db = new TempDb();
        Assert.Contains("0056_scripts", SchemaMigrator.Available);
        foreach (var table in new[] { "scripts", "script_versions", "schemas", "schema_versions", "script_member_state", "script_runs", "script_run_steps" })
            Assert.Equal(1L, Scalar(db, $"SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}';"));
        Exec(db, "INSERT INTO teams (name, created_at) VALUES ('Alpha', 'now');");
        Exec(db, "INSERT INTO members (team_id, name, role, created_at) VALUES (1, 'Ada', 'member', 'now');");
        Assert.Equal("agent", Scalar(db, "SELECT kind FROM members WHERE name = 'Ada';"));
        Assert.Throws<SqliteException>(() => Exec(db, "UPDATE members SET kind = 'robot';"));
        Exec(db, "INSERT INTO run_records (run_key, team, member, cli, kind, backend, started_at) VALUES ('a', 'Alpha', 'Ada', 'claude', 'headless', 'x', 'now');");
        Exec(db, "INSERT INTO run_records (run_key, team, member, cli, kind, backend, started_at) VALUES ('b', 'Alpha', 'Ada', 'script', 'script', 'script', 'now');");
        Assert.Throws<SqliteException>(() =>
            Exec(db, "INSERT INTO run_records (run_key, team, member, cli, kind, backend, started_at) VALUES ('c', 'Alpha', 'Ada', 'x', 'robot', 'x', 'now');"));
        // The rebuilt table keeps every column the launcher reads.
        Assert.Equal(2, new Yawble.Core.Launch.RunRecordStore(db.ConnectionString).All().Count);
    }

    // ------------------------------------------------------------------ scripts

    [Fact]
    public void ACleanSaveIsRunnable_AFailingSaveIsKeptAsADraft_AndTheRunnableVersionStays()
    {
        using var db = new TempDb();
        var lib = Library(db);
        var first = lib.Save(Docs.Clean());
        Assert.Equal((1, true, false), (first.Version, first.Ok, first.Draft));
        Assert.Equal(1, lib.RunnableVersion(first.Id));

        var draft = lib.Save(Docs.Draft());
        Assert.Equal((2, false, true), (draft.Version, draft.Ok, draft.Draft));
        Assert.Contains("teleport", draft.Check.ToJsonString());
        Assert.Equal(1, lib.RunnableVersion(first.Id)); // a member on latest keeps running version 1

        var summary = Assert.Single(lib.Scripts());
        Assert.Equal(("poller", 2, 1, true), (summary.Key, summary.LatestVersion, summary.RunnableVersion, summary.Draft));
        Assert.Equal([2, 1], lib.Versions(first.Id).Select(v => v.Version));
        Assert.False(lib.Get(first.Id).Ok);
        Assert.True(lib.Get(first.Id, 1).Ok);
        Assert.Equal(first.Id, lib.ScriptId("poller"));
        Assert.Equal(first.Id, lib.ScriptId(first.Id.ToString()));
    }

    [Fact]
    public void AScriptThatNeverPassedHasNoRunnableVersion_AndTheCatalogDecides()
    {
        using var db = new TempDb();
        var lib = Library(db);
        var draft = lib.Save(Docs.Draft("needs-mail"));
        Assert.Null(lib.RunnableVersion(draft.Id));
        // Once the action exists (SC-2/SC-3 fill the catalog), the same document checks clean.
        var withMail = Library(db, new FakeActionCatalog(("email", "teleport")));
        Assert.True(withMail.Save(Docs.Draft("needs-mail")).Ok);
        Assert.Equal(2, withMail.RunnableVersion(draft.Id));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"format":"yawble-script/1","name":"no id"}""")]
    [InlineData("""{"format":"yawble-script/1","id":"x"}""")]
    public void TextThatIsNotAScriptDocumentIsRefused_NothingSaved(string text)
    {
        using var db = new TempDb();
        var lib = Library(db);
        var e = Assert.Throws<ScriptLibraryException>(() => lib.Save(text));
        Assert.Equal("bad_script", e.Code);
        Assert.Empty(lib.Scripts());
    }

    [Fact]
    public void AWrongFormatIsADraft_CheckSavesNothing()
    {
        using var db = new TempDb();
        var lib = Library(db);
        var doc = JsonNode.Parse(Docs.Clean())!.AsObject();
        doc["format"] = "yawble-script/0";
        var check = lib.Check(doc.ToJsonString());
        Assert.False(check["ok"]!.GetValue<bool>());
        Assert.Empty(lib.Scripts());
        Assert.True(lib.Save(doc.ToJsonString()).Draft);
    }

    [Fact]
    public void ExportAndImportRoundTrip_AsANewVersion()
    {
        using var db = new TempDb();
        var lib = Library(db);
        var saved = lib.Save(Docs.Clean());
        var file = lib.Export(saved.Id);
        Assert.Equal("poller.json", file.FileName);
        var again = lib.Import(file.Json);
        Assert.Equal((saved.Id, 2, true), (again.Id, again.Version, again.Ok));
        Assert.True(JsonNode.DeepEquals(lib.Get(saved.Id, 1).Document, lib.Get(saved.Id, 2).Document));
    }

    // ------------------------------------------------------------------ schemas

    private const string Thing = """{"type":"object","properties":{"label":{"type":"string"},"count":{"type":"integer"},"from":{"type":"object","properties":{"address":{"type":"string"}}}},"required":["label"]}""";

    private static JsonArray Refs(params string[] paths) =>
        new(paths.Select(p => (JsonNode?)new JsonObject { ["schema"] = "Thing", ["fieldPath"] = p, ["stepId"] = "b" }).ToArray());

    [Fact]
    public void ASchemaSaveThatBreaksAScriptsReferenceIsRefused_ListingTheScriptAndStep()
    {
        using var db = new TempDb();
        var lib = Library(db);
        Assert.Equal(1, lib.SaveSchema("Thing", "a thing", Thing).Version);
        var script = lib.Save(Docs.Clean(refs: Refs("label", "from.address")));
        Assert.True(script.Ok);

        // Removing label: refused, nothing saved.
        var noLabel = """{"type":"object","properties":{"count":{"type":"integer"},"from":{"type":"object","properties":{"address":{"type":"string"}}}}}""";
        var e = Assert.Throws<ScriptLibraryException>(() => lib.SaveSchema("Thing", null, noLabel));
        Assert.Equal("schema_breaks", e.Code);
        Assert.Contains("poller v1, step b: uses Thing.label", e.Message);
        Assert.Equal(1, lib.GetSchema("Thing").Version);
        Assert.Single(lib.CheckSchema("Thing", noLabel));

        // Retyping a nested field a step uses: refused too.
        var retyped = Thing.Replace("""{"address":{"type":"string"}}""", """{"address":{"type":"integer"}}""");
        Assert.Equal("from.address", Assert.Single(lib.CheckSchema("Thing", retyped)).FieldPath);

        // Changing a field no script uses, or adding one: saved as a new version.
        var widened = Thing.Replace("\"count\":{\"type\":\"integer\"}", "\"count\":{\"type\":\"number\"},\"extra\":{\"type\":\"boolean\"}");
        Assert.Empty(lib.CheckSchema("Thing", widened));
        Assert.Equal(2, lib.SaveSchema("Thing", null, widened).Version);
        Assert.Equal([2, 1], lib.SchemaVersions("Thing").Select(v => v.Version));
    }

    [Fact]
    public void BuiltInSchemasAreReadOnly_AndListedFirst()
    {
        using var db = new TempDb();
        var lib = Library(db);
        var e = Assert.Throws<ScriptLibraryException>(() => lib.SaveSchema("EmailMessage", null, Thing));
        Assert.Equal("read_only", e.Code);
        var names = lib.Schemas().Select(s => s.Name).ToList();
        // Built-ins first, in the engine's own order (BuiltInSchemas.Names), and every expected one is there.
        Assert.Equal(BuiltInSchemas.Names, names.Take(BuiltInSchemas.Names.Count));
        Assert.Subset(names.ToHashSet(), new HashSet<string> { "TriggerFired", "FolderChange", "EventItem", "EmailMessage", "EmailAddress", "ApiResponse" });
        Assert.True(lib.GetSchema("TriggerFired").BuiltIn);
        Assert.NotNull(lib.Find("FolderChange"));
    }

    [Fact]
    public void SchemaExportAndImportRoundTrip_AndBadSchemasAreRefused()
    {
        using var db = new TempDb();
        var lib = Library(db);
        lib.SaveSchema("Thing", "a thing", Thing);
        var file = lib.ExportSchema("Thing");
        Assert.Equal("Thing.schema.json", file.FileName);
        Assert.Equal(2, lib.ImportSchema(file.Json).Version);
        Assert.Equal("bad_schema", Assert.Throws<ScriptLibraryException>(() => lib.SaveSchema("Thing", null, """{"type":"string"}""")).Code);
        Assert.Equal("bad_schema", Assert.Throws<ScriptLibraryException>(() => lib.SaveSchema("1bad", null, Thing)).Code);
    }

    [Fact]
    public void SchemaDiff_FindsRemovedAndRetypedFields_ThroughLists_AndEnumsThatLostAValue()
    {
        var old = JsonNode.Parse("""
            {"type":"object","properties":{
              "items":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"n":{"type":"integer"}}}},
              "kind":{"type":"string","enum":["a","b"]},
              "when":{"type":"string","format":"date-time"}}}
            """)!.AsObject();
        var changed = JsonNode.Parse("""
            {"type":"object","properties":{
              "items":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"}}}},
              "kind":{"type":"string","enum":["a","c"]},
              "when":{"type":"string","format":"date"}}}
            """)!.AsObject();
        Assert.Equal(["items.n", "kind", "when"], SchemaDiff.RemovedOrRetyped(old, changed).Order());
        Assert.Empty(SchemaDiff.RemovedOrRetyped(old, old));
        Assert.True(SchemaDiff.Covers("from", "from.address"));
        Assert.False(SchemaDiff.Covers("from", "fromage"));
    }
}
