using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A <c>"type": "table"</c> config field: its declaration (typed columns, a key, read-only columns,
/// row actions) and the rules a row is held to - the same sentences a hire's refusal uses, naming the
/// field, the row and the column. A secret cell is never a config value; a person's save never writes
/// a read-only cell; a run gets every cell but a secret one.
/// </summary>
public sealed class PluginTableManifestTests
{
    private const string Entries = """
        {
          "type": "table",
          "label": "Entries",
          "key": "name",
          "maxRows": 3,
          "columns": [
            { "name": "name", "type": "string", "required": true },
            { "name": "method", "type": "enum", "enum": ["password", "token", "interactive"], "required": true },
            { "name": "verifyEvery", "type": "number", "min": 1, "integer": true, "default": 24 },
            { "name": "active", "type": "bool", "default": true },
            { "name": "password", "type": "secret" },
            { "name": "username", "type": "secret", "masked": false },
            { "name": "session", "type": "secret", "readOnly": true },
            { "name": "status", "type": "string", "readOnly": true }
          ],
          "rowActions": [ { "action": "sign-in", "label": "Sign in", "when": { "column": "method", "equals": "interactive" } } ]
        }
        """;

    private static PluginConfigField Field(string json = Entries)
    {
        var (field, refusal) = PluginConfigField.Parse("entries", JsonDocument.Parse(json).RootElement);
        Assert.True(refusal is null, refusal);
        return field!;
    }

    private static string? Refusal(string json) =>
        PluginConfigField.Parse("entries", JsonDocument.Parse(json).RootElement).Refusal;

    private static JsonElement Rows(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void A_table_declares_typed_columns_a_key_and_row_actions_and_is_always_a_persons_starting_empty()
    {
        var field = Field();
        var table = field.Table!;

        Assert.Equal("table", field.Type);
        Assert.True(field.PersonOnly);
        Assert.Equal("[]", field.Default!.Value.GetRawText());
        Assert.Equal("name", table.Key);
        Assert.Equal(3, table.MaxRows);
        Assert.Equal(["string", "enum", "number", "bool", "secret", "secret", "secret", "string"], table.Columns.Select(c => c.Type));
        Assert.True(table.Column("password")!.Masked);
        Assert.False(table.Column("username")!.Masked);
        Assert.True(table.Column("status")!.ReadOnly);
        Assert.Equal(("sign-in", "Sign in", "method", "interactive"),
            (table.RowActions[0].Action, table.RowActions[0].Label, table.RowActions[0].Column, table.RowActions[0].EqualTo));
    }

    [Theory]
    [InlineData("""{"type":"table","key":"name","columns":[{"name":"name","type":"date"}]}""", "`config.entries.columns.name.type` must be string, number, bool, enum or secret")]
    [InlineData("""{"type":"table","key":"name","columns":[{"name":"name"},{"name":"m","type":"enum"}]}""", "`config.entries.columns.m.enum` must list the column's choices")]
    [InlineData("""{"type":"table","key":"name","columns":[{"name":"name"},{"name":"n","type":"number","min":5,"max":1}]}""", "`config.entries.columns.n.min` is greater than its `max`")]
    [InlineData("""{"type":"table","key":"name","columns":[{"name":"name"},{"name":"n","type":"string","min":1}]}""", "`config.entries.columns.n.min` applies only to a number column")]
    [InlineData("""{"type":"table","key":"other","columns":[{"name":"name"}]}""", "`config.entries.key` names `other`, which is not one of its columns")]
    [InlineData("""{"type":"table","key":"name","columns":[{"name":"name","readOnly":true}]}""", "`config.entries.key` must name a string column a person writes")]
    [InlineData("""{"type":"table","key":"name","columns":[{"name":"name","type":"secret"}]}""", "`config.entries.key` must name a string column a person writes")]
    [InlineData("""{"type":"table","key":"name","setBy":"anyone","columns":[{"name":"name"}]}""", "`config.entries.setBy` must be \"person\" or omitted")]
    [InlineData("""{"type":"table","key":"name","default":[],"columns":[{"name":"name"}]}""", "`config.entries.default` is not allowed")]
    [InlineData("""{"type":"table","key":"name","columns":[]}""", "`config.entries.columns` must be a non-empty array")]
    [InlineData("""{"type":"table","key":"name","columns":[{"name":"name"},{"name":"name"}]}""", "names `name` twice")]
    [InlineData("""{"type":"table","key":"name","columns":[{"name":"name"},{"name":"p","type":"secret","required":true}]}""", "`config.entries.columns.p.required` is not allowed on a secret column")]
    [InlineData("""{"type":"table","key":"name","columns":[{"name":"name"},{"name":"p","type":"secret","default":"x"}]}""", "`config.entries.columns.p.default` is not allowed on a secret column")]
    [InlineData("""{"type":"table","key":"name","columns":[{"name":"name"},{"name":"s","readOnly":true,"required":true}]}""", "cannot be both required and readOnly")]
    [InlineData("""{"type":"table","key":"name","columns":[{"name":"name"},{"name":"n","type":"number","max":3,"default":9}]}""", "`config.entries.columns.n.default`")]
    [InlineData("""{"type":"table","key":"name","maxRows":0,"columns":[{"name":"name"}]}""", "`config.entries.maxRows` must be a whole number from 1 to 500")]
    [InlineData("""{"type":"table","key":"name","columns":[{"name":"name"}],"rowActions":[{"action":"launch","when":{"column":"name","equals":"x"}}]}""", "`config.entries.rowActions[0].action` must be one of: sign-in")]
    [InlineData("""{"type":"table","key":"name","columns":[{"name":"name"},{"name":"m","type":"enum","enum":["a"]}],"rowActions":[{"action":"sign-in","when":{"column":"m","equals":"b"}}]}""", "`config.entries.rowActions[0].when.equals` must be one of `m`'s choices")]
    public void A_bad_declaration_is_refused_naming_the_field_and_column(string json, string expected)
    {
        var refusal = Refusal(json);
        Assert.NotNull(refusal);
        Assert.Contains(expected, refusal);
    }

    [Fact]
    public void A_table_takes_valid_rows()
    {
        var rows = Rows("""
            [
              { "name": "prod-admin", "method": "password", "verifyEvery": 6, "active": true },
              { "name": "staging", "method": "interactive" }
            ]
            """);

        Assert.Null(Field().Refusal("entries", rows));
        Assert.Equal(["prod-admin", "staging"], Field().Table!.RowNames(rows));
    }

    [Theory]
    [InlineData("""{"name":"a"}""", "`entries` must be a list of rows")]
    [InlineData("""[{"name":"a","method":"token"},{"name":"b","method":"token"},{"name":"c","method":"token"},{"name":"d","method":"token"}]""", "holds 4 rows; it takes at most 3")]
    [InlineData("""[{"method":"token"}]""", "`entries` row 1: `name` must be 1 to 64 letters")]
    [InlineData("""[{"name":"bad name","method":"token"}]""", "`entries` row 1: `name` must be 1 to 64 letters")]
    [InlineData("""[{"name":"Prod","method":"token"},{"name":"prod","method":"token"}]""", "two rows named 'prod'; each `name` must be unique, ignoring case")]
    [InlineData("""[{"name":"a","method":"token","colour":"red"}]""", "`entries[a].colour` is not a column of `entries`")]
    [InlineData("""[{"name":"a","method":"token","password":"hunter2-value"}]""", "`entries[a].password` is a secret column: its value is never part of the settings")]
    [InlineData("""[{"name":"a"}]""", "`entries[a].method` is required")]
    [InlineData("""[{"name":"a","method":"pigeon"}]""", "`entries[a].method`")]
    [InlineData("""[{"name":"a","method":"token","verifyEvery":0}]""", "`entries[a].verifyEvery`")]
    [InlineData("""[{"name":"a","method":"token","verifyEvery":1.5}]""", "`entries[a].verifyEvery`")]
    [InlineData("""[{"name":"a","method":"token","active":"yes"}]""", "`entries[a].active` must be a bool")]
    [InlineData("""[{"name":"a","method":"token","status":"two\nlines"}]""", "`entries[a].status` holds a line break")]
    public void A_bad_row_is_refused_naming_the_field_the_row_and_the_column(string json, string expected)
    {
        var refusal = Field().Refusal("entries", Rows(json));
        Assert.NotNull(refusal);
        Assert.Contains(expected, refusal);
    }

    [Fact]
    public void A_refusal_of_a_secret_cell_never_repeats_its_value()
    {
        var refusal = Field().Refusal("entries", Rows("""[{"name":"a","method":"token","password":"hunter2-value"}]"""));
        Assert.DoesNotContain("hunter2-value", refusal);
    }

    [Fact]
    public void A_persons_save_never_writes_a_read_only_cell_and_a_kept_row_keeps_its_status()
    {
        var table = Field().Table!;
        var stored = Rows("""[{"name":"prod","method":"token","status":"Verified"},{"name":"gone","method":"token","status":"Failed"}]""");
        var sent = Rows("""[{"name":"PROD","method":"password","status":"Forged"},{"name":"new","method":"token","status":"Forged"}]""");

        var merged = JsonNode.Parse(table.Merge(sent, stored).GetRawText())!.AsArray();

        Assert.Equal("password", (string?)merged[0]!["method"]);
        Assert.Equal("Verified", (string?)merged[0]!["status"]);
        Assert.False(merged[1]!.AsObject().ContainsKey("status"));
        Assert.Equal(["gone"], table.Removed(stored, table.Merge(sent, stored)));
    }

    [Fact]
    public void A_run_gets_each_row_with_its_defaults_and_never_a_secret_cell()
    {
        var table = Field().Table!;
        var rows = table.ForRun(Rows("""[{"name":"a","method":"token"}]"""));

        var row = rows[0]!.AsObject();
        Assert.Equal(24, (int)row["verifyEvery"]!);
        Assert.True((bool)row["active"]!);
        Assert.False(row.ContainsKey("password"));
        Assert.False(row.ContainsKey("session"));
    }

    [Fact]
    public void A_secret_cell_is_stored_under_a_fixed_length_name_no_field_can_have_and_the_same_for_any_case_of_its_row()
    {
        var name = PluginMemberSecretStore.CellField("entries", "Prod-Admin", "password");

        Assert.StartsWith(PluginMemberSecretStore.CellPrefix, name);
        Assert.Contains(":", name);
        Assert.True(name.Length <= 64);
        Assert.Equal(name, PluginMemberSecretStore.CellField("entries", "prod-admin", "password"));
        Assert.NotEqual(name, PluginMemberSecretStore.CellField("entries", "prod-admin", "session"));
        Assert.Equal(name.Length, PluginMemberSecretStore.CellField("entries", new string('x', 64), "password").Length);
    }

    [Fact]
    public void A_cell_change_is_held_to_its_table_row_and_column_and_a_person_never_sets_a_read_only_one()
    {
        var manifest = PluginManifest.Parse(PluginInstall.Manifest("tabled", edit: m =>
            m["config"] = new JsonObject { ["entries"] = JsonNode.Parse(Entries) }).ToJsonString()).Manifest!;
        var stored = new PluginMemberSettings(
            new Dictionary<string, JsonElement> { ["entries"] = Rows("""[{"name":"prod","method":"token"}]""") },
            new Dictionary<string, string>());

        Assert.Null(PluginMemberSecretStore.CellRefusal(manifest, stored, "entries", "PROD", "password", "a-good-value"));
        Assert.Null(PluginMemberSecretStore.CellRefusal(manifest, stored, "entries", "prod", "password", null));
        Assert.Contains("has no row 'ghost'", PluginMemberSecretStore.CellRefusal(manifest, stored, "entries", "ghost", "password", "a-good-value"));
        Assert.Contains("is not a secret column", PluginMemberSecretStore.CellRefusal(manifest, stored, "entries", "prod", "status", "a-good-value"));
        Assert.Contains("is not a table", PluginMemberSecretStore.CellRefusal(manifest, stored, "other", "prod", "password", "a-good-value"));
        Assert.Contains("written only by the platform", PluginMemberSecretStore.CellRefusal(manifest, stored, "entries", "prod", "session", "a-good-value"));
        Assert.Null(PluginMemberSecretStore.CellRefusal(manifest, stored, "entries", "prod", "session", "a-good-value", byPerson: false));
        Assert.Null(PluginMemberSecretStore.CellRefusal(manifest, stored, "entries", "prod", "session", null));

        var tooShort = PluginMemberSecretStore.CellRefusal(manifest, stored, "entries", "prod", "password", "abc");
        Assert.Contains("too short", tooShort);
        Assert.DoesNotContain("abc", tooShort!.Replace("`entries[prod].password`", ""));
    }

    [Fact]
    public void A_secret_field_may_not_take_a_tables_name()
    {
        var parsed = PluginManifest.Parse(PluginInstall.Manifest("clash", edit: m =>
        {
            m["config"] = new JsonObject { ["entries"] = JsonNode.Parse(Entries) };
            m["secrets"] = JsonNode.Parse("""{ "entries": { "description": "x" } }""");
        }).ToJsonString());

        Assert.Contains("`secrets.entries` has the name of the table `config.entries`", parsed.Refusal);
    }

    [Fact]
    public void An_agents_hire_may_leave_a_table_empty_and_nothing_else()
    {
        var field = Field();
        Assert.Null(field.AgentRefusal("entries", Rows("[]")));
        Assert.Contains("is a table only a person fills", field.AgentRefusal("entries", Rows("""[{"name":"a","method":"token"}]""")));
    }
}
