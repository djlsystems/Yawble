using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Harness.Contracts;
using Harness.Contracts.Link;
using Microsoft.AspNetCore.DataProtection;

namespace Harness.Host.Link;

/// <summary>What the live-status dropdown reads of the link. Never the credential.</summary>
public sealed record LinkView(
    LinkState State, DateTimeOffset Since, string? ServiceName, string? InstanceName, DateTimeOffset? LastReportAt, string? Reason);

/// <summary>
/// Where the link client takes the summary it reports: the latest one, and word when it changes. The
/// status summary proper is built elsewhere; the client only sends what this answers.
/// </summary>
public interface ILinkSummarySource
{
    StatusSummary Current();

    event Action? Changed;
}

/// <summary>A summary that never changes: healthy, this build's version, nothing else. A stand-in until the real summary is wired.</summary>
public sealed class SteadyLinkSummary : ILinkSummarySource
{
    private readonly StatusSummary _summary = new(StatusSummary.Healthy, BuildVersion.Current.Version, [], [], 0);

    public event Action? Changed
    {
        add { }
        remove { }
    }

    public StatusSummary Current() => _summary;
}

/// <summary>
/// THE LINK CLIENT: one outbound WebSocket to the stored management service (docs/management-link.md).
/// Nothing listens for the service. With no link stored it does nothing. It says hello, takes the
/// welcome, sends the latest status once and then heartbeats at the welcomed interval; each later
/// summary is one more status. A drop retries with backoff (1 s doubling to 60 s, times a jitter of
/// ±20 %); close 4401, or 401 on the upgrade, is a refusal: the row is marked refused, one tenant row is
/// written, and nothing retries until a new attach. <see cref="Changed"/> restarts on the stored row or
/// stops when it is gone. A service that is down never reaches the Host: every failure stays in here.
/// The credential is unsealed only to be sent in the upgrade's Authorization header; it is never logged,
/// written to the tenant log, or shown in <see cref="Current"/>.
/// </summary>
public sealed class LinkClient : BackgroundService, ILinkSignal
{
    private static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(2);

    private const int LongestFrame = 64 * 1024;

    private static readonly JsonSerializerOptions Detail = new(JsonSerializerDefaults.Web);

    private readonly ILinkStore _store;
    private readonly IDataProtectionProvider _protection;
    private readonly ITenantLog _tenantLog;
    private readonly ILinkSummarySource _summary;
    private readonly TimeProvider _clock;
    private readonly ILogger<LinkClient> _logger;
    private readonly Func<double> _jitter;
    private readonly DateTimeOffset _startedAt;

    // Word that the summary changed since the last status: at most one waiting, so an outage's changes are one status on reconnect.
    private readonly Channel<bool> _summaryChanged = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly Lock _gate = new();
    private readonly HashSet<string> _unknownLogged = new(StringComparer.Ordinal);

    private CancellationTokenSource _changed = new();
    private LinkView? _view;
    private long _seq;

    /// <param name="jitter">The factor each backoff wait is multiplied by, 0.8 to 1.2; random unless a test pins it.</param>
    public LinkClient(
        ILinkStore store, IDataProtectionProvider protection, ITenantLog tenantLog, ILinkSummarySource summary, TimeProvider clock,
        ILogger<LinkClient> logger, Func<double>? jitter = null)
    {
        _store = store;
        _protection = protection;
        _tenantLog = tenantLog;
        _summary = summary;
        _clock = clock;
        _logger = logger;
        _jitter = jitter ?? (() => 0.8 + (Random.Shared.NextDouble() * 0.4));
        _startedAt = clock.GetUtcNow();

        // A service keeps the status with the highest seq, so it grows across restarts too: it starts from the clock.
        _seq = _startedAt.ToUnixTimeMilliseconds();
        _summary.Changed += OnSummaryChanged;
    }

    /// <summary>Where the link stands, or null when no link is stored.</summary>
    public LinkView? Current => Volatile.Read(ref _view);

