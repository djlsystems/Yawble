using Harness.Host.Auth;
using Microsoft.Data.Sqlite;

namespace Harness.Host;

/// <summary>
/// When the delivery pump last started a pass. <see cref="PumpService"/> beats once per loop, so a
/// beat older than <see cref="Stale"/> means the loop has stopped or one pass is stuck - either way
/// nothing is being delivered, which is what "pump alive" is asking.
/// </summary>
public sealed class PumpHeartbeat(TimeProvider clock)
{
    /// <summary>An idle pass sleeps 250ms, and a busy one is a handful of database reads. A minute
    /// without a beat is not a slow pump.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(1);

    private long _lastBeatTicks;

    public void Beat() => Interlocked.Exchange(ref _lastBeatTicks, clock.GetUtcNow().UtcTicks);

    public DateTimeOffset? LastBeat =>
        Interlocked.Read(ref _lastBeatTicks) is var ticks and > 0
            ? new DateTimeOffset(ticks, TimeSpan.Zero)
            : null;

    public bool IsAlive => LastBeat is { } last && clock.GetUtcNow() - last < Stale;
}

public static class HealthEndpoints
{
    public const string Route = "/healthz";

    public sealed record Check(string Name, bool Ok, string Detail);

    public static void Map(WebApplication app, string database, string dataRoot)
    {
        // ANONYMOUS, because its caller is the container runtime's HEALTHCHECK (curl on loopback)
        // and it holds no credential. It says whether three things work and nothing else: no
        // paths, no counts, no team names - a stranger on the tunnel learns only "up" or "down".
        app.MapGet(Route, async (PumpHeartbeat pump, CancellationToken ct) =>
        {
            Check[] checks =
            [
                await DatabaseAsync(database, ct),
                DataRootWritable(dataRoot),
                pump.IsAlive
                    ? new Check("pump", true, "delivering")
                    : new Check("pump", false, pump.LastBeat is null ? "not started" : "no pass for over a minute"),
            ];

            var healthy = checks.All(c => c.Ok);

            return Results.Json(
                new { status = healthy ? "healthy" : "unhealthy", checks },
                statusCode: healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        })
            .AllowAnonymous()
            .NoPermitRequired()
            .WithTags("Diagnostics")
            .WithSummary("Whether this instance can serve")
            .WithDescription(
                "Anonymous. Three checks: the database opens and answers a query, the data root "
                + "accepts a write, and the delivery pump has made a pass in the last minute. 200 "
                + "when all three pass, 503 otherwise; the body names each check either way.");
    }

    private static async Task<Check> DatabaseAsync(string database, CancellationToken ct)
    {
        try
        {
            // ReadWrite, not the default ReadWriteCreate: a missing database is a failure to
            // report, not a file for a health probe to create.
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString();

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master";
            await command.ExecuteScalarAsync(ct);

            return new Check("database", true, "open");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            return new Check("database", false, "cannot open");
        }
    }

    /// <summary>Public because <c>--doctor</c> asks the same question and must not ask it a second
    /// way.</summary>
    public static Check DataRootWritable(string dataRoot)
    {
        var probe = Path.Combine(dataRoot, $".healthz-{Guid.NewGuid():N}");

        try
        {
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new Check("dataRoot", true, "writable");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Check("dataRoot", false, "not writable");
        }
    }
}
