using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// ONE PERSON'S SIGN-IN BROWSER: a headless Chromium (Playwright's <c>chrome-headless-shell</c> on the
/// data volume) with its OWN SANDBOX ON - never <c>--no-sandbox</c>, so it will not start as root - and
/// driven over its pipe (<see cref="CdpPipe"/>), never a port. Every top-level navigation of every page
/// is paused and judged by <see cref="SignInHosts"/>; the page on top is screencast as JPEG frames; the
/// person's input is dispatched to it; a save reads the cookies and local storage and prunes them.
/// </summary>
/// <remarks>
/// NOTHING THE BROWSER SEES IS LOGGED. A notice names a page by its origin and path only (an OAuth
/// redirect carries its code in the query), and Chromium's stderr goes to /dev/null.
/// </remarks>
public sealed class ChromiumSignInBrowser : IAsyncDisposable
{
    /// <summary>Where Playwright puts its browsers on the volume when nothing says otherwise.</summary>
    public const string DefaultBrowsersPath = "/data/ms-playwright";

    /// <summary>How long a stop waits for Chromium to close before it is killed.</summary>
    public static readonly TimeSpan CloseBound = TimeSpan.FromSeconds(3);

    private readonly string _session;
    private readonly SignInBrowserLaunch _launch;
    private readonly SignInHosts _hosts;
    private readonly Func<StreamChunk, Task<bool>> _send;
    private readonly ConcurrentDictionary<string, Page> _pages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _origins = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource<string> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;
    private CdpPipe? _cdp;
    private string? _profile;
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

    /// <summary>The hosts this browser may go to, and the providers it has admitted so far.</summary>
    public SignInHosts Hosts => _hosts;

    /// <summary>Completes with a sentence for the person when the browser is over, however it ended.</summary>
    public Task<string> Ended => _ended.Task;

