using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Identity;
using Harness.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// Instance-wide settings: every one is listed with where its value came from, a partial PUT
/// validates before it writes anything, every write is audited with the person's email, and a change
/// reaches its reader with no restart.
/// </summary>
public sealed class TenantSettingsTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static readonly string[] EightSettings =
    [
        "wip.maxRunning", "workflow.spendLimit", "concierge.idleTimeout", "quiet.window",
        "resume.maxAutomatic", "causation.depthLimit", "kanban.wipLimits", "theme.default",
    ];

    [Fact]
    public async Task Every_setting_is_listed_with_its_value_default_source_and_description()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();

        var settings = await ReadAsync(client, ct);

        foreach (var name in EightSettings)
        {
            var entry = Assert.Single(settings, s => s.GetProperty("name").GetString() == name);

            Assert.True(entry.TryGetProperty("value", out _), name);
            Assert.True(entry.TryGetProperty("default", out _), name);
            Assert.Contains(entry.GetProperty("source").GetString(), new[] { "row", "appsettings" });
            Assert.True(entry.TryGetProperty("updatedAt", out _), name);
            Assert.True(entry.TryGetProperty("updatedBy", out _), name);
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("description").GetString()), name);
            Assert.False(entry.GetProperty("readOnly").GetBoolean(), name);
        }

        // Never written by this class: the appsettings fallback, with nobody named.
        var theme = settings.Single(s => s.GetProperty("name").GetString() == "theme.default");
        Assert.Equal("appsettings", theme.GetProperty("source").GetString());
        Assert.Equal("auto", theme.GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, theme.GetProperty("updatedBy").ValueKind);

        // Why the spend limit is settable is stated where a person reads the setting.
        var spend = settings.Single(s => s.GetProperty("name").GetString() == "workflow.spendLimit");
        Assert.Contains("audited", spend.GetProperty("description").GetString());

        // The file-browser roots: read-only, with the mount note.
        var roots = settings.Where(s => s.GetProperty("name").GetString()!.StartsWith("fileBrowser.roots.")).ToList();
        Assert.NotEmpty(roots);
        Assert.All(roots, root =>
        {
            Assert.True(root.GetProperty("readOnly").GetBoolean());
            Assert.Contains("mount", root.GetProperty("note").GetString());
        });
    }

    [Fact]
    public async Task A_write_applies_without_a_restart_and_appends_an_audit_row()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();
        var wip = host.Services.GetRequiredService<WipLedger>();
        var before = wip.Max;
        var target = before == 3 ? 5 : 3;

        var response = await client.PutAsJsonAsync("/api/tenant/settings", new Dictionary<string, object>
        {
            ["wip.maxRunning"] = target,
            ["workflow.spendLimit"] = 42_000_000,
        }, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The reader, at runtime: the ledger's limit and the team budget fallback.
        Assert.Equal(target, wip.Max);
        Assert.Equal(42_000_000, host.Services.GetRequiredService<TeamRegistry>().EffectiveWorkflowBudgetFor(host.Beta));

        var settings = await ReadAsync(client, ct);
        var entry = settings.Single(s => s.GetProperty("name").GetString() == "wip.maxRunning");
        Assert.Equal(target, entry.GetProperty("value").GetInt32());
        Assert.Equal("row", entry.GetProperty("source").GetString());
        Assert.Equal("person@example.test", entry.GetProperty("updatedBy").GetString());
        Assert.NotEqual(JsonValueKind.Null, entry.GetProperty("updatedAt").ValueKind);

        var audit = await host.Services.GetRequiredService<ITenantLog>()
            .FindLatestAsync(TenantActions.TenantSettingChanged, "wip.maxRunning", ct);

        Assert.NotNull(audit);
        Assert.Equal("person@example.test", audit!.ActorEmail);
        using var detail = JsonDocument.Parse(audit.Detail!);
        Assert.Equal("wip.maxRunning", detail.RootElement.GetProperty("setting").GetString());
        Assert.Equal(before.ToString(), detail.RootElement.GetProperty("old").GetString());
        Assert.Equal(target.ToString(), detail.RootElement.GetProperty("new").GetString());
    }

    [Theory]
    [InlineData("wip.maxRunning", "-1")]
    [InlineData("wip.maxRunning", "\"four\"")]
    [InlineData("workflow.spendLimit", "1.5")]
    [InlineData("resume.maxAutomatic", "-3")]
    [InlineData("concierge.idleTimeout", "\"00:00:10\"")]
    [InlineData("quiet.window", "\"400.00:00:00\"")]
    [InlineData("quiet.window", "\"soon\"")]
    [InlineData("kanban.wipLimits", "{\"todo\":-1}")]
    [InlineData("kanban.wipLimits", "{\"in-progress\":3}")]
    [InlineData("kanban.wipLimits", "{\"review\":3}")]
    [InlineData("theme.default", "\"purple\"")]
    [InlineData("no.such.setting", "1")]
    public async Task An_invalid_value_is_refused_naming_the_field_and_nothing_is_written(string name, string json)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();
        var causationBefore = host.Services.GetRequiredService<TenantSettings>().CausationDepthLimit;

        // A valid change beside the invalid one must not land either.
        var body = $$"""{"causation.depthLimit": {{causationBefore + 7}}, "{{name}}": {{json}}}""";
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        var response = await client.PutAsync("/api/tenant/settings", content, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var refusal = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(name, refusal.RootElement.GetProperty("field").GetString());
        Assert.Contains(name, refusal.RootElement.GetProperty("error").GetString());

        Assert.Equal(causationBefore, host.Services.GetRequiredService<TenantSettings>().CausationDepthLimit);
    }

    [Fact]
    public async Task A_container_key_is_refused_the_write()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = host.Container(host.AlphaContainerKey);

        var response = await client.PutAsJsonAsync(
            "/api/tenant/settings", new Dictionary<string, object> { ["wip.maxRunning"] = 1 }, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(PermitGate.HumansOnlyMessage, body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Kanban_lanes_carry_their_wip_limit_and_in_progress_is_the_running_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();

        var put = await client.PutAsJsonAsync("/api/tenant/settings", new Dictionary<string, object>
        {
            ["kanban.wipLimits"] = new Dictionary<string, int> { ["todo"] = 3 },
        }, ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var max = host.Services.GetRequiredService<TenantSettings>().WipMaxRunning;

        foreach (var route in new[] { "/api/kanban/board", $"/api/teams/{host.Alpha}/kanban/board" })
        {
            using var board = JsonDocument.Parse(await client.GetStringAsync(route, ct));
            var lanes = board.RootElement.GetProperty("lanes").EnumerateArray()
                .ToDictionary(lane => lane.GetProperty("id").GetString()!, lane => lane.GetProperty("wipLimit"));

            Assert.Equal(max, lanes["in-progress"].GetInt32());
            Assert.Equal(3, lanes["todo"].GetInt32());
            Assert.Equal(JsonValueKind.Null, lanes["done"].ValueKind);
        }
    }

    [Fact]
    public async Task A_row_wins_over_appsettings_which_wins_over_the_built_in_default()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"harness-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "messages.db");

        try
        {
            await new SchemaMigrator(database).ApplyAsync(SchemaModules.All, ct);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Wip:MaxRunning"] = "7",
                    ["QuietSweepWindow"] = "00:00:30",
                })
                .Build();

            var settings = new TenantSettings(new SqliteTenantSettingsStore(database), configuration, cpuCount: 1);
            await settings.LoadAsync(ct);

            Assert.Equal(7, settings.WipMaxRunning);
            // A deployment's value outside the dialog's range still stands.
            Assert.Equal(TimeSpan.FromSeconds(30), settings.QuietWindow);
            Assert.Equal(25, settings.CausationDepthLimit);
            Assert.Equal(TimeSpan.FromHours(8), settings.ConciergeIdleTimeout);

            using var value = JsonDocument.Parse("9");
            await settings.WriteAsync(
                new Dictionary<string, JsonElement> { ["wip.maxRunning"] = value.RootElement.Clone() },
                null, "someone@example.test", ct);

            // A fresh start reads the row.
            var restarted = new TenantSettings(new SqliteTenantSettingsStore(database), configuration, cpuCount: 1);
            await restarted.LoadAsync(ct);
            Assert.Equal(9, restarted.WipMaxRunning);
            Assert.Equal("7", restarted.Fallback("wip.maxRunning"));

            // No appsettings and one CPU: max(2, cpus).
            var bare = new TenantSettings(
                new SqliteTenantSettingsStore(database), new ConfigurationBuilder().Build(), cpuCount: 1);
            Assert.Equal("2", bare.Fallback("wip.maxRunning"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_value_is_not_written_when_its_audit_row_cannot_be()
    {
        // One transaction: an audit insert that fails takes the value down with it.
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"harness-settings-tx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "messages.db");

        try
        {
            await new SchemaMigrator(database).ApplyAsync(SchemaModules.All, ct);
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync(ct);
                await using var drop = connection.CreateCommand();
                drop.CommandText = "DROP TABLE tenant_events";
                await drop.ExecuteNonQueryAsync(ct);
            }

            await Assert.ThrowsAnyAsync<Exception>(() => new SqliteTenantSettingsStore(database).WriteAsync(
                [new TenantSettingChange("wip.maxRunning", null, "9")], null, "someone@example.test", ct));

            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync(ct);
                await using var count = connection.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM tenant_settings";
                Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(ct))!);
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
    public void The_default_running_limit_is_the_cpu_count_when_that_is_above_two()
    {
        // max(2, cgroup CPUs): the one-CPU case is above; this is the other arm of the max.
        var settings = new TenantSettings(
            new SqliteTenantSettingsStore(Path.Combine(Path.GetTempPath(), $"harness-unused-{Guid.NewGuid():N}.db")),
            new ConfigurationBuilder().Build(), cpuCount: 6);

        Assert.Equal("6", settings.Fallback("wip.maxRunning"));
    }

    [Fact]
    public void The_cgroup_cpu_limit_is_read_from_cpu_max()
    {
        var file = Path.GetTempFileName();

        try
        {
            File.WriteAllText(file, "250000 100000\n");
            Assert.Equal(3, TenantSettings.CgroupCpus(file));

            File.WriteAllText(file, "max 100000\n");
            Assert.Null(TenantSettings.CgroupCpus(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static async Task<List<JsonElement>> ReadAsync(HttpClient client, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/tenant/settings", ct));
        return document.RootElement.GetProperty("settings").EnumerateArray().Select(e => e.Clone()).ToList();
    }
}

/// <summary>
/// A team whose Manager has a wake held by the WIP limit reads "waiting for a slot" and names
/// the held member, with running and waiting counted per team from the ledger.
/// </summary>
public sealed class HeldTeamRollupTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task A_team_whose_manager_waits_for_a_slot_reads_waiting_for_a_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        var wip = host.Services.GetRequiredService<WipLedger>();
        wip.SetMax(1);

        // Another team fills the pool, its Manager in the reserved slot.
        using var alphaWorker = wip.TryEnter(new ContainerId(host.Alpha, "Worker"));
        using var alphaManager = wip.TryEnter(new ContainerId(host.Alpha, "Manager"));
        Assert.NotNull(alphaWorker);
        Assert.NotNull(alphaManager);

        // Beta's Manager has a delivered wake and waits.
        Assert.Null(wip.TryEnter(new ContainerId(host.Beta, "Manager")));

        using var client = await host.PersonAsync();
        using var rollup = JsonDocument.Parse(await client.GetStringAsync("/api/teams/rollup", ct));
        var rows = rollup.RootElement.GetProperty("teams").EnumerateArray()
            .ToDictionary(row => row.GetProperty("team").GetString()!, StringComparer.OrdinalIgnoreCase);

        var beta = rows[host.Beta];
        Assert.Equal(0, beta.GetProperty("running").GetInt32());
        Assert.Equal(1, beta.GetProperty("waiting").GetInt32());
        Assert.Equal(["Manager"], beta.GetProperty("held").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("waiting for a slot", beta.GetProperty("slotStatus").GetString());

        var alpha = rows[host.Alpha];
        Assert.Equal(2, alpha.GetProperty("running").GetInt32());
        Assert.Equal(0, alpha.GetProperty("waiting").GetInt32());
        Assert.Equal(JsonValueKind.Null, alpha.GetProperty("slotStatus").ValueKind);
    }
}
