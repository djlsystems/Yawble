using System.Collections.Concurrent;
using System.Text.Json;
using Harness.Contracts;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// THE SIGN-IN BROWSERS ON THIS WORKER, by session: started, typed into, saved and stopped as control
/// says. Each browser's screen is a stream of its session's id; its end is one sequenced event,
/// published once, after its last frame was offered. A save answers with the storage state, which the
/// connection seals; nothing here logs it, or anything the browser showed.
/// </summary>
public sealed class WorkerSignInBrowsers(
    IRunStreamSink sink,
    Func<WorkerEvent, Task> publish,
    string? browsersPath = null,
    string? profileRoot = null,
    ILogger? log = null)
{
    /// <summary>What a worker with no Chromium on its volume says.</summary>
    public const string NoChromiumText =
        "This worker has no headless Chromium installed (Playwright's chrome-headless-shell under PLAYWRIGHT_BROWSERS_PATH).";

    private readonly ConcurrentDictionary<string, Open> _open = new(StringComparer.Ordinal);
    private readonly string _profileRoot = profileRoot ?? DefaultProfileRoot();

    /// <summary>The sessions open on this worker.</summary>
    public IReadOnlyCollection<string> Sessions => [.. _open.Keys];

    /// <summary>
    /// Where profiles go: the worker's own state folder when it has one (in the container, agents
    /// cannot reach it), else a folder of this user's own in the temp folder.
    /// </summary>
    public static string DefaultProfileRoot() =>
        Environment.GetEnvironmentVariable("HARNESS_WORKER_STATE_DIR") is { Length: > 0 } state
            ? Path.Combine(state, "sign-in")
            : Path.Combine(Path.GetTempPath(), "harness-sign-in-" + Environment.UserName);

    /// <summary>Starts a browser on its own task; a start that fails ends the session with a sentence.</summary>
    public void Start(StartSignInBrowser start)
    {
        if (_open.ContainsKey(start.Session)) return;

        var open = new Open();
        if (!_open.TryAdd(start.Session, open)) return;

        _ = Task.Run(async () =>
        {
            string reason;
            try
            {
                var chromium = ChromiumSignInBrowser.Find(browsersPath ?? Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH"))
                    ?? throw new InvalidOperationException(NoChromiumText);

                open.Browser = await ChromiumSignInBrowser.StartAsync(start.Session, start.Launch, chromium, _profileRoot, SendAsync);
                if (open.Stopping) await open.Browser.DisposeAsync();
                reason = await open.Browser.Ended;
            }
            catch (Exception exception)
            {
                // Chromium's or this worker's sentence about the start; never a page's content.
                log?.LogWarning("Sign-in browser {Session} did not start: {Message}", start.Session, exception.Message);
                reason = exception is InvalidOperationException ? exception.Message : "The browser could not start on this worker.";
            }

            if (open.Browser is { } browser) await browser.DisposeAsync();
            _open.TryRemove(start.Session, out _);
            await publish(new SignInBrowserEnded(start.Session, reason));
        });
    }

    /// <summary>One input of the person's, as the page sent it: JSON of <see cref="SignInBrowserInput"/>.</summary>
    public bool Input(StreamInput input)
    {
        if (!_open.TryGetValue(input.Stream, out var open)) return false;
        if (open.Browser is not { } browser || input.Bytes.Length > 4 * SignInBrowserInput.MaxText) return true;

        try
        {
            if (JsonSerializer.Deserialize<SignInBrowserInput>(input.Bytes, InputJson) is { } parsed) browser.Input(parsed);
        }
        catch (JsonException)
        {
        }

        return true;
    }

    /// <summary>Captures the storage state and answers it under the save's request, on its own task.</summary>
    public void Save(SaveSignInBrowser save) =>
        _ = Task.Run(async () =>
        {
            SignInBrowserSaved answer;
            if (!_open.TryGetValue(save.Session, out var open) || open.Browser is not { } browser)
            {
                answer = new SignInBrowserSaved(save.Session, save.Request, null, [], [], "The browser is not running.");
            }
            else
            {
                try
                {
                    using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var state = await browser.CaptureAsync(bound.Token);
                    answer = new SignInBrowserSaved(
                        save.Session, save.Request, new SignInStorageState(state), browser.Hosts.Kept, browser.Hosts.Providers, null);
                }
                catch (Exception exception) when (exception is IOException or CdpException or OperationCanceledException or JsonException)
                {
                    answer = new SignInBrowserSaved(save.Session, save.Request, null, [], [], "The browser's session could not be read.");
                }
            }

            await publish(answer);
        });

    /// <summary>Stops a browser; its end follows from its start's task.</summary>
    public async Task StopAsync(string session)
    {
        if (!_open.TryGetValue(session, out var open)) return;

        open.Stopping = true;
        if (open.Browser is { } browser) await browser.DisposeAsync();
    }

    /// <summary>Stops every browser: the worker is stopping.</summary>
    public async Task StopAllAsync()
    {
        foreach (var session in _open.Keys) await StopAsync(session);
    }

    private async Task<bool> SendAsync(StreamChunk chunk)
    {
        try
        {
            return await sink.StreamAsync(chunk);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web);

    private sealed class Open
    {
        public volatile ChromiumSignInBrowser? Browser;

        public volatile bool Stopping;
    }
}
