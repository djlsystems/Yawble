using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Harness.Contracts;
using Harness.Messaging;

namespace Harness.Host;

/// <summary>
/// Writes to the diagnostics log on behalf of the Host, shaping WHAT is recorded.
///
/// <para>
/// The sibling of <see cref="Auth.TenantLogging"/>, and it exists for the same reason: every call
/// site would otherwise repeat the same steps - find the route, name the exception type, serialise
/// a detail object - and get one of them subtly different.
/// </para>
///
/// <para>
/// <b>THE NEVER-THROWS GUARANTEE LIVES ON THE STORE, AND IS REPEATED HERE.</b> That is not
/// duplication for its own sake. The store has it because the write sites are spread across modules
/// that cannot see the Host - `SchemaMigrator` is in `Harness.Messaging` - so a guarantee that
/// lived only in a Host helper would hold for some writers and not others. This class repeats it
/// because the thing it protects is a REQUEST: an <see cref="IDiagnosticsLog"/> is an interface,
/// any implementation can be put behind it, and no request should change its behaviour because
/// somebody swapped the store. The rule is stronger than any one implementation of it.
/// </para>
/// </summary>
public sealed class DiagnosticsRecorder(IDiagnosticsLog log)
{
    /// <summary>The store, for the few callers that want to READ rather than write. Trimming goes
    /// through <see cref="TrimAsync"/>, which carries the same never-throws guarantee the write
    /// does.</summary>
    public IDiagnosticsLog Log => log;

    /// <summary>
    /// Applies the retention bound. <b>Never throws</b>, for the same reason the write does not -
    /// and it is a method here rather than a call straight through to <see cref="Log"/> because
    /// this one runs at STARTUP: a trim that threw on the way up would be a diagnostics store
    /// taking the host down, which is the exact inversion this whole design refuses.
    /// </summary>
    public async Task TrimAsync(CancellationToken ct = default)
    {
        try
        {
            await log.TrimAsync(ct);
        }
        catch
        {
            // See the summary. A trim that fails leaves a bigger table, which is recoverable.
        }
    }

    /// <summary>
    /// Records one fact. <paramref name="detail"/> is serialised here so no call site has to think
    /// about it - and REDACTION HAPPENS IN THE STORE, so a call site cannot forget it either.
    ///
    /// <b>Never throws.</b> See the class remarks.
    /// </summary>
    public async Task WriteAsync(
        DiagnosticSeverity severity,
        string kind,
        string? source = null,
        string? route = null,
        int? status = null,
        string? exceptionType = null,
        string? message = null,
        object? detail = null,
        CancellationToken ct = default)
    {
        try
        {
            await log.WriteAsync(
                severity, kind, source, route, status, exceptionType, message,
                Serialise(detail), ct);
        }
        catch
        {
            // Deliberately swallowed, cancellation included - see the class remarks. A failure to
            // record must not fail the act being recorded, and the act being recorded here is
            // usually one that is already going wrong.
        }
    }

    /// <summary>
    /// Records an exception, naming its TYPE and its message.
    ///
    /// <b>THE TYPE, NEVER THE STACK.</b> A stack trace is a kilobyte of repetition, and a storm of
    /// identical failures is precisely when this store is being written to hardest - it would be
    /// most of the bound spent on the least distinguishing part of the record. The type and the
    /// route together are what group a storm into one thing to fix.
    /// </summary>
    public Task WriteExceptionAsync(
        string kind,
        Exception exception,
        string? source = null,
        string? route = null,
        int? status = null,
        object? detail = null,
        CancellationToken ct = default) =>
        WriteAsync(
            DiagnosticSeverity.Error,
            kind,
            source,
            route,
            status,
            TypeNameOf(exception),
            exception.Message,
            detail,
            ct);

    /// <summary>
    /// THE FULL TYPE NAME, not the short one. `SqliteException` is a short name several libraries
    /// could have; the namespace is what makes a filter on this column mean one thing.
    /// </summary>
    public static string TypeNameOf(Exception exception) =>
        exception.GetType().FullName ?? exception.GetType().Name;

