using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A site's files folder in the team's documents, and the page's download route, on the real Host.
/// The page's requests are made by a client holding NO cookie, as a sandboxed page's are. Every
/// refusal is asserted by its exact status AND body, because the site-file catch-all also matches
/// <c>_api/files/...</c> and answers the capability's refusals and its own 404: only the new route's
/// own sentences tell the two apart.
/// </summary>
public sealed class SiteFilesTests(HostFixture host) : IClassFixture<HostFixture>
{
    private const string Email = "person@example.test";

    private static readonly SiteActor Person = SiteActor.Person("person-1", Email);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SiteService Sites => host.Services.GetRequiredService<SiteService>();

    private static string Unique(string stem) => $"{stem}-{Guid.NewGuid():N}"[..(stem.Length + 9)];

    private static string Marker() => $"marker-{Guid.NewGuid():N}";

    private string Docs(string team) => host.Services.GetRequiredService<TeamDocuments>().RootFor(team);

    private string FilesOf(string team, string site) => Path.Combine(Docs(team), "sites", site, "files");

    private static void Write(string folder, string relative, string text)
    {
        var full = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    /// <summary>A published site with <c>out/a.txt</c> in its files folder holding a fresh marker.</summary>
    private async Task<(string Site, string Marker)> SiteWithFileAsync(string team, string? name = null)
    {
        var site = name ?? Unique("board");
        Assert.True((await Sites.CreateAsync(team, site, Person, Ct)).Ok);

        var source = Path.Combine(Docs(team), "site-src", Guid.NewGuid().ToString("N"));
        Write(source, "index.html", "<p>page</p>");
        var published = await Sites.PublishAsync(team, site, source, Person, Ct);
        Assert.True(published.Ok, published.Refusal);

        var marker = Marker();
        Write(FilesOf(team, site), "out/a.txt", marker);
        return (site, marker);
    }

    /// <summary>A person opens the site; answers the capability path the entry redirected to.</summary>
    private async Task<string> OpenAsync(string team, string site)
    {
        using var person = await host.PersonAsync(allowAutoRedirect: false);
        var entry = await person.GetAsync($"/sites/{team}/{site}/", Ct);
        Assert.Equal(HttpStatusCode.Redirect, entry.StatusCode);
        return entry.Headers.Location!.OriginalString;
    }

    private static string CapabilityOf(string page) => page.Split('/')[5];

    private async Task<string> NewTeamAsync()
    {
        var registry = host.Services.GetRequiredService<TeamRegistry>();
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        return (await registry.CreateAsync($"Gamma{Guid.NewGuid():N}"[..12], agent, memberAgent: agent, ct: Ct)).Id;
    }

    private static async Task<(HttpStatusCode Status, string Body)> AnswerAsync(HttpResponseMessage response) =>
        (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string url)
    {
        using var sandbox = host.Anonymous(allowAutoRedirect: false);
        return await AnswerAsync(await sandbox.GetAsync(url, Ct));
    }

    /// <summary>A GET whose path reaches the app exactly as written, as Kestrel hands it over.</summary>
    private async Task<(int Status, string Body)> RawGetAsync(string path)
    {
        var context = await host.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = new PathString(path);
        }, Ct);

