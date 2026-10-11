using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// EVERY PERSON'S WRITE TO AN OUTCOME OR A LINK LANDS WITH ITS TENANT ROW, OR NOT AT ALL. With
/// `tenant_events` taken away, each one - create, rename, confirm, retire, reactivate, merge, reject,
/// linking a workflow, and a tell that names an outcome - is refused and changes nothing. A Host of its own, because the only honest
/// way to make the row unwritable is to take the table away.
/// </summary>
public sealed class OutcomeAuditTests : IAsyncLifetime
{
    private const string Email = "person@example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-outcome-audit-{Guid.NewGuid():N}");

    private WebApplicationFactory<Program> _factory = null!;

    private string _team = "";

    private IOutcomeStore Outcomes => _factory.Services.GetRequiredService<IOutcomeStore>();

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning"));

        var catalog = _factory.Services.GetRequiredService<AgentCatalog>();
        var agent = catalog.Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        _team = (await _factory.Services.GetRequiredService<TeamRegistry>().CreateAsync("Alpha", agent, memberAgent: agent)).Id;
        await _factory.Services.GetRequiredService<IUserStore>().CreateAsync(Email, HostFixture.Password);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();

        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Every_persons_write_is_refused_and_changes_nothing_when_its_tenant_row_cannot_be_written()
    {
        var person = _factory.CreateClient();
        (await person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = HostFixture.Password }, Ct)).EnsureSuccessStatusCode();

        async Task<Outcome> Create(string name) =>
            (await Outcomes.FindAsync((await (await person.PostAsJsonAsync("/api/outcomes", new { name }, Ct))
                .Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!, Ct))!;

        var active = await Create("Active outcome");
        var into = await Create("Into outcome");
        var retired = await Create("Retired outcome");
        (await person.PostAsync($"/api/outcomes/{retired.Id}/retire", null, Ct)).EnsureSuccessStatusCode();
        var proposed = (await Outcomes.ProposeAsync("Proposed outcome", null, new OutcomeActor("Alpha/Manager", OutcomeActorKind.Member),
            null, null, new TriggerAudit("Alpha/Manager", null, TenantActions.OutcomeCreated, null, null, null), Ct)).Outcome!;

        var told = await person.PostAsJsonAsync($"/api/teams/{_team}/containers/Manager/tell", new { instruction = "look" }, Ct);
        var workflow = (await told.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("correlationId").GetInt64();

        var before = await Outcomes.ListAsync(Ct);

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=false"))
        {
            await connection.OpenAsync(Ct);
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE tenant_events";
            await drop.ExecuteNonQueryAsync(Ct);
        }

        var writes = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Post, "/api/outcomes", new { name = "Never made" }),
            (HttpMethod.Patch, $"/api/outcomes/{active.Id}", new { name = "Never renamed" }),
            (HttpMethod.Patch, $"/api/outcomes/{active.Id}", new { description = "Never described" }),
            (HttpMethod.Post, $"/api/outcomes/{proposed.Id}/confirm", null),
            (HttpMethod.Post, $"/api/outcomes/{active.Id}/retire", null),
            (HttpMethod.Post, $"/api/outcomes/{retired.Id}/reactivate", null),
            (HttpMethod.Post, $"/api/outcomes/{active.Id}/merge", new { into = into.Id }),
            (HttpMethod.Delete, $"/api/outcomes/{proposed.Id}", null),
            (HttpMethod.Put, $"/api/teams/{_team}/workflows/{workflow}/outcome", new { outcome = active.Id }),
            (HttpMethod.Post, $"/api/teams/{_team}/containers/Manager/tell", new { instruction = "never told", outcome = active.Id }),
        };

        foreach (var (method, path, body) in writes)
        {
            using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
            var response = await person.SendAsync(request, Ct);
            Assert.False(response.IsSuccessStatusCode, $"{method} {path} succeeded without its tenant row.");
        }

        Assert.Equal(before, await Outcomes.ListAsync(Ct));
        Assert.Null(await Outcomes.CurrentLinkAsync(workflow, Ct));

        // The tell's instruction did not land either: it, its link and its row are one transaction.
        // Its own text is looked for, not a row count: the platform appends rows of its own meanwhile.
        Assert.Equal(0, await RowsSayingAsync("never told"));
    }

    [Fact]
    public async Task A_persons_none_is_refused_and_unlinks_nothing_when_its_tenant_row_cannot_be_written()
    {
        var person = _factory.CreateClient();
        (await person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = HostFixture.Password }, Ct)).EnsureSuccessStatusCode();

        var outcome = (await (await person.PostAsJsonAsync("/api/outcomes", new { name = "Served" }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        var told = await person.PostAsJsonAsync($"/api/teams/{_team}/containers/Manager/tell", new { instruction = "look", outcome }, Ct);
        var workflow = (await told.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("correlationId").GetInt64();
        var linked = (await Outcomes.CurrentLinkAsync(workflow, Ct))!;

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=false"))
        {
            await connection.OpenAsync(Ct);
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE tenant_events";
            await drop.ExecuteNonQueryAsync(Ct);
        }

        var refused = await person.DeleteAsync($"/api/teams/{_team}/workflows/{workflow}/outcome", Ct);
        Assert.False(refused.IsSuccessStatusCode, "The unlink succeeded without its tenant row.");
        Assert.Equal(linked, await Outcomes.CurrentLinkAsync(workflow, Ct));
    }

    private async Task<long> RowsSayingAsync(string text)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM messages WHERE instr(payload, $text) > 0";
        count.Parameters.AddWithValue("$text", text);
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }
}
