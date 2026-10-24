using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// THE ONE-TIME START STEP: on a volume from before members owned their values, every plugin member
/// bound to a key with a STORED value gets that value as its own, the step writes its
/// <c>member.secrets-copied</c> row once with counts only, a key set only in the Host's environment is
/// never copied, the binding and the stored key stay, and a later start copies nothing. Three Host
/// starts over one data root, a fixture plugin.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class MemberSecretsCopyTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-secrets-copy-{Guid.NewGuid():N}");
    private readonly string _storedKey = $"COPY_TEST_STORED_{Guid.NewGuid():N}".ToUpperInvariant();
    private readonly string _environmentKey = $"COPY_TEST_ENVIRONMENT_{Guid.NewGuid():N}".ToUpperInvariant();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Database => Path.Combine(_dataRoot, "messages.db");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        PluginInstall.Write(_dataRoot, "copied",
            manifest: PluginInstall.Manifest("copied", edit: m => m["secrets"] = JsonNode.Parse(
                """{"token":{"description":"The service's token.","required":true}}""")));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable(_environmentKey, null);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task The_start_step_copies_each_stored_key_onto_its_members_once_and_never_an_environment_value()
    {
        var stored = $"stored-{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(_environmentKey, $"environment-{Guid.NewGuid():N}");
        string team;

        // FIRST START: members bound by key, as every member was before the step - two on the stored
        // key, one on a key the Host holds only in its environment.
        await using (var first = Factory())
        {
            team = (await first.Services.GetRequiredService<TeamRegistry>()
                .CreateAsync("Copies", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
            await first.Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password, ct: Ct);
            using var person = await SignInAsync(first);

            Assert.Equal(HttpStatusCode.OK, (await person.PutAsJsonAsync($"/api/secrets/{_storedKey}", new { value = stored }, Ct)).StatusCode);
            foreach (var (name, key) in new[] { ("Alpha", _storedKey), ("Beta", _storedKey), ("Gamma", _environmentKey) })
            {
                var hired = await person.PostAsJsonAsync($"/api/teams/{team}/containers", new
                {
                    name, agent = "plugin:copied", secrets = new Dictionary<string, string> { ["token"] = key },
                }, Ct);
                Assert.True(hired.IsSuccessStatusCode, await hired.Content.ReadAsStringAsync(Ct));
            }
        }

        // The volume is now as one from before the step: no member owns a value and no step row.
        await ExecuteAsync("DELETE FROM plugin_member_secrets; DELETE FROM tenant_events WHERE action = 'member.secrets-copied'");

        // SECOND START: the step runs.
        await using (var second = Factory())
        {
            var values = second.Services.GetRequiredService<PluginMemberSecretStore>();
            Assert.Equal(stored, (await values.ValuesAsync(new ContainerId(team, "Alpha"), Ct)).GetValueOrDefault("token"));
            Assert.Equal(stored, (await values.ValuesAsync(new ContainerId(team, "Beta"), Ct)).GetValueOrDefault("token"));
            Assert.Empty(await values.ValuesAsync(new ContainerId(team, "Gamma"), Ct));

            var row = Assert.Single(await ColumnAsync("SELECT detail FROM tenant_events WHERE action = 'member.secrets-copied'"));
            Assert.Equal(JsonSerializer.Serialize(new { members = 2, fields = 2, keys = 1 }), row);

            // The binding and the stored key stay.
            Assert.Equal(stored, second.Services.GetRequiredService<PluginSecretStore>().TryGet(_storedKey));
            Assert.Equal(_storedKey, (await second.Services.GetRequiredService<IPluginMemberSettingsStore>().ForAsync(new ContainerId(team, "Alpha"), Ct)).Secrets["token"]);

            // A person clears one member's copied value.
            using var person = await SignInAsync(second);
            Assert.Equal(HttpStatusCode.OK, (await person.DeleteAsync($"/api/teams/{team}/members/Beta/secrets/token", Ct)).StatusCode);
        }

        // THIRD START: nothing is copied again, and no second row.
        await using (var third = Factory())
        {
            var values = third.Services.GetRequiredService<PluginMemberSecretStore>();
            Assert.Empty(await values.ValuesAsync(new ContainerId(team, "Beta"), Ct));
            Assert.Single(await ColumnAsync("SELECT detail FROM tenant_events WHERE action = 'member.secrets-copied'"));
        }

        // The environment value is in no file under the data root.
        SqliteConnection.ClearAllPools();
        var environmentValue = Environment.GetEnvironmentVariable(_environmentKey)!;
        foreach (var file in Directory.EnumerateFiles(_dataRoot, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, Ct); }
            catch (IOException) { continue; }
            Assert.True(bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(environmentValue)) < 0, file);
        }
    }

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
        .UseSetting("DataRoot", _dataRoot)
        .UseSetting("Logging:LogLevel:Default", "Warning")
        .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

    private static async Task<HttpClient> SignInAsync(WebApplicationFactory<Program> factory)
    {
        var person = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
        return person;
    }

    private async Task<IReadOnlyList<string>> ColumnAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) rows.Add(reader.GetString(0));
        return rows;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }
}
