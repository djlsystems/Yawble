using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// ONE PERSON'S SIGN-IN BROWSER: Google Chrome (stable, from the volume: <see cref="Find"/>) run HEADED
/// on its own virtual screen (<see cref="SignInDisplay"/>), so a site sees the browser a person uses
/// themselves - no "Headless" anywhere, no automation flag, a real window, WebGL, the person's
/// languages and time zone - with its OWN SANDBOX ON (never <c>--no-sandbox</c>, so it will not start
/// as root), driven over its pipe (<see cref="CdpPipe"/>), never a port. Each row has its own
/// profile, reused on every sign-in, so the site sees the same browser come back. Every top-level
/// navigation of every page is paused and judged by <see cref="SignInHosts"/>; the page on top is
/// screencast as JPEG frames; the person's input is dispatched to it; a save reads the cookies and
/// local storage, prunes them, and says which browser it was (<see cref="IdentityAsync"/>).
/// </summary>
/// <remarks>
/// NOTHING THE BROWSER SEES IS LOGGED. A notice names a page by its origin and path only (an OAuth
/// redirect carries its code in the query), and Chrome's stderr goes to /dev/null. Nothing is run in
/// the page's own world: the identity is read in an isolated one, invisible to the page's scripts.
/// </remarks>
public sealed partial class ChromiumSignInBrowser : IAsyncDisposable
{
    /// <summary>Where Google Chrome is on the volume when nothing says otherwise: one folder per version.</summary>
    public const string DefaultChromeFolder = "google-chrome";

    /// <summary>The screen every sign-in browser's display has: the commonest desktop screen.</summary>
    public const int ScreenWidth = 1920, ScreenHeight = 1080;

    /// <summary>How long a stop waits for Chromium to close before it is killed.</summary>
    public static readonly TimeSpan CloseBound = TimeSpan.FromSeconds(3);

    private readonly string _session;
    private readonly SignInBrowserLaunch _launch;
    private readonly SignInHosts _hosts;
    private readonly Func<StreamChunk, Task<bool>> _send;
    private readonly ConcurrentDictionary<string, Page> _pages = new(StringComparer.Ordinal);

    /// <summary>
    /// EACH ORIGIN'S LOCAL STORAGE AS LAST SEEN, kept from the moment a page loads it and through every
    /// change the page makes: Chrome reads an origin's storage only through a page that is on it, so
    /// an origin the person has passed through and left would otherwise be lost at Done.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, string>> _storage = new(StringComparer.Ordinal);
    private readonly Lock _storageGate = new();

    /// <summary>A top-level request's host, by its request id and its network id: where a redirect it was answered with came from.</summary>
    private readonly ConcurrentDictionary<string, string> _hops = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource<string> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;
    private CdpPipe? _cdp;
    private SignInDisplay? _display;
    private FileStream? _lock;
    private string? _folder;
    private bool _fresh;
    private string? _active;
    private bool _home;
    private Task? _disposing;
    private long _n;

    /// <summary>
    /// Chunks leave in the order they are numbered: control drops a chunk numbered below one it has
    /// already taken, so a notice overtaken by a frame would be lost.
    /// </summary>
    private readonly Channel<StreamChunk> _outbox = Channel.CreateUnbounded<StreamChunk>();
    private readonly Lock _outboxGate = new();
    private readonly Task _sending;

    /// <summary>Frames waiting beyond this are dropped: the next frame shows the screen as it is.</summary>
    private const int MaxWaiting = 8;

    private ChromiumSignInBrowser(string session, SignInBrowserLaunch launch, Func<StreamChunk, Task<bool>> send)
    {
        _session = session;
        _launch = launch;
        _hosts = new SignInHosts(launch.Hosts.Append(new Uri(launch.StartUrl).Host));
        _send = send;
        _sending = Task.Run(SendAllAsync);
    }

    /// <summary>Chrome's own process id while it runs: what a test walks to see its renderers sandboxed.</summary>
    public int? ProcessId => _process is { HasExited: false } process ? process.Id : null;

    /// <summary>The hosts this browser may go to, and the providers it has admitted so far.</summary>
    public SignInHosts Hosts => _hosts;

    /// <summary>Completes with a sentence for the person when the browser is over, however it ended.</summary>
    public Task<string> Ended => _ended.Task;

