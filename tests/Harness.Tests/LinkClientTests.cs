using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Harness.Contracts;
using Harness.Contracts.Link;
using Harness.Host;
using Harness.Host.Link;
using Harness.Tests.Contracts;
using Harness.Tests.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harness.Tests;

/// <summary>
/// THE LINK CLIENT: one outbound WebSocket to the stored management service. It says hello, takes the
/// welcome, sends the latest status once and then heartbeats; a drop retries with backoff on the
/// injected clock and resends the latest status once; 4401, or 401 on the upgrade, is a refusal that
/// stops retrying and writes one tenant row; an unknown frame is ignored and logged once per type;
/// detach closes the socket. The service here is an in-process fake on loopback Kestrel.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class LinkClientTests : IAsyncLifetime
{
    private const string Credential = "link-credential-7f3a";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SqliteIdentityDatabase _database = new();

    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();

    private readonly LinkClock _clock = new(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));

    private readonly FakeLinkService _service = new();

    private readonly FakeSummary _summary = new();

    private readonly ListLogger<LinkClient> _logger = new();

    private LinkClient? _client;

    private ILinkStore Store => _database.Links();

    private ITenantLog Tenant => _database.TenantLog();

    public async ValueTask InitializeAsync() => await _service.StartAsync();

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.StopAsync(CancellationToken.None);
            _client.Dispose();
        }

        await _service.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task With_no_link_nothing_connects()
    {
        var client = await StartAsync();

        // Even a signal and a long while on the clock reach nothing.
        client.Changed();
        await Settle();
        _clock.Advance(TimeSpan.FromMinutes(5));
        await Settle();

        Assert.Empty(_service.Attempts);
        Assert.Null(client.Current);
    }

    [Fact]
    public async Task It_sends_hello_then_status_then_heartbeats_at_the_welcomed_interval()
    {
        await StoreLinkAsync();
        var client = await StartAsync();

        var session = await _service.NextSessionAsync();
        var attempt = Assert.Single(_service.Attempts);
        Assert.Equal("Bearer " + Credential, attempt.Authorization);
        Assert.Equal(LinkProtocol.Subprotocol, session.Subprotocol);

        var hello = Assert.IsType<Hello>(await session.NextFrameAsync());
        Assert.Equal(BuildVersion.Current.Version, hello.Version);

        await session.SendAsync(new Welcome("Manage", "office", HeartbeatSeconds: 30));

        var status = Assert.IsType<Status>(await session.NextFrameAsync());
        Assert.Equal(_summary.Current(), status.Summary, SummaryComparer.Instance);
        await Until(async () => (await Store.GetAsync(Ct))?.LastReportAt == status.At);

        var link = await Store.GetAsync(Ct);
        Assert.Equal(LinkState.Connected, link!.State);
        Assert.Equal(LinkState.Connected, client.Current!.State);
        Assert.Equal("Manage", client.Current.ServiceName);
        Assert.Equal("office", client.Current.InstanceName);

        // The heartbeat waits the welcomed 30 s, measured on the injected clock.
        var due = await _clock.NextDueAsync();
        Assert.Equal(TimeSpan.FromSeconds(30), due - _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(30));
        var beat = Assert.IsType<Heartbeat>(await session.NextFrameAsync());
        Assert.Equal(_clock.GetUtcNow(), beat.At);

        // A heartbeat is not a report.
        Assert.Equal(status.At, (await Store.GetAsync(Ct))!.LastReportAt);

        await BeatAsync(TimeSpan.FromSeconds(30));
        Assert.IsType<Heartbeat>(await session.NextFrameAsync());

        // A new summary is a new status, with a higher seq.
        _summary.Set(_summary.Current() with { NeedsYou = 4 });
        var next = Assert.IsType<Status>(await session.NextFrameAsync());
        Assert.True(next.Seq > status.Seq);
        Assert.Equal(4, next.Summary.NeedsYou);
    }

    [Fact]
    public async Task A_welcome_out_of_range_heartbeats_every_15_seconds()
    {
        await StoreLinkAsync();
        await StartAsync();

        var session = await _service.NextSessionAsync();
        Assert.IsType<Hello>(await session.NextFrameAsync());
        await session.SendAsync(new Welcome("Manage", "office", HeartbeatSeconds: 2));
        Assert.IsType<Status>(await session.NextFrameAsync());

        var due = await _clock.NextDueAsync();
        Assert.Equal(TimeSpan.FromSeconds(LinkProtocol.DefaultHeartbeatSeconds), due - _clock.GetUtcNow());
    }

    [Fact]
    public async Task A_drop_retries_with_backoff_and_resends_the_latest_status_once()
    {
        _jitter = 1.1;
        await StoreLinkAsync();
        var client = await StartAsync();

        var first = await _service.NextSessionAsync();
        Assert.IsType<Hello>(await first.NextFrameAsync());
        await first.SendAsync(new Welcome("Manage", "office", 15));
        var sent = Assert.IsType<Status>(await first.NextFrameAsync());

        // The service goes away: the drop, then three upgrades it cannot answer.
        _service.UpgradeStatus = HttpStatusCode.ServiceUnavailable;
        var droppedAt = _clock.GetUtcNow();
        await first.CloseAsync(WebSocketCloseStatus.EndpointUnavailable);
        await Until(() => client.Current?.State == LinkState.Retrying);
        Assert.Equal(LinkState.Retrying, (await Store.GetAsync(Ct))!.State);
        Assert.Equal(droppedAt, client.Current!.Since);

        // While it is down, the summary changes twice.
        _summary.Set(_summary.Current() with { NeedsYou = 2 });
        _summary.Set(_summary.Current() with { NeedsYou = 3 });

        // 1 s, 2 s, 4 s, each times the pinned jitter, measured on the clock.
        foreach (var seconds in new[] { 1, 2, 4 })
        {
            var attempts = _service.Attempts.Count;
            var due = await _clock.NextDueAsync();
            Assert.Equal(TimeSpan.FromSeconds(seconds * 1.1), due - _clock.GetUtcNow());
            _clock.Advance(due - _clock.GetUtcNow());
            await Until(() => _service.Attempts.Count == attempts + 1);
        }

        // Back: the next attempt, after 8 s, is welcomed and gets the latest summary once.
        _service.UpgradeStatus = null;
        var last = await _clock.NextDueAsync();
        Assert.Equal(TimeSpan.FromSeconds(8 * 1.1), last - _clock.GetUtcNow());
        _clock.Advance(last - _clock.GetUtcNow());

        var second = await _service.NextSessionAsync();
        Assert.IsType<Hello>(await second.NextFrameAsync());
        await second.SendAsync(new Welcome("Manage", "office", 15));
        var resent = Assert.IsType<Status>(await second.NextFrameAsync());
        Assert.Equal(3, resent.Summary.NeedsYou);
        Assert.True(resent.Seq > sent.Seq);

        // Once: the next frame is the heartbeat, not a second status.
        await BeatAsync(TimeSpan.FromSeconds(15));
        Assert.IsType<Heartbeat>(await second.NextFrameAsync());
        Assert.Equal(LinkState.Connected, client.Current!.State);
    }

    [Fact]
    public async Task The_backoff_doubles_to_60_seconds_and_no_further()
    {
        _jitter = 1.0;
        _service.UpgradeStatus = HttpStatusCode.ServiceUnavailable;
        await StoreLinkAsync();
        await StartAsync();

        await Until(() => _service.Attempts.Count == 1);
        var waits = new List<TimeSpan>();
        for (var i = 0; i < 8; i++)
        {
            var attempts = _service.Attempts.Count;
            var due = await _clock.NextDueAsync();
            waits.Add(due - _clock.GetUtcNow());
            _clock.Advance(due - _clock.GetUtcNow());
            await Until(() => _service.Attempts.Count == attempts + 1);
        }

        Assert.Equal(new[] { 1, 2, 4, 8, 16, 32, 60, 60 }.Select(s => TimeSpan.FromSeconds(s)), waits);
    }

    [Fact]
    public async Task Close_4401_is_refused_stops_retrying_and_writes_one_tenant_row()
    {
        await StoreLinkAsync();
        var client = await StartAsync();

        var session = await _service.NextSessionAsync();
        Assert.IsType<Hello>(await session.NextFrameAsync());
        await session.SendAsync(new Welcome("Manage", "office", 15));
        Assert.IsType<Status>(await session.NextFrameAsync());

        await session.SendAsync(new Refused("This instance was detached."));
        await session.CloseAsync((WebSocketCloseStatus)LinkProtocol.CloseCredentialRefused, "credential revoked");

        await Until(() => client.Current?.State == LinkState.Refused);
        var link = await Store.GetAsync(Ct);
        Assert.Equal(LinkState.Refused, link!.State);
        Assert.Equal("This instance was detached.", link.Reason);
        Assert.Equal("This instance was detached.", client.Current!.Reason);

        // No retry, however long the clock runs, and a signal on the refused row reaches nothing.
        var attempts = _service.Attempts.Count;
        _clock.Advance(TimeSpan.FromMinutes(10));
        client.Changed();
        await Settle();
        _clock.Advance(TimeSpan.FromMinutes(10));
        await Settle();
        Assert.Equal(attempts, _service.Attempts.Count);

        var row = Assert.Single((await Tenant.ReadAsync(ct: Ct)).Events, e => e.Action == TenantActions.LinkRefused);
        Assert.Contains(_service.Url, row.Detail);
        Assert.Contains("This instance was detached.", row.Detail);
        AssertNoCredential();

        // A new attach is a new row, and the signal reconnects it.
        await StoreLinkAsync();
        client.Changed();
        var again = await _service.NextSessionAsync();
        Assert.IsType<Hello>(await again.NextFrameAsync());
    }

    [Fact]
    public async Task A_401_on_the_upgrade_is_refused_too()
    {
        _service.UpgradeStatus = HttpStatusCode.Unauthorized;
        await StoreLinkAsync();
        var client = await StartAsync();

        await Until(() => client.Current?.State == LinkState.Refused);
        Assert.Equal(LinkState.Refused, (await Store.GetAsync(Ct))!.State);

        Assert.Single(_service.Attempts);
        _clock.Advance(TimeSpan.FromMinutes(10));
        await Settle();
        Assert.Single(_service.Attempts);

        var row = Assert.Single((await Tenant.ReadAsync(ct: Ct)).Events, e => e.Action == TenantActions.LinkRefused);
        Assert.Contains(_service.Url, row.Detail);
        AssertNoCredential();
    }

    [Fact]
    public async Task Close_4400_retries_with_backoff()
    {
        _jitter = 1.0;
        await StoreLinkAsync();
        var client = await StartAsync();

        var session = await _service.NextSessionAsync();
        Assert.IsType<Hello>(await session.NextFrameAsync());
        await session.CloseAsync((WebSocketCloseStatus)LinkProtocol.CloseProtocolError, "bad frame");

        await Until(() => client.Current?.State == LinkState.Retrying);
        var due = await _clock.NextDueAsync();
        Assert.Equal(TimeSpan.FromSeconds(1), due - _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsType<Hello>(await (await _service.NextSessionAsync()).NextFrameAsync());
        Assert.DoesNotContain((await Tenant.ReadAsync(ct: Ct)).Events, e => e.Action == TenantActions.LinkRefused);
    }

    [Fact]
    public async Task An_unknown_frame_type_is_ignored_and_logged_once()
    {
        await StoreLinkAsync();
        var client = await StartAsync();

        var session = await _service.NextSessionAsync();
        Assert.IsType<Hello>(await session.NextFrameAsync());
        await session.SendAsync(new Welcome("Manage", "office", 15));
        Assert.IsType<Status>(await session.NextFrameAsync());

        await session.SendTextAsync("""{ "type": "relay.open", "id": 1 }""");
        await session.SendTextAsync("""{ "type": "relay.open", "id": 2 }""");
        await session.SendTextAsync("""{ "type": "surprise" }""");
        await session.SendTextAsync("""{ "type": "surprise" }""");

        await Until(() => _logger.Lines.Any(l => l.Contains("relay.open", StringComparison.Ordinal))
            && _logger.Lines.Any(l => l.Contains("surprise", StringComparison.Ordinal)));
        await Settle();

        // Still open: the next heartbeat crosses on the same socket.
        await BeatAsync(TimeSpan.FromSeconds(15));
        Assert.IsType<Heartbeat>(await session.NextFrameAsync());
        Assert.False(session.Ended.IsCompleted);
        Assert.Equal(LinkState.Connected, client.Current!.State);
        Assert.Single(_service.Attempts);

        Assert.Single(_logger.Lines, l => l.Contains("relay.open", StringComparison.Ordinal));
        Assert.Single(_logger.Lines, l => l.Contains("surprise", StringComparison.Ordinal));
        AssertNoCredential();
    }

    [Fact]
    public async Task Detach_closes_the_socket_and_stops()
    {
        await StoreLinkAsync();
        var client = await StartAsync();

        var session = await _service.NextSessionAsync();
        Assert.IsType<Hello>(await session.NextFrameAsync());
        await session.SendAsync(new Welcome("Manage", "office", 15));
        Assert.IsType<Status>(await session.NextFrameAsync());

        // What LinkRequests does on detach: the row is cleared, then the signal.
        await Store.ClearAsync(Ct);
        client.Changed();

        await session.Ended.WaitAsync(Patience, Ct);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, session.ClosedWith);
        await Until(() => client.Current is null);

        var attempts = _service.Attempts.Count;
        _clock.Advance(TimeSpan.FromMinutes(10));
        await Settle();
        Assert.Equal(attempts, _service.Attempts.Count);
        Assert.Null(await Store.GetAsync(Ct));
    }

    // ---- THE HOST, WITH THE SERVICE DOWN ----

    [Fact]
    public async Task The_host_starts_and_serves_while_the_service_is_down()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"harness-link-client-{Guid.NewGuid():N}")).FullName;
        try
        {
            var closed = ClosedLoopbackUrl();

            // One host stores a link aimed at a port nothing listens on...
            await using (var seed = Factory(root))
            {
                var protection = seed.Services.GetRequiredService<IDataProtectionProvider>();
                var now = DateTimeOffset.UtcNow;
                await seed.Services.GetRequiredService<ILinkStore>().SaveAsync(new StoredLink(
                    closed, "Manage", "office", LinkCredential.Protect(protection, Credential), now, LinkState.Connecting, now, null, null), Ct);
            }

            // ...and the next one starts on it, retries, and serves.
            await using var app = Factory(root);
            var client = app.Services.GetRequiredService<LinkClient>();
            Assert.Same(client, app.Services.GetRequiredService<ILinkSignal>());

            await app.Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", "correct horse battery", Ct);
            using var http = app.CreateClient();
            (await http.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = "correct horse battery" }, Ct))
                .EnsureSuccessStatusCode();

            await Until(() => client.Current?.State == LinkState.Retrying);

            using var answer = await http.GetAsync("/api/connections", Ct);
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            Assert.Equal(LinkState.Retrying, client.Current!.State);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static WebApplicationFactory<Program> Factory(string root) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("DataRoot", root)
            .UseSetting("Logging:LogLevel:Default", "Warning"));

    private static string ClosedLoopbackUrl()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }

    // ---- HELPERS ----

    private double _jitter = 1.0;

    private async Task<LinkClient> StartAsync()
    {
        _client = new LinkClient(Store, _protection, Tenant, _summary, _clock, _logger, () => _jitter);
        await _client.StartAsync(Ct);
        return _client;
    }

    // Moves the clock to the heartbeat once the client is waiting for it, which is the welcomed interval away.
    private async Task BeatAsync(TimeSpan interval)
    {
        var due = await _clock.NextDueAsync();
        Assert.Equal(interval, due - _clock.GetUtcNow());
        _clock.Advance(interval);
    }

    private async Task StoreLinkAsync()
    {
        var now = _clock.GetUtcNow();
        await Store.SaveAsync(new StoredLink(
            _service.Url, "Manage", "office", LinkCredential.Protect(_protection, Credential), now, LinkState.Connecting, now, null, null), Ct);
    }

    private void AssertNoCredential()
    {
        foreach (var line in _logger.Lines) Assert.DoesNotContain(Credential, line);
        Assert.DoesNotContain(Credential, JsonSerializer.Serialize(_client?.Current));
        foreach (var row in Tenant.ReadAsync(ct: Ct).GetAwaiter().GetResult().Events)
        {
            Assert.DoesNotContain(Credential, JsonSerializer.Serialize(row));
        }
    }

    // Gives the client's loop real time to act on something it should NOT act on.
    private static Task Settle() => Task.Delay(TimeSpan.FromMilliseconds(300), Ct);

    private static Task Until(Func<bool> condition) => Until(() => Task.FromResult(condition()));

    private static async Task Until(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not come true in time.");
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>A summary the test sets by hand; each set is heard as a change.</summary>
    private sealed class FakeSummary : ILinkSummarySource
    {
        private StatusSummary _current = new(
            StatusSummary.Healthy, "1.2.3", [new WorkerState("worker-1", WorkerState.Connected)],
            [new TeamState("team-1", "Core", "running", 2, 1, 0, 1)], 1);

        public event Action? Changed;

        public StatusSummary Current() => Volatile.Read(ref _current);

        public void Set(StatusSummary summary)
        {
            Volatile.Write(ref _current, summary);
            Changed?.Invoke();
        }
    }

    private sealed class SummaryComparer : IEqualityComparer<StatusSummary>
    {
        public static readonly SummaryComparer Instance = new();

        public bool Equals(StatusSummary? x, StatusSummary? y) =>
            JsonSerializer.Serialize(x, LinkJson.Options) == JsonSerializer.Serialize(y, LinkJson.Options);

        public int GetHashCode(StatusSummary obj) => 0;
    }

    /// <summary>A clock moved by hand, which says when its next timer is due.</summary>
    internal sealed class LinkClock(DateTimeOffset start) : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<Timer> _timers = [];
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _now;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        /// <summary>When the earliest waiting timer is due, once one is waiting.</summary>
        public async Task<DateTimeOffset> NextDueAsync()
        {
            var deadline = DateTime.UtcNow + Patience;
            while (true)
            {
                lock (_gate)
                {
                    if (_timers.Count > 0) return _timers.Min(t => t.Due);
                }

                if (DateTime.UtcNow > deadline) throw new TimeoutException("No timer started.");
                await Task.Delay(10, Ct);
            }
        }

        public void Advance(TimeSpan by)
        {
            List<Timer> due;
            lock (_gate)
            {
                _now += by;
                due = [.. _timers.Where(t => t.Due <= _now)];
                foreach (var timer in due) _timers.Remove(timer);
            }

            foreach (var timer in due) timer.Fire();
        }

        private sealed class Timer(LinkClock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset Due { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    clock._timers.Remove(this);
                    if (dueTime == Timeout.InfiniteTimeSpan) return true;

                    Due = clock._now + dueTime;
                    clock._timers.Add(this);
                }

                return true;
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
                lock (clock._gate) clock._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Every line logged, formatted, so a test can count them and look for what must not be there.</summary>
    internal sealed class ListLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _lines.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }

    /// <summary>
    /// A management service on loopback Kestrel speaking the pinned link: it records each upgrade, refuses
    /// it with <see cref="UpgradeStatus"/> when set, and otherwise hands the test the session to script.
    /// </summary>
    internal sealed class FakeLinkService : IAsyncDisposable
    {
        private readonly Channel<Session> _sessions = Channel.CreateUnbounded<Session>();
        private readonly ConcurrentQueue<Attempt> _attempts = new();
        private WebApplication? _app;

        public string Url { get; private set; } = "";

        public HttpStatusCode? UpgradeStatus { get; set; }

        public IReadOnlyList<Attempt> Attempts => [.. _attempts];

        public async Task StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            _app = builder.Build();
            _app.UseWebSockets();
            _app.Map(LinkProtocol.LinkPath, async context =>
            {
                _attempts.Enqueue(new Attempt(context.Request.Headers.Authorization.ToString()));
                if (UpgradeStatus is { } refuse)
                {
                    context.Response.StatusCode = (int)refuse;
                    return;
                }

                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                var offered = context.WebSockets.WebSocketRequestedProtocols;
                var socket = await context.WebSockets.AcceptWebSocketAsync(offered.Contains(LinkProtocol.Subprotocol) ? LinkProtocol.Subprotocol : null);
                var session = new Session(socket);
                await _sessions.Writer.WriteAsync(session);
                await session.PumpAsync();
            });

            await _app.StartAsync();
            Url = _app.Urls.Single().TrimEnd('/');
        }

        public async Task<Session> NextSessionAsync() => await _sessions.Reader.ReadAsync(Ct).AsTask().WaitAsync(Patience, Ct);

        public async ValueTask DisposeAsync()
        {
            if (_app is not null) await _app.DisposeAsync();
        }

        public sealed record Attempt(string Authorization);

        public sealed class Session(WebSocket socket)
        {
            private readonly Channel<string> _received = Channel.CreateUnbounded<string>();
            private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public string? Subprotocol => socket.SubProtocol;

            public Task Ended => _ended.Task;

            public WebSocketCloseStatus? ClosedWith { get; private set; }

            public async Task<LinkFrame?> NextFrameAsync() =>
                LinkJson.Parse(await _received.Reader.ReadAsync(Ct).AsTask().WaitAsync(Patience, Ct));

            public Task SendAsync(LinkFrame frame) => SendTextAsync(LinkJson.Serialize(frame));

            public Task SendTextAsync(string text) =>
                socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, Ct);

            public async Task CloseAsync(WebSocketCloseStatus status, string reason = "")
            {
                await socket.CloseOutputAsync(status, reason, Ct);
                await Ended.WaitAsync(Patience, Ct);
            }

            public async Task PumpAsync()
            {
                var buffer = new byte[64 * 1024];
                try
                {
                    while (true)
                    {
                        using var message = new MemoryStream();
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                            message.Write(buffer, 0, result.Count);
                        }
                        while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            ClosedWith = result.CloseStatus;
                            if (socket.State == WebSocketState.CloseReceived)
                            {
                                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                            }

                            return;
                        }

                        await _received.Writer.WriteAsync(Encoding.UTF8.GetString(message.ToArray()));
                    }
                }
                catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or IOException)
                {
                }
                finally
                {
                    _ended.TrySetResult();
                }
            }
        }
    }
}
