using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A TEAM'S NAME IS A LABEL. <c>PUT /api/teams/{team}/name</c> changes what the team is shown by, and
/// what its Manager and members are told it is called; its identifier, folder and member ids are kept.
/// The label and its <c>team.renamed</c> row land together or not at all. A Concierge that names the
/// team by its new name, or by its earlier one, still reaches it (<see cref="TeamNames"/>). A Host of
/// its own per test, because one case takes the tenant table away.
/// </summary>
public sealed class TeamRenameTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"harness-team-rename-{Guid.NewGuid():N}");

    private WebApplicationFactory<Program> _factory = null!;

    private string Database => Path.Combine(_dataRoot, "messages.db");

    private TeamRegistry Teams => _factory.Services.GetRequiredService<TeamRegistry>();

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning"));

        var agent = _factory.Services.GetRequiredService<AgentCatalog>().Definitions
            .First(d => d.Mode == AgentMode.Headless).Name;
        await Teams.CreateAsync("Alpha", agent, memberAgent: agent);
        await Teams.CreateAsync("Beta", agent, memberAgent: agent);
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

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }

    [Fact]
    public async Task A_rename_changes_the_label_and_the_prompts_writes_its_row_and_keeps_the_identifier_and_folder()
    {
        var client = await PersonAsync();
        var folder = Teams.All(withRoot: true).Single(t => t.Id == "Alpha").Root!;

        var answer = await client.PutAsJsonAsync("/api/teams/alpha/name", new { name = "  Gamma Squad  " }, Ct);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var body = await answer.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("Alpha", "Gamma Squad"), (body.GetProperty("team").GetString(), body.GetProperty("name").GetString()));

        var summary = Teams.All(withRoot: true).Single(t => t.Id == "Alpha");
        Assert.Equal("Gamma Squad", summary.Name);
        Assert.Equal(folder, summary.Root);
        Assert.True(Directory.Exists(folder));
        Assert.Equal("Gamma Squad", await ScalarAsync("SELECT name FROM teams WHERE id = 'Alpha'"));

        var manager = _factory.Services.GetRequiredService<ContainerHost>().Find(new ContainerId("Alpha", "Manager"))!;
        Assert.Contains("Gamma Squad", manager.SystemPrompt, StringComparison.Ordinal);

        var row = Assert.Single(
            (await _factory.Services.GetRequiredService<ITenantLog>().ReadAsync(take: 100, ct: Ct)).Events,
            r => r.Action == TenantActions.TeamRelabelled);
        Assert.Equal(("Alpha", "person@example.test"), (row.Subject, row.ActorEmail));
        Assert.Contains("\"from\":\"Alpha\"", row.Detail, StringComparison.Ordinal);
        Assert.Contains("\"to\":\"Gamma Squad\"", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blank_an_over_long_and_another_teams_name_are_refused_with_a_sentence_and_nothing_changes()
    {
        var client = await PersonAsync();

        var blank = await client.PutAsJsonAsync("/api/teams/Alpha/name", new { name = "   " }, Ct);
        var tooLong = await client.PutAsJsonAsync("/api/teams/Alpha/name", new { name = new string('x', 61) }, Ct);
        var taken = await client.PutAsJsonAsync("/api/teams/Alpha/name", new { name = "beta" }, Ct);
        var missing = await client.PutAsJsonAsync("/api/teams/Nobody/name", new { name = "Gamma" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(
            "Another team is already called 'Beta'. Choose another name.",
            (await taken.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());

        Assert.Equal("Alpha", Teams.LabelFor("Alpha"));
        Assert.Equal(DBNull.Value, await ScalarAsync("SELECT name FROM teams WHERE id = 'Alpha'") ?? DBNull.Value);
    }

    [Fact]
    public async Task A_team_is_not_renamed_when_its_tenant_row_cannot_be_written()
    {
        var client = await PersonAsync();
        await using (var connection = new SqliteConnection($"Data Source={Database};Pooling=false"))
        {
            await connection.OpenAsync(Ct);
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE tenant_events";
            await drop.ExecuteNonQueryAsync(Ct);
        }

        var answer = await client.PutAsJsonAsync("/api/teams/Alpha/name", new { name = "Gamma" }, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, answer.StatusCode);
        Assert.Equal("Alpha", Teams.LabelFor("Alpha"));
        Assert.Equal(DBNull.Value, await ScalarAsync("SELECT name FROM teams WHERE id = 'Alpha'") ?? DBNull.Value);
    }

    /// <summary>
    /// A CONCIERGE THAT NAMES THE TEAM still reaches it: by its id, by its name now, and by its name
    /// before the rename, which comes with a note saying what it is called now. A name nobody has is
    /// nothing, and a team's current name wins over another team's earlier one.
    /// </summary>
    [Fact]
    public async Task A_team_is_found_by_its_id_its_name_now_and_its_earlier_name_with_a_note()
    {
        var client = await PersonAsync();
        (await client.PutAsJsonAsync("/api/teams/Alpha/name", new { name = "Gamma" }, Ct)).EnsureSuccessStatusCode();
        var names = _factory.Services.GetRequiredService<TeamNames>();

        Assert.Equal(new TeamNames.Found("Alpha", null), await names.ResolveAsync("alpha", Ct));
        Assert.Equal(new TeamNames.Found("Alpha", null), await names.ResolveAsync(" gamma ", Ct));
        Assert.Null(await names.ResolveAsync("Delta", Ct));

        (await client.PutAsJsonAsync("/api/teams/Alpha/name", new { name = "Delta" }, Ct)).EnsureSuccessStatusCode();

        var earlier = await names.ResolveAsync("Gamma", Ct);
        Assert.Equal("Alpha", earlier!.Id);
        Assert.Equal(TeamNames.EarlierNameNote("Gamma", "Alpha", "Delta"), earlier.Note);

        // Beta now takes the name Alpha went by: the team called that now wins.
        (await client.PutAsJsonAsync("/api/teams/Beta/name", new { name = "Gamma" }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(new TeamNames.Found("Beta", null), await names.ResolveAsync("Gamma", Ct));
    }
}
