using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harness.Tests;

/// <summary>
/// THE SIGN-IN BROWSER, FOR REAL: Google Chrome from the volume, headed on its own display and sandboxed, against a
/// local fake site whose made-up hosts (site.test, idp.test, evil.test) Chromium resolves to it. The
/// entry is site.test; idp.test is reached only through an OAuth-shaped link, which admits it as a
/// provider; evil.test is refused as a page, and its cookie is pruned from the save.
/// Skipped where no Google Chrome (or no Xvfb) is installed, and as root, where Chrome's sandbox will not start.
/// </summary>
public sealed class SignInBrowserChromiumTests : IAsyncLifetime
{
    private WebApplication? _site;
    private int _port;
    private string? _profiles;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Google Chrome as the worker finds it (<c>HARNESS_SIGN_IN_CHROME</c>, else the volume), or null with no Xvfb to
    /// run it on, or as root: Chrome's own sandbox refuses root, and the browser never runs without it (the release
    /// runs this suite as root inside the image; the worker runs Chrome as its own user).
    /// </summary>
    internal static string? Chromium() =>
        Environment.UserName != "root" && File.Exists(SignInDisplay.DefaultXvfb) ? ChromiumSignInBrowser.Find() : null;

    internal const string NoChrome = "No Google Chrome (HARNESS_SIGN_IN_CHROME or google-chrome on the volume) or no Xvfb is installed, or the suite runs as root, where Chrome's own sandbox will not start.";

    public async ValueTask InitializeAsync()
    {
        _profiles = Path.Combine(Path.GetTempPath(), "sign-in-tests-" + Guid.NewGuid().ToString("N"));
        if (Chromium() is null) return;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _site = builder.Build();

        // site.test: the entry. Its page sets a cookie and local storage, shows an image from evil.test
        // (which tries a cookie of its own), and goes where ?go= says.
        // ?then= is where it goes a while later: a page still alive after ?go= was refused gets there.
        _site.MapGet("/", (HttpContext context, string? go, string? then) =>
        {
            var host = context.Request.Host.Host;
            context.Response.Cookies.Append(host + "_sid", "cookie-of-" + host);
            var script = $"localStorage.setItem('token', 'local-of-{host}');"
                + (go is null ? "" : $"setTimeout(() => location.href = {JsonSerializer.Serialize(go)}, 50);")
                + (then is null ? "" : $"setTimeout(() => location.href = {JsonSerializer.Serialize(then)}, 1500);");
            return Results.Content(
                $"<html><head><title>{host}</title></head><body><img src='http://evil.test:{_port}/pixel'>"
                + $"<script>{script}</script></body></html>", "text/html");
        });

        // idp.test: a provider's authorize step, which signs in and sends the person back with a code.
        _site.MapGet("/authorize", (HttpContext context, string redirect_uri) =>
        {
            context.Response.Cookies.Append("idp_sid", "cookie-of-idp");
            return Results.Redirect(redirect_uri + "?code=oauth-code");
        });

        // Any host: a server redirect to ?to=.
        _site.MapGet("/bounce", (string to) => Results.Redirect(to));

        _site.MapGet("/cb", (HttpContext context) =>
        {
            context.Response.Cookies.Append("session", "signed-in");
            return Results.Content("<html><head><title>signed in</title></head><body>done</body></html>", "text/html");
        });

        _site.MapGet("/pixel", (HttpContext context) =>
        {
            context.Response.Cookies.Append("tracker", "evil", new CookieOptions { SameSite = SameSiteMode.None, Secure = false });
            return Results.Bytes([], "image/gif");
        });

        await _site.StartAsync(Ct);
        _port = new Uri(_site.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_site is not null) await _site.DisposeAsync();
        if (_profiles is not null && Directory.Exists(_profiles)) Directory.Delete(_profiles, recursive: true);
    }

    private string Url(string host, string path = "/") => $"http://{host}:{_port}{path}";