    /// <summary>
    /// The newest Google Chrome under <paramref name="root"/> (<c>&lt;root&gt;/&lt;version&gt;/chrome</c>, as
    /// <c>scripts/install-google-chrome.sh</c> lays it out), or null when none is installed. The root is
    /// <c>HARNESS_SIGN_IN_CHROME</c>, else <c>google-chrome</c> in the data root.
    /// </summary>
    public static string? Find(string? root = null)
    {
        root ??= Environment.GetEnvironmentVariable("HARNESS_SIGN_IN_CHROME") is { Length: > 0 } set
            ? set
            : Path.Combine(Environment.GetEnvironmentVariable("HARNESS_DATA_ROOT") is { Length: > 0 } data ? data : "/data", DefaultChromeFolder);
        if (!Directory.Exists(root)) return null;

        return Directory.EnumerateDirectories(root)
            .Select(d => (Path: d, Version: System.Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
            .Where(d => d.Version is not null)
            .OrderByDescending(d => d.Version)
            .Select(d => Path.Combine(d.Path, "chrome"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Starts Chrome at <paramref name="chrome"/> on a display of its own, with the row's profile under
    /// <paramref name="profileRoot"/> (or a fresh one), and opens the entry's Site URL. Throws, with
    /// nothing left running, when it cannot. <paramref name="extra"/> is more of Chrome's arguments,
    /// for a test (a host resolver rule that sends its made-up hosts to its own site).
    /// </summary>
    public static async Task<ChromiumSignInBrowser> StartAsync(
        string session, SignInBrowserLaunch launch, string chrome, string profileRoot,
        Func<StreamChunk, Task<bool>> send, IReadOnlyList<string>? extra = null, CancellationToken ct = default,
        string xvfb = SignInDisplay.DefaultXvfb)
    {
        var browser = new ChromiumSignInBrowser(session, launch, send);
        try
        {
            await browser.LaunchAsync(chrome, profileRoot, extra ?? [], xvfb, ct);
            return browser;
        }
        catch
        {
            await browser.DisposeAsync();
            throw;
        }
    }

    /// <summary>What the person sees when the row's browser is already open on another sign-in.</summary>
    public const string InUseText = "This entry's browser is already open for another sign-in. Try again once that one has closed.";

    private async Task LaunchAsync(string chrome, string profileRoot, IReadOnlyList<string> extra, string xvfb, CancellationToken ct)
    {
        SignInProfiles.MakePrivate(profileRoot);
        if (_launch.Profile is { } key)
        {
            if (!SignInProfiles.IsKey(key)) throw new InvalidOperationException("That is not a sign-in profile's name.");
            _folder = SignInProfiles.Folder(profileRoot, key);
            SignInProfiles.MakePrivate(Path.GetDirectoryName(_folder)!);
            try
            {
                _lock = SignInProfiles.Lock(_folder);
            }
            catch (IOException)
            {
                throw new InvalidOperationException(InUseText);
            }
        }
        else
        {
            _fresh = true;
            _folder = Path.Combine(profileRoot, "profile-" + Guid.NewGuid().ToString("N"));
        }

        SignInProfiles.MakePrivate(_folder);
        var data = Path.Combine(_folder, "chrome");
        var home = Path.Combine(_folder, "home");
        var run = Path.Combine(_folder, "run");
        foreach (var folder in new[] { data, home, run }) SignInProfiles.MakePrivate(folder);

        var languages = Languages(_launch.Languages);
        SignInProfiles.Prepare(data, languages);

        var screen = (Width: Math.Max(ScreenWidth, _launch.Width + 200), Height: Math.Max(ScreenHeight, _launch.Height + 200));
        _display = await SignInDisplay.StartAsync(run, screen.Width, screen.Height, xvfb, ct);

        // fd 3 is what Chrome reads commands from, fd 4 where it writes; the shell hands it this
        // process's stdin and stdout as those, then shuts its own stdio. The worker's environment -
        // its key among it - is not the browser's.
        var start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            WorkingDirectory = home,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("exec \"$0\" \"$@\" 3<&0 4>&1 0</dev/null 1>/dev/null 2>/dev/null");
        start.ArgumentList.Add(chrome);
        var window = (Width: _launch.Width, Height: _launch.Height + WindowChrome);
        var at = ((screen.Width - window.Width) / 2, (screen.Height - window.Height) / 2);
        foreach (var argument in extra.Concat(Arguments(data, window, at, languages[0]))) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        start.Environment["HOME"] = home;
        start.Environment["DISPLAY"] = _display.Display;
        start.Environment["XAUTHORITY"] = _display.Authority;
        start.Environment["LANG"] = "en_US.UTF-8";
        start.Environment["LANGUAGE"] = languages[0].Replace('-', '_');
        start.Environment["TZ"] = TimeZone(_launch.TimeZone);

        _process = Process.Start(start) ?? throw new InvalidOperationException("Chrome did not start.");
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => End("The browser closed.");

        _cdp = new CdpPipe(_process.StandardInput.BaseStream, _process.StandardOutput.BaseStream);
        _cdp.Event += OnEvent;
        _ = _cdp.Closed.ContinueWith(_ => End("The browser closed."), TaskScheduler.Default);

        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(TimeSpan.FromSeconds(30));

        await _cdp.SendAsync("Target.setDiscoverTargets", new { discover = true }, ct: bound.Token);
        await _cdp.SendAsync("Target.setAutoAttach", new { autoAttach = true, waitForDebuggerOnStart = true, flatten = true }, ct: bound.Token);

        // The page Chrome opened at launch was there before auto-attach: attach it as any other.
        var targets = await _cdp.SendAsync("Target.getTargets", ct: bound.Token);
        foreach (var target in targets.GetProperty("targetInfos").EnumerateArray())
        {
            if (target.GetProperty("type").GetString() == "page" && !target.GetProperty("attached").GetBoolean())
            {
                await _cdp.SendAsync("Target.attachToTarget", new { targetId = target.GetProperty("targetId").GetString(), flatten = true }, ct: bound.Token);
            }
        }
    }

    /// <summary>
    /// How much taller a window is than its page: tabs and the address bar. A first guess, made exact
    /// once the page is up (<see cref="FitAsync"/>), so the viewport is the size the person's page shows.
    /// </summary>
    private const int WindowChrome = 87;

    /// <summary>
    /// Chrome's arguments. ONLY WHAT A SITE CANNOT SEE, AND WHAT MAKES IT AN ORDINARY BROWSER:
    /// <list type="bullet">
    /// <item><c>--disable-blink-features=AutomationControlled</c>: a browser with a DevTools pipe says
    /// <c>navigator.webdriver = true</c>; a person's does not.</item>
    /// <item><c>--test-type</c>: no warning bar about that flag, which would take height from the page.</item>
    /// <item><c>--use-angle=gl --ignore-gpu-blocklist</c>: WebGL through the machine's own OpenGL (Mesa),
    /// as on a Linux desktop without a graphics card, rather than none at all.</item>
    /// </list>
    /// Never <c>--headless</c>, <c>--enable-automation</c>, <c>--no-sandbox</c> or a fixed device metric.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string profile, (int Width, int Height) window, (int X, int Y) at, string language) =>
    [
        "--remote-debugging-pipe",
        "--user-data-dir=" + profile,
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-blink-features=AutomationControlled",
        "--test-type",
        "--use-angle=gl",
        "--ignore-gpu-blocklist",
        "--disable-breakpad",
        "--disable-sync",
        "--password-store=basic",
        "--disable-dev-shm-usage",
        "--lang=" + language,
        $"--window-size={window.Width},{window.Height}",
        $"--window-position={at.X},{at.Y}",
        "about:blank",
    ];

    /// <summary>The person's languages as the browser takes them: language tags only, at most 10, en-US when none is.</summary>
    public static IReadOnlyList<string> Languages(IReadOnlyList<string>? languages)
    {
        var kept = (languages ?? [])
            .Where(l => l is { Length: > 0 and <= 35 } && LanguageTag().IsMatch(l))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
        return kept.Count > 0 ? kept : ["en-US", "en"];
    }

    /// <summary>The person's IANA time zone when this machine knows it, else UTC.</summary>
    public static string TimeZone(string? zone)
    {
        if (zone is not { Length: > 0 and <= 64 } || !ZoneName().IsMatch(zone) || zone.Contains("..", StringComparison.Ordinal)) return "UTC";
        return File.Exists(Path.Combine("/usr/share/zoneinfo", zone)) ? zone : "UTC";
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{1,8})*$")]
    private static partial System.Text.RegularExpressions.Regex LanguageTag();

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z][A-Za-z0-9_+-]*(/[A-Za-z0-9_+-]+)*$")]
    private static partial System.Text.RegularExpressions.Regex ZoneName();

    /// <summary>Applies one thing the person did to the page on top. Anything not understood is dropped.</summary>
    public void Input(SignInBrowserInput input)
    {
        if (_cdp is null || Active() is not { } page) return;

        var x = Math.Clamp(input.X, 0, _launch.Width);
        var y = Math.Clamp(input.Y, 0, _launch.Height);
        var modifiers = input.Modifiers & 0b1111;
        switch (input.Type)
        {
            case "mouse" when input.Event is "mousePressed" or "mouseReleased" or "mouseMoved":
                _cdp.Post("Input.dispatchMouseEvent", new
                {
                    type = input.Event,
                    x,
                    y,
                    modifiers,
                    button = input.Button is "left" or "middle" or "right" ? input.Button : "none",
                    clickCount = Math.Clamp(input.ClickCount, 0, 3),
                }, page.Session);
                break;

            case "wheel":
                _cdp.Post("Input.dispatchMouseEvent", new
                {
                    type = "mouseWheel", x, y, modifiers,
                    deltaX = Math.Clamp(input.DeltaX, -10_000, 10_000),
                    deltaY = Math.Clamp(input.DeltaY, -10_000, 10_000),
                }, page.Session);
                break;

            case "key" when input.Event is "keyDown" or "keyUp" && (input.Key?.Length ?? 0) <= 32 && (input.Code?.Length ?? 0) <= 32:
                var text = input.Event == "keyDown" && input.Text is { Length: > 0 and <= 4 } typed ? typed : null;
                _cdp.Post("Input.dispatchKeyEvent", new
                {
                    type = input.Event == "keyUp" ? "keyUp" : text is null ? "rawKeyDown" : "keyDown",
                    key = input.Key ?? "",
                    code = input.Code ?? "",
                    windowsVirtualKeyCode = Math.Clamp(input.KeyCode, 0, 255),
                    text = text ?? "",
                    unmodifiedText = text ?? "",
                    modifiers,
                }, page.Session);
                break;

            case "text" when input.Text is { Length: > 0 and <= SignInBrowserInput.MaxText }:
                _cdp.Post("Input.insertText", new { text = input.Text }, page.Session);
                break;

            case "back":
                _ = BackAsync(page);
                break;

            case "reload":
                _cdp.Post("Page.reload", new { }, page.Session);
                break;
        }
    }

    /// <summary>
    /// The storage state now - cookies and local storage - pruned to the entry's hosts and the
    /// providers passed through. The browser keeps running.
    /// </summary>
    public async Task<string> CaptureAsync(CancellationToken ct = default)
    {
        var cdp = _cdp ?? throw new InvalidOperationException("The browser is not running.");
        var cookies = await cdp.SendAsync("Storage.getCookies", ct: ct);

        // Every page open now is read afresh; an origin left earlier is as it was last seen.
        foreach (var page in _pages.Values) await SnapshotAsync(page, ct);

        List<SignInStorage.Origin> origins;
        lock (_storageGate)
        {
            origins = [.. _storage.Where(o => _hosts.KeepsOrigin(o.Key)).Select(o => new SignInStorage.Origin(o.Key, [.. o.Value]))];
        }

        return SignInStorage.Build(_hosts, cookies.GetProperty("cookies").EnumerateArray(), origins);
    }

    /// <summary>Reads the local storage of the origin <paramref name="page"/> is on, through that page, into what is kept.</summary>
    private async Task SnapshotAsync(Page page, CancellationToken ct = default)
    {
        if (_cdp is not { } cdp || page.Url is not { Scheme: "http" or "https" } url) return;

        var origin = url.GetLeftPart(UriPartial.Authority);
        try
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(CloseBound);
            var items = await cdp.SendAsync(
                "DOMStorage.getDOMStorageItems", new { storageId = new { securityOrigin = origin, isLocalStorage = true } }, page.Session, bound.Token);
            var read = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in items.GetProperty("entries").EnumerateArray()) read[entry[0].GetString() ?? ""] = entry[1].GetString() ?? "";
            lock (_storageGate) _storage[origin] = read;
        }
        catch (Exception exception) when (exception is IOException or CdpException or OperationCanceledException or KeyNotFoundException or InvalidOperationException)
        {
            // A page that moved on or closed meanwhile: its origin is as last seen.
        }
    }

    /// <summary>A change a page made to an origin's local storage (a <c>DOMStorage</c> event).</summary>
    private void StorageChanged(string method, JsonElement p)
    {
        if (!p.TryGetProperty("storageId", out var id) || !id.TryGetProperty("isLocalStorage", out var local) || local.ValueKind != JsonValueKind.True) return;
        var origin = id.TryGetProperty("securityOrigin", out var so) && so.GetString() is { Length: > 0 } named
            ? named
            : id.TryGetProperty("storageKey", out var sk) && sk.GetString() is { Length: > 0 } key ? key.Split('^')[0].TrimEnd('/') : null;
        if (origin is null) return;

        string? Text(string name) => p.TryGetProperty(name, out var v) ? v.GetString() : null;
        lock (_storageGate)
        {
            if (!_storage.TryGetValue(origin, out var items)) _storage[origin] = items = new Dictionary<string, string>(StringComparer.Ordinal);
            switch (method)
            {
                case "DOMStorage.domStorageItemAdded":
                case "DOMStorage.domStorageItemUpdated":
                    if (Text("key") is { } added) items[added] = Text("newValue") ?? "";
                    break;
                case "DOMStorage.domStorageItemRemoved":
                    if (Text("key") is { } removed) items.Remove(removed);
                    break;
                case "DOMStorage.domStorageItemsCleared":
                    items.Clear();
                    break;
            }
        }
    }

    private Page? Active()
    {
        lock (_gate) return _active is { } id ? _pages.GetValueOrDefault(id) : null;
    }

    private void OnEvent(string method, JsonElement p, string? session)
    {
        switch (method)
        {
            case "Target.attachedToTarget":
                var info = p.GetProperty("targetInfo");
                var child = p.GetProperty("sessionId").GetString()!;
                if (info.GetProperty("type").GetString() == "page")
                {
                    var page = new Page(
                        info.GetProperty("targetId").GetString()!, child,
                        info.TryGetProperty("openerId", out var opener) ? opener.GetString() : null);
                    _pages[page.Target] = page;
                    _ = Task.Run(() => SetUpAsync(page));
                }
                else
                {
                    _cdp?.Post("Runtime.runIfWaitingForDebugger", null, child);
                }

                break;

            case "Target.detachedFromTarget":
                var gone = _pages.Values.FirstOrDefault(pg => pg.Session == p.GetProperty("sessionId").GetString());
                if (gone is null) break;

                _pages.TryRemove(gone.Target, out _);
                string? next = null;
                lock (_gate)
                {
                    if (_active == gone.Target)
                    {
                        _active = null;
                        next = _pages.Values.OrderByDescending(pg => pg.Order).FirstOrDefault()?.Target;
                    }
                }

                if (next is not null) Activate(next);
                break;

            case "Target.targetInfoChanged":
                var changed = p.GetProperty("targetInfo");
                if (_pages.TryGetValue(changed.GetProperty("targetId").GetString()!, out var titled))
                {
                    titled.Title = Title(changed);
                    if (Active() == titled) Notice(PageNotice(titled));
                }

                break;

            case "Fetch.requestPaused":
                Paused(p, session);
                break;

            case "Page.frameNavigated":
                var frame = p.GetProperty("frame");
                if (frame.TryGetProperty("parentId", out _)) break;
                if (_pages.Values.FirstOrDefault(pg => pg.Session == session) is not { } navigated) break;
                if (!Uri.TryCreate(frame.GetProperty("url").GetString(), UriKind.Absolute, out var url)) break;

                navigated.Url = url;
                if (Active() == navigated) Notice(PageNotice(navigated));
                break;

            case "Page.loadEventFired":
                // Chromium does not always tell a title that came with the page: ask once it has loaded.
                if (_pages.Values.FirstOrDefault(pg => pg.Session == session) is { } loaded)
                {
                    _ = RetitleAsync(loaded);
                    _ = SnapshotAsync(loaded);
                }

                break;

            case "DOMStorage.domStorageItemAdded" or "DOMStorage.domStorageItemUpdated" or "DOMStorage.domStorageItemRemoved" or "DOMStorage.domStorageItemsCleared":
                StorageChanged(method, p);
                break;

            case "Page.screencastFrame":
                _cdp?.Post("Page.screencastFrameAck", new { sessionId = p.GetProperty("sessionId").GetInt32() }, session);
                if (Active() is { } top && top.Session == session)
                {
                    Enqueue(Convert.FromBase64String(p.GetProperty("data").GetString()!), null);
                }

                break;
        }
    }

    private async Task SetUpAsync(Page page)
    {
        var cdp = _cdp!;
        try
        {
            await cdp.SendAsync("Page.enable", session: page.Session);
            await cdp.SendAsync("DOMStorage.enable", session: page.Session);
            await cdp.SendAsync("Fetch.enable", new { patterns = new[] { new { urlPattern = "*", requestStage = "Request" } } }, page.Session);
            await cdp.SendAsync("Runtime.runIfWaitingForDebugger", session: page.Session);

            Activate(page.Target);

            bool first;
            lock (_gate)
            {
                first = !_home;
                _home = true;
            }

            if (!first) return;

            await FitAsync(page);
            await cdp.SendAsync("Page.navigate", new { url = _launch.StartUrl }, page.Session);
        }
        catch (Exception exception) when (exception is IOException or CdpException or OperationCanceledException)
        {
            // A page that closed while it was being set up is simply gone.
        }
    }

    /// <summary>
    /// Sizes the first page's window so its viewport is the launch's: the window keeps its tabs and
    /// address bar, as a person's does, and the page is exactly what the person's screen shows.
    /// </summary>
    private async Task FitAsync(Page page)
    {
        var cdp = _cdp!;
        using var bound = new CancellationTokenSource(CloseBound);
        var window = await cdp.SendAsync("Browser.getWindowForTarget", new { targetId = page.Target }, ct: bound.Token);
        var bounds = window.GetProperty("bounds");
        var metrics = await cdp.SendAsync("Page.getLayoutMetrics", session: page.Session, ct: bound.Token);
        var viewport = metrics.GetProperty("cssLayoutViewport");
        var width = bounds.GetProperty("width").GetInt32() + _launch.Width - viewport.GetProperty("clientWidth").GetInt32();
        var height = bounds.GetProperty("height").GetInt32() + _launch.Height - viewport.GetProperty("clientHeight").GetInt32();
        if (width == bounds.GetProperty("width").GetInt32() && height == bounds.GetProperty("height").GetInt32()) return;

        await cdp.SendAsync(
            "Browser.setWindowBounds",
            new { windowId = window.GetProperty("windowId").GetInt32(), bounds = new { width, height } }, ct: bound.Token);
    }

    /// <summary>Back one page in the page's own history, without running anything in the page.</summary>
    private async Task BackAsync(Page page)
    {
        if (_cdp is not { } cdp) return;
        try
        {
            using var bound = new CancellationTokenSource(CloseBound);
            var history = await cdp.SendAsync("Page.getNavigationHistory", session: page.Session, ct: bound.Token);
            var current = history.GetProperty("currentIndex").GetInt32();
            if (current <= 0) return;

            var entry = history.GetProperty("entries")[current - 1].GetProperty("id").GetInt32();
            await cdp.SendAsync("Page.navigateToHistoryEntry", new { entryId = entry }, page.Session, bound.Token);
        }
        catch (Exception exception) when (exception is IOException or CdpException or OperationCanceledException or KeyNotFoundException or IndexOutOfRangeException)
        {
        }
    }

    /// <summary>
    /// THE BROWSER THE SITE SEES, read from Chrome itself: its version and user agent, its client
    /// hints with their high-entropy values, the screen, the viewport and the scale, read in an
    /// isolated world of the page on top - the page's own scripts never see it run.
    /// </summary>
    public async Task<SignInBrowserIdentity> IdentityAsync(CancellationToken ct = default)
    {
        var cdp = _cdp ?? throw new InvalidOperationException("The browser is not running.");
        var page = Active() ?? throw new InvalidOperationException("The browser has no page open.");

        var version = await cdp.SendAsync("Browser.getVersion", ct: ct);
        var world = await cdp.SendAsync(
            "Page.createIsolatedWorld", new { frameId = page.Target, worldName = "sign-in-identity", grantUniveralAccess = false }, page.Session, ct);
        var read = await cdp.SendAsync(
            "Runtime.evaluate",
            new
            {
                expression = IdentityScript,
                contextId = world.GetProperty("executionContextId").GetInt32(),
                awaitPromise = true,
                returnByValue = true,
            },
            page.Session, ct);
        var seen = read.GetProperty("result").GetProperty("value");

        var product = version.GetProperty("product").GetString() ?? "";
        var hints = seen.GetProperty("hints");
        List<SignInBrowserBrand> Brands(string name) =>
        [
            .. hints.GetProperty(name).EnumerateArray()
                .Select(b => new SignInBrowserBrand(b.GetProperty("brand").GetString() ?? "", b.GetProperty("version").GetString() ?? "")),
        ];
        var brands = Brands("brands");
        string Hint(string name) => hints.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

        var languages = Languages(_launch.Languages);
        return new SignInBrowserIdentity(
            brands.Any(b => b.Brand == "Google Chrome") ? "chrome" : "chromium",
            product.Contains('/', StringComparison.Ordinal) ? product[(product.IndexOf('/', StringComparison.Ordinal) + 1)..] : product,
            version.GetProperty("userAgent").GetString() ?? "",
            new SignInUserAgentMetadata(
                brands, Brands("fullVersionList"), Hint("platform"), Hint("platformVersion"), Hint("architecture"), Hint("model"),
                hints.TryGetProperty("mobile", out var mobile) && mobile.ValueKind == JsonValueKind.True, Hint("bitness")),
            languages[0],
            languages,
            seen.GetProperty("timeZone").GetString() ?? "UTC",
            new SignInBrowserSize(seen.GetProperty("screenWidth").GetInt32(), seen.GetProperty("screenHeight").GetInt32()),
            new SignInBrowserSize(seen.GetProperty("width").GetInt32(), seen.GetProperty("height").GetInt32()),
            seen.GetProperty("scale").GetDouble());
    }

    private const string IdentityScript = """
        (async () => ({
          hints: await navigator.userAgentData.getHighEntropyValues(
            ['architecture', 'bitness', 'model', 'platformVersion', 'fullVersionList']),
          timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
          screenWidth: screen.width, screenHeight: screen.height,
          width: innerWidth, height: innerHeight, scale: devicePixelRatio,
        }))()
        """;

    /// <summary>Puts <paramref name="target"/> on top: its screen is the one the person sees.</summary>
    private void Activate(string target)
    {
        string? previous;
        lock (_gate)
        {
            previous = _active;
            _active = target;
        }

        if (previous is not null && previous != target && _pages.TryGetValue(previous, out var old))
        {
            _cdp?.Post("Page.stopScreencast", null, old.Session);
        }

        if (_pages.TryGetValue(target, out var page))
        {
            _cdp?.Post("Page.startScreencast", new { format = "jpeg", quality = 70, maxWidth = _launch.Width, maxHeight = _launch.Height, everyNthFrame = 1 }, page.Session);
            Notice(PageNotice(page));
        }
    }

    private void Paused(JsonElement p, string? session)
    {
        var id = p.GetProperty("requestId").GetString()!;
        var request = p.GetProperty("request");
        var page = _pages.Values.FirstOrDefault(pg => pg.Session == session);
        if (page is null || !Uri.TryCreate(request.GetProperty("url").GetString(), UriKind.Absolute, out var url))
        {
            _cdp?.Post("Fetch.failRequest", new { requestId = id, errorReason = "BlockedByClient" }, session);
            return;
        }

        var topLevel = p.GetProperty("resourceType").GetString() == "Document" && p.GetProperty("frameId").GetString() == page.Target;
        if (!topLevel)
        {
            if (_hosts.Part(url) == SignInHosts.Verdict.Refuse) _cdp?.Post("Fetch.failRequest", new { requestId = id, errorReason = "BlockedByClient" }, session);
            else _cdp?.Post("Fetch.continueRequest", new { requestId = id }, session);
            return;
        }

        // A server's redirect: the host that answered it, by the request it answered (or its network id).
        var network = p.TryGetProperty("networkId", out var n) ? n.GetString() : null;
        var redirectFrom = p.TryGetProperty("redirectedRequestId", out var r) && r.GetString() is { } previous && _hops.TryGetValue(previous, out var was)
            ? was
            : network is not null && _hops.TryGetValue("n:" + network, out var seen) ? seen : null;

        var verdict = _hosts.TopLevel(url, From(page), request.TryGetProperty("postData", out var form) ? form.GetString() : null, redirectFrom);
        if (verdict == SignInHosts.Verdict.Refuse)
        {
            if (network is not null) _hops.TryRemove("n:" + network, out _);

            // NO CONTENT, NOT AN ERROR: a navigation answered 204 does not happen, so the page stays on
            // the last allowed page, alive, rather than becoming Chrome's error page. A new page that
            // had nothing allowed on it yet is closed, back to its opener.
            _cdp?.Post("Fetch.fulfillRequest", new { requestId = id, responseCode = 204, responseHeaders = Array.Empty<object>(), body = "" }, session);
            Notice(new SignInBrowserNotice("refused", Host: url.Host));
            if (page.Url is not { Scheme: "http" or "https" } && page.Opener is not null) _cdp?.Post("Target.closeTarget", new { targetId = page.Target });
            return;
        }

        if (_hops.Count > 512) _hops.Clear();
        _hops[id] = url.Host;
        if (network is not null) _hops["n:" + network] = url.Host;

        _cdp?.Post("Fetch.continueRequest", new { requestId = id }, session);
        if (verdict == SignInHosts.Verdict.Admit) Notice(new SignInBrowserNotice("provider", Host: url.Host));
    }

    /// <summary>The host a page's next navigation is made from: its own, or for a new page its opener's.</summary>
    private string? From(Page page)
    {
        if (page.Url is { Scheme: "http" or "https" } url) return url.Host;
        return page.Opener is { } opener && _pages.TryGetValue(opener, out var parent) && parent.Url is { Scheme: "http" or "https" } at ? at.Host : null;
    }

    /// <summary>
    /// A page's title, or null while it has none: Chromium then titles it by its address, whose query
    /// may carry an OAuth code.
    /// </summary>
    private static string? Title(JsonElement info)
    {
        var title = info.TryGetProperty("title", out var t) ? t.GetString() : null;
        var url = info.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(title)) return null;

        var bare = url.Contains("://", StringComparison.Ordinal) ? url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..] : url;
        // Chromium's own title for an untitled page is its address, scheme dropped, often cut short.
        return title == url || bare.StartsWith(title, StringComparison.Ordinal) || url.EndsWith(title, StringComparison.Ordinal) ? null : title;
    }

