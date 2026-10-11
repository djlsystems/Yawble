using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// ON THE REAL HOST: a file a plugin member or an agent member writes into the site's files folder,
/// where it was told the folder is, downloads from the site's page by the relative path stored in
/// the site's data. The plugin is hired through the ordinary member route with a bound secret and a
/// connection, and told work through the ordinary `tell` route. Everything the site then serves -
/// the files, the data, every answer the test was given - is scanned for anything secret.
/// </summary>
public sealed class SiteFilesEndToEndTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string WorkerKey = "the-worker-key-for-the-files-test-Q3";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-site-files-e2e-{Guid.NewGuid():N}");

    // OUTSIDE the data root: where the plugin copies its stdin for the test to read.
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"site-files-e2e-stdin-{Guid.NewGuid():N}");

    private readonly string _secretKey = $"SITE_FILES_SECRET_{Guid.NewGuid():N}".ToUpperInvariant();
    private readonly string _secret = $"secret-{Guid.NewGuid():N}";
    private readonly string _accessToken = $"access-{Guid.NewGuid():N}";

    private readonly List<string> _answers = [];

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private ContainerId Writer => new(_team, "Writer");

    private string StdinCopy => Path.Combine(_outside, "request.json");

    private SiteService Sites => Services.GetRequiredService<SiteService>();

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_outside);
        Environment.SetEnvironmentVariable(_secretKey, _secret);

        // The plugin: reads its folder and the marker it is told from stdin, writes the file there, and
        // puts the relative path in the site's data - with the secret in a note, which is redacted.
        PluginInstall.Write(_dataRoot, "board-writer",
            script: $$$"""
                req=$(cat)
                printf '%s' "$req" > '{{{StdinCopy}}}'
                folder=$(printf '%s' "$req" | grep -o '"folder":"[^"]*"' | head -1 | cut -d'"' -f4)
                marker=$(printf '%s' "$req" | grep -o '"instruction":"[^"]*"' | head -1 | cut -d'"' -f4)
                secret=$(printf '%s' "$req" | grep -o '"token":"[^"]*"' | head -1 | cut -d'"' -f4)
                mkdir -p "$folder/out"
                printf '%s' "$marker" > "$folder/out/$marker.txt"
                echo "{\"t\":\"site.put\",\"site\":\"board\",\"collection\":\"items\",\"id\":\"i1\",\"doc\":{\"file\":\"out/$marker.txt\",\"note\":\"$secret\"}}"
                echo '{"t":"result","ok":true,"output":"written"}'
                """,
            manifest: PluginInstall.Manifest("board-writer", edit: m =>
            {
                m["secrets"] = JsonNode.Parse("""{"token":{"required":true}}""");
                m["connections"] = JsonNode.Parse("""{"store":{"description":"A store.","providers":["google"],"scopes":{"google":["read"]},"required":true}}""");
            }));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Workers:Key", WorkerKey)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Boards", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = await SignedInAsync(allowAutoRedirect: true);
    }

    public async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable(_secretKey, null);
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

    /// <summary>A published site, opened by the person: the page's capability path.</summary>
    private async Task<string> PublishedAndOpenedAsync(string site)
    {
        var person = SiteActor.Person("person-1", Email);
        Assert.True((await Sites.CreateAsync(_team, site, person, Ct)).Ok);

        var folder = Path.Combine(Services.GetRequiredService<TeamDocuments>().EnsureFor(_team), "site-src", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.html"), "<p>board</p>");
        var published = await Sites.PublishAsync(_team, site, folder, person, Ct);
        Assert.True(published.Ok, published.Refusal);

        using var opener = await SignedInAsync(allowAutoRedirect: false);
        var entry = await opener.GetAsync($"/sites/{_team}/{site}/", Ct);
        Assert.Equal(HttpStatusCode.Redirect, entry.StatusCode);
        return entry.Headers.Location!.OriginalString;
    }

    /// <summary>A GET from the page, a client with no cookie; its body kept for the scan.</summary>
    private async Task<HttpResponseMessage> PageGetAsync(string url)
    {
        using var page = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await page.GetAsync(url, Ct);
        _answers.Add(await response.Content.ReadAsStringAsync(Ct));
        return response;
    }

    /// <summary>A connection with a live access token, stored as the person's, and the plugin hired
    /// with it and the secret bound.</summary>
    private async Task HireWriterAsync()
    {
        var user = (await Services.GetRequiredService<IUserStore>().ListAsync(Ct)).Single(u => u.Email == Email);
        var id = $"conn-{Guid.NewGuid():N}"[..17];
        await Services.GetRequiredService<ConnectionStore>().InsertAsync(
            new ConnectionRecord(id, "Store", "google", "account@example.test", ["read"], DateTimeOffset.UtcNow, null, ConnectionRecord.Ok, null),
            new ConnectionTokens("refresh-unused", _accessToken, DateTimeOffset.UtcNow.AddDays(1)),
            user.Id,
            new TriggerAudit(user.Id, Email, "connection.connected", id, id, null),
            Ct);

        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Writer",
            agent = "plugin:board-writer",
            secrets = new Dictionary<string, string> { ["token"] = _secretKey },
            connections = new Dictionary<string, string> { ["store"] = id },
        }, Ct);
        Assert.True(hired.StatusCode == HttpStatusCode.OK, await hired.Content.ReadAsStringAsync(Ct));
    }

    private async Task<Message> TellAndAwaitAsync(string instruction)
    {
        var told = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers/Writer/tell", new { instruction }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var rows = await Services.GetRequiredService<IMessageLog>()
                .ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct);
            if (rows.FirstOrDefault(m => m.Source == Writer.ToString()) is { } row) return row;
            await Task.Delay(50, Ct);
        }

        throw new TimeoutException("The plugin run did not end.");
    }

    /// <summary>Every file in the site's files folder, every document in its collections, and every
    /// answer the page was given: none holds any of <paramref name="secrets"/>. (What the site tool
    /// answers an agent is not served: it names the folder by its absolute path on purpose.)</summary>
    private async Task NothingSecretIsServedAsync(string site, params string[] secrets)
    {
        var served = new List<string>(_answers);

        foreach (var file in Directory.EnumerateFiles(Sites.Files.FolderFor(_team, site), "*", SearchOption.AllDirectories))
        {
            served.Add(File.ReadAllText(file));
        }

        var store = Services.GetRequiredService<ISiteStore>();
        foreach (var collection in await store.CollectionsAsync(_team, site, Ct))
        {
            foreach (var document in await store.ListDocumentsAsync(_team, site, collection, Ct)) served.Add(document.Json);
        }

        Assert.NotEmpty(served);
        foreach (var text in served)
        {
            foreach (var secret in secrets) Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_file_a_plugin_writes_for_a_site_downloads_from_the_page_by_its_stored_path()
    {
        var page = await PublishedAndOpenedAsync("board");
        var other = await PublishedAndOpenedAsync("ledger");
        await HireWriterAsync();

        var marker = $"marker-{Guid.NewGuid():N}";
        var ended = await TellAndAwaitAsync(marker);
        Assert.Equal(MessageTypes.Completed, ended.Type);

        // The run was told the folder and the secret and token it was bound; it put them nowhere served.
        var stdin = JsonNode.Parse(File.ReadAllText(StdinCopy))!;
        Assert.Equal(_secret, (string?)stdin["secrets"]!["token"]);
        Assert.Equal(_accessToken, (string?)stdin["connections"]!["store"]!["accessToken"]);
        Assert.Equal(Sites.Files.FolderFor(_team, "board"), (string?)stdin["siteFiles"]!.AsArray().Single(s => (string?)s!["site"] == "board")!["folder"]);

        var data = await PageGetAsync($"{page}_api/data/items");
        Assert.Equal(HttpStatusCode.OK, data.StatusCode);
        var stored = JsonDocument.Parse(_answers[^1]).RootElement[0].GetProperty("doc").GetProperty("file").GetString()!;
        Assert.Equal($"out/{marker}.txt", stored);

        var download = await PageGetAsync($"{page}_api/files/{stored}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(marker, _answers[^1]);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("text/plain; charset=utf-8", download.Content.Headers.ContentType?.ToString());

        // And the refusals the page might be given.
        Assert.Equal(HttpStatusCode.NotFound, (await PageGetAsync($"{page}_api/files/out/missing.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PageGetAsync($"{page}_api/files/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PageGetAsync($"/sites/{_team}/board/_c/{other.Split('/')[5]}/_api/files/{stored}")).StatusCode);

        await NothingSecretIsServedAsync("board", _secret, _accessToken, WorkerKey, other.Split('/')[5], _dataRoot);
    }

    [Fact]
    public async Task A_file_written_where_the_site_tool_says_downloads_by_the_path_put_through_the_tool()
    {
        var page = await PublishedAndOpenedAsync("board");
        var other = await PublishedAndOpenedAsync("ledger");
        var (tools, key) = Tools();

        var shown = await tools.Site("show", site: "board", cancellationToken: Ct);
        Assert.StartsWith("HTTP 200", shown);
        var folder = JsonDocument.Parse(shown[shown.IndexOf('\n')..]).RootElement.GetProperty("site").GetProperty("filesFolder").GetString()!;

        var marker = $"marker-{Guid.NewGuid():N}";
        Directory.CreateDirectory(Path.Combine(folder, "notes"));
        File.WriteAllText(Path.Combine(folder, "notes", "today.csv"), marker);

        var put = await tools.Site("data", site: "board", op: "put", collection: "items", id: "i1",
            doc: """{"file":"notes/today.csv"}""", cancellationToken: Ct);
        Assert.StartsWith("HTTP 200", put);

        var data = await PageGetAsync($"{page}_api/data/items");
        Assert.Equal(HttpStatusCode.OK, data.StatusCode);
        var stored = JsonDocument.Parse(_answers[^1]).RootElement[0].GetProperty("doc").GetProperty("file").GetString()!;

        var download = await PageGetAsync($"{page}_api/files/{stored}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(marker, _answers[^1]);
        Assert.Equal("text/csv; charset=utf-8", download.Content.Headers.ContentType?.ToString());

        Assert.Equal(HttpStatusCode.BadRequest, (await PageGetAsync($"{page}_api/files/notes%2Ftoday.csv")).StatusCode);

        await NothingSecretIsServedAsync("board", key, WorkerKey, other.Split('/')[5], _dataRoot);
    }

    /// <summary>The site tool as a member of the team with its own key. Not async: the accessor keeps
    /// its context in an AsyncLocal, which would not flow back out of an async method.</summary>
    private (PlatformMcpTools Tools, string Key) Tools()
    {
        var id = new ContainerId(_team, "Builder").ToString();
        HashSet<string> permits = [Permits.Read, Permits.Sites];
        var key = Services.GetRequiredService<IPrincipalStore>()
            .MintAsync(id, PrincipalKind.Container, _team, permits, ct: Ct).GetAwaiter().GetResult();

        var context = new DefaultHttpContext();
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = key;
        context.User = PrincipalClaims.ToClaimsPrincipal(new Principal(id, PrincipalKind.Container, permits), "test");

        return (new PlatformMcpTools(
            new HttpContextAccessor { HttpContext = context },
            Services.GetRequiredService<IPrincipalStore>(),
            Services.GetRequiredService<AgentCatalog>(),
            new ServerClients(_factory.Server.CreateHandler())), key);
    }

    private sealed class ServerClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }
}
