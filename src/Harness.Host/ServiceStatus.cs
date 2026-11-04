using System.Diagnostics;
using Harness.Contracts;
using Harness.Host.Auth;
using Harness.Host.Link;

namespace Harness.Host;

/// <summary>One worker as the status control lists it: its build, whether it is connected, and since when.</summary>
public sealed record ServiceWorker(string Id, string? Version, string State, DateTimeOffset ConnectedSince, DateTimeOffset? DroppedAt);

/// <summary>
/// The management link as the status control shows it, read-only: the link client's own view. It
/// names the service and this instance, never the service's address or the credential.
/// </summary>
public sealed record ServiceManagement(
    string State, DateTimeOffset Since, string? ServiceName, string? InstanceName, DateTimeOffset? LastReportAt, string? Reason)
{
    /// <summary>The link client's view as the dropdown reads it; null when the instance was never attached.</summary>
    public static ServiceManagement? From(LinkView? view) => view is null
        ? null
        : new ServiceManagement(Word(view.State), view.Since, view.ServiceName, view.InstanceName, view.LastReportAt, view.Reason);

    private static string Word(LinkState state) => state switch
    {
        LinkState.Connected => "connected",
        LinkState.Retrying => "retrying",
        LinkState.Connecting => "connecting",
        LinkState.Refused => "refused",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };
}

/// <summary>
/// The service's own state, as the app bar's status control shows it. Nothing here is estimated:
/// <see cref="StartedAt"/> is the process's own start (null when it cannot be read), the checks are
/// <c>/healthz</c>'s three, <see cref="PumpLastPass"/> is the pump's last beat, the build is the
/// assembly's, the workers are the pool's and <see cref="Management"/> is the link client's view.
/// </summary>
public sealed record ServiceStatus(
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset Now,
    IReadOnlyList<HealthEndpoints.Check> Checks,
    DateTimeOffset? PumpLastPass,
    BuildVersion Build,
    IReadOnlyList<ServiceWorker> Workers,
    ServiceManagement? Management);

/// <summary>
/// <c>GET /api/service-status</c>: what the live chip's dropdown shows a person - how long the
/// instance has been up, whether it can serve (the same three checks <c>/healthz</c> makes, made the
/// same way), which build it is and which workers it has. A read that changes nothing. People only:
/// unlike <c>/healthz</c> it names the start time, the build and the workers, which a stranger on the
/// tunnel is not told.
/// </summary>
public static class ServiceStatusEndpoints
{
    public const string Route = "/api/service-status";

    /// <summary>When this process started, read from the process itself; null when that cannot be read.</summary>
    public static DateTimeOffset? StartedAt { get; } = ReadStart();

    private static DateTimeOffset? ReadStart()
    {
        try
        {
            return new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public static void Map(WebApplication app, string dataRoot, Func<IReadOnlyList<WorkerSample>> workers)
    {
        app.MapGet(Route, async (IDatabaseHealth database, PumpHeartbeat pump, LinkClient link, TimeProvider clock, CancellationToken ct) =>
        {
            var checks = await HealthEndpoints.ChecksAsync(database, dataRoot, pump, ct);

            return Results.Ok(new ServiceStatus(
                checks.All(c => c.Ok) ? "healthy" : "unhealthy",
                StartedAt,
                clock.GetUtcNow(),
                checks,
                pump.LastBeat,
                BuildVersion.Current,
                workers().Select(w => new ServiceWorker(w.Id, w.Version, w.State, w.ConnectedSince, w.DroppedAt)).ToList(),
                ServiceManagement.From(link.Current)));
        })
            .HumansOnly()
            .WithTags("Diagnostics")
            .WithSummary("How long this instance has been up, whether it can serve, its build, its workers and its management link")
            .WithDescription(
                "People only. `startedAt` is when the Host process started (null when it cannot be read) and `now` "
                + "the Host's clock, so a client works out the uptime without trusting its own clock. `status` and "
                + "`checks` are /healthz's own three checks, made the same way: the database opens, the data root "
                + "accepts a write, the delivery pump has made a pass in the last minute (`pumpLastPass` is that "
                + "pass). `build` is /api/version's answer. `workers` is each worker's `id`, `version`, `state` "
                + "(`connected` or `dropped`), `connectedSince` and `droppedAt`; a Host that runs its runs itself "
                + "lists its own one worker. `management` is null until the instance is attached to a management "
                + "service; then it is the link's `state` (`connected`, `retrying`, `connecting` or `refused`), `since` "
                + "(when it entered that state), `serviceName`, `instanceName`, `lastReportAt` and `reason`, the last two "
                + "null when there is none. It never carries the service's address or the credential.");
    }
}