    private async Task RetitleAsync(Page page)
    {
        if (_cdp is not { } cdp) return;
        try
        {
            using var bound = new CancellationTokenSource(CloseBound);
            var info = await cdp.SendAsync("Target.getTargetInfo", new { targetId = page.Target }, ct: bound.Token);
            var title = Title(info.GetProperty("targetInfo"));
            if (title == page.Title) return;

            page.Title = title;
            if (Active() == page) Notice(PageNotice(page));
        }
        catch (Exception exception) when (exception is IOException or CdpException or OperationCanceledException or KeyNotFoundException)
        {
        }
    }

    private static SignInBrowserNotice PageNotice(Page page) =>
        new("page", page.Url is { Scheme: "http" or "https" } url ? url.GetLeftPart(UriPartial.Path) : null, page.Title);

    private void Notice(SignInBrowserNotice notice) =>
        Enqueue(null, JsonSerializer.Serialize(notice, NoticeJson));

    private static readonly JsonSerializerOptions NoticeJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Numbers a frame or notice and queues it. A frame is dropped while too many wait; a notice never is.</summary>
    private void Enqueue(byte[]? frame, string? text)
    {
        lock (_outboxGate)
        {
            if (frame is not null && _outbox.Reader.Count >= MaxWaiting) return;
            _outbox.Writer.TryWrite(new StreamChunk(_session, ++_n, Text: text, Bytes: frame));
        }
    }

