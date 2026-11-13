using System.Diagnostics;

namespace Yawble.Core.Credentials;

public sealed class GrokKeeperOptions
{
    /// <summary>xAI's grok binary, by absolute path: the one the person installed (detected), used when
    /// <see cref="ResolveBinary"/> is not set or finds none.</summary>
    public required string GrokBinary { get; init; }

    /// <summary>Finds the person's grok again at each use (it may have been updated or moved); null = not installed.</summary>
    public Func<string?>? ResolveBinary { get; init; }

    /// <summary>A non-inference command first: whether it refreshes the session is assumed, not documented.</summary>
    public IReadOnlyList<string> RefreshArgs { get; init; } = ["models"];

    /// <summary>If the first did not refresh: a one-turn headless prompt, which costs a little quota.</summary>
    public IReadOnlyList<string> FallbackArgs { get; init; } = ["--no-auto-update", "-p", "Reply with OK.", "--max-turns", "1"];

    /// <summary>Refresh this long before expires_at.</summary>
    public TimeSpan RefreshAhead { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>When the session has no known expiry, look again after this long.</summary>
    public TimeSpan IdleRecheck { get; init; } = TimeSpan.FromMinutes(30);

    public Action<string>? Log { get; init; }
}

public sealed record GrokRefreshResult(bool Valid, bool Changed, string Detail, GrokSession? Session)
{
    public override string ToString() => $"GrokRefreshResult(valid {Valid}, changed {Changed}, {Detail})";
}

/// <summary>
/// The Grok keeper: runs xAI's OWN grok on the host, against the app-private host Grok home, so that
/// xAI's CLI refreshes the person's session itself against auth.x.ai. The app makes no OAuth call of its own and is
/// never an xAI OAuth client. The keeper is the only process that touches the refresh token, so no copy of the session
/// can rotate the others out.
///
/// Refreshes are single-flight: concurrent requests (the timer, and the gateway after an upstream 401) share one run.
/// The child gets a built environment, never the core's: in particular no XAI_API_KEY can leak in and outrank the
/// session.
/// </summary>
public sealed class GrokKeeper : IAsyncDisposable
{
    private readonly GrokHome _home;
    private readonly GrokKeeperOptions _options;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _stop = new();
    private Task<GrokRefreshResult>? _inFlight;
    private Task? _loop;

    public GrokKeeper(GrokHome home, GrokKeeperOptions options, TimeProvider? clock = null)
    {
        _home = home;
        _options = options;
        _clock = clock ?? TimeProvider.System;
    }

    public GrokHome Home => _home;

    /// <summary>How many times the keeper actually ran grok (for the card and the tests).</summary>
    public int Runs { get; private set; }

    /// <summary>The current session if it is valid now, otherwise null.</summary>
    public GrokSession? CurrentSession()
    {
        var session = _home.ReadSession();
        return session is null || session.ExpiredAt(_clock.GetUtcNow()) ? null : session;
    }

    /// <summary>Starts the background loop that refreshes before expiry.</summary>
    public void Start() => _loop ??= Task.Run(LoopAsync);

    /// <summary>Asks for a refresh without waiting (the gateway after an upstream 401).</summary>
    public void RequestRefresh(bool force = true) => _ = RefreshAsync(force);

    /// <summary>
    /// Runs grok to refresh unless the session is still good for <see cref="GrokKeeperOptions.RefreshAhead"/>
    /// (or <paramref name="force"/>, after the upstream refused it). Joins a refresh already in flight.
    /// </summary>
    public Task<GrokRefreshResult> RefreshAsync(bool force = false)
    {
        lock (_lock)
        {
            if (_inFlight is { IsCompleted: false } running) return running;
            return _inFlight = Task.Run(() => DoRefreshAsync(force));
        }
    }