    /// <summary>
    /// THE ROUTE TEMPLATE, NEVER THE RAW URL.
    ///
    /// <para>
    /// Two reasons and both matter. A template carries no query string and no path value, which is
    /// where a credential pasted into a URL would be - and this store must never hold one. And a
    /// template is the only spelling two requests to the same endpoint share, so it is the only one
    /// worth grouping a storm of failures by: a hundred rows reading
    /// `/api/teams/{team}/members/{name}` are one problem, where a hundred distinct URLs are a
    /// hundred.
    /// </para>
    ///
    /// <para>
    /// Null endpoint means the request never reached routing - a static file, the SPA rewrite, or
    /// something that threw in the pipeline before routing ran. The PATH is the fallback, without
    /// its query string, because "it failed before routing" still has to say where.
    /// </para>
    /// </summary>
    public static string? RouteOf(HttpContext context) =>
        context.GetEndpoint() is RouteEndpoint endpoint
            ? endpoint.RoutePattern.RawText
            : context.Request.Path.HasValue ? context.Request.Path.Value : null;

    private static string? Serialise(object? detail)
    {
        if (detail is null) return null;

        try
        {
            return detail as string ?? JsonSerializer.Serialize(detail);
        }
        catch
        {
            // A detail object that cannot be serialised must not be the reason a failure goes
            // unrecorded. The row is still worth having without it.
            return null;
        }
    }
}

/// <summary>
/// WHOSE HUB CONNECTION HAS JUST DROPPED, so the one that arrives next can be told from a first
/// connection.
///
/// <para>
/// A DROP AND A RECONNECT ARE DIFFERENT FACTS AND THE RATIO BETWEEN THEM IS THE DIAGNOSIS. One drop
/// and one reconnect is a laptop that slept. Forty of each in two minutes is the reconnect storm
/// the console could not recover from on the night this store was asked for - and from the server's
/// side both are made of the same two events, so without this the log would say "someone connected"
/// forty times and mean nothing.
/// </para>
///
/// <para>
/// IN MEMORY AND DELIBERATELY LOSSY. It is a hint used to choose between two row kinds, not a
/// record: a host restart forgets it, and the worst that happens is that the first reconnect after
/// a restart is recorded as an ordinary connection. Putting it in the database would be a write per
/// socket event to improve a label.
/// </para>
/// </summary>
public sealed class TransportWatch
{
    /// <summary>
    /// How soon after a drop a connection counts as a reconnect. Comfortably longer than SignalR's
    /// own automatic retry schedule, which is what produces the connections this exists to name,
    /// and short enough that a person coming back after lunch is a fresh connection.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>A ceiling, so a long-lived instance cannot grow this without bound. Well above any
    /// plausible number of people holding sockets at once; when it is passed, everything outside
    /// the window goes.</summary>
    private const int Capacity = 512;

    private readonly Dictionary<string, DateTimeOffset> _dropped = new(StringComparer.Ordinal);

