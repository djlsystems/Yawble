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
/// <c>concierge.mayMerge</c> is off on a fresh volume and on an existing one, is listed for
/// the Settings dialog with one sentence, and its write and reset land with their tenant rows in
/// one transaction or not at all.
/// </summary>
public sealed class ConciergeMayMergeSettingTests(HostFixture host) : IClassFixture<HostFixture>
{
    private const string Name = TenantSettings.ConciergeMayMergeName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Its_name_is_the_one_the_web_dialog_and_the_skills_use() =>
        Assert.Equal("concierge.mayMerge", Name);

    [Fact]
    public async Task A_fresh_volume_reads_off()
    {
        await InFreshDatabase(async database =>
        {
            var settings = await LoadedAsync(database);

            Assert.False(settings.ConciergeMayMerge);
            Assert.Equal("off", settings.Current(Name));
            Assert.Equal("builtIn", settings.FallbackSource(Name));
            Assert.Null(settings.Row(Name));
        });
    }

    [Fact]
    public async Task An_existing_volume_with_other_settings_reads_off()
    {
        await InFreshDatabase(async database =>
        {
            // A volume from before this setting existed: other rows, none for it.
            await new SqliteTenantSettingsStore(database).WriteAsync(
                [new TenantSettingChange("quiet.window", null, "00:45:00")], null, "someone@example.test", Ct);

            var settings = await LoadedAsync(database);

            Assert.False(settings.ConciergeMayMerge);
            Assert.Null(settings.Row(Name));
            Assert.Equal(TimeSpan.FromMinutes(45), settings.QuietWindow);
        });
    }

    [Fact]
    public async Task It_is_listed_with_one_sentence_saying_what_it_allows_and_that_it_is_off_unless_a_person_turns_it_on()
    {
        using var client = await host.PersonAsync();
        var body = await client.GetFromJsonAsync<JsonElement>("/api/tenant/settings", Ct);
        var entry = body.GetProperty("settings").EnumerateArray().Single(s => s.GetProperty("name").GetString() == Name);

        Assert.Equal("off", entry.GetProperty("default").GetString());
        Assert.Equal("builtIn", entry.GetProperty("defaultSource").GetString());
        Assert.False(entry.GetProperty("readOnly").GetBoolean());

        var description = entry.GetProperty("description").GetString()!;
        Assert.Contains("Concierge", description, StringComparison.Ordinal);
        Assert.Contains("Merge to main", description, StringComparison.Ordinal);
        Assert.Contains("off unless a person turns it on", description, StringComparison.Ordinal);
        Assert.Single(description.Split(". ", StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task A_write_appends_its_tenant_row_and_any_other_value_is_refused_naming_it()
    {
        using var client = await host.PersonAsync();
        var settings = host.Services.GetRequiredService<TenantSettings>();

        var bad = await client.PutAsJsonAsync("/api/tenant/settings", new Dictionary<string, object> { [Name] = "yes" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using (var refused = JsonDocument.Parse(await bad.Content.ReadAsStringAsync(Ct)))
        {
            Assert.Equal(Name, refused.RootElement.GetProperty("field").GetString());
        }

        Assert.False(settings.ConciergeMayMerge);

        var set = await client.PutAsJsonAsync("/api/tenant/settings", new Dictionary<string, object> { [Name] = "on" }, Ct);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.True(settings.ConciergeMayMerge);

        var row = await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.TenantSettingChanged, Name, Ct);
        Assert.NotNull(row);
        Assert.Equal("person@example.test", row!.ActorEmail);

        using var content = new StringContent($$"""{"{{Name}}": null}""", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync("/api/tenant/settings", content, Ct)).StatusCode);
        Assert.False(settings.ConciergeMayMerge);
        Assert.NotNull(await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.TenantSettingReset, Name, Ct));
    }

    [Fact]
    public async Task A_write_is_not_made_when_its_tenant_row_cannot_be()
    {
        await InFreshDatabase(async database =>
        {
            var settings = await LoadedAsync(database);
            await DropTenantEventsAsync(database);

            using var on = JsonDocument.Parse("\"on\"");
            await Assert.ThrowsAnyAsync<Exception>(() => settings.WriteAsync(
                new Dictionary<string, JsonElement> { [Name] = on.RootElement.Clone() }, null, "someone@example.test", Ct));

            Assert.False(settings.ConciergeMayMerge);
            Assert.Null(settings.Row(Name));
            Assert.DoesNotContain(await new SqliteTenantSettingsStore(database).ReadAllAsync(Ct), r => r.Name == Name);
        });
    }

    [Fact]
    public async Task A_reset_removes_nothing_when_its_tenant_row_cannot_be()
    {
        await InFreshDatabase(async database =>
        {
            var settings = await LoadedAsync(database);
            using var on = JsonDocument.Parse("\"on\"");
            await settings.WriteAsync(
                new Dictionary<string, JsonElement> { [Name] = on.RootElement.Clone() }, null, "someone@example.test", Ct);
            Assert.True(settings.ConciergeMayMerge);

            await DropTenantEventsAsync(database);
            using var reset = JsonDocument.Parse("null");
            await Assert.ThrowsAnyAsync<Exception>(() => settings.WriteAsync(
                new Dictionary<string, JsonElement> { [Name] = reset.RootElement.Clone() }, null, "someone@example.test", Ct));

            Assert.True(settings.ConciergeMayMerge);
            Assert.Equal("on", Assert.Single(await new SqliteTenantSettingsStore(database).ReadAllAsync(Ct), r => r.Name == Name).Value);
        });
    }

    private static async Task<TenantSettings> LoadedAsync(string database)
    {
        var settings = new TenantSettings(new SqliteTenantSettingsStore(database), new ConfigurationBuilder().Build(), cpuCount: 1);
        await settings.LoadAsync(Ct);
        return settings;
    }

    private static async Task DropTenantEventsAsync(string database)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database}");
        await connection.OpenAsync(Ct);
        await using var drop = connection.CreateCommand();
        drop.CommandText = "DROP TABLE tenant_events";
        await drop.ExecuteNonQueryAsync(Ct);
    }

    private static async Task InFreshDatabase(Func<string, Task> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"harness-may-merge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "messages.db");

        try
        {
            await new SchemaMigrator(database).ApplyAsync(SchemaModules.All, Ct);
            await body(database);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }
}