    private async Task<(ChromiumSignInBrowser Browser, ConcurrentQueue<StreamChunk> Chunks)> StartAsync(string startUrl)
    {
        var chunks = new ConcurrentQueue<StreamChunk>();
        var browser = await ChromiumSignInBrowser.StartAsync(
            "sign-in:test", new SignInBrowserLaunch(startUrl, ["site.test"], 800, 600), Chromium()!, _profiles!,
            chunk =>
            {
                chunks.Enqueue(chunk);
                return Task.FromResult(true);
            },
            [$"--host-resolver-rules=MAP *.test 127.0.0.1"], Ct);
        return (browser, chunks);
    }

    private static IEnumerable<SignInBrowserNotice> Notices(IEnumerable<StreamChunk> chunks) =>
        chunks.Where(c => c.Text is not null).Select(c => JsonSerializer.Deserialize<SignInBrowserNotice>(c.Text!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for " + what + ".");
            await Task.Delay(100, Ct);
        }
    }

    [Fact]
    public async Task A_page_on_another_host_is_refused_and_the_screen_is_streamed()
    {
        Assert.SkipWhen(Chromium() is null, NoChrome);

        var (browser, chunks) = await StartAsync(Url("site.test", "/?go=" + Uri.EscapeDataString(Url("evil.test"))));
        await using (browser)
        {
            await Until(() => Notices(chunks).Any(n => n.Kind == "refused"), "the refusal");

            Assert.Contains(Notices(chunks), n => n is { Kind: "refused", Host: "evil.test" });
            Assert.Contains(Notices(chunks), n => n.Kind == "page" && n.Url == Url("site.test"));
            Assert.DoesNotContain(Notices(chunks), n => n.Kind == "page" && n.Url?.Contains("evil.test", StringComparison.Ordinal) == true);
            Assert.Empty(browser.Hosts.Providers);

            await Until(() => chunks.Any(c => c.Bytes is { Length: > 0 }), "a frame");
            var frame = chunks.First(c => c.Bytes is { Length: > 0 }).Bytes!;
            Assert.Equal([0xFF, 0xD8], frame[..2]);
        }

        Assert.Empty(Directory.EnumerateDirectories(_profiles!));
    }

