using System.Net;
using System.Text;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// Through the real Host: <c>GET .../documents/view</c> answers each listed type
/// with its content type, <c>inline</c> and <c>nosniff</c>, and every one, PDF included, carries a
/// sandboxed CSP in place of the app's.
/// </summary>
public sealed class DocumentViewTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Write(string folder, string relative, string content)
    {
        var docs = host.Services.GetRequiredService<TeamDocuments>();
        var root = folder == host.Alpha ? docs.EnsureFor(folder) : docs.RootFor(folder);
        var file = Path.Combine(root, "view", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        return $"view/{relative}";
    }

    private async Task<HttpResponseMessage> ViewAsync(string folder, string path)
    {
        using var client = await host.PersonAsync();
        return await client.GetAsync(
            $"/api/teams/{folder}/documents/view?path={Uri.EscapeDataString(path)}", Ct);
    }

    [Theory]
    [InlineData("readme.md", "text/html", "utf-8")]
    [InlineData("notes.markdown", "text/html", "utf-8")]
    [InlineData("report.html", "text/html", null)]
    [InlineData("report.htm", "text/html", null)]
    [InlineData("a.txt", "text/plain", "utf-8")]
    [InlineData("a.log", "text/plain", "utf-8")]
    [InlineData("a.json", "text/plain", "utf-8")]
    [InlineData("a.csv", "text/plain", "utf-8")]
    [InlineData("a.yaml", "text/plain", "utf-8")]
    [InlineData("a.yml", "text/plain", "utf-8")]
    [InlineData("a.xml", "text/plain", "utf-8")]
    [InlineData("a.toml", "text/plain", "utf-8")]
    [InlineData("a.cs", "text/plain", "utf-8")]
    [InlineData("a.ts", "text/plain", "utf-8")]
    [InlineData("a.js", "text/plain", "utf-8")]
    [InlineData("a.vue", "text/plain", "utf-8")]
    [InlineData("a.py", "text/plain", "utf-8")]
    [InlineData("a.go", "text/plain", "utf-8")]
    [InlineData("a.sh", "text/plain", "utf-8")]
    [InlineData("a.ps1", "text/plain", "utf-8")]
    [InlineData("a.sql", "text/plain", "utf-8")]
    [InlineData("a.png", "image/png", null)]
    [InlineData("a.jpg", "image/jpeg", null)]
    [InlineData("a.jpeg", "image/jpeg", null)]
    [InlineData("a.gif", "image/gif", null)]
    [InlineData("a.webp", "image/webp", null)]
    [InlineData("a.svg", "image/svg+xml", null)]
    [InlineData("a.pdf", "application/pdf", null)]
    public async Task View_answers_each_listed_type_inline_and_nosniff(
        string name, string mediaType, string? charset)
    {
        var path = Write(host.Alpha, $"types/{name}", "content");

        var response = await ViewAsync(host.Alpha, path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(charset, response.Content.Headers.ContentType?.CharSet);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
    }

    [Theory]
    [InlineData("readme.md")]
    [InlineData("report.html")]
    [InlineData("a.txt")]
    [InlineData("a.png")]
    [InlineData("a.svg")]
    public async Task Everything_but_pdf_carries_the_sandbox_policy_and_not_the_apps(string name)
    {
        var path = Write(host.Alpha, $"csp/{name}", "content");

        var response = await ViewAsync(host.Alpha, path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csp = Header(response, "Content-Security-Policy");
        Assert.Equal(
            "sandbox; default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:", csp);
        Assert.DoesNotContain("'self'", csp);
    }

    [Fact]
    public async Task Pdf_carries_the_sandbox_with_default_src_none_and_not_the_apps()
    {
        var path = Write(host.Alpha, "csp/doc.pdf", "%PDF-1.4");

        var response = await ViewAsync(host.Alpha, path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csp = Header(response, "Content-Security-Policy");
        Assert.Equal("sandbox; default-src 'none'", csp);
        Assert.DoesNotContain("'self'", csp);
    }

    [Theory]
    [InlineData("archive.zip")]
    [InlineData("binary.exe")]
    [InlineData("noextension")]
    [InlineData("sheet.xlsx")]
    public async Task An_unlisted_type_answers_415(string name)
    {
        var path = Write(host.Alpha, $"unlisted/{name}", "content");

        var response = await ViewAsync(host.Alpha, path);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Theory]
    [InlineData("../../Beta/secret.txt")]
    [InlineData("view/../../outside.txt")]
    [InlineData("..\\..\\outside.txt")]
    public async Task A_path_that_climbs_out_answers_400(string path)
    {
        var response = await ViewAsync(host.Alpha, path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_missing_file_answers_404()
    {
        var response = await ViewAsync(host.Alpha, "view/not-there.md");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Markdown_renders_to_a_page_titled_with_the_file_name()
    {
        var path = Write(host.Alpha, "md/Weekly report.md", "# Heading\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n```\ncode\n```\n");

        var response = await ViewAsync(host.Alpha, path);
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains("<title>Weekly report.md</title>", html);
        Assert.Contains("<h1", html);
        Assert.Contains("<table>", html);
        Assert.Contains("<pre><code>", html);
        Assert.Contains("prefers-color-scheme: dark", html);
        Assert.Contains("max-width", html);
        Assert.Contains("system-ui", html);
    }

    [Fact]
    public async Task Markdown_with_raw_script_renders_the_tag_as_text()
    {
        var path = Write(
            host.Alpha, "md/evil.md",
            "Hello\n\n<script>fetch('/api/me')</script>\n\ninline <img src=x onerror=alert(1)> here\n");

        var response = await ViewAsync(host.Alpha, path);
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;fetch(", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
    }

    [Fact]
    public void Markdown_title_is_html_encoded()
    {
        var html = DocumentView.RenderMarkdown("<b>x</b>.md", "text");

        Assert.Contains("<title>&lt;b&gt;x&lt;/b&gt;.md</title>", html);
    }

    [Fact]
    public async Task Html_is_served_as_it_is()
    {
        const string page = "<!DOCTYPE html><p>hi</p><script>fetch('/api/me')</script>";
        var path = Write(host.Alpha, "html/page.html", page);

        var response = await ViewAsync(host.Alpha, path);

        Assert.Equal(page, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_gone_teams_folder_is_viewable()
    {
        var gone = "Departed";
        Directory.CreateDirectory(host.Services.GetRequiredService<TeamDocuments>().RootFor(gone));
        var path = Write(gone, "note.md", "kept");

        var response = await ViewAsync(gone, path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("kept", await response.Content.ReadAsStringAsync(Ct));
        Assert.StartsWith("sandbox;", Header(response, "Content-Security-Policy"));
    }

    [Fact]
    public async Task A_retired_folder_is_viewable()
    {
        var retired = "Alpha" + TeamPaths.RetiredSuffix + "abc123";
        Directory.CreateDirectory(host.Services.GetRequiredService<TeamDocuments>().RootFor(retired));
        var path = Write(retired, "old.txt", "record");

        var response = await ViewAsync(retired, path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("record", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_folder_that_is_not_there_answers_404()
    {
        var response = await ViewAsync("Nobody", "a.md");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        var path = Write(host.Alpha, "anon/a.txt", "x");
        using var client = host.Anonymous();

        var response = await client.GetAsync(
            $"/api/teams/{host.Alpha}/documents/view?path={Uri.EscapeDataString(path)}", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_container_with_read_on_its_own_team_can_view()
    {
        var path = Write(host.Alpha, "agent/a.txt", "x");
        using var client = host.Container(host.AlphaContainerKey);

        var response = await client.GetAsync(
            $"/api/teams/{host.Alpha}/documents/view?path={Uri.EscapeDataString(path)}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_other_documents_routes_keep_the_apps_policy()
    {
        var path = Write(host.Alpha, "other/a.txt", "x");
        using var client = await host.PersonAsync();

        var response = await client.GetAsync(
            $"/api/teams/{host.Alpha}/documents/content?path={Uri.EscapeDataString(path)}", Ct);

        Assert.Equal(SecurityHeaders.ContentSecurityPolicy, Header(response, "Content-Security-Policy"));
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? string.Join(",", values)
            : response.Content.Headers.TryGetValues(name, out var content) ? string.Join(",", content) : null;
}
