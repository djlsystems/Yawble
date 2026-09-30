using System.Net;
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
/// A PERSON'S BACKLOG EDIT AND ITS TENANT ROWS ARE ONE TRANSACTION, through the real Host.
/// `PATCH /api/backlog/{id}` changes the item's fields and the outcome it serves; with
/// `tenant_events` taken away it answers 500 in a sentence and neither changes. A Host of its own
/// per test, because the only honest way to make the row unwritable is to take the table away.
/// </summary>
public sealed class BacklogEditAuditTransactionTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"harness-backlog-audit-{Guid.NewGuid():N}");

    private WebApplicationFactory<Program> _factory = null!;

    private string Database => Path.Combine(_dataRoot, "messages.db");

    private IBacklogStore Backlog => _factory.Services.GetRequiredService<IBacklogStore>();

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning"));

        await _factory.Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", HostFixture.Password);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();

        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<HttpClient> PersonAsync()
    {
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync(
            "/api/auth/login", new { email = "person@example.test", password = HostFixture.Password }, Ct))
            .EnsureSuccessStatusCode();
        return client;
    }

    private async Task DropTenantEventsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var drop = connection.CreateCommand();
        drop.CommandText = "DROP TABLE tenant_events";
        await drop.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task<string> OutcomeAsync(HttpClient client, string name) =>
        (await JsonAsync(await client.PostAsJsonAsync("/api/outcomes", new { name }, Ct))).GetProperty("id").GetString()!;

    private static async Task<long> ItemAsync(HttpClient client) =>
        (await JsonAsync(await client.PostAsJsonAsync("/api/backlog", new { title = "Crawl openings", body = "spec" }, Ct)))
            .GetProperty("id").GetInt64();

    [Fact]
    public async Task An_edit_lands_with_its_tenant_row_naming_the_person_and_the_outcome()
    {
        var client = await PersonAsync();
        var outcome = await OutcomeAsync(client, "Qualified openings");
        var item = await ItemAsync(client);

        var edited = await client.PatchAsJsonAsync(
            $"/api/backlog/{item}", new { state = "implemented", outcomeId = outcome }, Ct);
        Assert.Equal(outcome, (await JsonAsync(edited)).GetProperty("outcomeId").GetString());

        var rows = (await _factory.Services.GetRequiredService<ITenantLog>().ReadAsync(take: 100, ct: Ct)).Events;
        var subject = PlatformBacklogId.Format(item);
        var row = Assert.Single(rows, r => r.Action == TenantActions.BacklogItemEdited && r.Subject == subject);
        Assert.Equal("person@example.test", row.ActorEmail);
        Assert.Contains(outcome, row.Detail, StringComparison.Ordinal);
        Assert.Single(rows, r => r.Action == TenantActions.BacklogItemImplemented && r.Subject == subject);
    }

    [Fact]
    public async Task A_backlog_edit_and_its_outcome_change_are_not_saved_when_its_tenant_row_cannot_be_written()
    {
        var client = await PersonAsync();
        var first = await OutcomeAsync(client, "Qualified openings");
        var second = await OutcomeAsync(client, "Fewer stale listings");
        var item = await ItemAsync(client);
        (await client.PatchAsJsonAsync($"/api/backlog/{item}", new { outcomeId = first }, Ct)).EnsureSuccessStatusCode();
        var before = (await Backlog.GetAsync(item, Ct))!;
        await DropTenantEventsAsync();

        var moved = await client.PatchAsJsonAsync($"/api/backlog/{item}", new
        {
            title = "Renamed", body = "new spec", state = "implemented", outcomeId = second,
        }, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, moved.StatusCode);
        var error = (await moved.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString()!;
        Assert.StartsWith($"{PlatformBacklogId.Format(item)} was not changed", error, StringComparison.Ordinal);

        var cleared = await client.PatchAsJsonAsync($"/api/backlog/{item}", new { outcomeId = "" }, Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, cleared.StatusCode);

        var after = (await Backlog.GetAsync(item, Ct))!;
        Assert.Equal(
            (before.Title, before.Body, before.State, before.OutcomeId, before.UpdatedAt),
            (after.Title, after.Body, after.State, after.OutcomeId, after.UpdatedAt));
        Assert.Equal(first, after.OutcomeId);
    }
}