    private async Task<GrokRefreshResult> DoRefreshAsync(bool force)
    {
        var before = _home.ReadSession();
        if (before is null)
        {
            return new GrokRefreshResult(false, false, "not signed in: run `grok login` in the host terminal", null);
        }
        if (!force && !before.ExpiredAt(_clock.GetUtcNow(), _options.RefreshAhead))
        {
            return new GrokRefreshResult(true, false, "session still fresh", before);
        }
        foreach (var args in new[] { _options.RefreshArgs, _options.FallbackArgs })
        {
            if (args.Count == 0) continue;
            var outcome = await RunGrokAsync(args);
            var after = _home.ReadSession();
            var changed = after is not null && (after.AccessToken != before.AccessToken || after.ExpiresAt != before.ExpiresAt);
            var fresh = after is not null && !after.ExpiredAt(_clock.GetUtcNow(), _options.RefreshAhead);
            _options.Log?.Invoke($"grok keeper: `grok {string.Join(' ', args)}` {outcome}; session {(changed ? "refreshed" : "unchanged")}");
            if (changed && fresh) return new GrokRefreshResult(true, true, $"refreshed by `grok {args[0]}`", after);
        }
        var final = _home.ReadSession();
        var valid = final is not null && !final.ExpiredAt(_clock.GetUtcNow());
        return new GrokRefreshResult(valid && !force, false,
            valid && !force ? "session unchanged and still valid" : "Grok login expired: sign in again", final);
    }

    private async Task<string> RunGrokAsync(IReadOnlyList<string> args)
    {
        Runs++;
        var psi = StartInfo(args);
        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("grok did not start");
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(_options.CommandTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return "timed out";
            }
            await Task.WhenAll(stdout, stderr);
            return $"exited {process.ExitCode}";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "could not start: " + ex.Message;
        }
    }

    /// <summary>
    /// The process the keeper runs: the person's grok by absolute path, in the host Grok home, with an environment
    /// built from scratch (the person's PATH for the OS basics, nothing else of the core's).
    /// </summary>
    public ProcessStartInfo StartInfo(IReadOnlyList<string> args)
    {
        // The person's own home is not a working folder: grok runs in an empty temp folder then.
        var workingDirectory = _home.Path;
        if (_home.PersonsOwn)
        {
            workingDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yawble-grok-renew");
            Directory.CreateDirectory(workingDirectory);
        }
        var psi = new ProcessStartInfo(_options.ResolveBinary?.Invoke() ?? _options.GrokBinary)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var (k, v) in Environment(_home)) psi.Environment[k] = v;
        return psi;
    }

    /// <summary>
    /// The command for the one-time sign-in in a host terminal pane: xAI's own `grok login` (browser via auth.x.ai,
    /// or --device-auth) against the host Grok home. The app drives no OAuth flow itself.
    /// </summary>
    public ProcessStartInfo LoginStartInfo(bool deviceAuth)
    {
        var psi = StartInfo(deviceAuth ? ["login", "--device-auth"] : ["login"]);
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = false;
        return psi;
    }

    public static IReadOnlyDictionary<string, string> Environment(GrokHome home)
    {
        // The app-private home doubles as HOME; the person's own home keeps the person's HOME (grok's other files).
        var profile = home.PersonsOwn ? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile) : home.Path;
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GROK_HOME"] = home.Path,
            ["HOME"] = profile,
            ["GROK_DISABLE_AUTOUPDATER"] = "1",
            ["GROK_TELEMETRY_ENABLED"] = "false",
            ["DISABLE_ERROR_REPORTING"] = "1",
            ["PATH"] = System.Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
        };
        if (OperatingSystem.IsWindows())
        {
            env["USERPROFILE"] = profile;
            foreach (var name in new[] { "SystemRoot", "TEMP", "TMP", "LOCALAPPDATA", "APPDATA" })
            {
                if (System.Environment.GetEnvironmentVariable(name) is { } value) env[name] = value;
            }
        }
        return env;
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var session = _home.ReadSession();
            var wait = session?.ExpiresAt is { } at
                ? at - _options.RefreshAhead - _clock.GetUtcNow()
                : _options.IdleRecheck;
            if (wait < TimeSpan.FromSeconds(5)) wait = TimeSpan.FromSeconds(5);
            if (wait > _options.IdleRecheck) wait = _options.IdleRecheck;
            try
            {
                await Task.Delay(wait, _clock, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (_home.ReadSession() is not null)
            {
                var result = await RefreshAsync();
                _options.Log?.Invoke($"grok keeper: {result}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null) await _loop;
        Task? inFlight;
        lock (_lock) inFlight = _inFlight;
        if (inFlight is not null)
        {
            try { await inFlight; } catch (Exception) { }
        }
        _stop.Dispose();
    }
}