        using var reader = new StreamReader(context.Response.Body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync(Ct));
    }

    private static void NothingOf(string body, params string[] markers)
    {
        foreach (var marker in markers) Assert.DoesNotContain(marker, body, StringComparison.Ordinal);
    }

    // ---- the folder: created, repaired, kept ----

    [Fact]
    public async Task Creating_a_site_creates_its_files_folder_in_the_teams_documents()
    {
        var (_, key) = await MemberKeyAsync(host.Alpha);
        using var member = host.Container(key);
        var site = Unique("board");

        var created = await member.PostAsJsonAsync($"/api/teams/{host.Alpha}/sites", new { name = site }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(Directory.Exists(FilesOf(host.Alpha, site)));
        Assert.Equal(FilesOf(host.Alpha, site), (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("filesFolder").GetString());
        Assert.StartsWith($"{host.Alpha}\n", File.ReadAllText(Path.Combine(Docs(host.Alpha), TeamPaths.MarkerFileName)), StringComparison.Ordinal);

        // And through the site tool.
        var tool = Unique("board");
        Assert.StartsWith("HTTP 201", await Tools(host.Alpha).Site("create", site: tool, cancellationToken: Ct));
        Assert.True(Directory.Exists(FilesOf(host.Alpha, tool)));
    }

    [Fact]
    public async Task The_site_tool_recreates_a_files_folder_a_person_removed()
    {
        var site = Unique("board");
        Assert.True((await Sites.CreateAsync(host.Alpha, site, Person, Ct)).Ok);

        using var person = await host.PersonAsync();
        var removed = await person.DeleteAsync($"/api/teams/{host.Alpha}/documents?path=sites/{site}&recursive=true", Ct);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.False(Directory.Exists(FilesOf(host.Alpha, site)));

        var shown = await Tools(host.Alpha).Site("show", site: site, cancellationToken: Ct);
        Assert.StartsWith("HTTP 200", shown);
        Assert.True(Directory.Exists(FilesOf(host.Alpha, site)));
        Assert.Contains(JsonSerializer.Serialize(FilesOf(host.Alpha, site)), shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_download_never_creates_the_folder()
    {
        var (site, _) = await SiteWithFileAsync(host.Alpha);
        var page = await OpenAsync(host.Alpha, site);
        Directory.Delete(Path.Combine(Docs(host.Alpha), "sites", site), recursive: true);

        Assert.Equal((HttpStatusCode.NotFound, SiteEndpoints.NoSuchSiteFile), await GetAsync($"{page}_api/files/out/a.txt"));
        Assert.False(Directory.Exists(Path.Combine(Docs(host.Alpha), "sites", site)));
    }

    [Fact]
    public async Task A_team_deletion_keeps_a_sites_files_and_lists_them_as_documents()
    {
        var gamma = await NewTeamAsync();
        var (site, marker) = await SiteWithFileAsync(gamma);
        Directory.Delete(Path.Combine(Docs(gamma), "site-src"), recursive: true);

        using var person = await host.PersonAsync();
        Assert.True((await person.DeleteAsync($"/api/teams/{gamma}", Ct)).IsSuccessStatusCode);

        Assert.Equal(marker, File.ReadAllText(Path.Combine(FilesOf(gamma, site), "out", "a.txt")));

        var folders = (await person.GetFromJsonAsync<JsonElement>("/api/documents", Ct)).GetProperty("folders").EnumerateArray();
        var kept = folders.Single(f => f.GetProperty("team").GetString() == gamma);
        Assert.False(kept.GetProperty("exists").GetBoolean());

        var listed = await person.GetStringAsync($"/api/teams/{gamma}/documents?path=sites/{site}/files/out", Ct);
        Assert.Contains("\"a.txt\"", listed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_team_whose_sites_made_no_files_leaves_no_documents_folder()
    {
        var gamma = await NewTeamAsync();
        var site = Unique("board");
        Assert.True((await Sites.CreateAsync(gamma, site, Person, Ct)).Ok);
        Assert.True(Directory.Exists(FilesOf(gamma, site)));

        using var person = await host.PersonAsync();
        Assert.True((await person.DeleteAsync($"/api/teams/{gamma}", Ct)).IsSuccessStatusCode);

        Assert.False(Directory.Exists(Docs(gamma)));
    }

    [Fact]
    public async Task A_site_made_again_with_a_deleted_sites_name_reuses_its_kept_files()
    {
        var (site, marker) = await SiteWithFileAsync(host.Alpha);
        var folder = FilesOf(host.Alpha, site);

        using var person = await host.PersonAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await person.DeleteAsync($"/api/teams/{host.Alpha}/sites/{site}?confirm=true", Ct)).StatusCode);
        Assert.True(File.Exists(Path.Combine(folder, "out", "a.txt")));

        var again = await SiteWithFileAsync(host.Alpha, site);
        File.WriteAllText(Path.Combine(folder, "out", "a.txt"), marker);

        var shown = await Tools(host.Alpha).Site("show", site: site, cancellationToken: Ct);
        Assert.Contains($"\"filesFolder\":{JsonSerializer.Serialize(folder)}", shown, StringComparison.Ordinal);

        var page = await OpenAsync(host.Alpha, again.Site);
        Assert.Equal((HttpStatusCode.OK, marker), await GetAsync($"{page}_api/files/out/a.txt"));
    }

    [Theory]
    [InlineData("files", true)]
    [InlineData("files", false)]
    [InlineData("site", true)]
    [InlineData("site", false)]
    public async Task Pruning_never_follows_a_link_out_of_the_documents_folder(string level, bool holdsAFile)
    {
        foreach (var teamDelete in new[] { false, true })
        {
            var team = teamDelete ? await NewTeamAsync() : host.Alpha;
            var site = Unique("board");
            Assert.True((await Sites.CreateAsync(team, site, Person, Ct)).Ok);

            var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
            Directory.CreateDirectory(outside);
            var marker = Path.Combine(outside, "outside.txt");
            if (holdsAFile) File.WriteAllText(marker, Marker());

            var linked = level == "files" ? FilesOf(team, site) : Path.Combine(Docs(team), "sites", site);
            Directory.Delete(linked, recursive: true);
            Directory.CreateSymbolicLink(linked, outside);

            try
            {
                using var person = await host.PersonAsync();

                if (teamDelete)
                {
                    Assert.True((await person.DeleteAsync($"/api/teams/{team}", Ct)).IsSuccessStatusCode);
                }
                else
                {
                    Assert.Equal(HttpStatusCode.NoContent, (await person.DeleteAsync($"/api/teams/{team}/sites/{site}?confirm=true", Ct)).StatusCode);
                }

                Assert.True(Directory.Exists(outside));
                Assert.Equal(holdsAFile, File.Exists(marker));
            }
            finally
            {
                if (new FileInfo(linked).LinkTarget is not null) File.Delete(linked);
                Directory.Delete(outside, recursive: true);
            }
        }
    }

    // ---- the download route ----

    [Fact]
    public async Task A_file_in_the_sites_folder_downloads_as_an_attachment_under_the_site_policy()
    {
        var (site, marker) = await SiteWithFileAsync(host.Alpha);
        var page = await OpenAsync(host.Alpha, site);
        using var sandbox = host.Anonymous();

        var response = await sandbox.GetAsync($"{page}_api/files/out/a.txt", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(marker, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("a.txt", response.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal("a.txt", response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("nosniff", string.Join(",", response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(
            SitePolicy.For("http://localhost", host.Alpha, site, CapabilityOf(page)),
            string.Join(",", response.Headers.GetValues("Content-Security-Policy")));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("a.txt", "text/plain; charset=utf-8")]
    [InlineData("a.TXT", "text/plain; charset=utf-8")]
    [InlineData("a.csv", "text/csv; charset=utf-8")]
    [InlineData("a.json", "application/json")]
    [InlineData("a.png", "image/png")]
    [InlineData("a.jpg", "image/jpeg")]
    [InlineData("a.jpeg", "image/jpeg")]
    [InlineData("a.gif", "image/gif")]
    [InlineData("a.webp", "image/webp")]
    [InlineData("a.zip", "application/zip")]
    [InlineData("a.html", "application/octet-stream")]
    [InlineData("a.htm", "application/octet-stream")]
    [InlineData("a.xhtml", "application/octet-stream")]
    [InlineData("a.svg", "application/octet-stream")]
    [InlineData("a.xml", "application/octet-stream")]
    [InlineData("a.js", "application/octet-stream")]
    [InlineData("a.mjs", "application/octet-stream")]
    [InlineData("a.pdf", "application/octet-stream")]
    [InlineData("a.bin", "application/octet-stream")]
    [InlineData("noextension", "application/octet-stream")]
    public async Task Each_extension_gets_its_allow_listed_type_and_anything_a_browser_could_run_is_octet_stream(string name, string type)
    {
        var (site, _) = await SiteWithFileAsync(host.Alpha);
        Write(FilesOf(host.Alpha, site), $"out/{name}", "<script>x</script>");
        var page = await OpenAsync(host.Alpha, site);
        using var sandbox = host.Anonymous();

        var response = await sandbox.GetAsync($"{page}_api/files/out/{name}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType?.ToString());
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Fact]
    public void The_allow_list_holds_exactly_the_documented_extensions()
    {
        Assert.Equal(
            [".csv", ".gif", ".jpeg", ".jpg", ".json", ".png", ".txt", ".webp", ".zip"],
            SiteFiles.ContentTypes.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("application/octet-stream", SiteFiles.FallbackContentType);
    }

    [Fact]
    public async Task A_name_with_a_space_and_non_ascii_downloads_with_its_name()
    {
        var (site, _) = await SiteWithFileAsync(host.Alpha);
        Write(FilesOf(host.Alpha, site), "out/naïve name.txt", "spaced");
        var page = await OpenAsync(host.Alpha, site);
        using var sandbox = host.Anonymous();

        var response = await sandbox.GetAsync($"{page}_api/files/out/{Uri.EscapeDataString("naïve name.txt")}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("spaced", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("naïve name.txt", response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal("na_ve name.txt", response.Content.Headers.ContentDisposition?.FileName);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("HEAD")]
    public async Task Only_get_is_answered_and_nothing_is_written(string method)
    {
        var (site, marker) = await SiteWithFileAsync(host.Alpha);
        var file = Path.Combine(FilesOf(host.Alpha, site), "out", "a.txt");
        var written = File.GetLastWriteTimeUtc(file);
        var expired = host.Services.GetRequiredService<SiteCapability>()
            .Issue(host.Alpha, site, "person-1", Email, DateTimeOffset.UtcNow.AddMinutes(-1));

        foreach (var capability in new[] { CapabilityOf(await OpenAsync(host.Alpha, site)), expired })
        {
            using var sandbox = host.Anonymous(allowAutoRedirect: false);
            using var request = new HttpRequestMessage(new HttpMethod(method), $"/sites/{host.Alpha}/{site}/_c/{capability}/_api/files/out/a.txt")
            {
                Content = method is "HEAD" ? null : new StringContent("overwritten", Encoding.UTF8, "text/plain"),
            };

            var response = await sandbox.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
        }

        Assert.Equal(marker, File.ReadAllText(file));
        Assert.Equal(written, File.GetLastWriteTimeUtc(file));
        Assert.Equal(["a.txt"], Directory.GetFileSystemEntries(Path.GetDirectoryName(file)!).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("", 404, SiteEndpoints.SiteFilesNotListed)]
    [InlineData("out/", 404, SiteEndpoints.SiteFilesNotListed)]
    [InlineData("out", 404, SiteEndpoints.NoSuchSiteFile)]
    public async Task A_folder_or_an_empty_path_is_never_listed(string path, int status, string sentence)
    {
        var (site, _) = await SiteWithFileAsync(host.Alpha);
        var page = await OpenAsync(host.Alpha, site);

        var (got, body) = await GetAsync($"{page}_api/files/{path}");

        Assert.Equal((HttpStatusCode)status, got);
        Assert.Equal(sentence, body);
        Assert.DoesNotContain("a.txt", body, StringComparison.Ordinal);
    }

    /// <summary>Each path as Kestrel hands it to the app: <c>%2F</c> and <c>%5C</c> stay literal,
    /// <c>%252F</c> arrives as <c>%2F</c>, <c>%2e</c> arrives as <c>.</c>. The expected value is the
    /// path the route answers about, or null for the not-found sentence.</summary>
    [Theory]
    [InlineData("../outside.txt", "../outside.txt")]
    [InlineData("out/../../outside.txt", "out/../../outside.txt")]
    [InlineData("out/../../../outside.txt", "out/../../../outside.txt")]
    [InlineData("..%2Foutside.txt", "..%2Foutside.txt")]
    [InlineData("out%2F..%2F..%2Foutside.txt", "out%2F..%2F..%2Foutside.txt")]
    [InlineData("..%5Coutside.txt", "..%5Coutside.txt")]
    [InlineData("out\\..\\..\\outside.txt", "out\\..\\..\\outside.txt")]
    [InlineData("out%2Fa.txt", "out%2Fa.txt")]
    [InlineData("/etc/hostname", "/etc/hostname")]
    [InlineData("a%00.txt", "a%00.txt")]
    [InlineData("a\0.txt", "a\0.txt")]
    [InlineData("C:/x", "C:/x")]
    [InlineData(".hidden", ".hidden")]
    [InlineData("out/.hidden", "out/.hidden")]
    [InlineData("../../.harness-team", "../../.harness-team")]
    public async Task A_path_that_leaves_the_folder_is_refused_and_serves_nothing(string raw, string? refused)
    {
        var (site, marker) = await SiteWithFileAsync(host.Alpha);
        var inDocs = Marker();
        File.WriteAllText(Path.Combine(Docs(host.Alpha), "sites", site, "outside.txt"), inDocs);
        File.WriteAllText(Path.Combine(Docs(host.Alpha), "sites", "outside.txt"), inDocs);
        Write(FilesOf(host.Alpha, site), "out/.hidden", inDocs);
        var page = await OpenAsync(host.Alpha, site);

        var (status, body) = await RawGetAsync($"{page}_api/files/{raw}");

        Assert.Equal((400, SiteRules.NotAFilePath(refused)), (status, body));
        NothingOf(body, marker, inDocs, host.DataRoot);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("/abs")]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData("a\\b")]
    [InlineData("a\0b")]
    [InlineData("a\u001fb")]
    [InlineData("a\u007fb")]
    [InlineData("a%2Fb")]
    [InlineData("a%b")]
    [InlineData("a:b")]
    [InlineData(".x")]
    [InlineData("out/.x")]
    [InlineData("")]
    public void The_file_path_rule_refuses_every_escape_spelled_raw(string path)
    {
        Assert.False(SiteRules.IsFilePath(path));
        Assert.Equal(SiteFileOutcome.NotFound, Sites.Files.Open(host.Alpha, "board", path).Outcome);
    }

    [Fact]
    public void The_file_path_rule_bounds_names_and_length_and_accepts_ordinary_paths()
    {
        Assert.False(SiteRules.IsFilePath(new string('a', 256)));
        Assert.True(SiteRules.IsFilePath(new string('a', 255)));
        Assert.False(SiteRules.IsFilePath(new string('é', 128)));
        Assert.True(SiteRules.IsFilePath(string.Join('/', Enumerable.Repeat("abcd", 205))));
        Assert.False(SiteRules.IsFilePath(string.Join('/', Enumerable.Repeat("abcd", 205)) + "/a"));

        foreach (var path in new[] { "a.txt", "out/a.txt", "naïve name.txt", "2026-10/report-7.txt", "a..b", "a." })
        {
            Assert.True(SiteRules.IsFilePath(path), path);
        }
    }

    /// <summary>Where the link is, from the leaf up to the team's documents folder; the last two are
    /// links INSIDE files/ to what is inside files/, which "realpath stays inside" would follow.</summary>
    [Theory]
    [InlineData("leaf")]
    [InlineData("out")]
    [InlineData("files")]
    [InlineData("site")]
    [InlineData("sites")]
    [InlineData("documents")]
    [InlineData("inside-file")]
    [InlineData("inside-folder")]
    public async Task A_link_at_any_level_is_not_followed(string level)
    {
        var team = await NewTeamAsync();
        var (site, marker) = await SiteWithFileAsync(team);
        var files = FilesOf(team, site);
        var page = await OpenAsync(team, site);
        var path = level switch { "inside-file" => "out/b.txt", "inside-folder" => "in/a.txt", _ => "out/a.txt" };

        if (level == "inside-file") Write(files, "out/b.txt", marker);
        if (level == "inside-folder") Write(files, "in/a.txt", marker);

        // Positive control: the same path is served before the link is put in.
        Assert.Equal((HttpStatusCode.OK, marker), await GetAsync($"{page}_api/files/{path}"));

        var linked = level switch
        {
            "leaf" => Path.Combine(files, "out", "a.txt"),
            "out" => Path.Combine(files, "out"),
            "files" => files,
            "site" => Path.GetDirectoryName(files)!,
            "sites" => Path.Combine(Docs(team), "sites"),
            "documents" => Docs(team),
            "inside-file" => Path.Combine(files, "out", "b.txt"),
            _ => Path.Combine(files, "in"),
        };

        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
        var outsideMarker = Marker();

        try
        {
            if (level is "inside-file")
            {
                File.Delete(linked);
                File.CreateSymbolicLink(linked, "a.txt");
            }
            else if (level is "inside-folder")
            {
                Directory.Delete(linked, recursive: true);
                Directory.CreateSymbolicLink(linked, "out");
            }
            else if (level is "leaf")
            {
                File.WriteAllText(outside, outsideMarker);
                File.Delete(linked);
                File.CreateSymbolicLink(linked, outside);
            }
            else
            {
                // The real folder moves out, its file now holding the outside marker, and a link to it
                // takes its place: served through the link would be the outside marker.
                Directory.Move(linked, outside);
                File.WriteAllText(Path.Combine(outside, Path.GetRelativePath(linked, Path.Combine(files, "out", "a.txt"))), outsideMarker);
                Directory.CreateSymbolicLink(linked, outside);
            }

            var (status, body) = await GetAsync($"{page}_api/files/{path}");

            Assert.Equal((HttpStatusCode.NotFound, SiteEndpoints.NoSuchSiteFile), (status, body));
            NothingOf(body, marker, outsideMarker);
        }
        finally
        {
            if (new FileInfo(linked).LinkTarget is not null) File.Delete(linked);
            if (Directory.Exists(outside) && !Directory.Exists(linked)) Directory.Move(outside, linked);
            if (File.Exists(outside)) File.Delete(outside);
        }
    }

    [Fact]
    public async Task A_folder_swapped_for_a_link_after_the_check_is_not_followed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The held-open walk is Linux's; elsewhere the path fallback narrows the window only.");

        var (site, marker) = await SiteWithFileAsync(host.Alpha);
        var page = await OpenAsync(host.Alpha, site);
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
        var outsideMarker = Marker();
        Write(outside, "a.txt", outsideMarker);

        var calls = 0;
        Sites.Files.BeforeLeafOpen = folder =>
        {
            calls++;
            Directory.Move(folder, folder + "-held");
            Directory.CreateSymbolicLink(folder, outside);
        };

        try
        {
            var (status, body) = await GetAsync($"{page}_api/files/out/a.txt");

            Assert.Equal(1, calls);
            Assert.Equal((HttpStatusCode.OK, marker), (status, body));
            NothingOf(body, outsideMarker);
        }
        finally
        {
            Sites.Files.BeforeLeafOpen = null;
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task A_fifo_is_not_served_and_does_not_hang()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "mkfifo is Linux's here.");

        var (site, _) = await SiteWithFileAsync(host.Alpha);
        var fifo = Path.Combine(FilesOf(host.Alpha, site), "out", "pipe.txt");
        using (var made = Process.Start(new ProcessStartInfo("/usr/bin/mkfifo", [fifo]) { RedirectStandardInput = true })!)
        {
            made.StandardInput.Close();
            await made.WaitForExitAsync(Ct);
            Assert.Equal(0, made.ExitCode);
        }

        var page = await OpenAsync(host.Alpha, site);
        var answered = GetAsync($"{page}_api/files/out/pipe.txt");

        Assert.Same(answered, await Task.WhenAny(answered, Task.Delay(TimeSpan.FromSeconds(5), Ct)));
        Assert.Equal((HttpStatusCode.NotFound, SiteEndpoints.NoSuchSiteFile), await answered);
    }

    [Fact]
    public async Task A_file_the_platform_cannot_read_is_refused_with_a_sentence()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes.");
        Assert.SkipWhen(Environment.UserName == "root", "root reads a mode-000 file.");

        var (site, marker) = await SiteWithFileAsync(host.Alpha);
        var file = Path.Combine(FilesOf(host.Alpha, site), "out", "a.txt");
        File.SetUnixFileMode(file, UnixFileMode.None);
        var page = await OpenAsync(host.Alpha, site);

        try
        {
            var (status, body) = await GetAsync($"{page}_api/files/out/a.txt");

            Assert.Equal((HttpStatusCode.Forbidden, SiteEndpoints.SiteFileUnreadable), (status, body));
            NothingOf(body, marker, host.DataRoot);
        }
        finally
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task The_files_route_is_refused_without_a_valid_capability_for_this_live_site()
    {
        var (site, marker) = await SiteWithFileAsync(host.Alpha);
        var (other, _) = await SiteWithFileAsync(host.Alpha);
        var page = await OpenAsync(host.Alpha, site);
        var url = $"{page}_api/files/out/a.txt";

        // Positive control: the same URL with a valid capability.
        Assert.Equal((HttpStatusCode.OK, marker), await GetAsync(url));

        var expired = host.Services.GetRequiredService<SiteCapability>()
            .Issue(host.Alpha, site, "person-1", Email, DateTimeOffset.UtcNow.AddMinutes(-1));
        var expiredUrl = $"/sites/{host.Alpha}/{site}/_c/{expired}/_api/files/out/a.txt";

        using var sandbox = host.Anonymous(allowAutoRedirect: false);
        var refusals = new List<HttpResponseMessage>();

        var gone = await sandbox.GetAsync(expiredUrl, Ct);
        refusals.Add(gone);
        Assert.Equal((HttpStatusCode.Unauthorized, SiteEndpoints.CapabilityRefused), await AnswerAsync(gone));

        using (var navigate = new HttpRequestMessage(HttpMethod.Get, expiredUrl))
        {
            navigate.Headers.Add("Sec-Fetch-Mode", "navigate");
            var redirected = await sandbox.SendAsync(navigate, Ct);
            Assert.Equal(HttpStatusCode.Redirect, redirected.StatusCode);
            Assert.Equal($"/sites/{host.Alpha}/{site}/", redirected.Headers.Location?.OriginalString);
        }

        var otherSite = await sandbox.GetAsync($"/sites/{host.Alpha}/{other}/_c/{CapabilityOf(page)}/_api/files/out/a.txt", Ct);
        refusals.Add(otherSite);
        Assert.Equal((HttpStatusCode.Forbidden, SiteEndpoints.CapabilityForAnotherSite), await AnswerAsync(otherSite));

        Assert.True((await Sites.UnpublishAsync(host.Alpha, site, Person, Ct)).Ok);
        var unpublished = await sandbox.GetAsync(url, Ct);
        refusals.Add(unpublished);
        Assert.Equal((HttpStatusCode.NotFound, SiteService.NotPublished), await AnswerAsync(unpublished));

        // The cookie alone opens nothing but the entry: no capability, no file.
        using var person = await host.PersonAsync(allowAutoRedirect: false);
        var cookie = await person.GetAsync($"/sites/{host.Alpha}/{site}/_api/files/out/a.txt", Ct);
        Assert.Equal(HttpStatusCode.NotFound, cookie.StatusCode);
        NothingOf(await cookie.Content.ReadAsStringAsync(Ct), marker);

        // A container's key is not a person, whatever the path holds.
        using var container = host.Container(host.AlphaContainerKey);
        var key = await container.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, key.StatusCode);
        Assert.Equal(PermitGate.HumansOnlyMessage, (await key.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());

        foreach (var refusal in refusals)
        {
            var policy = string.Join(",", refusal.Headers.GetValues("Content-Security-Policy"));
            Assert.Equal(SecurityHeaders.ContentSecurityPolicy, policy);
            NothingOf(await refusal.Content.ReadAsStringAsync(Ct), marker, host.DataRoot);
        }
    }

    [Fact]
    public async Task A_capability_never_reaches_another_teams_files()
    {
        var (site, marker) = await SiteWithFileAsync(host.Alpha);
        var (_, betaMarker) = await SiteWithFileAsync(host.Beta, site);
        var capability = CapabilityOf(await OpenAsync(host.Alpha, site));

        Assert.Equal(
            (HttpStatusCode.Forbidden, SiteEndpoints.CapabilityForAnotherSite),
            await GetAsync($"/sites/{host.Beta}/{site}/_c/{capability}/_api/files/out/a.txt"));

        var own = await GetAsync($"/sites/{host.Alpha}/{site}/_c/{capability}/_api/files/out/a.txt");
        Assert.Equal((HttpStatusCode.OK, marker), own);
        NothingOf(own.Body, betaMarker);
    }

    [Fact]
    public async Task No_refusal_names_the_data_root()
    {
        var (site, marker) = await SiteWithFileAsync(host.Alpha);
        Directory.CreateDirectory(Path.Combine(FilesOf(host.Alpha, site), "empty"));
        var page = await OpenAsync(host.Alpha, site);

        foreach (var path in new[] { "", "out/", "out", "missing.txt", "empty/x.txt", "../a", ".x", "a:b", "a%2Fb", "out/a.txt/x" })
        {
            var (status, body) = await RawGetAsync($"{page}_api/files/{path}");

            Assert.NotEqual(200, status);
            NothingOf(body, host.DataRoot, "/documents/", marker);
        }
    }

    [Fact]
    public async Task The_marker_and_dot_files_are_never_served()
    {
        var (site, _) = await SiteWithFileAsync(host.Alpha);
        var secret = Marker();
        Write(FilesOf(host.Alpha, site), ".secret", secret);
        var page = await OpenAsync(host.Alpha, site);

        Assert.Equal((400, SiteRules.NotAFilePath(".secret")), await RawGetAsync($"{page}_api/files/.secret"));
        Assert.Equal((400, SiteRules.NotAFilePath(".harness-team")), await RawGetAsync($"{page}_api/files/.harness-team"));
        Assert.Equal(
            (400, SiteRules.NotAFilePath("../../../.harness-team")),
            await RawGetAsync($"{page}_api/files/../../../.harness-team"));
        Assert.Equal((HttpStatusCode.NotFound, SiteEndpoints.NoSuchSiteFile), await GetAsync($"{page}_api/files/harness-team"));
    }

    // ---- the helper script ----

    [Fact]
    public async Task The_helper_builds_a_link_the_route_serves_for_a_stored_path()
    {
        Assert.SkipWhen(Harness.Pty.PathSearch.Find("node") is null, "node is not on PATH.");

        var (site, marker) = await SiteWithFileAsync(host.Alpha);
        Write(FilesOf(host.Alpha, site), "out/naïve name.txt", "spaced");
        var page = await OpenAsync(host.Alpha, site);

        string[] accepted = ["out/a.txt", "out/naïve name.txt"];
        string[] refused = ["..", "a/../b", "/abs", "a\\b", "a\0b", "a%2Fb", "a:b", ".x", "", new string('a', 256)];

        var answers = await RunHelperAsync(page, [.. accepted, .. refused]);

        Assert.Equal($"/sites/{host.Alpha}/{site}/_c/{CapabilityOf(page)}/_api/files/out/a.txt", answers[0].GetProperty("url").GetString());
        Assert.Equal((HttpStatusCode.OK, marker), await GetAsync(answers[0].GetProperty("url").GetString()!));
        Assert.Equal((HttpStatusCode.OK, "spaced"), await GetAsync(answers[1].GetProperty("url").GetString()!));

        for (var i = 0; i < refused.Length; i++)
        {
            var answer = answers[accepted.Length + i];
            Assert.Equal(400, answer.GetProperty("status").GetInt32());
            Assert.Equal(SiteRules.NotAFilePath(refused[i]), answer.GetProperty("error").GetString());

            // The same set the server refuses with that sentence.
            Assert.False(SiteRules.IsFilePath(refused[i]));
        }

        var outside = await RunHelperAsync("/index.html", ["out/a.txt"]);
        Assert.Equal("This page was not opened as a site, so it has no access to site data.", outside[0].GetProperty("error").GetString());
    }

    /// <summary>Runs the real helper script under node with a page at <paramref name="pathname"/>, and
    /// answers what <c>site.files.url</c> made of each path: <c>{url}</c> or <c>{error, status}</c>.</summary>
    private static async Task<JsonElement[]> RunHelperAsync(string pathname, string[] paths)
    {
        var script = Path.Combine(Path.GetTempPath(), $"site-helper-{Guid.NewGuid():N}.js");
        File.WriteAllText(script,
            $"globalThis.window = {{ location: {{ pathname: {JsonSerializer.Serialize(pathname)} }} }};\n"
            + SiteEndpoints.Sdk + "\n"
            + $"const out = {JsonSerializer.Serialize(paths)}.map(p => {{ try {{ return {{ url: window.site.files.url(p) }}; }} "
            + "catch (e) { return { error: e.message, status: e.status }; } });\n"
            + "process.stdout.write(JSON.stringify(out));\n");

        try
        {
            var start = new ProcessStartInfo(Harness.Pty.PathSearch.Find("node")!, [script])
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            AgentCliIsolation.Guard(start.Environment);

            using var node = Process.Start(start)!;
            node.StandardInput.Close();
            var output = await node.StandardOutput.ReadToEndAsync(Ct);
            var error = await node.StandardError.ReadToEndAsync(Ct);
            await node.WaitForExitAsync(Ct);

            Assert.True(node.ExitCode == 0, error);
            return [.. JsonDocument.Parse(output).RootElement.EnumerateArray()];
        }
        finally
        {
            File.Delete(script);
        }
    }

    // ---- Documents ----

    [Fact]
    public async Task The_documents_routes_list_and_delete_a_sites_files_like_any_documents()
    {
        var (site, marker) = await SiteWithFileAsync(host.Alpha);
        var page = await OpenAsync(host.Alpha, site);
        using var person = await host.PersonAsync();

        Assert.Contains("\"sites\"", await person.GetStringAsync($"/api/teams/{host.Alpha}/documents", Ct), StringComparison.Ordinal);
        Assert.Contains("\"a.txt\"", await person.GetStringAsync($"/api/teams/{host.Alpha}/documents?path=sites/{site}/files/out", Ct), StringComparison.Ordinal);
        Assert.Equal(marker, await person.GetStringAsync($"/api/teams/{host.Alpha}/documents/content?path=sites/{site}/files/out/a.txt", Ct));

        var deleted = await person.DeleteAsync($"/api/teams/{host.Alpha}/documents?path=sites/{site}/files/out/a.txt", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var row = await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.DocumentsDeleted, host.Alpha, Ct);
        Assert.Contains($"sites/{site}/files/out/a.txt", row?.Detail, StringComparison.Ordinal);

        Assert.Equal((HttpStatusCode.NotFound, SiteEndpoints.NoSuchSiteFile), await GetAsync($"{page}_api/files/out/a.txt"));
    }

    // ---- callers ----

    private async Task<(string Id, string Key)> MemberKeyAsync(string team)
    {
        var id = new ContainerId(team, Unique("m")).ToString();
        var key = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            id, PrincipalKind.Container, team, new HashSet<string> { Permits.Read, Permits.Sites }, ct: Ct);
        return (id, key);
    }

    /// <summary>The site tool as a member of <paramref name="team"/> calls it. Not async: the accessor
    /// keeps its context in an AsyncLocal, which would not flow back out of an async method.</summary>
    private PlatformMcpTools Tools(string team)
    {
        var (id, key) = MemberKeyAsync(team).GetAwaiter().GetResult();
        var context = new DefaultHttpContext();
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = key;
        context.User = PrincipalClaims.ToClaimsPrincipal(
            new Principal(id, PrincipalKind.Container, new HashSet<string> { Permits.Read, Permits.Sites }), "test");

        return new PlatformMcpTools(
            new HttpContextAccessor { HttpContext = context },
            host.Services.GetRequiredService<IPrincipalStore>(),
            host.Services.GetRequiredService<AgentCatalog>(),
            new ServerClients(host.ServerHandler()));
    }

    private sealed class ServerClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }
}
