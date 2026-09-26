using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Harness.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// The tags of a built-in preset are an operator's to correct, through the tenant
/// setting <c>agents.tags</c>; everything else about a built-in stays from the build. The
/// override is read where tags are read - the catalog, the hiring view and the next hire - with no
/// restart.
/// </summary>
public sealed class BuiltInTagOverrideTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_built_ins_tags_can_be_changed_and_the_next_hire_uses_them_without_a_restart()
    {
        using var person = await host.PersonAsync();
        await host.Services.GetRequiredService<TeamRegistry>()
            .SetMemberAgentsAsync(host.Alpha, ["claude-headless", "grok-headless"], Ct);

        try
        {
            // The build's tags: only claude-headless carries developer.
            Assert.Equal("claude-headless", await HireAsync(person, "Before", "developer"));

            await WriteAsync(person, new Dictionary<string, string[]>
            {
                ["grok-headless"] = ["developer", "researcher"],
                ["claude-headless"] = ["tester"],
            });

            // The very next hire, same process.
            Assert.Equal("grok-headless", await HireAsync(person, "After", "developer"));

            // The catalog shows the operator's tags, says they are the operator's, and keeps the
            // build's beside them.
            var grok = await AgentAsync(person, "grok-headless");
            Assert.Equal(["developer", "researcher"], Strings(grok.GetProperty("tags")));
            Assert.True(grok.GetProperty("tagsFromOperator").GetBoolean());
            Assert.Equal(["researcher"], Strings(grok.GetProperty("buildTags")));

            var codex = await AgentAsync(person, "codex-headless");
            Assert.False(codex.GetProperty("tagsFromOperator").GetBoolean());

            // A machine principal reads the same tags.
            using var member = host.Container(host.AlphaContainerKey);
            var redacted = await member.GetFromJsonAsync<JsonElement>("/api/agents", Ct);
            var grokRedacted = redacted.GetProperty("agents").EnumerateArray()
                .Single(a => a.GetProperty("name").GetString() == "grok-headless");
            Assert.Equal(["developer", "researcher"], Strings(grokRedacted.GetProperty("tags")));
            Assert.True(grokRedacted.GetProperty("tagsFromOperator").GetBoolean());

            // And the hiring view.
            var hiring = await person.GetFromJsonAsync<JsonElement>($"/api/teams/{host.Alpha}/hiring", Ct);
            var rows = hiring.GetProperty("allowlist").EnumerateArray().ToList();
            Assert.Equal(["tester"], Strings(rows.Single(r => r.GetProperty("agent").GetString() == "claude-headless").GetProperty("tags")));
            Assert.Equal(["developer", "researcher"], Strings(rows.Single(r => r.GetProperty("agent").GetString() == "grok-headless").GetProperty("tags")));
        }
        finally
        {
            await WriteAsync(person, new Dictionary<string, string[]>());
        }
    }

    [Fact]
    public async Task Resetting_removes_the_override_and_the_builds_tags_return()
    {
        using var person = await host.PersonAsync();
        var catalog = host.Services.GetRequiredService<AgentCatalog>();

        await WriteAsync(person, new Dictionary<string, string[]> { ["grok-headless"] = ["developer"] });
        Assert.Equal(["developer"], catalog.Definition("grok-headless")!.Tags);

        await WriteAsync(person, new Dictionary<string, string[]>());

        var build = AgentCatalogFile.BuiltIns().Single(a => a.Name == "grok-headless").Tags;
        Assert.Equal(build, catalog.Definition("grok-headless")!.Tags);

        var grok = await AgentAsync(person, "grok-headless");
        Assert.Equal(build, Strings(grok.GetProperty("tags")));
        Assert.False(grok.GetProperty("tagsFromOperator").GetBoolean());
    }

    [Fact]
    public async Task An_override_for_an_unknown_preset_is_ignored_not_fatal_and_says_so()
    {
        using var person = await host.PersonAsync();

        try
        {
            await WriteAsync(person, new Dictionary<string, string[]>
            {
                ["no-such-preset"] = ["developer"],
                ["grok-headless"] = ["developer"],
            });

            var catalog = await person.GetFromJsonAsync<JsonElement>("/api/agents", Ct);
            Assert.Equal(["no-such-preset"], Strings(catalog.GetProperty("ignoredTagOverrides")));
            Assert.DoesNotContain(
                catalog.GetProperty("agents").EnumerateArray(),
                a => a.GetProperty("name").GetString() == "no-such-preset");

            // The known entry still applies beside it.
            Assert.Equal(["developer"], host.Services.GetRequiredService<AgentCatalog>().Definition("grok-headless")!.Tags);
        }
        finally
        {
            await WriteAsync(person, new Dictionary<string, string[]>());
        }
    }

    [Theory]
    [InlineData("[\"developer\"]")]
    [InlineData("{\"grok-headless\":\"developer\"}")]
    [InlineData("{\"grok-headless\":[\"\"]}")]
    [InlineData("{\"grok-headless\":[1]}")]
    [InlineData("{\"\":[\"developer\"]}")]
    public async Task An_invalid_value_is_refused_naming_the_field_and_nothing_is_written(string json)
    {
        using var person = await host.PersonAsync();
        var settings = host.Services.GetRequiredService<TenantSettings>();
        var before = settings.Current(TenantSettings.AgentTagsName);

        using var content = new StringContent(
            $$"""{"agents.tags": {{json}}}""", System.Text.Encoding.UTF8, "application/json");
        var response = await person.PutAsync("/api/tenant/settings", content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var refusal = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("agents.tags", refusal.RootElement.GetProperty("field").GetString());
        Assert.Contains("agents.tags", refusal.RootElement.GetProperty("error").GetString());
        Assert.Equal(before, settings.Current(TenantSettings.AgentTagsName));
    }

    [Fact]
    public async Task The_write_appends_a_tenant_events_row()
    {
        using var person = await host.PersonAsync();

        try
        {
            await WriteAsync(person, new Dictionary<string, string[]> { ["grok-headless"] = ["researcher", "developer"] });

            var audit = await host.Services.GetRequiredService<ITenantLog>()
                .FindLatestAsync(TenantActions.TenantSettingChanged, TenantSettings.AgentTagsName, Ct);

            Assert.NotNull(audit);
            Assert.Equal("person@example.test", audit!.ActorEmail);
            using var detail = JsonDocument.Parse(audit.Detail!);
            Assert.Equal("""{"grok-headless":["researcher","developer"]}""", detail.RootElement.GetProperty("new").GetString());
        }
        finally
        {
            await WriteAsync(person, new Dictionary<string, string[]>());
        }
    }

    [Fact]
    public async Task The_override_is_not_written_when_its_audit_row_cannot_be()
    {
        // The same transaction: with tenant_events gone, the write fails and neither the row nor
        // the value in memory changes.
        var directory = Path.Combine(Path.GetTempPath(), $"harness-agent-tags-tx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "messages.db");

        try
        {
            await new SchemaMigrator(database).ApplyAsync(SchemaModules.All, Ct);
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync(Ct);
                await using var drop = connection.CreateCommand();
                drop.CommandText = "DROP TABLE tenant_events";
                await drop.ExecuteNonQueryAsync(Ct);
            }

            var settings = new TenantSettings(
                new SqliteTenantSettingsStore(database), new ConfigurationBuilder().Build(), cpuCount: 2);
            await settings.LoadAsync(Ct);

            using var body = JsonDocument.Parse("""{"grok-headless":["developer"]}""");
            await Assert.ThrowsAnyAsync<Microsoft.Data.Sqlite.SqliteException>(() => settings.WriteAsync(
                new Dictionary<string, JsonElement> { [TenantSettings.AgentTagsName] = body.RootElement },
                null, "someone@example.test", Ct));

            Assert.Empty(settings.AgentTags);

            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync(Ct);
                await using var count = connection.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM tenant_settings";
                Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(Ct))!);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_built_in_sent_back_with_its_operator_tags_is_accepted_and_any_other_change_is_still_refused()
    {
        using var person = await host.PersonAsync();

        try
        {
            await WriteAsync(person, new Dictionary<string, string[]> { ["grok-headless"] = ["developer"] });
            var grok = AgentCatalogFile.BuiltIns().Single(a => a.Name == "grok-headless");

            // What the Agents screen was handed and sends back whole: the operator's tags.
            var asListed = await person.PutAsJsonAsync(
                "/api/agents", new { agents = new[] { grok with { Tags = ["developer"] } } }, Ct);
            Assert.Equal(HttpStatusCode.NoContent, asListed.StatusCode);

            // Tags changed through the catalog save are not how an override is made.
            var retagged = await person.PutAsJsonAsync(
                "/api/agents", new { agents = new[] { grok with { Tags = ["tester"] } } }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, retagged.StatusCode);
            Assert.Contains("agents.tags", await retagged.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

            // Anything else, with or without the tags, is the built-in refusal.
            var changed = await person.PutAsJsonAsync(
                "/api/agents", new { agents = new[] { grok with { Tags = ["developer"], TimeoutSeconds = 5 } } }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
            Assert.Contains(
                "Built-in presets change only with the product",
                await changed.Content.ReadAsStringAsync(Ct),
                StringComparison.Ordinal);
        }
        finally
        {
            await WriteAsync(person, new Dictionary<string, string[]>());
        }
    }

    private async Task<string> HireAsync(HttpClient person, string name, string tag)
    {
        var response = await person.PostAsJsonAsync(
            $"/api/teams/{host.Alpha}/containers", new { name, @for = tag }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Harness-Hiring-Notice"));

        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("agent").GetString()!;
    }

    private static async Task WriteAsync(HttpClient person, Dictionary<string, string[]> tags)
    {
        var response = await person.PutAsJsonAsync(
            "/api/tenant/settings", new Dictionary<string, object> { ["agents.tags"] = tags }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<JsonElement> AgentAsync(HttpClient person, string name)
    {
        var catalog = await person.GetFromJsonAsync<JsonElement>("/api/agents", Ct);
        return catalog.GetProperty("agents").EnumerateArray().Single(a => a.GetProperty("name").GetString() == name);
    }

    private static string[] Strings(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetString()!).ToArray();
}
