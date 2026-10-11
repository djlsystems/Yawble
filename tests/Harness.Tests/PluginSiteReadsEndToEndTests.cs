using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// ON THE REAL HOST: what a person writes and deletes through a site's page is what a plugin member
/// of the same team reads on its next run - hired through the ordinary member route, told work
/// through the ordinary `tell` route, its site data read as the member, appending nothing to the
/// tenant log.
/// </summary>
public sealed class PluginSiteReadsEndToEndTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-plugin-reads-e2e-{Guid.NewGuid():N}");

    // OUTSIDE the data root: where the plugin copies its stdin for the test to read.
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"plugin-reads-e2e-stdin-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private ContainerId Keeper => new(_team, "Keeper");

    private string StdinCopy => Path.Combine(_outside, "request.json");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_outside);

        PluginInstall.Write(_dataRoot, "board-keeper",
            script: $"cat > '{StdinCopy}'\necho '{{\"t\":\"result\",\"ok\":true,\"output\":\"read\"}}'",
            manifest: PluginInstall.Manifest("board-keeper", edit: m =>
                m["reads"] = JsonNode.Parse("""[{"site":"board","collection":"items"}]""")));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Boards", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = await SignedInAsync(allowAutoRedirect: true);

        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name = "Keeper", agent = "plugin:board-keeper" }, Ct);
        Assert.Equal(HttpStatusCode.OK, hired.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var directory in new[] { _dataRoot, _outside })
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    private async Task<HttpClient> SignedInAsync(bool allowAutoRedirect)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = allowAutoRedirect });
        (await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>The site, created and published as a person, as <c>SitesTests</c> does; then opened by
    /// the person, answering the page's data API under its capability.</summary>
    private async Task<string> PublishedAndOpenedAsync()
    {
        var sites = Services.GetRequiredService<SiteService>();
        var person = SiteActor.Person("person-1", Email);
        Assert.True((await sites.CreateAsync(_team, "board", person, Ct)).Ok);

        var folder = Path.Combine(Services.GetRequiredService<TeamDocuments>().EnsureFor(_team), "site-src", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.html"), "<p>board</p>");
        var published = await sites.PublishAsync(_team, "board", folder, person, Ct);
        Assert.True(published.Ok, published.Refusal);

        using var opener = await SignedInAsync(allowAutoRedirect: false);
        var entry = await opener.GetAsync($"/sites/{_team}/board/", Ct);
        Assert.Equal(HttpStatusCode.Redirect, entry.StatusCode);
        return $"{entry.Headers.Location!.OriginalString}_api/";
    }

    private static StringContent Text(string body) => new(body, Encoding.UTF8, "text/plain");

    private async Task<long> TenantRowsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")}");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tenant_events";
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<IReadOnlyList<Message>> TerminalRowsAsync() =>
        [.. (await Services.GetRequiredService<IMessageLog>()
                .ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct))
            .Where(m => m.Source == Keeper.ToString())];

    /// <summary>One plugin run through the person's `tell` route: its stdin's one envelope, and how
    /// many tenant rows the run appended.</summary>
    private async Task<(JsonNode Envelope, long TenantRows)> RunAsync()
    {
        var after = (await TerminalRowsAsync()).Select(m => m.Seq).DefaultIfEmpty(0).Max();
        var before = await TenantRowsAsync();

        var told = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers/Keeper/tell", new { instruction = "sync" }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        Message? row = null;
        while (row is null && DateTime.UtcNow < deadline)
        {
            row = (await TerminalRowsAsync()).FirstOrDefault(m => m.Seq > after);
            if (row is null) await Task.Delay(50, Ct);
        }

        Assert.NotNull(row);
        Assert.Equal(MessageTypes.Completed, row.Type);

        var envelope = JsonNode.Parse(File.ReadAllText(StdinCopy))!["sites"]!.AsArray().Single()!;
        return (envelope, await TenantRowsAsync() - before);
    }

    private static string[] Ids(JsonNode envelope) =>
        [.. envelope["documents"]!.AsArray().Select(d => (string)d!["id"]!)];

    [Fact]
    public async Task What_a_person_writes_through_the_page_is_what_the_plugin_reads_on_its_next_run()
    {
        var api = await PublishedAndOpenedAsync();
        using var page = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await page.PostAsync($"{api}data/items/t1", Text("""{"title":"First","status":"open"}"""), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await page.PostAsync($"{api}data/items/t2", Text("""{"title":"Second","status":"open"}"""), Ct)).StatusCode);

        var (first, firstRows) = await RunAsync();
        Assert.Equal(["t1", "t2"], Ids(first).Order(StringComparer.Ordinal));
        Assert.Equal(0, firstRows);

        // The person archives one and deletes the other, on the page.
        Assert.Equal(HttpStatusCode.OK, (await page.PostAsync($"{api}data/items/t1", Text("""{"title":"First","status":"archived"}"""), Ct)).StatusCode);
        Assert.Equal("""{"deleted":true}""", await (await page.PostAsync($"{api}data/items/t2/delete", null, Ct)).Content.ReadAsStringAsync(Ct));

        var (second, secondRows) = await RunAsync();
        var kept = Assert.Single(second["documents"]!.AsArray())!;
        Assert.Equal("t1", (string)kept["id"]!);
        Assert.Equal("archived", (string)kept["doc"]!["status"]!);
        Assert.Equal(Email, (string)kept["updatedBy"]!);
        Assert.Equal(1, (int)second["total"]!);
        Assert.Null(second["cut"]);
        Assert.Null(second["missing"]);
        Assert.Equal(0, secondRows);
    }
}