    /// <summary>The stored link changed: drop the connection or the wait, and start again from the stored row.</summary>
    public void Changed()
    {
        CancellationTokenSource changed;
        lock (_gate) changed = _changed;

        try
        {
            changed.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public override void Dispose()
    {
        _summary.Changed -= OnSummaryChanged;
        base.Dispose();
    }

    private void OnSummaryChanged() => _summaryChanged.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            CancellationTokenSource changed;
            lock (_gate)
            {
                changed = _changed.IsCancellationRequested ? new CancellationTokenSource() : _changed;
                if (!ReferenceEquals(changed, _changed))
                {
                    _changed.Dispose();
                    _changed = changed;
                }
            }

            using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, changed.Token);

            try
            {
                var link = await _store.GetAsync(stoppingToken);
                Show(link);

                if (link is not null && Unusable(link) is { } why)
                {
                    _logger.LogWarning("The management link to {Url} is not used: {Reason}", link.ServiceUrl, why);
                }

                if (link is null || link.State == LinkState.Refused || Unusable(link) is not null)
                {
                    // Nothing to reach until the stored link changes: no link, a refused one, or one that cannot be used.
                    attempt = 0;
                    await WaitAsync(Timeout.InfiniteTimeSpan, session.Token);
                    continue;
                }

                var outcome = await ConnectAsync(link, session.Token);
                if (outcome.Welcomed) attempt = 0;

                if (outcome.Refusal is { } reason)
                {
                    await RefuseAsync(link, reason, stoppingToken);
                    continue;
                }

                if (session.IsCancellationRequested) continue;

                await RetryingAsync(stoppingToken);
                await WaitAsync(Backoff(attempt++), session.Token);
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested)
            {
                // Stopping, or the link changed: the loop decides which.
            }
            catch (Exception exception)
            {
                // A database or socket failure is the link's alone: the Host keeps serving, and the link tries again.
                _logger.LogWarning("The management link failed: {Kind}.", exception.GetType().Name);
                await WaitAsync(Backoff(attempt++), session.Token);
            }
        }
    }

    /// <summary>The wait before the next attempt: 1 s doubling to 60 s, times the jitter.</summary>
    private TimeSpan Backoff(int attempt)
    {
        var wait = attempt >= 6 ? LongestWait : TimeSpan.FromTicks(Math.Min(FirstWait.Ticks << attempt, LongestWait.Ticks));
        return TimeSpan.FromTicks((long)(wait.Ticks * _jitter()));
    }

    private async Task WaitAsync(TimeSpan wait, CancellationToken ct)
    {
        try
        {
            await Task.Delay(wait, _clock, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string? Unusable(StoredLink link) =>
        Uri.TryCreate(link.ServiceUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? WorkerProcess.Plaintext(uri) is null ? null : "it is neither loopback nor private, so it must be https://."
            : "it is not an http(s) URL.";

    private static Uri SocketUri(string serviceUrl)
    {
        var builder = new UriBuilder(serviceUrl.TrimEnd('/') + LinkProtocol.LinkPath);
        builder.Scheme = builder.Scheme == "https" ? "wss" : "ws";
        return builder.Uri;
    }

    private sealed record Outcome(bool Welcomed, string? Refusal);

    private async Task<Outcome> ConnectAsync(StoredLink link, CancellationToken ct)
    {
        string credential;
        try
        {
            credential = LinkCredential.Unprotect(_protection, link.CredentialCipher);
        }
        catch (CryptographicException)
        {
            _logger.LogWarning("The management link's credential for {Url} cannot be unsealed with this instance's keys; attach again.", link.ServiceUrl);
            await WaitAsync(Timeout.InfiniteTimeSpan, ct);
            return new Outcome(false, null);
        }

        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(LinkProtocol.Subprotocol);
        socket.Options.SetRequestHeader("Authorization", "Bearer " + credential);
        socket.Options.CollectHttpResponseDetails = true;

        try
        {
            await socket.ConnectAsync(SocketUri(link.ServiceUrl), ct);
        }
        catch (Exception exception) when (exception is WebSocketException or HttpRequestException or IOException)
        {
            if (socket.HttpStatusCode == HttpStatusCode.Unauthorized)
            {
                return new Outcome(false, "The service refused this instance's credential.");
            }

            _logger.LogInformation("The management link to {Url} could not connect ({Status}).", link.ServiceUrl,
                socket.HttpStatusCode == 0 ? exception.GetType().Name : ((int)socket.HttpStatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
            return new Outcome(false, null);
        }

        using var sending = new SemaphoreSlim(1, 1);
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? sender = null;
        Task? goodbye = null;
        var welcomed = false;
        string? refusedReason = null;

        // Socket calls never take a token: a cancelled ClientWebSocket call aborts the socket, and a detach
        // says goodbye with a close. A service that does not answer the close is cut off after a moment.
        using (ct.Register(() => goodbye = GoodbyeAsync(socket, sending, connection)))
        {
            try
            {
                await SendAsync(socket, sending, new Hello(BuildVersion.Current.Version, BuildVersion.Current.Commit, _startedAt), connection.Token);

                while (await ReceiveAsync(socket) is { } text)
                {
                    switch (Parse(text))
                    {
                        case Welcome welcome when !welcomed:
                            welcomed = true;
                            var seconds = welcome.HeartbeatSeconds is >= LinkProtocol.MinHeartbeatSeconds and <= LinkProtocol.MaxHeartbeatSeconds
                                ? welcome.HeartbeatSeconds
                                : LinkProtocol.DefaultHeartbeatSeconds;
                            await ConnectedAsync(welcome, connection.Token);
                            sender = SendLoopAsync(socket, sending, TimeSpan.FromSeconds(seconds), connection.Token);
                            break;
                        case Refused refused:
                            refusedReason = string.IsNullOrWhiteSpace(refused.Reason) ? null : refused.Reason.Trim();
                            break;
                    }
                }

                // The service closed: answer its close, so its code and reason are read on both sides.
                if (socket.State == WebSocketState.CloseReceived && !ct.IsCancellationRequested)
                {
                    await sending.WaitAsync(CloseWait);
                    try
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    }
                    finally
                    {
                        sending.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is WebSocketException or IOException or OperationCanceledException)
            {
                if (!ct.IsCancellationRequested)
                {
                    _logger.LogInformation("The management link to {Url} dropped: {Kind}.", link.ServiceUrl, exception.GetType().Name);
                }
            }
            finally
            {
                await connection.CancelAsync();
                await Quietly(sender);
            }
        }

        await Quietly(goodbye);

        if (socket.CloseStatus is { } status && (int)status == LinkProtocol.CloseCredentialRefused)
        {
            return new Outcome(welcomed, refusedReason ?? (string.IsNullOrWhiteSpace(socket.CloseStatusDescription)
                ? "The service refused this instance's credential."
                : socket.CloseStatusDescription.Trim()));
        }

        if (socket.CloseStatus is { } other)
        {
            _logger.LogInformation("The management link to {Url} was closed ({Code}).", link.ServiceUrl, (int)other);
        }

        return new Outcome(welcomed, null);
    }

    /// <summary>The latest status at once, then a heartbeat every interval and a status whenever the summary changes.</summary>
    private async Task SendLoopAsync(ClientWebSocket socket, SemaphoreSlim sending, TimeSpan interval, CancellationToken ct)
    {
        _summaryChanged.Reader.TryRead(out _);
        await SendStatusAsync(socket, sending, ct);

        var nextBeat = _clock.GetUtcNow() + interval;
        while (!ct.IsCancellationRequested)
        {
            using var turn = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var wait = nextBeat - _clock.GetUtcNow();
            var beat = Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, _clock, turn.Token);
            var change = _summaryChanged.Reader.WaitToReadAsync(turn.Token).AsTask();

            var first = await Task.WhenAny(beat, change);
            await turn.CancelAsync();
            ct.ThrowIfCancellationRequested();

            if (first == change && _summaryChanged.Reader.TryRead(out _))
            {
                await SendStatusAsync(socket, sending, ct);
            }
            else if (first == beat)
            {
                await SendAsync(socket, sending, new Heartbeat(_clock.GetUtcNow()), ct);
                nextBeat += interval;
            }
        }
    }

    private async Task SendStatusAsync(ClientWebSocket socket, SemaphoreSlim sending, CancellationToken ct)
    {
        var at = _clock.GetUtcNow();
        await SendAsync(socket, sending, new Status(Interlocked.Increment(ref _seq), at, _summary.Current()), ct);
        await _store.ReportedAsync(at, ct);
        if (Current is { } view) Volatile.Write(ref _view, view with { LastReportAt = at });
    }

    private static async Task SendAsync(ClientWebSocket socket, SemaphoreSlim sending, LinkFrame frame, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(LinkJson.Serialize(frame));
        await sending.WaitAsync(ct);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        finally
        {
            sending.Release();
        }
    }

    /// <summary>The next text message, or null once the service closes. A binary or oversized message is skipped.</summary>
    private async Task<string?> ReceiveAsync(ClientWebSocket socket)
    {
        var buffer = new byte[8 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult result;
            var tooLong = false;
            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
                if (message.Length + result.Count > LongestFrame) tooLong = true;
                else message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType == WebSocketMessageType.Text && !tooLong) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);

            LogUnknownOnce(tooLong ? "(oversized)" : "(binary)");
        }
    }

    /// <summary>The frame, or null - ignored - for anything this version does not know, logged once per type.</summary>
    private LinkFrame? Parse(string text)
    {
        try
        {
            if (LinkJson.Parse(text) is { } frame) return frame;

            using var document = JsonDocument.Parse(text);
            LogUnknownOnce(document.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString()!
                : "(no type)");
        }
        catch (JsonException)
        {
            LogUnknownOnce("(not JSON)");
        }

        return null;
    }

    private void LogUnknownOnce(string type)
    {
        bool first;
        lock (_unknownLogged) first = _unknownLogged.Add(type);
        if (first) _logger.LogInformation("The management link ignored a frame of type {Type}, which this version does not know.", type);
    }

    /// <summary>Stops sending, closes the socket normally, and cuts it off if the service does not answer in a moment.</summary>
    private static async Task GoodbyeAsync(ClientWebSocket socket, SemaphoreSlim sending, CancellationTokenSource connection)
    {
        try
        {
            await connection.CancelAsync();
            if (await sending.WaitAsync(CloseWait))
            {
                try
                {
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).WaitAsync(CloseWait);
                    }
                }
                finally
                {
                    sending.Release();
                }
            }

            // The receive loop reads the service's answering close; wait for it, briefly.
            var deadline = Environment.TickCount64 + (long)CloseWait.TotalMilliseconds;
            while (socket.State != WebSocketState.Closed && Environment.TickCount64 < deadline) await Task.Delay(20);
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or TimeoutException or ObjectDisposedException or OperationCanceledException)
        {
        }

        try
        {
            if (socket.State != WebSocketState.Closed) socket.Abort();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task Quietly(Task? task)
    {
        if (task is null) return;

        try
        {
            await task;
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    // ---- STATES ----

    private void Show(StoredLink? link) =>
        Volatile.Write(ref _view, link is null
            ? null
            : new LinkView(link.State, link.StateSince, link.ServiceName, link.InstanceName, link.LastReportAt, link.Reason));

    private async Task ConnectedAsync(Welcome welcome, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        await _store.MarkAsync(LinkState.Connected, now, null, ct);
        if (Current is { } view)
        {
            Volatile.Write(ref _view, view with
            {
                State = LinkState.Connected,
                Since = now,
                Reason = null,
                ServiceName = string.IsNullOrWhiteSpace(welcome.ServiceName) ? view.ServiceName : welcome.ServiceName.Trim(),
                InstanceName = string.IsNullOrWhiteSpace(welcome.InstanceName) ? view.InstanceName : welcome.InstanceName.Trim(),
            });
        }
    }

    // "Not connected since" is the first drop: a retry that fails again keeps that time.
    private async Task RetryingAsync(CancellationToken ct)
    {
        if (Current is not { } view || view.State == LinkState.Retrying) return;

        var now = _clock.GetUtcNow();
        await _store.MarkAsync(LinkState.Retrying, now, null, ct);
        Volatile.Write(ref _view, view with { State = LinkState.Retrying, Since = now, Reason = null });
    }

    private async Task RefuseAsync(StoredLink link, string reason, CancellationToken ct)
    {
        // The row may have been replaced or cleared while the socket closed: only the link that was refused is marked.
        if (await _store.GetAsync(ct) is not { } stored || stored.CredentialCipher != link.CredentialCipher) return;

        var now = _clock.GetUtcNow();
        await _store.MarkAsync(LinkState.Refused, now, reason, ct);
        if (Current is { } view) Volatile.Write(ref _view, view with { State = LinkState.Refused, Since = now, Reason = reason });

        _logger.LogWarning("The management service at {Url} refused this instance: {Reason}", link.ServiceUrl, reason);
        await _tenantLog.WriteAsync(null, null, TenantActions.LinkRefused, link.ServiceUrl, link.ServiceName,
            JsonSerializer.Serialize(new { url = link.ServiceUrl, reason, via = "link" }, Detail), ct);
    }
}
