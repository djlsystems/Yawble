using System.Net;
using System.Text.Json;
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
