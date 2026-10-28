using System.Collections.Concurrent;
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
/// THE SIGN-IN BROWSER IS A PERSON'S CHROME, AS A SITE SEES IT. A local fake page reports what a
/// fingerprinting script reads - the user agent, its client hints with their high-entropy values,
/// <c>navigator.webdriver</c>, plugins, languages, WebGL, screen, viewport, time zone - and the server
/// keeps the headers it was sent. Nothing may say Headless, nothing may say automation, the headers
/// must agree with the script, the locale must be the person's, and the row's profile must come back
/// on the next sign-in. On <c>localhost</c>, which Chrome treats as secure, so the client hints are sent
/// as on any https site. Skipped where no Google Chrome (or no Xvfb) is installed.
/// </summary>
public sealed class SignInBrowserIdentityTests : IAsyncLifetime
{
    private WebApplication? _site;
    private int _port;
    private string? _profiles;
    private readonly ConcurrentQueue<Dictionary<string, string>> _documents = new();
    private readonly ConcurrentQueue<JsonElement> _reports = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Row = "0123456789abcdef0123456789abcdef/fedcba9876543210fedcba9876543210";

    private const string Probe = """
        <html><head><title>probe</title></head><body><script>
        (async () => {
          const r = {};
          r.userAgent = navigator.userAgent;
          r.webdriver = navigator.webdriver;
          r.plugins = navigator.plugins.length;
          r.mimeTypes = navigator.mimeTypes.length;
          r.languages = navigator.languages;
          r.brands = navigator.userAgentData.brands;
          r.platform = navigator.userAgentData.platform;
          r.mobile = navigator.userAgentData.mobile;
          r.high = await navigator.userAgentData.getHighEntropyValues(
            ['architecture', 'bitness', 'model', 'platformVersion', 'fullVersionList', 'uaFullVersion']);
          const gl = document.createElement('canvas').getContext('webgl');
          const info = gl && gl.getExtension('WEBGL_debug_renderer_info');
          r.webglVendor = info ? gl.getParameter(info.UNMASKED_VENDOR_WEBGL) : null;
          r.webglRenderer = info ? gl.getParameter(info.UNMASKED_RENDERER_WEBGL) : null;
          r.screen = { width: screen.width, height: screen.height };
          r.outer = { width: outerWidth, height: outerHeight };
          r.viewport = { width: innerWidth, height: innerHeight };
          r.timeZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
          r.chrome = typeof window.chrome;
          r.counter = Number(localStorage.getItem('visits') || '0');
          localStorage.setItem('visits', String(r.counter + 1));
          r.cookie = document.cookie;
          await fetch('/report', { method: 'POST', body: JSON.stringify(r) });
        })();
        </script></body></html>
        """;

    public async ValueTask InitializeAsync()
    {
        _profiles = Path.Combine(Path.GetTempPath(), "sign-in-identity-" + Guid.NewGuid().ToString("N"));
        if (SignInBrowserChromiumTests.Chromium() is null) return;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _site = builder.Build();

        _site.MapGet("/", (HttpContext context) =>
        {
            _documents.Enqueue(context.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString()));

            // Ask for the high-entropy hints as a site does; Critical-CH has Chrome send them on this very load.
            context.Response.Headers["Accept-CH"] = "Sec-CH-UA-Full-Version-List, Sec-CH-UA-Platform-Version, Sec-CH-UA-Arch, Sec-CH-UA-Bitness, Sec-CH-UA-Model";
            context.Response.Headers["Critical-CH"] = "Sec-CH-UA-Full-Version-List, Sec-CH-UA-Platform-Version";
            if (!context.Request.Cookies.ContainsKey("first")) context.Response.Cookies.Append("first", "run-1", new CookieOptions { Expires = DateTimeOffset.UtcNow.AddDays(30) });
            return Results.Content(Probe, "text/html");
        });