    public void NoteDropped(string principalId)
    {
        lock (_dropped)
        {
            if (_dropped.Count >= Capacity)
            {
                var stale = DateTimeOffset.UtcNow - Window;

                foreach (var key in _dropped.Where(e => e.Value < stale).Select(e => e.Key).ToArray())
                {
                    _dropped.Remove(key);
                }
            }

            _dropped[principalId] = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Whether this principal dropped within <see cref="Window"/>. CONSUMES the note, so
    /// one drop names one reconnect - otherwise a person who connects three tabs after a single
    /// drop reads as three reconnects.</summary>
    public bool WasRecentlyDropped(string principalId)
    {
        lock (_dropped)
        {
            if (!_dropped.TryGetValue(principalId, out var at)) return false;

            _dropped.Remove(principalId);

            return DateTimeOffset.UtcNow - at <= Window;
        }
    }
}

/// <summary>
/// THE SEAM THAT DID NOT EXIST: a 500 reached the console the Host was started from and nowhere
/// else.
///
/// <para>
/// <b>IT RE-THROWS.</b> This middleware records and gets out of the way - it does not produce a
/// response, does not set a status and does not swallow. Every existing behaviour downstream of an
/// unhandled exception is exactly what it was; the only change is that there is now a row. A
/// middleware that started answering the request would be a second error-handling policy, and the
/// suite is full of specs that assert what a failing request looks like today.
/// </para>
///
/// <para>
/// <b>WHAT IS RECORDED IS A CLOSED SET, AND WIDENING IT IS A DECISION.</b> An unhandled exception,
/// any 5xx, and a 409. Nothing else. 401, 403 and 404 are the ordinary answer to a signed-out
/// browser polling and would be this store's highest-volume rows sitting next to its most useful
/// ones - which is the `container.progress` mistake at instance scale, and is the failure this
/// whole store is written to refuse. Per-request timings are out for the same reason.
/// </para>
/// </summary>
public static class DiagnosticsMiddleware
{
    /// <summary>
    /// FIRST IN THE PIPELINE, so it sees what everything else does.
    ///
    /// An exception thrown in authentication, in the team gate or in a static-file handler is still
    /// a 500 nobody could explain, and a middleware placed after them would be blind to exactly the
    /// failures that are hardest to reason about.
    /// </summary>
    public static void Use(IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        try
        {
            await next();
        }
        catch (Exception exception)
        {
            var recorder = context.RequestServices.GetService<DiagnosticsRecorder>();

            if (recorder is not null)
            {
                // A BUSY DATABASE IS RECORDED AS ONE, not as a generic 500. The route and the
                // status are still on the row - it is still a failed request - but the KIND says
                // `db.busy`, so "is this instance contending on its one SQLite file" is a filter
                // rather than an inference from exception types. This product's whole shape makes
                // that the likeliest real database failure: one file, several writers, a WAL.
                //
                // Classified by a Messaging helper, because THE HOST NAMES NO
                // `Microsoft.Data.Sqlite` TYPE - see SqliteTrouble for how that rule is kept.
                var databaseKind = SqliteTrouble.KindOf(exception);

                // AWAITED, not fired and forgotten. A background write racing the end of the
                // request is a write against a store whose scope may be gone, and the guarantee
                // this store makes is that what was recorded stays recorded - which a task nobody
                // awaited cannot give. The cost is one INSERT on a path that is already failing.
                await recorder.WriteExceptionAsync(
                    databaseKind ?? DiagnosticKinds.HttpUnhandledException,
                    exception,
                    databaseKind is null ? DiagnosticSources.Http : DiagnosticSources.Database,
                    DiagnosticsRecorder.RouteOf(context),

                    // 500 EXPLICITLY, rather than reading the response. Nothing has written a
                    // status yet - that is what makes this exception unhandled - and 500 is what
                    // the server will answer once this re-throws. Reading `Response.StatusCode`
                    // here records 200, which is the one answer this request will certainly not
                    // give.
                    StatusCodes.Status500InternalServerError,
                    new { method = context.Request.Method });
            }

            // RE-THROWN UNCHANGED. See the class remarks.
            throw;
        }

        var status = context.Response.StatusCode;

        if (!ShouldRecord(status)) return;

        var after = context.RequestServices.GetService<DiagnosticsRecorder>();

        if (after is null) return;

        await after.WriteAsync(
            status >= 500 ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
            status >= 500 ? DiagnosticKinds.HttpServerError : DiagnosticKinds.HttpRefused,
            DiagnosticSources.Http,
            DiagnosticsRecorder.RouteOf(context),
            status,
            detail: new { method = context.Request.Method },
            ct: context.RequestAborted);
    });

    /// <summary>
    /// Whether a completed request is worth a row. THE WHOLE OF THE VOLUME POLICY, in one
    /// predicate, so widening it is an edit somebody has to justify rather than a condition that
    /// drifted.
    ///
    /// 409 is here because a refusal from a busy guard is ordinary once and a symptom repeated, and
    /// the only way to see the difference is to have the rows. Nothing else in the 4xx range is:
    /// see the class remarks.
    /// </summary>
    public static bool ShouldRecord(int status) =>
        status >= 500 || status == StatusCodes.Status409Conflict;
}
