using Yawble.Core.Integrations;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>SpecFinder locates an OpenAPI spec through an injected handler: no test here reaches the network.</summary>
public class SpecFinderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Spec = """{"openapi":"3.0.0","info":{"title":"Pets","version":"1"},"paths":{"/pets":{"get":{"summary":"List"}}}}""";
    private const string SpecYaml = "openapi: 3.0.0\ninfo:\n  title: Pets\n  version: '1'\npaths: {}\n";
    private static readonly Uri Service = new("https://api.example.com/v1");

    [Fact]
    public async Task SpecFinder_DocsUrlIsSpec()
    {
        var http = new FakeHttp().Add("https://docs.example.com/spec.json", Spec);
        var (text, url) = await new SpecFinder(http).FindAsync(new Uri("https://docs.example.com/spec.json"), Service, Ct);
        Assert.Equal(Spec, text);
        Assert.Equal("https://docs.example.com/spec.json", url.AbsoluteUri);
        Assert.Single(http.Requests);
        // it sends no sign-in
        Assert.All(http.Requests, r => Assert.Null(r.Headers.Authorization));
    }

    [Fact]
    public async Task SpecFinder_HtmlPageLinksToSpec()
    {
        var http = new FakeHttp()
            .Add("https://docs.example.com/", """<html><a href="/static/logo.json">x</a><a href="/static/openapi.yaml">API</a></html>""", "text/html")
            .Add("https://docs.example.com/static/openapi.yaml", SpecYaml, "application/yaml");
        var (text, url) = await new SpecFinder(http).FindAsync(new Uri("https://docs.example.com/"), Service, Ct);
        Assert.Equal(SpecYaml, text);
        Assert.Equal("https://docs.example.com/static/openapi.yaml", url.AbsoluteUri);
        Assert.DoesNotContain("https://docs.example.com/static/logo.json", http.Urls); // not named openapi or swagger
    }

    [Fact]
    public async Task SpecFinder_TriesAtMostFivePageLinks()
    {
        var links = string.Concat(Enumerable.Range(1, 8).Select(i => $"<a href=\"/openapi{i}.json\">x</a>"));
        var http = new FakeHttp().Add("https://docs.example.com/", links, "text/html").Add("https://docs.example.com/openapi7.json", Spec);
        await Assert.ThrowsAsync<ArgumentException>(() => new SpecFinder(http).FindAsync(new Uri("https://docs.example.com/"), Service, Ct));
        Assert.Equal(5, http.Urls.Count(u => u.StartsWith("https://docs.example.com/openapi", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SpecFinder_FallsBackToWellKnownPaths()
    {
        var http = new FakeHttp()
            .Add("https://docs.example.com/", "<html>no links</html>", "text/html")
            .Add("https://api.example.com/v1/swagger.json", Spec);
        var (_, url) = await new SpecFinder(http).FindAsync(new Uri("https://docs.example.com/"), Service, Ct);
        Assert.Equal("https://api.example.com/v1/swagger.json", url.AbsoluteUri);
        Assert.Equal(["https://docs.example.com/", "https://api.example.com/v1/openapi.json", "https://api.example.com/v1/openapi.yaml", "https://api.example.com/v1/swagger.json"], http.Urls);
    }

    [Fact]
    public async Task SpecFinder_NoDocsUrl_UsesTheUsualPaths()
    {
        var http = new FakeHttp().Add("https://api.example.com/v1/.well-known/openapi.json", Spec);
        var (_, url) = await new SpecFinder(http).FindAsync(null, Service, Ct);
        Assert.EndsWith("/.well-known/openapi.json", url.AbsoluteUri);
    }

    [Fact]
    public async Task SpecFinder_NothingFound_PlainMessage()
    {
        var http = new FakeHttp().Add("https://docs.example.com/", "<html>hello</html>", "text/html");
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => new SpecFinder(http).FindAsync(new Uri("https://docs.example.com/"), Service, Ct));
        Assert.Equal("no OpenAPI spec found at the documentation URL or the usual places; paste the link to the spec itself (often ends in openapi.json)", ex.Message);
    }

    [Fact]
    public async Task SpecFinder_StopsAt20MB()
    {
        var http = new FakeHttp().AddGenerated("https://docs.example.com/openapi.json", 200L * 1024 * 1024);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => new SpecFinder(http).FindAsync(new Uri("https://docs.example.com/openapi.json"), Service, Ct));
        Assert.Contains("20 MB", ex.Message);
        Assert.True(http.BytesServed <= 20L * 1024 * 1024 + 81920, $"read {http.BytesServed} bytes");
    }

    [Fact]
    public async Task SpecFinder_HostileSpec_IsNotUsable_AndItsMessageSurfacesIfNothingElseWorks()
    {
        var deep = new string('[', 5000) + new string(']', 5000);
        var http = new FakeHttp().Add("https://docs.example.com/openapi.json", deep);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => new SpecFinder(http).FindAsync(new Uri("https://docs.example.com/openapi.json"), Service, Ct));
        Assert.Contains("too deeply nested", ex.Message);
        // but another candidate that works wins
        var ok = new FakeHttp().Add("https://docs.example.com/openapi.json", deep).Add("https://api.example.com/v1/openapi.json", Spec);
        var (_, url) = await new SpecFinder(ok).FindAsync(new Uri("https://docs.example.com/openapi.json"), Service, Ct);
        Assert.Equal("https://api.example.com/v1/openapi.json", url.AbsoluteUri);
    }

    [Fact]
    public async Task SpecFinder_ContentLengthOverTheCap_IsRefusedWithoutReadingTheBody()
    {
        var http = new FakeHttp().AddGenerated("https://docs.example.com/openapi.json", 1000, declaredLength: 30L * 1024 * 1024);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => new SpecFinder(http).FindAsync(new Uri("https://docs.example.com/openapi.json"), Service, Ct));
        Assert.Contains("20 MB", ex.Message);
        Assert.Equal(0, http.BytesServed);
    }

    [Fact]
    public async Task SpecFinder_AnOversizedHtmlDocsPage_FallsThroughToThePlainMessage()
    {
        var http = new FakeHttp().AddGenerated("https://docs.example.com/", 200L * 1024 * 1024, "text/html");
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => new SpecFinder(http).FindAsync(new Uri("https://docs.example.com/"), Service, Ct));
        Assert.Equal(SpecFinder.NotFound, ex.Message);
    }

    [Fact]
    public async Task SpecFinder_ADroppedConnection_MovesOnToTheNextCandidate()
    {
        var http = new FakeHttp().AddDropped("https://docs.example.com/openapi.json").Add("https://api.example.com/v1/openapi.json", Spec);
        var (_, url) = await new SpecFinder(http).FindAsync(new Uri("https://docs.example.com/openapi.json"), Service, Ct);
        Assert.Equal("https://api.example.com/v1/openapi.json", url.AbsoluteUri);
    }

    [Fact]
    public async Task SpecFinder_UsualPathsIgnoreTheServiceUrlsQueryAndFragment()
    {
        var http = new FakeHttp().Add("https://api.example.com:8443/v1/openapi.json", Spec);
        var (_, url) = await new SpecFinder(http).FindAsync(null, new Uri("https://api.example.com:8443/v1/?key=1#top"), Ct);
        Assert.Equal("https://api.example.com:8443/v1/openapi.json", url.AbsoluteUri);
        Assert.Equal("https://api.example.com:8443/v1/openapi.json", http.Urls.First());
    }

    [Theory]
    [InlineData("openapi.json", "https://docs.example.com/guide/openapi.json")]
    [InlineData("../openapi.yaml", "https://docs.example.com/openapi.yaml")]
    [InlineData("//cdn.example.org/openapi.json", "https://cdn.example.org/openapi.json")]
    public async Task SpecFinder_RelativeAndProtocolRelativeLinksResolve(string href, string resolved)
    {
        var http = new FakeHttp()
            .Add("https://docs.example.com/guide/", $"<a href=\"{href}\">spec</a>", "text/html")
            .Add(resolved, Spec);
        var (_, url) = await new SpecFinder(http).FindAsync(new Uri("https://docs.example.com/guide/"), Service, Ct);
        Assert.Equal(resolved, url.AbsoluteUri);
    }

    [Fact]
    public async Task SpecFinder_AFileLinkIsIgnored()
    {
        var http = new FakeHttp().Add("https://docs.example.com/", "<a href=\"file:///c:/openapi.json\">x</a>", "text/html");
        await Assert.ThrowsAsync<ArgumentException>(() => new SpecFinder(http).FindAsync(new Uri("https://docs.example.com/"), Service, Ct));
        Assert.DoesNotContain(http.Urls, u => u.StartsWith("file:", StringComparison.Ordinal));
    }
}