    /// <summary>
    /// The newest <c>chrome-headless-shell</c> under <paramref name="browsersPath"/> (Playwright's
    /// <c>PLAYWRIGHT_BROWSERS_PATH</c>), or null when none is installed.
    /// </summary>
    public static string? Find(string? browsersPath)
    {
        var root = string.IsNullOrWhiteSpace(browsersPath) ? DefaultBrowsersPath : browsersPath;
        if (!Directory.Exists(root)) return null;

        return Directory.EnumerateDirectories(root, "chromium_headless_shell-*")
            .OrderByDescending(d => int.TryParse(Path.GetFileName(d).Split('-').Last(), out var revision) ? revision : 0)
            .SelectMany(d => Directory.EnumerateDirectories(d, "chrome-headless-shell-*"))
            .Select(d => Path.Combine(d, "chrome-headless-shell"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Starts Chromium at <paramref name="chromium"/> with a fresh profile inside
    /// <paramref name="profileRoot"/>, and opens the entry's Site URL. Throws, with nothing left
    /// running, when it cannot. <paramref name="extra"/> is more of Chromium's arguments, for a test
    /// (a host resolver rule that sends its made-up hosts to its own site).
    /// </summary>
    public static async Task<ChromiumSignInBrowser> StartAsync(
        string session, SignInBrowserLaunch launch, string chromium, string profileRoot,
        Func<StreamChunk, Task<bool>> send, IReadOnlyList<string>? extra = null, CancellationToken ct = default)
    {
        var browser = new ChromiumSignInBrowser(session, launch, send);
        try
        {
            await browser.LaunchAsync(chromium, profileRoot, extra ?? [], ct);
            return browser;
        }
        catch
        {
            await browser.DisposeAsync();
            throw;
        }
    }

    private async Task LaunchAsync(string chromium, string profileRoot, IReadOnlyList<string> extra, CancellationToken ct)
    {
        Directory.CreateDirectory(profileRoot);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(profileRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _profile = Path.Combine(profileRoot, "profile-" + Guid.NewGuid().ToString("N"));
        var created = Directory.CreateDirectory(_profile);
        if (!OperatingSystem.IsWindows()) created.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        // fd 3 is what Chromium reads commands from, fd 4 where it writes; the shell hands it this
        // process's stdin and stdout as those, then shuts its own stdio. The worker's environment -
        // its key among it - is not the browser's.
        var start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            WorkingDirectory = _profile,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("exec \"$0\" \"$@\" 3<&0 4>&1 0</dev/null 1>/dev/null 2>/dev/null");
        start.ArgumentList.Add(chromium);
        foreach (var argument in extra.Concat(Arguments(_profile, _launch.Width, _launch.Height))) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        start.Environment["HOME"] = _profile;
        start.Environment["LANG"] = "en_US.UTF-8";

        _process = Process.Start(start) ?? throw new InvalidOperationException("Chromium did not start.");
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => End("The browser closed.");

        _cdp = new CdpPipe(_process.StandardInput.BaseStream, _process.StandardOutput.BaseStream);
        _cdp.Event += OnEvent;
        _ = _cdp.Closed.ContinueWith(_ => End("The browser closed."), TaskScheduler.Default);

        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(TimeSpan.FromSeconds(30));

        await _cdp.SendAsync("Target.setDiscoverTargets", new { discover = true }, ct: bound.Token);
        await _cdp.SendAsync("Target.setAutoAttach", new { autoAttach = true, waitForDebuggerOnStart = true, flatten = true }, ct: bound.Token);

        // The page Chromium opened at launch was there before auto-attach: attach it as any other.
        var targets = await _cdp.SendAsync("Target.getTargets", ct: bound.Token);
        foreach (var target in targets.GetProperty("targetInfos").EnumerateArray())
        {
            if (target.GetProperty("type").GetString() == "page" && !target.GetProperty("attached").GetBoolean())
            {
                await _cdp.SendAsync("Target.attachToTarget", new { targetId = target.GetProperty("targetId").GetString(), flatten = true }, ct: bound.Token);
            }
        }
    }

    /// <summary>Chromium's arguments: headless, its own sandbox on, no first-run, sync, extensions or background traffic.</summary>
    public static IReadOnlyList<string> Arguments(string profile, int width, int height) =>
    [
        "--remote-debugging-pipe",
        "--user-data-dir=" + profile,
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-background-networking",
        "--disable-component-update",
        "--disable-default-apps",
        "--disable-extensions",
        "--disable-sync",
        "--disable-breakpad",
        "--metrics-recording-only",
        "--password-store=basic",
        "--use-mock-keychain",
        "--disable-dev-shm-usage",
        "--hide-scrollbars",
        "--mute-audio",
        $"--window-size={width},{height}",
        "about:blank",
    ];

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
                _cdp.Post("Runtime.evaluate", new { expression = "history.back()" }, page.Session);
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

        var origins = new List<SignInStorage.Origin>();
        if (Active() is { } page)
        {
            await cdp.SendAsync("DOMStorage.enable", session: page.Session, ct: ct);
            foreach (var origin in _origins.Keys.Where(_hosts.KeepsOrigin))
            {
                try
                {
                    var items = await cdp.SendAsync(
                        "DOMStorage.getDOMStorageItems", new { storageId = new { securityOrigin = origin, isLocalStorage = true } }, page.Session, ct);
                    origins.Add(new SignInStorage.Origin(origin, [.. items.GetProperty("entries").EnumerateArray()
                        .Select(e => new KeyValuePair<string, string>(e[0].GetString() ?? "", e[1].GetString() ?? ""))]));
                }
                catch (CdpException)
                {
                    // An origin whose storage Chromium no longer has is simply not kept.
                }
            }
        }

        return SignInStorage.Build(_hosts, cookies.GetProperty("cookies").EnumerateArray(), origins);
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
                if (url.Scheme is "http" or "https") _origins.TryAdd(url.GetLeftPart(UriPartial.Authority), 0);
                if (Active() == navigated) Notice(PageNotice(navigated));
                break;

            case "Page.loadEventFired":
                // Chromium does not always tell a title that came with the page: ask once it has loaded.
                if (_pages.Values.FirstOrDefault(pg => pg.Session == session) is { } loaded) _ = RetitleAsync(loaded);
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
            await cdp.SendAsync("Fetch.enable", new { patterns = new[] { new { urlPattern = "*", requestStage = "Request" } } }, page.Session);
            await cdp.SendAsync(
                "Emulation.setDeviceMetricsOverride",
                new { width = _launch.Width, height = _launch.Height, deviceScaleFactor = 1, mobile = false }, page.Session);
            await cdp.SendAsync("Runtime.runIfWaitingForDebugger", session: page.Session);

            Activate(page.Target);

            bool first;
            lock (_gate)
            {
                first = !_home;
                _home = true;
            }

            if (first) await cdp.SendAsync("Page.navigate", new { url = _launch.StartUrl }, page.Session);
        }
        catch (Exception exception) when (exception is IOException or CdpException or OperationCanceledException)
        {
            // A page that closed while it was being set up is simply gone.
        }
    }

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
        var id = p.GetProperty("requestId").GetString();
        var request = p.GetProperty("request");
        var page = _pages.Values.FirstOrDefault(pg => pg.Session == session);
        if (page is null || !Uri.TryCreate(request.GetProperty("url").GetString(), UriKind.Absolute, out var url))
        {
            _cdp?.Post("Fetch.failRequest", new { requestId = id, errorReason = "BlockedByClient" }, session);
            return;
        }

        var topLevel = p.GetProperty("resourceType").GetString() == "Document" && p.GetProperty("frameId").GetString() == page.Target;
        var verdict = topLevel
            ? _hosts.TopLevel(url, From(page), request.TryGetProperty("postData", out var form) ? form.GetString() : null)
            : _hosts.Part(url);

        if (verdict == SignInHosts.Verdict.Refuse)
        {
            _cdp?.Post("Fetch.failRequest", new { requestId = id, errorReason = "BlockedByClient" }, session);
            if (topLevel) Notice(new SignInBrowserNotice("refused", Host: url.Host));
            return;
        }

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

        if (_profile is { } profile && Directory.Exists(profile))
        {
            try
            {
                // RECURSIVE DELETE REVIEWED: agents cannot write here. A sign-in browser's profile: the worker makes it 0700 as itself, inside a 0700 folder of its own.
                Directory.Delete(profile, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

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