    /// <summary>Sends each chunk in turn. One that cannot be sent is dropped: the next frame shows the screen as it is.</summary>
    private async Task SendAllAsync()
    {
        await foreach (var chunk in _outbox.Reader.ReadAllAsync())
        {
            try
            {
                await _send(chunk);
            }
            catch (Exception)
            {
            }
        }
    }

    private void End(string reason) => _ended.TrySetResult(reason);

    /// <summary>Closes Chromium, kills it if it does not close, and removes its profile. Once, however often it is asked.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate) _disposing ??= DisposeOnceAsync();
        return new ValueTask(_disposing);
    }

    private async Task DisposeOnceAsync()
    {
        _outbox.Writer.TryComplete();
        if (_cdp is { } cdp)
        {
            try
            {
                using var bound = new CancellationTokenSource(CloseBound);
                await cdp.SendAsync("Browser.close", ct: bound.Token);
            }
            catch (Exception exception) when (exception is IOException or CdpException or OperationCanceledException)
            {
            }
        }

        if (_process is { } process)
        {
            try
            {
                using var bound = new CancellationTokenSource(CloseBound);
                await process.WaitForExitAsync(bound.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { await process.WaitForExitAsync(CancellationToken.None); } catch (InvalidOperationException) { }
            }

            process.Dispose();
        }

        if (_cdp is { } pipe) await pipe.DisposeAsync();
        if (_display is { } display) await display.DisposeAsync();

        if (_folder is { } folder)
        {
            // The row's profile stays for its next sign-in; its run folder (the display's cookie) and a
            // fresh profile do not.
            SignInProfiles.Remove(_fresh ? folder : Path.Combine(folder, "run"));
        }

        _lock?.Dispose();

        try
        {
            await _sending.WaitAsync(CloseBound);
        }
        catch (TimeoutException)
        {
        }

        End("The browser closed.");
    }

    /// <summary>One page (a tab or a sign-in popup): its target, its flat session, its opener and where it is.</summary>
    private sealed class Page(string target, string session, string? opener)
    {
        private static long _order;

        public string Target { get; } = target;

        public string Session { get; } = session;

        public string? Opener { get; } = opener;

        public long Order { get; } = Interlocked.Increment(ref _order);

        public Uri? Url { get; set; }

        public string? Title { get; set; }
    }
}
