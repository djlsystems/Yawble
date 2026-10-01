using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host;
using Harness.Identity;
using Harness.Messaging;
using Microsoft.Extensions.Configuration;

namespace Harness.Tests.Host;

/// <summary>
/// "System packages" is a tenant setting like the others - listed, validated,
/// audited, kept across a restart - and the host hands it to the entrypoint as a plain file on the
/// data root, because the entrypoint installs it before the host (and its database) is running.
/// </summary>
public sealed class SystemPackagesSettingTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task The_setting_is_listed_with_the_restart_sentence()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();

        var entry = (await ReadAsync(client, ct)).Single(s => s.GetProperty("name").GetString() == "system.packages");

        Assert.Equal(JsonValueKind.Array, entry.GetProperty("value").ValueKind);
        Assert.Contains("Adding one costs a restart, not an image rebuild.", entry.GetProperty("description").GetString());
        Assert.False(entry.GetProperty("readOnly").GetBoolean());
    }

    [Fact]
    public async Task A_change_is_written_to_the_file_the_entrypoint_reads()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();

        var response = await client.PutAsJsonAsync("/api/tenant/settings", new Dictionary<string, object>
        {
            ["system.packages"] = new[] { "htop", " postgresql-client ", "htop", "libstdc++6", "g++-12" },
        }, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = (await ReadAsync(client, ct)).Single(s => s.GetProperty("name").GetString() == "system.packages");
        Assert.Equal(["htop", "postgresql-client", "libstdc++6", "g++-12"],
            entry.GetProperty("value").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("row", entry.GetProperty("source").GetString());

        var path = Path.Combine(host.DataRoot, SystemPackages.FileName);
        var names = File.ReadAllLines(path).Where(line => !line.StartsWith('#')).ToArray();
        Assert.Equal(["htop", "postgresql-client", "libstdc++6", "g++-12"], names);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public async Task A_reset_rewrites_the_packages_file()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();
        var path = Path.Combine(host.DataRoot, SystemPackages.FileName);

        var set = await client.PutAsJsonAsync("/api/tenant/settings", new Dictionary<string, object>
        {
            ["system.packages"] = new[] { "ripgrep" },
        }, ct);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Contains("ripgrep", File.ReadAllLines(path));

        using var content = new StringContent(
            """{"system.packages": null}""", System.Text.Encoding.UTF8, "application/json");
        var reset = await client.PutAsync("/api/tenant/settings", content, ct);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        // The file follows the fallback now in force, with no restart.
        var entry = (await ReadAsync(client, ct)).Single(s => s.GetProperty("name").GetString() == "system.packages");
        Assert.Equal("appsettings", entry.GetProperty("source").GetString());
        var fallback = entry.GetProperty("value").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(fallback, File.ReadAllLines(path).Where(line => !line.StartsWith('#')).ToArray());
        Assert.DoesNotContain("ripgrep", File.ReadAllLines(path));
    }

    [Theory]
    [InlineData("vim; rm -rf /")]
    [InlineData("$(touch /tmp/pwned)")]
    [InlineData("-o=APT::Get::AllowUnauthenticated=true")]
    [InlineData("Vim")]
    [InlineData("v")]
    [InlineData("vim=2:9.1")]
    [InlineData("vim\nnano")]
    [InlineData("")]
    public async Task A_name_that_is_not_a_package_name_is_refused_and_nothing_is_written(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();
        var path = Path.Combine(host.DataRoot, SystemPackages.FileName);
        var before = File.Exists(path) ? File.ReadAllText(path) : null;

        var response = await client.PutAsJsonAsync("/api/tenant/settings", new Dictionary<string, object>
        {
            ["system.packages"] = new[] { "jq", name },
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("system.packages", body.RootElement.GetProperty("field").GetString());
        Assert.Equal(before, File.Exists(path) ? File.ReadAllText(path) : null);
    }

    [Fact]
    public async Task A_list_that_is_not_an_array_of_strings_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();

        foreach (var value in new object[] { "htop", 3, new[] { 1 } })
        {
            var response = await client.PutAsJsonAsync("/api/tenant/settings",
                new Dictionary<string, object> { ["system.packages"] = value }, ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task The_list_survives_a_restart_and_the_file_is_rewritten_from_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"harness-packages-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "messages.db");

        try
        {
            await new SchemaMigrator(database).ApplyAsync(SchemaModules.All, ct);
            var configuration = new ConfigurationBuilder().Build();

            var settings = new TenantSettings(new SqliteTenantSettingsStore(database), configuration, cpuCount: 1);
            await settings.LoadAsync(ct);
            Assert.Empty(settings.SystemPackages);

            using var value = JsonDocument.Parse("""["ffmpeg","imagemagick"]""");
            await settings.WriteAsync(
                new Dictionary<string, JsonElement> { ["system.packages"] = value.RootElement.Clone() },
                null, "someone@example.test", ct);

            var restarted = new TenantSettings(new SqliteTenantSettingsStore(database), configuration, cpuCount: 1);
            await restarted.LoadAsync(ct);
            Assert.Equal(["ffmpeg", "imagemagick"], restarted.SystemPackages);

            // What Program.cs does at start: the file follows the row, whatever the file said.
            File.WriteAllText(Path.Combine(directory, SystemPackages.FileName), "stale\n");
            SystemPackages.WriteFile(directory, restarted.SystemPackages);
            Assert.Equal(["ffmpeg", "imagemagick"],
                File.ReadAllLines(Path.Combine(directory, SystemPackages.FileName)).Where(l => !l.StartsWith('#')));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void An_appsettings_list_is_the_fallback()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SystemPackages:0"] = "htop",
                ["SystemPackages:1"] = "jq",
            })
            .Build();

        var settings = new TenantSettings(new SqliteTenantSettingsStore(":memory:"), configuration, cpuCount: 1);

        Assert.Equal("""["htop","jq"]""", settings.Fallback("system.packages"));
    }

    private static async Task<List<JsonElement>> ReadAsync(HttpClient client, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/tenant/settings", ct));
        return document.RootElement.GetProperty("settings").EnumerateArray().Select(e => e.Clone()).ToList();
    }
}
