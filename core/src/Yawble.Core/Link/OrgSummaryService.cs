using System.Text.Json.Nodes;
using Yawble.Core.Ipc;
using Yawble.Core.Provenance;

namespace Yawble.Core.Link;

/// <summary>
/// B048: org.summary (one snapshot, <see cref="OrgSummary"/>), org.summary.watch and org.summary.unwatch (all reads).
/// While at least one viewer watches, the snapshot is rebuilt every <see cref="PushEvery"/> and, when a section changed,
/// <see cref="ChangedEvent"/> is pushed with the changed sections: at most one push per 2 s, however much changes. With
/// nobody watching nothing is built and nothing is pushed. A viewer is the caller's sign-in (cloud:&lt;user&gt;, or the
/// window); a Cloud Link viewer who leaves (the site's viewer list, or the link dropping) is unwatched
/// (<see cref="ViewersPresent"/>).
/// </summary>
public sealed class OrgSummaryService : IAsyncDisposable
{
    public const string ChangedEvent = "org.summaryChanged";
    public static readonly TimeSpan PushEvery = TimeSpan.FromSeconds(2);

    private readonly IOrgSummarySource _source;
    private readonly Ui.AppEvents _events;
    private readonly TimeProvider _clock;
    private readonly Action<string>? _log;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _tick = new(1, 1);
    private readonly HashSet<string> _watchers = new(StringComparer.Ordinal);
    private JsonObject? _last;
    private DateTimeOffset? _lastPushAt;
    private CancellationTokenSource? _loop;
    private Task _running = Task.CompletedTask;

    public OrgSummaryService(IOrgSummarySource source, Ui.AppEvents events, TimeProvider? clock = null, Action<string>? log = null)
    {
        _source = source;
        _events = events;
        _clock = clock ?? TimeProvider.System;
        _log = log;
    }

    public static bool Handles(string method) => method is "org.summary" or "org.summary.watch" or "org.summary.unwatch";

    /// <summary>How many viewers watch now.</summary>
    public int Watching { get { lock (_lock) return _watchers.Count; } }

    public async Task<JsonNode?> DispatchAsync(string method, JsonObject? p, CancellationToken ct)
    {
        _ = p;
        var viewer = ViewerOf(Actor.Current);
        switch (method)
        {
            case "org.summary":
                return await SnapshotAsync(ct).ConfigureAwait(false);
            case "org.summary.watch":
            {
                var snapshot = await SnapshotAsync(ct).ConfigureAwait(false);
                lock (_lock)
                {
                    _watchers.Add(viewer);
                    // The first viewer's snapshot is what later pushes are told against; a later viewer gets its own
                    // full snapshot here and the same pushes as everyone after.
                    _last ??= snapshot.DeepClone().AsObject();
                }
                return snapshot;
            }
            case "org.summary.unwatch":
                Unwatch(viewer);
                return new JsonObject { ["watching"] = false };
            default:
                throw new RpcException("unknown_method", $"no method named '{method}'");
        }
    }

    /// <summary>A watcher's key: how it came in and who, so one viewer's unwatch never ends another's.</summary>
    internal static string ViewerOf(Actor? actor) => actor is null ? $"{Actor.App}:" : $"{actor.Via}:{actor.User}";

    private void Unwatch(string viewer)
    {
        lock (_lock)
        {
            _watchers.Remove(viewer);
            if (_watchers.Count == 0) Forget();
        }
    }

    /// <summary>
    /// The Cloud Link's viewers now (their sign-ins): every cloud watcher not among them is unwatched. The link calls
    /// this with each viewer list from the site, and with none when the connection ends: a disconnect is an unwatch.
    /// </summary>
    public void ViewersPresent(IReadOnlyCollection<string> cloudUsers)
    {
        var present = cloudUsers.Select(u => ViewerOf(Actor.CloudViewer(u))).ToHashSet(StringComparer.Ordinal);
        lock (_lock)
        {
            _watchers.RemoveWhere(w => w.StartsWith(Actor.Cloud + ":", StringComparison.Ordinal) && !present.Contains(w));
            if (_watchers.Count == 0) Forget();
        }
    }

    private void Forget()
    {
        _last = null;
        _lastPushAt = null;
    }

    private async Task<JsonObject> SnapshotAsync(CancellationToken ct) =>
        OrgSummary.Build(await _source.ReadAsync(ct).ConfigureAwait(false), _clock.GetUtcNow());

    /// <summary>
    /// One beat of the watch: with a viewer watching and the last push at least <see cref="PushEvery"/> ago, the snapshot
    /// is rebuilt and the sections that changed are pushed. True when it pushed. Nobody watching: nothing is read.
    /// </summary>
    public async Task<bool> TickAsync(CancellationToken ct)
    {
        await _tick.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            JsonObject? last;
            lock (_lock)
            {
                if (_watchers.Count == 0) return false;
                if (_lastPushAt is { } at && _clock.GetUtcNow() - at < PushEvery) return false;
                last = _last;
            }
            var next = await SnapshotAsync(ct).ConfigureAwait(false);
            var changed = OrgSummary.Changed(last, next);
            lock (_lock)
            {
                // Everyone left while it was built: nothing goes out.
                if (_watchers.Count == 0 || changed.Count == 0) return false;
                _last = next;
                _lastPushAt = _clock.GetUtcNow();
            }
            _events.Publish(ChangedEvent, OrgSummary.ChangeOf(next, changed));
            return true;
        }
        finally
        {
            _tick.Release();
        }
    }

    /// <summary>Starts the watch's beat (every <see cref="PushEvery"/>); it does nothing while nobody watches.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_loop is not null) return;
            _loop = new CancellationTokenSource();
            var ct = _loop.Token;
            _running = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(PushEvery, _clock, ct).ConfigureAwait(false);
                        await TickAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception e)
                    {
                        _log?.Invoke($"org.summary: the watch could not build a snapshot: {e.GetType().Name}: {e.Message}");
                    }
                }
            }, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? loop;
        lock (_lock) loop = _loop;
        if (loop is null) return;
        await loop.CancelAsync().ConfigureAwait(false);
        try { await _running.ConfigureAwait(false); } catch (OperationCanceledException) { }
        loop.Dispose();
    }
}
