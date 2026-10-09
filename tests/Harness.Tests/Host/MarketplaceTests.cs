using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.Tests.Host;

/// <summary>
/// THE PACKAGE CATALOG says what may be added and whether this instance has it, and nothing it does
/// not know; a fetch brings one checked zip into the instance's documents and never installs. The
/// feed and the download are faked: no test reads the network.
/// </summary>
public sealed class MarketplaceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    private const string Address = "https://example.test/owner/packages/releases/latest/download/catalog.json";
    private const string Downloads = "https://example.test/owner/packages/releases/download/catalog-2026.10.09.1/";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-marketplace-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class FakeFeed(string? address = Address) : IMarketplaceFeed
    {
        public string Catalog { get; set; } = "";
        public Exception? Failure { get; set; }
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public int Reads { get; private set; }
        public List<Uri> Downloaded { get; } = [];
        public Uri? Address { get; } = address is null ? null : new Uri(address);

        public Task<string> ReadCatalogAsync(CancellationToken ct)
        {
            Reads++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(Catalog);
        }

        public Task<byte[]> DownloadAsync(Uri url, long maxBytes, CancellationToken ct)
        {
            Downloaded.Add(url);
            return Files.TryGetValue(url.AbsoluteUri, out var bytes)
                ? Task.FromResult(bytes)
                : throw new HttpRequestException("Response status code does not indicate success: 404 (Not Found).");
        }
    }

    private sealed class RecordingLog(bool refuse = false) : ITenantLog
    {
        public List<(string? Email, string Action, string? Subject, string? Detail)> Rows { get; } = [];

        public Task WriteAsync(
            string? actorId, string? actorEmail, string action, string? subject = null,
            string? subjectName = null, string? detail = null, CancellationToken ct = default)
        {
            if (refuse) throw new IOException("the disk is full");
            Rows.Add((actorEmail, action, subject, detail));
            return Task.CompletedTask;
        }

        public Task<TenantEvent?> FindLatestAsync(string action, string subject, CancellationToken ct = default) =>
            Task.FromResult<TenantEvent?>(null);

        public Task<IReadOnlyList<TenantEvent>> FindLatestBySubjectAsync(
            IReadOnlyCollection<string> actions, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TenantEvent>>([]);

        public Task<TenantLogPage> ReadAsync(long? before = null, int take = 50, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static readonly ClaimsPrincipal Person = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "u1"), new Claim(ClaimTypes.Email, "person@example.test")], "test"));

    /// <summary>A zip of a package: its files, and optionally one entry that is a link.</summary>
    private static byte[] Zip(IReadOnlyDictionary<string, string> files, string? link = null)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                var entry = zip.CreateEntry(path);
                entry.ExternalAttributes = 0x81A4 << 16;
                using var writer = new StreamWriter(entry.Open());
                writer.Write(text);
            }

            if (link is not null)
            {
                var entry = zip.CreateEntry(link);
                entry.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
                using var writer = new StreamWriter(entry.Open());
                writer.Write("/etc/passwd");
            }
        }

        return buffer.ToArray();
    }

    private static readonly byte[] MailZip = Zip(new Dictionary<string, string>
    {
        ["solution.json"] = """{ "id": "mail" }""",
        ["plugins/mail/plugin.json"] = """{ "id": "mail" }""",
    });

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static object Entry(
        string id, string kind, string version, byte[] zip, string? url = null, string? sha256 = null, long? bytes = null) => new
        {
            id,
            kind,
            name = id == "mail" ? "Mail" : id,
            version,
            summary = $"The {id} package.",
            description = $"The {id} package.",
            needs = new
            {
                connections = kind == "solution"
                    ? new object[] { new { slot = "mailbox", providers = new[] { "imap", "microsoft", "google" }, required = true, why = "The account it reads" } }
                    : [],
                secrets = new object[] { new { key = "ADZUNA_KEY", why = "Searches Adzuna.", when = "when the sources setting includes adzuna" } },
                inputs = new object[] { new { name = "Resume", kind = "documents", required = false, why = "Your resume" } },
                runtimes = new[] { "python3" },
            },
            plugins = new[] { new { id, version } },
            platforms = Array.Empty<string>(),
            download = new { url = url ?? $"{Downloads}{id}-{version}.zip", sha256 = sha256 ?? Sha(zip), bytes = bytes ?? zip.LongLength },
            source = $"https://example.test/owner/packages/tree/main/packages/{id}",
        };

    private static string Catalog(params object[] packages) =>
        JsonSerializer.Serialize(new { schema = 1, generatedAt = "2026-10-09T08:00:00Z", packages });

    private FakeFeed MailFeed(string? url = null, string? sha256 = null, long? bytes = null, byte[]? zip = null)
    {
        var feed = new FakeFeed();
        zip ??= MailZip;
        feed.Catalog = Catalog(Entry("mail", "solution", "2.0.3", zip, url, sha256, bytes));
        feed.Files[url ?? $"{Downloads}mail-2.0.3.zip"] = zip;
        return feed;
    }

    private static Marketplace Market(
        FakeFeed feed, bool enabled = true,
        IEnumerable<(string, TeamSolution)>? solutions = null, Func<string, string?>? plugins = null) =>
        new(feed, () => enabled, () => Now, () => solutions ?? [], plugins ?? (_ => null), NullLogger<Marketplace>.Instance);

    private TeamDocuments Documents() => new(new TeamPaths(_root));

    private string Fetched => Path.Combine(_root, "documents", Marketplace.Folder);

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    // ---- reading the catalog ----

    [Fact]
    public async Task A_read_catalog_lists_each_package_with_its_needs_in_words()
    {
        var feed = MailFeed();
        var market = Market(feed);

        var status = await market.CheckAsync(Ct);

        Assert.True(status.Checked);
        Assert.Null(status.Reason);
        Assert.Equal(Now, status.CheckedAt);
        var mail = Assert.Single(status.Packages);
        Assert.Equal(("mail", "solution", "Mail", "2.0.3", "The mail package."), (mail.Id, mail.Kind, mail.Name, mail.Version, mail.Summary));
        Assert.Equal(
            [
                "Needs a mailbox account connected (imap, microsoft or google): The account it reads.",
                "Needs the secret ADZUNA_KEY set on the instance, when the sources setting includes adzuna: Searches Adzuna.",
                "Can take documents in Resume at install: Your resume.",
                "Needs python3 on the instance.",
            ],
            mail.Needs);
        Assert.False(mail.Installed);
        Assert.Null(mail.InstalledVersion);
        Assert.Empty(mail.InstalledOn);
        Assert.False(mail.UpdateAvailable);
    }

    [Fact]
    public async Task A_read_catalog_also_answers_its_needs_as_the_catalog_fields_so_the_console_can_word_them()
    {
        var status = await Market(MailFeed()).CheckAsync(Ct);

        var fields = Assert.Single(status.Packages).CatalogNeeds;
        var connection = Assert.Single(fields.Connections);
        Assert.Equal(("mailbox", true), (connection.Slot, connection.Required));
        Assert.Equal(["imap", "microsoft", "google"], connection.Providers);
        var secret = Assert.Single(fields.Secrets);
        Assert.Equal(("ADZUNA_KEY", "when the sources setting includes adzuna"), (secret.Key, secret.When));
        var input = Assert.Single(fields.Inputs);
        Assert.Equal(("Resume", "documents", false), (input.Name, input.Kind, input.Required));
        Assert.Equal(["python3"], fields.Runtimes);
    }

    [Fact]
    public void The_console_has_words_for_every_provider_a_manifest_may_name()
    {
        var table = File.ReadAllText(Path.Combine(SolutionSamples.RepoRoot(), "web", "src", "lib", "marketplace.ts"));

        Assert.Equal(["google", "microsoft", "custom", "imap"], ConnectionProviders.ManifestProviders);
        foreach (var provider in ConnectionProviders.ManifestProviders)
        {
            Assert.Contains($"\n  {provider}: {{ name: '", table, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_connection_slot_named_account_is_not_said_twice()
    {
        using var entry = JsonDocument.Parse("""
            { "needs": { "connections": [{ "slot": "account", "providers": ["google"], "required": true, "why": "The Google account to report on." }] } }
            """);

        Assert.Equal(["Needs an account connected (google): The Google account to report on."], Marketplace.Needs(entry.RootElement));
    }

    [Fact]
    public async Task Nothing_is_guessed_before_a_read_after_a_failed_read_or_without_an_address()
    {
        var feed = MailFeed();
        var market = Market(feed);

        var before = market.Status();
        Assert.False(before.Checked);
        Assert.Equal("Not checked yet.", before.Reason);
        Assert.Null(before.CheckedAt);
        Assert.Empty(before.Packages);

        await market.CheckAsync(Ct);
        feed.Failure = new HttpRequestException("Name or service not known");
        var failed = await market.CheckAsync(Ct);
        Assert.False(failed.Checked);
        Assert.Equal("Not checked: Name or service not known.", failed.Reason);
        Assert.Equal(Now, failed.CheckedAt);
        Assert.Empty(failed.Packages);

        var unconfigured = new FakeFeed(address: null);
        var bare = Market(unconfigured);
        var none = await bare.CheckAsync(Ct);
        Assert.Equal(0, unconfigured.Reads);
        Assert.False(none.Checked);
        Assert.Equal("Not checked: this instance was not told where the package catalog is published.", none.Reason);
    }

    [Fact]
    public async Task Turned_off_it_reads_nothing_and_says_so()
    {
        var feed = MailFeed();
        var market = Market(feed, enabled: false);

        var status = await market.CheckAsync(Ct);

        Assert.Equal(0, feed.Reads);
        Assert.False(status.Checked);
        Assert.Equal("Reading the package catalog is turned off in Settings.", status.Reason);
        Assert.Empty(status.Packages);
    }

    [Theory]
    [InlineData("""{ "schema": 2, "packages": [] }""", "Not checked: the catalog is schema 2, which this build does not read (it reads schema 1).")]
    [InlineData("""{ "packages": [] }""", "Not checked: the catalog does not say which schema it is.")]
    [InlineData("""not json""", "Not checked: the catalog is not JSON that can be read.")]
    public async Task A_catalog_this_build_cannot_read_is_refused_with_a_sentence(string catalog, string reason)
    {
        var feed = new FakeFeed { Catalog = catalog };
        var status = await Market(feed).CheckAsync(Ct);

        Assert.False(status.Checked);
        Assert.Equal(reason, status.Reason);
        Assert.Empty(status.Packages);
    }

    // ---- installed and update states ----

    [Fact]
    public async Task A_solution_is_installed_by_package_id_on_its_teams_and_an_older_copy_offers_the_update()
    {
        var feed = MailFeed();
        var market = Market(feed, solutions:
        [
            ("beta", new TeamSolution("mail", "Mail", "2.0.1", ["mail"])),
            ("alpha", new TeamSolution("mail", "Mail", "2.0.3", ["mail"])),
            ("gamma", new TeamSolution("job-tracker", "Job Tracker", "1.0.0", [])),
        ]);

        var mail = Assert.Single((await market.CheckAsync(Ct)).Packages);

        Assert.True(mail.Installed);
        Assert.Equal(["alpha", "beta"], mail.InstalledOn);
        Assert.Equal("2.0.1", mail.InstalledVersion);
        Assert.True(mail.UpdateAvailable);
    }

    [Fact]
    public async Task A_current_solution_has_no_update()
    {
        var market = Market(MailFeed(), solutions: [("alpha", new TeamSolution("mail", "Mail", "2.0.3", []))]);

        var mail = Assert.Single((await market.CheckAsync(Ct)).Packages);

        Assert.True(mail.Installed);
        Assert.False(mail.UpdateAvailable);
    }

    [Fact]
    public async Task A_plugin_is_installed_by_id_and_active_version()
    {
        var feed = new FakeFeed
        {
            Catalog = Catalog(Entry("whoami", "plugin", "1.2.0", MailZip), Entry("echo", "plugin", "1.0.0", MailZip), Entry("other", "plugin", "3.0.0", MailZip)),
        };
        var market = Market(feed, plugins: id => id switch { "whoami" => "1.1.9", "echo" => "1.0.0", _ => null });

        var packages = (await market.CheckAsync(Ct)).Packages.ToDictionary(p => p.Id);

        Assert.True(packages["whoami"].Installed);
        Assert.Equal("1.1.9", packages["whoami"].InstalledVersion);
        Assert.True(packages["whoami"].UpdateAvailable);
        Assert.Empty(packages["whoami"].InstalledOn);
        Assert.True(packages["echo"].Installed);
        Assert.False(packages["echo"].UpdateAvailable);
        Assert.False(packages["other"].Installed);
        Assert.False(packages["other"].UpdateAvailable);
    }

    // ---- fetching ----

    [Fact]
    public async Task A_fetch_writes_its_row_first_and_unpacks_where_it_says()
    {
        var feed = MailFeed();
        var market = Market(feed);
        await market.CheckAsync(Ct);
        var log = new RecordingLog();

        var fetched = await market.FetchAsync("mail", Documents(), log, Person, Ct);

        Assert.Equal(new MarketplaceFetched("mail", "2.0.3", "solution", "Marketplace/mail-2.0.3"), fetched);
        var folder = Path.Combine(_root, "documents", "Marketplace", "mail-2.0.3");
        Assert.Equal("""{ "id": "mail" }""", await File.ReadAllTextAsync(Path.Combine(folder, "solution.json"), Ct));
        Assert.True(File.Exists(Path.Combine(folder, "plugins", "mail", "plugin.json")));
        var row = Assert.Single(log.Rows);
        Assert.Equal(("person@example.test", TenantActions.MarketplaceFetched, "mail"), (row.Email, row.Action, row.Subject));
        using var detail = JsonDocument.Parse(row.Detail!);
        Assert.Equal("2.0.3", detail.RootElement.GetProperty("version").GetString());
        Assert.Equal(Sha(MailZip), detail.RootElement.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task A_fetch_whose_row_cannot_be_written_unpacks_nothing()
    {
        var market = Market(MailFeed());
        await market.CheckAsync(Ct);

        var refusal = await Assert.ThrowsAsync<MarketplaceRefusal>(() => market.FetchAsync("mail", Documents(), new RecordingLog(refuse: true), Person, Ct));

        Assert.Equal(500, refusal.Status);
        Assert.Equal("The fetch could not be recorded, so nothing was unpacked.", refusal.Message);
        Assert.False(Directory.Exists(Path.Combine(Fetched, "mail-2.0.3")));
    }

    public static TheoryData<string, string> Refused => new()
    {
        { "sha256", "The download does not match the catalog's sha256, so nothing was written." },
        { "size", "The download is " },
        { "host", "is not a release download of the catalog it was listed in, so it was not fetched." },
        { "outside", "is not a release download of the catalog it was listed in, so it was not fetched." },
        { "dots", "is not a release download of the catalog it was listed in, so it was not fetched." },
        { "http", "is not a release download of the catalog it was listed in, so it was not fetched." },
        { "oversize", "The package is larger than 100 MB, so it was not fetched." },
        { "link", "The zip was not unpacked: evil is a link." },
        { "parent", "The zip was not unpacked: ../escape.txt leaves the folder." },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_bad_download_is_refused_and_writes_nothing(string bad, string sentence)
    {
        var feed = bad switch
        {
            "sha256" => MailFeed(sha256: new string('0', 64)),
            "size" => MailFeed(bytes: MailZip.LongLength + 1),
            "host" => MailFeed(url: "https://elsewhere.test/owner/packages/releases/download/t/mail-2.0.3.zip"),
            "outside" => MailFeed(url: "https://example.test/owner/packages/archive/mail-2.0.3.zip"),
            "dots" => MailFeed(url: "https://example.test/owner/packages/releases/../../other/releases/download/t/mail-2.0.3.zip"),
            "http" => MailFeed(url: "http://example.test/owner/packages/releases/download/t/mail-2.0.3.zip"),
            "oversize" => MailFeed(bytes: Marketplace.MaxDownloadBytes + 1),
            "link" => MailFeed(zip: Zip(new Dictionary<string, string> { ["solution.json"] = "{}" }, link: "evil")),
            "parent" => MailFeed(zip: Zip(new Dictionary<string, string> { ["solution.json"] = "{}", ["../escape.txt"] = "x" })),
            _ => throw new ArgumentOutOfRangeException(nameof(bad)),
        };
        var market = Market(feed);
        await market.CheckAsync(Ct);
        var log = new RecordingLog();

        var refusal = await Assert.ThrowsAsync<MarketplaceRefusal>(() => market.FetchAsync("mail", Documents(), log, Person, Ct));

        Assert.Contains(sentence, refusal.Message, StringComparison.Ordinal);
        Assert.InRange(refusal.Status, 400, 499);
        Assert.Empty(log.Rows);
        Assert.False(Directory.Exists(Fetched));
        Assert.False(File.Exists(Path.Combine(_root, "documents", "escape.txt")));
        if (bad is "host" or "outside" or "dots" or "http" or "oversize") Assert.Empty(feed.Downloaded);
    }

    [Fact]
    public async Task A_fetch_before_the_catalog_was_read_or_of_an_unlisted_package_is_refused()
    {
        var market = Market(MailFeed());

        var early = await Assert.ThrowsAsync<MarketplaceRefusal>(() => market.FetchAsync("mail", Documents(), new RecordingLog(), Person, Ct));
        Assert.Equal(409, early.Status);

        await market.CheckAsync(Ct);
        var unknown = await Assert.ThrowsAsync<MarketplaceRefusal>(() => market.FetchAsync("nope", Documents(), new RecordingLog(), Person, Ct));
        Assert.Equal(404, unknown.Status);
        Assert.False(Directory.Exists(Fetched));
    }

    [Fact]
    public async Task A_second_fetch_of_the_same_version_is_refused_and_leaves_the_first()
    {
        var market = Market(MailFeed());
        await market.CheckAsync(Ct);
        await market.FetchAsync("mail", Documents(), new RecordingLog(), Person, Ct);
        var log = new RecordingLog();

        var again = await Assert.ThrowsAsync<MarketplaceRefusal>(() => market.FetchAsync("mail", Documents(), log, Person, Ct));

        Assert.Equal(409, again.Status);
        Assert.Empty(log.Rows);
        Assert.True(File.Exists(Path.Combine(Fetched, "mail-2.0.3", "solution.json")));
    }

    // ---- the routes ----

    [Fact]
    public async Task A_person_reads_refreshes_and_fetches_through_the_routes_and_a_machine_principal_is_refused_every_one()
    {
        var dataRoot = Directory.CreateTempSubdirectory("harness-marketplace-routes-").FullName;
        var feed = MailFeed();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<IMarketplaceFeed>(feed))));
        try
        {
            await factory.Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", HostFixture.Password, Ct);
            using var person = factory.CreateClient();
            (await person.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = HostFixture.Password }, Ct))
                .EnsureSuccessStatusCode();

            using (var before = JsonDocument.Parse(await person.GetStringAsync(MarketplaceEndpoints.Route, Ct)))
            {
                Assert.False(before.RootElement.GetProperty("checked").GetBoolean());
                Assert.Equal("Not checked yet.", before.RootElement.GetProperty("reason").GetString());
            }

            var refreshed = await person.PostAsync(MarketplaceEndpoints.Route + "/refresh", null, Ct);
            refreshed.EnsureSuccessStatusCode();
            using (var after = JsonDocument.Parse(await person.GetStringAsync(MarketplaceEndpoints.Route, Ct)))
            {
                var root = after.RootElement;
                Assert.Equal(["checked", "reason", "checkedAt", "packages"], root.EnumerateObject().Select(p => p.Name));
                Assert.True(root.GetProperty("checked").GetBoolean());
                var mail = root.GetProperty("packages")[0];
                Assert.Equal(
                    ["id", "kind", "name", "summary", "version", "needs", "installed", "installedVersion", "installedOn", "updateAvailable", "catalogNeeds"],
                    mail.EnumerateObject().Select(p => p.Name));
                Assert.Equal("solution", mail.GetProperty("kind").GetString());

                // The shape the console's CatalogNeeds type reads.
                var fields = mail.GetProperty("catalogNeeds");
                Assert.Equal(["connections", "secrets", "inputs", "runtimes"], fields.EnumerateObject().Select(p => p.Name));
                Assert.Equal(["slot", "providers", "required"], fields.GetProperty("connections")[0].EnumerateObject().Select(p => p.Name));
                Assert.Equal(["key", "when"], fields.GetProperty("secrets")[0].EnumerateObject().Select(p => p.Name));
                Assert.Equal(["name", "kind", "required"], fields.GetProperty("inputs")[0].EnumerateObject().Select(p => p.Name));
            }

            var fetch = await person.PostAsync(MarketplaceEndpoints.Route + "/mail/fetch", null, Ct);
            Assert.Equal(HttpStatusCode.OK, fetch.StatusCode);
            string fetched;
            using (var body = JsonDocument.Parse(await fetch.Content.ReadAsStringAsync(Ct)))
            {
                fetched = body.RootElement.GetProperty("folder").GetString()!;
                Assert.Equal("Marketplace/mail-2.0.3", fetched);
                Assert.Equal("solution", body.RootElement.GetProperty("kind").GetString());
            }

            Assert.True(File.Exists(Path.Combine(dataRoot, "documents", "Marketplace", "mail-2.0.3", "solution.json")));

            // THE SEAM THE WEB'S GET CROSSES: the documents root `GET /api/documents` answers, joined
            // with the folder the fetch answered, is a folder the install wizard's check takes - the
            // same form Upload a package (.zip) fills Folder with.
            using (var documents = JsonDocument.Parse(await person.GetStringAsync("/api/documents", Ct)))
            {
                var folder = $"{documents.RootElement.GetProperty("root").GetString()!.TrimEnd('/')}/{fetched}";
                var check = await person.PostAsJsonAsync("/api/solutions/check", new { folder }, Ct);
                var answer = await check.Content.ReadAsStringAsync(Ct);
                Assert.True(check.StatusCode == HttpStatusCode.OK, answer);
                using var checkedBody = JsonDocument.Parse(answer);
                Assert.Equal(
                    Path.GetFullPath(Path.Combine(dataRoot, "documents", "Marketplace", "mail-2.0.3")),
                    Path.GetFullPath(checkedBody.RootElement.GetProperty("folder").GetString()!));
            }

            var rows = await factory.Services.GetRequiredService<ITenantLog>().ReadAsync(ct: Ct);
            Assert.Contains(rows.Events, e => e.Action == TenantActions.MarketplaceFetched && e.Subject == "mail");

            var unknown = await person.PostAsync(MarketplaceEndpoints.Route + "/nope/fetch", null, Ct);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            using (var error = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync(Ct)))
            {
                Assert.Equal("The package catalog lists no package 'nope'.", error.RootElement.GetProperty("error").GetString());
            }

            var agent = factory.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
            var team = await factory.Services.GetRequiredService<TeamRegistry>().CreateAsync("Alpha", agent, memberAgent: agent, ct: Ct);
            var key = await factory.Services.GetRequiredService<IPrincipalStore>().MintAsync(
                new ContainerId(team.Id, "Worker").ToString(), PrincipalKind.Container, team.Id, Permits.All, ct: Ct);
            using var machine = factory.CreateClient();
            machine.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);
            var downloads = feed.Downloaded.Count;
            foreach (var response in new[]
            {
                await machine.GetAsync(MarketplaceEndpoints.Route, Ct),
                await machine.PostAsync(MarketplaceEndpoints.Route + "/refresh", null, Ct),
                await machine.PostAsync(MarketplaceEndpoints.Route + "/mail/fetch", null, Ct),
            })
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
                Assert.Equal(PermitGate.HumansOnlyMessage, body.RootElement.GetProperty("error").GetString());
            }

            Assert.Equal(downloads, feed.Downloaded.Count);
        }
        finally
        {
            await factory.DisposeAsync();
            try { Directory.Delete(dataRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task The_setting_is_on_by_default_and_the_address_comes_from_configuration_only()
    {
        var dataRoot = Directory.CreateTempSubdirectory("harness-marketplace-setting-").FullName;
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .UseSetting(Marketplace.CatalogSetting, Address)
            // NO NETWORK: the real feed is kept, to see the address it was given, but its client
            // answers from here, should the start-up read come before the factory is gone.
            .ConfigureTestServices(services => services.AddHttpClient(nameof(HttpMarketplaceFeed))
                .ConfigurePrimaryHttpMessageHandler(() => new Offline())));
        try
        {
            var settings = factory.Services.GetRequiredService<TenantSettings>();
            Assert.True(settings.MarketplaceCheck);
            Assert.Equal("on", settings.Current(TenantSettings.MarketplaceCheckName));
            Assert.Equal(new Uri(Address), factory.Services.GetRequiredService<IMarketplaceFeed>().Address);
            Assert.Equal("https://example.test/owner/packages/releases/", factory.Services.GetRequiredService<Marketplace>().DownloadPrefix);
        }
        finally
        {
            await factory.DisposeAsync();
            try { Directory.Delete(dataRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