    [Fact]
    public async Task An_oauth_step_admits_its_provider_and_the_save_is_pruned_to_the_entry_and_the_provider()
    {
        Assert.SkipWhen(Chromium() is null, NoChrome);

        var authorize = Url("idp.test", "/authorize?client_id=app&response_type=code&redirect_uri=" + Uri.EscapeDataString(Url("site.test", "/cb")));
        var (browser, chunks) = await StartAsync(Url("site.test", "/?go=" + Uri.EscapeDataString(authorize)));
        await using (browser)
        {
            await Until(() => Notices(chunks).Any(n => n.Kind == "page" && n.Url == Url("site.test", "/cb")), "the way back from the provider");

            Assert.Equal(["idp.test"], browser.Hosts.Providers);
            Assert.Contains(Notices(chunks), n => n is { Kind: "provider", Host: "idp.test" });
            // The code in the way back's query is never in a notice.
            Assert.DoesNotContain(chunks, c => c.Text?.Contains("oauth-code", StringComparison.Ordinal) == true);

            using var state = JsonDocument.Parse(await browser.CaptureAsync(Ct));
            var cookies = state.RootElement.GetProperty("cookies").EnumerateArray()
                .Select(c => (c.GetProperty("domain").GetString(), c.GetProperty("name").GetString())).ToList();

            Assert.Contains(("site.test", "site.test_sid"), cookies);
            Assert.Contains(("site.test", "session"), cookies);
            Assert.Contains(("idp.test", "idp_sid"), cookies);
            Assert.DoesNotContain(cookies, c => c.Item1 == "evil.test");

            var origins = state.RootElement.GetProperty("origins").EnumerateArray()
                .ToDictionary(o => o.GetProperty("origin").GetString()!, o => o.GetProperty("localStorage")[0].GetProperty("value").GetString());
            Assert.Equal("local-of-site.test", origins[Url("site.test").TrimEnd('/')]);
            Assert.DoesNotContain(origins.Keys, o => o.Contains("evil.test", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_refused_page_leaves_the_browser_on_the_last_allowed_page_alive()
    {
        Assert.SkipWhen(Chromium() is null, NoChrome);

        var start = Url("site.test", "/?go=" + Uri.EscapeDataString(Url("evil.test")) + "&then=" + Uri.EscapeDataString(Url("site.test", "/cb")));
        var (browser, chunks) = await StartAsync(start);
        await using (browser)
        {
            await Until(() => Notices(chunks).Any(n => n.Kind == "refused"), "the refusal");
            // The page that tried to leave is still there, its script still running: it goes on to /cb.
            await Until(() => Notices(chunks).Any(n => n.Kind == "page" && n.Url == Url("site.test", "/cb")), "the next allowed page");

            Assert.DoesNotContain(Notices(chunks), n => n.Kind == "page" && n.Url is not null && !n.Url.StartsWith(Url("site.test", ""), StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task From_a_providers_page_a_link_or_its_redirect_to_another_host_is_refused_and_nothing_of_it_kept()
    {
        Assert.SkipWhen(Chromium() is null, NoChrome);

        // On the provider's page: first a redirect it serves to far.test, later a plain link there.
        var provider = Url("idp.test", "/?client_id=app&redirect_uri=" + Uri.EscapeDataString(Url("site.test", "/cb"))
            + "&go=" + Uri.EscapeDataString(Url("idp.test", "/bounce?to=" + Uri.EscapeDataString(Url("far.test"))))
            + "&then=" + Uri.EscapeDataString(Url("far.test", "/?x=1")));
        var (browser, chunks) = await StartAsync(Url("site.test", "/?go=" + Uri.EscapeDataString(provider)));
        await using (browser)
        {
            await Until(() => Notices(chunks).Count(n => n is { Kind: "refused", Host: "far.test" }) >= 2, "both ways to far.test refused");

            Assert.Equal(["idp.test"], browser.Hosts.Providers);
            Assert.DoesNotContain(Notices(chunks), n => n is { Kind: "provider", Host: "far.test" });

            using var state = JsonDocument.Parse(await browser.CaptureAsync(Ct));
            Assert.DoesNotContain(state.RootElement.GetProperty("cookies").EnumerateArray(), c => c.GetProperty("domain").GetString() == "far.test");
            Assert.DoesNotContain(state.RootElement.GetProperty("origins").EnumerateArray(), o => o.GetProperty("origin").GetString()!.Contains("far.test", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_providers_redirect_in_a_chain_started_on_the_entry_admits_its_next_host()
    {
        Assert.SkipWhen(Chromium() is null, NoChrome);

        var next = Url("idp2.test", "/");
        var bounce = Url("idp.test", "/bounce?client_id=app&redirect_uri=x&to=" + Uri.EscapeDataString(next));
        var (browser, chunks) = await StartAsync(Url("site.test", "/?go=" + Uri.EscapeDataString(bounce)));
        await using (browser)
        {
            await Until(() => Notices(chunks).Any(n => n.Kind == "page" && n.Url == next), "the provider's next host");
            Assert.Equal(["idp.test", "idp2.test"], browser.Hosts.Providers);
        }
    }

    [Fact]
    public async Task Local_storage_of_every_kept_origin_passed_through_is_saved_not_only_the_one_open_at_done()
    {
        Assert.SkipWhen(Chromium() is null, NoChrome);

        // site.test, then the provider's own page (which sets its local storage), then back to site.test.
        var provider = Url("idp.test", "/?client_id=app&redirect_uri=x&go=" + Uri.EscapeDataString(Url("site.test", "/cb")));
        var (browser, chunks) = await StartAsync(Url("site.test", "/?go=" + Uri.EscapeDataString(provider)));
        await using (browser)
        {
            await Until(() => Notices(chunks).Any(n => n.Kind == "page" && n.Url == Url("site.test", "/cb")), "the way back from the provider");

            using var state = JsonDocument.Parse(await browser.CaptureAsync(Ct));
            var origins = state.RootElement.GetProperty("origins").EnumerateArray()
                .ToDictionary(o => o.GetProperty("origin").GetString()!, o => o.GetProperty("localStorage")[0].GetProperty("value").GetString());
            Assert.Equal("local-of-site.test", origins[Url("site.test").TrimEnd('/')]);
            Assert.Equal("local-of-idp.test", origins[Url("idp.test").TrimEnd('/')]);
        }
    }
}
