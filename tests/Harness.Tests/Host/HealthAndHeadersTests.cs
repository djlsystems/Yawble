using System.Net;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// An anonymous /healthz naming three checks, and the security headers on
/// every response - the bundle, an API answer, and a refusal alike.
/// </summary>
public sealed class HealthAndHeadersTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task Healthz_is_anonymous_and_names_three_passing_checks()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = host.Anonymous();

        var response = await client.GetAsync("/healthz", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("healthy", body.RootElement.GetProperty("status").GetString());

        var checks = body.RootElement.GetProperty("checks").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("ok").GetBoolean());

        Assert.Equal(["dataRoot", "database", "pump"], checks.Keys.Order(StringComparer.Ordinal));
        Assert.All(checks.Values, Assert.True);
    }

    [Fact]
    public void A_pump_that_has_not_beaten_for_a_minute_is_not_alive()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-09-23T12:00:00Z"));
        var pump = new PumpHeartbeat(clock);

        Assert.False(pump.IsAlive);

        pump.Beat();
        Assert.True(pump.IsAlive);

        clock.Now += PumpHeartbeat.Stale;
        Assert.False(pump.IsAlive);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/healthz")]
    [InlineData("/api/auth/state")]
    [InlineData("/api/teams")]
    [InlineData("/sites/_sdk/site.js")]
    [InlineData("/sites/alpha/nosuch/")]
    [InlineData("/sites/alpha/nosuch/_c/not-a-capability/index.html")]
    [InlineData("/sites/alpha/nosuch/_c/not-a-capability/_api/data/jobs")]
    public async Task Every_response_carries_the_security_headers(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = host.Anonymous();

        var response = await client.GetAsync(path, ct);

        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("same-origin", Header(response, "Referrer-Policy"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));

        var csp = Header(response, "Content-Security-Policy");
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("script-src 'self';", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
    }

    /// <summary>
    /// The documents view: the one response whose CSP is not the app's, and it is STRICTER. Every other
    /// header is still there.
    /// </summary>
    [Fact]
    public async Task A_documents_view_response_carries_the_sandboxed_policy_in_place_of_the_apps()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = host.Services.GetRequiredService<TeamDocuments>().EnsureFor(host.Alpha);
        await File.WriteAllTextAsync(Path.Combine(root, "headers.html"), "<script>fetch('/api/me')</script>", ct);
        using var client = await host.PersonAsync();

        var response = await client.GetAsync($"/api/teams/{host.Alpha}/documents/view?path=headers.html", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("same-origin", Header(response, "Referrer-Policy"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal(DocumentView.SandboxPolicy, Header(response, "Content-Security-Policy"));
        Assert.Equal(
            "sandbox; default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:",
            DocumentView.SandboxPolicy);

        // PDF is sandboxed too: Chrome's viewer displays it under this policy.
        await File.WriteAllTextAsync(Path.Combine(root, "headers.pdf"), "%PDF-1.4", ct);
        var pdf = await client.GetAsync($"/api/teams/{host.Alpha}/documents/view?path=headers.pdf", ct);

        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("sandbox; default-src 'none'", Header(pdf, "Content-Security-Policy"));
    }

    /// <summary>
    /// A site: the other response whose CSP is not the app's. A sandbox that runs the site's scripts
    /// in an opaque origin - no allow-same-origin, no top navigation, no popups - with its scripts,
    /// styles and images from its own files under its capability, and its connections to its own
    /// data and actions only. The entry that issues the capability, a refused capability and every
    /// other response keep the app's policy.
    /// </summary>
    [Fact]
    public async Task A_site_response_carries_the_site_policy_in_place_of_the_apps_and_nothing_else_does()
    {
        var ct = TestContext.Current.CancellationToken;
        var sites = host.Services.GetRequiredService<SiteService>();
        var person = SiteActor.Person("person-1", "person@example.test");

        var folder = Path.Combine(host.Services.GetRequiredService<TeamDocuments>().EnsureFor(host.Alpha), "headers-site");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "index.html"), "<script src=\"/sites/_sdk/site.js\"></script>", ct);
        Assert.True((await sites.CreateAsync(host.Alpha, "headers", person, ct)).Ok);
        Assert.True((await sites.PublishAsync(host.Alpha, "headers", folder, person, ct)).Ok);

        using var browser = await host.PersonAsync(allowAutoRedirect: false);
        var entry = await browser.GetAsync($"/sites/{host.Alpha}/headers/", ct);
        Assert.Equal(HttpStatusCode.Redirect, entry.StatusCode);
        Assert.Contains("script-src 'self';", Header(entry, "Content-Security-Policy"));

        var page = entry.Headers.Location!.OriginalString;
        var capability = page.Split('/')[5];
        using var sandbox = host.Anonymous();

        foreach (var path in new[] { page, $"{page}index.html", $"{page}_api/data/jobs", $"{page}_api/whoami" })
        {
            var response = await sandbox.GetAsync(path, ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
            Assert.Equal("same-origin", Header(response, "Referrer-Policy"));
            Assert.Equal("DENY", Header(response, "X-Frame-Options"));
            Assert.Equal(SitePolicy.For("http://localhost", host.Alpha, "headers", capability), Header(response, "Content-Security-Policy"));
        }

        var files = $"http://localhost/sites/{host.Alpha}/headers/_c/{capability}/";
        Assert.Equal(
            "sandbox allow-scripts allow-forms; default-src 'none'; "
            + $"script-src {files} http://localhost/sites/_sdk/site.js; style-src {files}; img-src {files}; font-src {files}; "
            + $"connect-src {files}_api/; form-action 'none'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'",
            SitePolicy.For("http://localhost", host.Alpha, "headers", capability));

        var policy = SitePolicy.For("http://localhost", host.Alpha, "headers", capability);
        Assert.DoesNotContain("allow-same-origin", policy);
        Assert.DoesNotContain("allow-top-navigation", policy);
        Assert.DoesNotContain("allow-popups", policy);
        Assert.DoesNotContain("'unsafe-inline'", policy);

        // The app itself is untouched.
        Assert.Contains("script-src 'self';", Header(await browser.GetAsync("/api/teams", ct), "Content-Security-Policy"));
        Assert.Equal(
            "default-src 'self'; script-src 'self'; style-src 'self'; style-src-elem 'self' 'unsafe-inline'; img-src 'self'; "
            + "font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'",
            SecurityHeaders.ContentSecurityPolicy);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? string.Join(",", values)
            : response.Content.Headers.TryGetValues(name, out var content) ? string.Join(",", content) : null;

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
