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
        "wip.maxRunning", "wip.memoryPerRunMb", "workflow.spendLimit", "concierge.idleTimeout", "quiet.window",
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
            Assert.Contains(entry.GetProperty("defaultSource").GetString(), new[] { "appsettings", "builtIn" });
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
    [InlineData("wip.memoryPerRunMb", "0")]
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
    [InlineData("no.such.setting", "null")]
    [InlineData("fileBrowser.roots.data", "null")]
    [InlineData("kanban.wipLimits", "{\"todo\":null}")]
    [InlineData("system.packages", "[null]")]
    [InlineData("agents.tags", "{\"claude\":null}")]
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
            Assert.Equal("appsettings", restarted.FallbackSource("wip.maxRunning"));
            Assert.Equal("builtIn", restarted.FallbackSource("causation.depthLimit"));

            // No appsettings and one CPU: max(1, cpus - 1).
            var bare = new TenantSettings(
                new SqliteTenantSettingsStore(database), new ConfigurationBuilder().Build(), cpuCount: 1, memoryLimitMb: 0);
            Assert.Equal("1", bare.Fallback("wip.maxRunning"));
            Assert.Equal("builtIn", bare.FallbackSource("wip.maxRunning"));
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
    public async Task A_reset_removes_the_row_and_appends_its_tenant_row()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();
        var settings = host.Services.GetRequiredService<TenantSettings>();
        var fallback = int.Parse(settings.Fallback("resume.maxAutomatic"));
        var target = fallback + 3;

        var set = await client.PutAsJsonAsync(
            "/api/tenant/settings", new Dictionary<string, object> { ["resume.maxAutomatic"] = target }, ct);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(target, settings.ResumeMaxAutomatic);

        // What the Settings dialog's "Reset to default" sends: the setting, as JSON null.
        using var content = new StringContent(
            """{"resume.maxAutomatic": null}""", System.Text.Encoding.UTF8, "application/json");
        var reset = await client.PutAsync("/api/tenant/settings", content, ct);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        // The row is gone, from the store and from memory: the fallback applies with no restart.
        Assert.Null(settings.Row("resume.maxAutomatic"));
        Assert.Equal(fallback, settings.ResumeMaxAutomatic);
        var stored = await new SqliteTenantSettingsStore(Path.Combine(host.DataRoot, "messages.db")).ReadAllAsync(ct);
        Assert.DoesNotContain(stored, row => row.Name == "resume.maxAutomatic");

        using var answer = JsonDocument.Parse(await reset.Content.ReadAsStringAsync(ct));
        var entry = answer.RootElement.GetProperty("settings").EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "resume.maxAutomatic");
        Assert.Equal("appsettings", entry.GetProperty("source").GetString());
        Assert.Equal(fallback, entry.GetProperty("value").GetInt32());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("updatedBy").ValueKind);

        var audit = await host.Services.GetRequiredService<ITenantLog>()
            .FindLatestAsync(TenantActions.TenantSettingReset, "resume.maxAutomatic", ct);
        Assert.NotNull(audit);
        Assert.Equal("person@example.test", audit!.ActorEmail);
        using var detail = JsonDocument.Parse(audit.Detail!);
        Assert.Equal("resume.maxAutomatic", detail.RootElement.GetProperty("setting").GetString());
        Assert.Equal(target.ToString(), detail.RootElement.GetProperty("old").GetString());
        Assert.Equal(JsonValueKind.Null, detail.RootElement.GetProperty("new").ValueKind);
        Assert.Equal("reset to default", detail.RootElement.GetProperty("change").GetString());
    }

    [Fact]
    public async Task A_reset_removes_nothing_when_its_tenant_row_cannot_be_written()
    {
        // One transaction: an audit insert that fails keeps the row, in the file and in memory.
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"harness-settings-reset-tx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "messages.db");

        try
        {
            await new SchemaMigrator(database).ApplyAsync(SchemaModules.All, ct);
            var settings = new TenantSettings(
                new SqliteTenantSettingsStore(database), new ConfigurationBuilder().Build(), cpuCount: 8, memoryLimitMb: 12288);
            await settings.LoadAsync(ct);

            using var value = JsonDocument.Parse("9");
            await settings.WriteAsync(
                new Dictionary<string, JsonElement> { ["wip.maxRunning"] = value.RootElement.Clone() },
                null, "someone@example.test", ct);

            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync(ct);
                await using var drop = connection.CreateCommand();
                drop.CommandText = "DROP TABLE tenant_events";
                await drop.ExecuteNonQueryAsync(ct);
            }

            var changed = new List<string>();
            settings.Changed += changed.Add;
            using var reset = JsonDocument.Parse("null");
            await Assert.ThrowsAnyAsync<Exception>(() => settings.WriteAsync(
                new Dictionary<string, JsonElement> { ["wip.maxRunning"] = reset.RootElement.Clone() },
                null, "someone@example.test", ct));

            Assert.Empty(changed);
            Assert.Equal(9, settings.WipMaxRunning);
            Assert.NotNull(settings.Row("wip.maxRunning"));
            var stored = await new SqliteTenantSettingsStore(database).ReadAllAsync(ct);
            Assert.Equal("9", Assert.Single(stored, row => row.Name == "wip.maxRunning").Value);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_reset_of_a_setting_with_no_row_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"harness-settings-reset-none-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "messages.db");

        try
        {
            await new SchemaMigrator(database).ApplyAsync(SchemaModules.All, ct);
            var settings = new TenantSettings(
                new SqliteTenantSettingsStore(database), new ConfigurationBuilder().Build(), cpuCount: 8, memoryLimitMb: 12288);
            await settings.LoadAsync(ct);
            var changed = new List<string>();
            settings.Changed += changed.Add;

            using var reset = JsonDocument.Parse("null");
            var written = await settings.WriteAsync(
                new Dictionary<string, JsonElement> { ["quiet.window"] = reset.RootElement.Clone() },
                null, "someone@example.test", ct);

            Assert.Empty(written);
            Assert.Empty(changed);

            // The store alone holds the same line, should memory and the file ever disagree.
            Assert.Empty(await new SqliteTenantSettingsStore(database).WriteAsync(
                [new TenantSettingChange("quiet.window", null, null)], null, "someone@example.test", ct));

            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database}");
            await connection.OpenAsync(ct);
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT (SELECT COUNT(*) FROM tenant_settings) + (SELECT COUNT(*) FROM tenant_events)";
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(ct))!);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_reset_of_the_running_limit_applies_the_computed_bound_and_raises_changed()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"harness-settings-reset-wip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "messages.db");

        try
        {
            await new SchemaMigrator(database).ApplyAsync(SchemaModules.All, ct);
            var settings = new TenantSettings(
                new SqliteTenantSettingsStore(database), new ConfigurationBuilder().Build(), cpuCount: 8, memoryLimitMb: 12288);
            await settings.LoadAsync(ct);

            using var value = JsonDocument.Parse("5");
            await settings.WriteAsync(
                new Dictionary<string, JsonElement> { ["wip.maxRunning"] = value.RootElement.Clone() },
                null, "someone@example.test", ct);
            Assert.Equal("setting", settings.RunLimit().Bound);

            // Program.cs re-applies the ledger's limit on this event, for a reset as for a write.
            var changed = new List<string>();
            settings.Changed += changed.Add;
            using var reset = JsonDocument.Parse("null");
            await settings.WriteAsync(
                new Dictionary<string, JsonElement> { ["wip.maxRunning"] = reset.RootElement.Clone() },
                null, "someone@example.test", ct);

            Assert.Equal(["wip.maxRunning"], changed);
            Assert.Equal(6, settings.WipMaxRunning);
            Assert.Equal("memory", settings.RunLimit().Bound);

            // A fresh start agrees: there is no row.
            var restarted = new TenantSettings(
                new SqliteTenantSettingsStore(database), new ConfigurationBuilder().Build(), cpuCount: 8, memoryLimitMb: 12288);
            await restarted.LoadAsync(ct);
            Assert.Null(restarted.Row("wip.maxRunning"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void The_default_running_limit_is_one_below_the_cpu_count_or_3_when_that_is_not_known()
    {
        // max(1, cgroup CPUs - 1) with no memory limit: the one-CPU case is above.
        Assert.Equal("5", Bare(cpuCount: 6, memoryLimitMb: 0).Fallback("wip.maxRunning"));

        var unknown = Bare(cpuCount: 0, memoryLimitMb: 0).RunLimit();
        Assert.Equal(3, unknown.Limit);
        Assert.Equal("cpu", unknown.Bound);
        Assert.Null(unknown.Cpus);
        Assert.Contains("not known", unknown.Reason);
    }

    [Fact]
    public void The_memory_bound_lowers_the_default_when_it_is_the_smaller()
    {
        // 8 CPUs and 12 GB: CPU bound 7, memory bound 12288 / 2048 = 6.
        var memory = Bare(cpuCount: 8, memoryLimitMb: 12288).RunLimit();
        Assert.Equal(6, memory.Limit);
        Assert.Equal("memory", memory.Bound);
        Assert.Equal(7, memory.CpuBound);
        Assert.Equal(6, memory.MemoryBound);
        Assert.Contains("memory bound 6", memory.Reason);
        Assert.Contains("CPU bound 7", memory.Reason);

        // 4 CPUs and 12 GB: CPU bound 3 is the smaller.
        var cpu = Bare(cpuCount: 4, memoryLimitMb: 12288).RunLimit();
        Assert.Equal(3, cpu.Limit);
        Assert.Equal("cpu", cpu.Bound);
        Assert.Equal(6, cpu.MemoryBound);

        // A limit below one allowance still runs one.
        Assert.Equal(1, Bare(cpuCount: 8, memoryLimitMb: 1024).RunLimit().Limit);
    }

    [Fact]
    public async Task The_per_run_allowance_moves_the_default_without_a_restart_and_a_configured_value_wins()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"harness-settings-mem-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "messages.db");

        try
        {
            await new SchemaMigrator(database).ApplyAsync(SchemaModules.All, ct);
            var settings = new TenantSettings(
                new SqliteTenantSettingsStore(database), new ConfigurationBuilder().Build(), cpuCount: 8, memoryLimitMb: 12288);
            await settings.LoadAsync(ct);
            Assert.Equal(2048, settings.WipMemoryPerRunMb);
            Assert.Equal(6, settings.WipMaxRunning);

            using var allowance = JsonDocument.Parse("4096");
            await settings.WriteAsync(
                new Dictionary<string, JsonElement> { ["wip.memoryPerRunMb"] = allowance.RootElement.Clone() },
                null, "someone@example.test", ct);

            Assert.Equal(3, settings.WipMaxRunning);
            Assert.Equal("3", settings.Fallback("wip.maxRunning"));
            Assert.Equal("memory", settings.RunLimit().Bound);
            Assert.Contains("Now 3:", settings.DescriptionOf("wip.maxRunning"));
            Assert.Contains("memory bound applies", settings.DescriptionOf("wip.maxRunning"));

            // A row wins over both bounds, and the reason still says what the default would be.
            using var row = JsonDocument.Parse("9");
            await settings.WriteAsync(
                new Dictionary<string, JsonElement> { ["wip.maxRunning"] = row.RootElement.Clone() },
                null, "someone@example.test", ct);

            var set = settings.RunLimit();
            Assert.Equal(9, settings.WipMaxRunning);
            Assert.Equal(9, set.Limit);
            Assert.Equal("setting", set.Bound);
            Assert.Contains("default would be 3", set.Reason);

            // An appsettings value wins over both bounds too.
            var configured = new TenantSettings(
                new SqliteTenantSettingsStore(database),
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Wip:MaxRunning"] = "7",
                }).Build(),
                cpuCount: 8, memoryLimitMb: 12288);

            Assert.Equal(7, configured.WipMaxRunning);
            Assert.Equal("7", configured.Fallback("wip.maxRunning"));
            Assert.Equal("configuration", configured.RunLimit().Bound);
            Assert.Contains("default would be 6", configured.RunLimit().Reason);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void The_cgroup_memory_limit_is_read_from_memory_max()
    {
        var file = Path.GetTempFileName();

        try
        {
            File.WriteAllText(file, "12884901888\n");
            Assert.Equal(12288, TenantSettings.CgroupMemoryMb(file));

            File.WriteAllText(file, "max\n");
            Assert.Null(TenantSettings.CgroupMemoryMb(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_runs_memory_limit_defaults_to_the_container_limit_less_the_host_reserve_per_run_and_a_set_value_wins()
    {
        // 8 CPUs and 12 GB: 6 runs, so (12288 - 1024) / 6 = 1877 MB each.
        var computed = Bare(cpuCount: 8, memoryLimitMb: 12288).RunMemoryLimit();
        Assert.Equal(1877, computed.Mb);
        Assert.Contains("runs.memoryLimitMb is 0", computed.Source);
        Assert.Contains("12288 MB container limit - 1024 MB for the Host", computed.Source);
        Assert.Contains("6 (wip.maxRunning)", computed.Source);

        // No container limit and nothing set: no limit, said.
        var none = Bare(cpuCount: 8, memoryLimitMb: 0).RunMemoryLimit();
        Assert.Null(none.Mb);
        Assert.Contains("no memory limit", none.Source);

        // Never below the floor an agent CLI needs to start.
        Assert.Equal(TenantSettings.MinRunMemoryLimitMb, Bare(cpuCount: 8, memoryLimitMb: 1024).RunMemoryLimit().Mb);

        var configured = new TenantSettings(
            new SqliteTenantSettingsStore(Path.Combine(Path.GetTempPath(), $"harness-unused-{Guid.NewGuid():N}.db")),
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Runs:MemoryLimitMb"] = "3000" }).Build(),
            8, 12288).RunMemoryLimit();
        Assert.Equal(3000, configured.Mb);
        Assert.Equal("runs.memoryLimitMb is set to 3000 MB", configured.Source);
    }

    [Fact]
    public void A_heavy_run_gets_the_container_limit_less_the_host_reserve_less_what_the_other_runs_are_measured_to_use()
    {
        // 8 CPUs and 12 GB: 6 runs of 1877 MB. Four others measured at 2000 MB: 12288 - 1024 - 2000.
        var settings = Bare(cpuCount: 8, memoryLimitMb: 12288);
        var heavy = settings.HeavyRunMemoryLimit(2000, 4);
        Assert.Equal(9264, heavy.Mb);
        Assert.Contains("holds the heavy lease", heavy.Source);
        Assert.Contains("12288 MB container limit - 1024 MB for the Host - 2000 MB measured in use by 4 other running runs", heavy.Source);

        // Never below the run's own limit: the others leave too little, so it keeps 1877 MB.
        var crowded = settings.HeavyRunMemoryLimit(10000, 5);
        Assert.Equal(1877, crowded.Mb);
        Assert.Contains("keeps 1877 MB", crowded.Source);

        // Nothing to measure against without a container limit: the run keeps its own (here none).
        var unbounded = Bare(cpuCount: 8, memoryLimitMb: 0).HeavyRunMemoryLimit(0, 0);
        Assert.Null(unbounded.Mb);
        Assert.Contains("no memory limit to measure headroom against", unbounded.Source);

        // The ceiling every run's hard limit is under rlimit: what a heavy run with nobody else could get.
        Assert.Equal(11264, settings.RunMemoryCeiling().Mb);
        Assert.Null(Bare(cpuCount: 8, memoryLimitMb: 0).RunMemoryCeiling().Mb);
        Assert.Equal(TenantSettings.MinRunMemoryLimitMb, Bare(cpuCount: 8, memoryLimitMb: 1024).RunMemoryCeiling().Mb);

        // The Settings description says what a heavy run gets.
        var described = Assert.Single(settings.Definitions, d => d.Name == TenantSettings.RunsMemoryLimitMbName).Description;
        Assert.Contains("A run holding the heavy lease gets more", described);
        Assert.Contains("what the other running runs are measured to use", described);
    }

    private static TenantSettings Bare(int cpuCount, long memoryLimitMb) => new(
        new SqliteTenantSettingsStore(Path.Combine(Path.GetTempPath(), $"harness-unused-{Guid.NewGuid():N}.db")),
        new ConfigurationBuilder().Build(), cpuCount, memoryLimitMb);

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
/// <c>GET /api/wip</c> names the bound the running limit comes from: the default's CPU or memory
/// bound on a host nobody has configured, and the setting once a person sets one. Its own host, so
/// no other test's row is in force.
/// </summary>
public sealed class WipLimitBoundTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task Api_wip_names_the_bound_and_a_setting_wins_over_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();
        var wip = host.Services.GetRequiredService<WipLedger>();

        // Inside an instance container the operator CLI's Wip__MaxRunning reaches this host as configuration;
        // anywhere else nothing configures it and the default's own bound is named.
        var configured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Wip__MaxRunning"));
        var limit = await LimitAsync(client, ct);
        Assert.Equal(wip.Max, limit.GetProperty("limit").GetInt32());

        if (configured)
        {
            Assert.Equal("configuration", limit.GetProperty("bound").GetString());
            Assert.Contains("default would be", limit.GetProperty("reason").GetString());
        }
        else
        {
            Assert.Contains(limit.GetProperty("bound").GetString(), new[] { "cpu", "memory" });
            Assert.Contains("bound applies", limit.GetProperty("reason").GetString());

            // The largest allowance: under a memory limit the memory bound (1) applies, and the
            // ledger follows it with no restart; with none (or one CPU, an equal bound), the CPU
            // bound stays.
            var allowance = await client.PutAsJsonAsync(
                "/api/tenant/settings", new Dictionary<string, object> { ["wip.memoryPerRunMb"] = 1_048_576 }, ct);
            Assert.Equal(HttpStatusCode.OK, allowance.StatusCode);

            limit = await LimitAsync(client, ct);
            var memoryApplies = limit.GetProperty("memoryLimitMb").ValueKind == JsonValueKind.Number
                && limit.GetProperty("cpuBound").GetInt32() > 1;
            Assert.Equal(memoryApplies ? "memory" : "cpu", limit.GetProperty("bound").GetString());
            if (memoryApplies) Assert.Equal(1, limit.GetProperty("limit").GetInt32());
            Assert.Equal(wip.Max, limit.GetProperty("limit").GetInt32());
        }

        var set = await client.PutAsJsonAsync(
            "/api/tenant/settings", new Dictionary<string, object> { ["wip.maxRunning"] = 5 }, ct);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        limit = await LimitAsync(client, ct);
        Assert.Equal("setting", limit.GetProperty("bound").GetString());
        Assert.Equal(5, limit.GetProperty("limit").GetInt32());
        Assert.Equal(5, wip.Max);
        Assert.Contains("set to 5", limit.GetProperty("reason").GetString());

        // The Settings description says the same.
        using var settings = JsonDocument.Parse(await client.GetStringAsync("/api/tenant/settings", ct));
        var entry = settings.RootElement.GetProperty("settings").EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "wip.maxRunning");
        Assert.Contains("Now 5: wip.maxRunning is set to 5", entry.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Resetting_the_running_limit_names_the_cpu_or_memory_bound_without_a_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();
        var wip = host.Services.GetRequiredService<WipLedger>();
        var configured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Wip__MaxRunning"));

        var set = await client.PutAsJsonAsync(
            "/api/tenant/settings", new Dictionary<string, object> { ["wip.maxRunning"] = 5 }, ct);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal("setting", (await LimitAsync(client, ct)).GetProperty("bound").GetString());

        using var content = new StringContent(
            """{"wip.maxRunning": null}""", System.Text.Encoding.UTF8, "application/json");
        var reset = await client.PutAsync("/api/tenant/settings", content, ct);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        // The ledger follows the computed default at once, and /api/wip says which bound it is.
        var limit = await LimitAsync(client, ct);
        Assert.Equal(wip.Max, limit.GetProperty("limit").GetInt32());

        if (configured)
        {
            Assert.Equal("configuration", limit.GetProperty("bound").GetString());
        }
        else
        {
            Assert.Contains(limit.GetProperty("bound").GetString(), new[] { "cpu", "memory" });
            Assert.Contains("bound applies", limit.GetProperty("reason").GetString());
        }

        Assert.DoesNotContain("set to 5", limit.GetProperty("reason").GetString());
    }

    private static async Task<JsonElement> LimitAsync(HttpClient client, CancellationToken ct)
    {
        using var view = JsonDocument.Parse(await client.GetStringAsync("/api/wip", ct));
        Assert.Equal(view.RootElement.GetProperty("max").GetInt32(), view.RootElement.GetProperty("limit").GetProperty("limit").GetInt32());
        return view.RootElement.GetProperty("limit").Clone();
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
