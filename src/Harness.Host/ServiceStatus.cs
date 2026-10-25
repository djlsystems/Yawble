using System.Diagnostics;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>One worker as the status control lists it: its build, whether it is connected, and since when.</summary>
public sealed record ServiceWorker(string Id, string? Version, string State, DateTimeOffset ConnectedSince, DateTimeOffset? DroppedAt);

/// <summary>
/// The service's own state, as the app bar's status control shows it. Nothing here is estimated:
/// <see cref="StartedAt"/> is the process's own start (null when it cannot be read), the checks are
/// <c>/healthz</c>'s three, <see cref="PumpLastPass"/> is the pump's last beat, the build is the
/// assembly's and the workers are the pool's.
/// </summary>
public sealed record ServiceStatus(
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset Now,
    IReadOnlyList<HealthEndpoints.Check> Checks,
    DateTimeOffset? PumpLastPass,
    BuildVersion Build,
    IReadOnlyList<ServiceWorker> Workers);

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
        app.MapGet(Route, async (IDatabaseHealth database, PumpHeartbeat pump, TimeProvider clock, CancellationToken ct) =>
        {
            var checks = await HealthEndpoints.ChecksAsync(database, dataRoot, pump, ct);

            return Results.Ok(new ServiceStatus(
                checks.All(c => c.Ok) ? "healthy" : "unhealthy",
                StartedAt,
                clock.GetUtcNow(),
                checks,
                pump.LastBeat,
                BuildVersion.Current,
                workers().Select(w => new ServiceWorker(w.Id, w.Version, w.State, w.ConnectedSince, w.DroppedAt)).ToList()));
        })
            .HumansOnly()
            .WithTags("Diagnostics")
            .WithSummary("How long this instance has been up, whether it can serve, its build and its workers")
            .WithDescription(
                "People only. `startedAt` is when the Host process started (null when it cannot be read) and `now` "
                + "the Host's clock, so a client works out the uptime without trusting its own clock. `status` and "
                + "`checks` are /healthz's own three checks, made the same way: the database opens, the data root "
                + "accepts a write, the delivery pump has made a pass in the last minute (`pumpLastPass` is that "
                + "pass). `build` is /api/version's answer. `workers` is each worker's `id`, `version`, `state` "
                + "(`connected` or `dropped`), `connectedSince` and `droppedAt`; a Host that runs its runs itself "
                + "lists its own one worker.");
    }
}