        _site.MapPost("/report", async (HttpContext context) =>
        {
            _reports.Enqueue(await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: Ct));
            return Results.Ok();
        });

        await _site.StartAsync(Ct);
        _port = new Uri(_site.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_site is not null) await _site.DisposeAsync();
        if (_profiles is not null && Directory.Exists(_profiles)) Directory.Delete(_profiles, recursive: true);
    }

    private Task<ChromiumSignInBrowser> StartAsync() =>
        ChromiumSignInBrowser.StartAsync(
            "sign-in:identity",
            new SignInBrowserLaunch($"http://localhost:{_port}/", ["localhost"], 1280, 800, Row, ["de-DE", "de", "en-US", "en"], "Europe/Berlin"),
            SignInBrowserChromiumTests.Chromium()!, _profiles!, _ => Task.FromResult(true), ct: Ct);

    private async Task<JsonElement> ReportAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (_reports.Count < count)
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The probe page did not report.");
            await Task.Delay(100, Ct);
        }

        return _reports.ElementAt(count - 1);
    }

    private static string Header(IReadOnlyDictionary<string, string> headers, string name)
    {
        Assert.True(headers.TryGetValue(name, out var value), $"No {name} header was sent.");
        return value;
    }

    /// <summary>Sec-CH-UA's form of a brand list: <c>"Brand";v="1", ...</c>.</summary>
    private static string Structured(JsonElement brands) =>
        string.Join(", ", brands.EnumerateArray().Select(b => $"\"{b.GetProperty("brand").GetString()}\";v=\"{b.GetProperty("version").GetString()}\""));

    [Fact]
    public async Task The_site_sees_a_persons_chrome_in_their_locale_and_the_row_comes_back_with_its_profile()
    {
        Assert.SkipWhen(SignInBrowserChromiumTests.Chromium() is null, SignInBrowserChromiumTests.NoChrome);

        SignInBrowserIdentity identity;
        await using (var browser = await StartAsync())
        {
            var seen = await ReportAsync(1);
            var document = _documents.Last(d => d.ContainsKey("sec-ch-ua-full-version-list"));

            // NOTHING SAYS HEADLESS, in the script or on the wire.
            var userAgent = seen.GetProperty("userAgent").GetString()!;
            Assert.DoesNotContain("Headless", userAgent, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(userAgent, Header(document, "user-agent"));
            foreach (var (name, value) in document.Where(h => h.Key.StartsWith("sec-ch-ua", StringComparison.Ordinal)))
            {
                Assert.DoesNotContain("Headless", value, StringComparison.OrdinalIgnoreCase);
            }

            Assert.DoesNotContain("Headless", seen.GetProperty("brands").GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Headless", seen.GetProperty("high").GetProperty("fullVersionList").GetRawText(), StringComparison.OrdinalIgnoreCase);

            // NOTHING SAYS AUTOMATION.
            Assert.NotEqual(JsonValueKind.True, seen.GetProperty("webdriver").ValueKind);
            Assert.Equal(5, seen.GetProperty("plugins").GetInt32());
            Assert.Equal(2, seen.GetProperty("mimeTypes").GetInt32());
            Assert.Equal("object", seen.GetProperty("chrome").GetString());

            // THE HEADERS AGREE WITH THE SCRIPT, and both say Google Chrome on Linux, as the fonts and WebGL do.
            Assert.Equal(Structured(seen.GetProperty("brands")), Header(document, "sec-ch-ua"));
            Assert.Equal(Structured(seen.GetProperty("high").GetProperty("fullVersionList")), Header(document, "sec-ch-ua-full-version-list"));
            Assert.Equal($"\"{seen.GetProperty("platform").GetString()}\"", Header(document, "sec-ch-ua-platform"));
            Assert.Equal($"\"{seen.GetProperty("high").GetProperty("platformVersion").GetString()}\"", Header(document, "sec-ch-ua-platform-version"));
            Assert.Equal("?0", Header(document, "sec-ch-ua-mobile"));
            Assert.Equal("Linux", seen.GetProperty("platform").GetString());
            Assert.Contains("(X11; Linux x86_64)", userAgent);
            Assert.Contains(seen.GetProperty("brands").EnumerateArray(), b => b.GetProperty("brand").GetString() == "Google Chrome");
            var major = seen.GetProperty("high").GetProperty("uaFullVersion").GetString()!.Split('.')[0];
            Assert.Contains($"Chrome/{major}.0.0.0", userAgent);

            // A real WebGL, through the machine's own OpenGL.
            Assert.False(string.IsNullOrEmpty(seen.GetProperty("webglVendor").GetString()));
            Assert.DoesNotContain("SwiftShader", seen.GetProperty("webglRenderer").GetString() ?? "", StringComparison.Ordinal);

            // A desktop screen and a window with its tabs and address bar around the page the person sees.
            Assert.Equal(ChromiumSignInBrowser.ScreenWidth, seen.GetProperty("screen").GetProperty("width").GetInt32());
            Assert.Equal(ChromiumSignInBrowser.ScreenHeight, seen.GetProperty("screen").GetProperty("height").GetInt32());
            Assert.Equal(1280, seen.GetProperty("viewport").GetProperty("width").GetInt32());
            Assert.Equal(800, seen.GetProperty("viewport").GetProperty("height").GetInt32());
            Assert.True(seen.GetProperty("outer").GetProperty("height").GetInt32() > 800, seen.GetProperty("outer").GetRawText());

            // THE PERSON'S LOCALE: what their page sent.
            Assert.Equal(["de-DE", "de", "en-US", "en"], seen.GetProperty("languages").EnumerateArray().Select(l => l.GetString()));
            Assert.StartsWith("de-DE,de;q=0.9,en-US;q=0.8,en;q=0.7", Header(document, "accept-language"));
            Assert.Equal("Europe/Berlin", seen.GetProperty("timeZone").GetString());

            // THE IDENTITY SAVED WITH THE SESSION is what the site saw.
            identity = await browser.IdentityAsync(Ct);
            Assert.Equal("chrome", identity.Browser);
            Assert.Equal(userAgent, identity.UserAgent);
            Assert.Equal(seen.GetProperty("high").GetProperty("uaFullVersion").GetString(), identity.Version);
            Assert.Equal(Structured(seen.GetProperty("brands")), string.Join(", ", identity.UserAgentMetadata.Brands.Select(b => $"\"{b.Brand}\";v=\"{b.Version}\"")));
            Assert.Equal(
                Header(document, "sec-ch-ua-full-version-list"),
                string.Join(", ", identity.UserAgentMetadata.FullVersionList.Select(b => $"\"{b.Brand}\";v=\"{b.Version}\"")));
            Assert.Equal("Linux", identity.UserAgentMetadata.Platform);
            Assert.False(identity.UserAgentMetadata.Mobile);
            Assert.Equal("de-DE", identity.Locale);
            Assert.Equal(["de-DE", "de", "en-US", "en"], identity.Languages);
            Assert.Equal("Europe/Berlin", identity.TimezoneId);
            Assert.Equal(new SignInBrowserSize(1280, 800), identity.Viewport);
            Assert.Equal(new SignInBrowserSize(ChromiumSignInBrowser.ScreenWidth, ChromiumSignInBrowser.ScreenHeight), identity.Screen);
            Assert.Equal(1, identity.DeviceScaleFactor);

            // The envelope carries both, and reads back.
            var envelope = SignInSessionValue.Write(await browser.CaptureAsync(Ct), identity);
            var (state, read) = SignInSessionValue.Read(envelope);
            Assert.Equal(identity.UserAgent, read!.UserAgent);
            Assert.Contains("\"first\"", state);

            // CHROME'S OWN SANDBOX IS ON: every renderer under seccomp-BPF, in a PID namespace of its own.
            var renderers = Descendants(browser.ProcessId!.Value)
                .Where(pid => Read($"/proc/{pid}/cmdline").Contains("--type=renderer", StringComparison.Ordinal))
                .ToList();
            Assert.NotEmpty(renderers);
            foreach (var pid in renderers)
            {
                Assert.Contains("Seccomp:\t2", Read($"/proc/{pid}/status"));
                Assert.NotEqual(new FileInfo("/proc/self/ns/pid").LinkTarget, new FileInfo($"/proc/{pid}/ns/pid").LinkTarget);
            }

            Assert.DoesNotContain(Read($"/proc/{browser.ProcessId}/cmdline").Split('\0'), a => a is "--no-sandbox" or "--headless" or "--enable-automation");

            Assert.Equal(0, seen.GetProperty("counter").GetInt32());
            TestContext.Current.SendDiagnosticMessage(
                $"UA: {userAgent} | Sec-CH-UA: {Header(document, "sec-ch-ua")} | Sec-CH-UA-Full-Version-List: {Header(document, "sec-ch-ua-full-version-list")} | "
                + $"Sec-CH-UA-Platform: {Header(document, "sec-ch-ua-platform")} | webdriver: {seen.GetProperty("webdriver")} | "
                + $"WebGL: {seen.GetProperty("webglVendor")} / {seen.GetProperty("webglRenderer")} | plugins: {seen.GetProperty("plugins")}");
        }

        // The row's profile stays, the worker's alone; the display's cookie does not.
        var folder = Path.Combine(_profiles!, Row.Split('/')[0], Row.Split('/')[1]);
        Assert.True(Directory.Exists(Path.Combine(folder, "chrome")));
        Assert.False(Directory.Exists(Path.Combine(folder, "run")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(folder)!));

        // THE SECOND SIGN-IN FOR THE ROW IS THE SAME BROWSER COMING BACK: its cookie and local storage are there from the start.
        await using (await StartAsync())
        {
            var again = await ReportAsync(2);
            Assert.Equal(1, again.GetProperty("counter").GetInt32());
            Assert.Contains("first=run-1", again.GetProperty("cookie").GetString());
            Assert.Equal(identity.UserAgent, again.GetProperty("userAgent").GetString());
        }

        // Deleting the row removes its profile.
        SignInProfiles.Delete(_profiles!, Row);
        Assert.False(Directory.Exists(folder));
    }

    private static string Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>Every process under <paramref name="root"/>, by the parent each names in /proc.</summary>
    private static List<int> Descendants(int root)
    {
        var parents = new Dictionary<int, int>();
        foreach (var folder in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(folder), out var pid)) continue;
            var stat = Read(Path.Combine(folder, "stat"));
            var fields = stat.Length > 0 ? stat[(stat.LastIndexOf(')') + 2)..].Split(' ') : [];
            if (fields.Length > 1 && int.TryParse(fields[1], out var parent)) parents[pid] = parent;
        }

        var found = new List<int>();
        var queue = new Queue<int>([root]);
        while (queue.TryDequeue(out var next))
        {
            foreach (var child in parents.Where(p => p.Value == next).Select(p => p.Key))
            {
                found.Add(child);
                queue.Enqueue(child);
            }
        }

        return found;
    }

    [Fact]
    public async Task A_rows_profile_is_one_browsers_at_a_time()
    {
        Assert.SkipWhen(SignInBrowserChromiumTests.Chromium() is null, SignInBrowserChromiumTests.NoChrome);

        await using var first = await StartAsync();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(StartAsync);
        Assert.Equal(ChromiumSignInBrowser.InUseText, refused.Message);
    }
}
