using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>One run a worker holds, as <c>workers.json</c> names it.</summary>
public sealed record WorkerRecordRun(string Run, string Team, string Member);

/// <summary>A worker's figures as control last measured them. <c>NotMeasured</c>: any figure is missing.</summary>
public sealed record WorkerRecordCapacity(double? Cpus, long? MemoryLimitBytes, long? MemoryInUseBytes, int? Bound, bool NotMeasured);

/// <summary>One worker control has: connected or dropped (within its grace), draining or not, and its runs.</summary>
public sealed record WorkerRecordItem(
    string Id, string? Version, string State, bool Draining, DateTimeOffset ConnectedSince, DateTimeOffset? DroppedAt,
    IReadOnlyList<WorkerRecordRun> Runs, WorkerRecordCapacity Capacity);

/// <summary>
/// CONTROL'S RECORD OF ITS WORKERS, in <c>&lt;dataRoot&gt;/workers.json</c>, for <c>--doctor</c>: the
/// doctor is another process and has no connection to read, so it reads what control last wrote. Written
/// by control alone (a .tmp then a rename) when a worker connects, drops, drains or goes, and every few
/// seconds besides, so its runs and figures stay current. A host entry: harness's alone, as the ownership
/// map keeps it, because an agent that could write it could make the doctor say a worker is connected.
/// </summary>
public sealed record WorkersRecord(DateTimeOffset RecordedAt, IReadOnlyList<WorkerRecordItem> Items)
{
    public const string FileName = "workers.json";

    /// <summary>How often control writes it besides on a change.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The workers now, from the pool, the ledger's figures and the runs placed on each.</summary>
    public static WorkersRecord Of(WorkerPool pool, WipLedger wip, Func<WorkerId, IReadOnlyCollection<RunId>> openOn, string version) =>
        new(DateTimeOffset.UtcNow, [.. WorkersView.Of(pool, wip, version).Select(sample =>
        {
            var id = new WorkerId(sample.Id);
            var capacity = sample.Capacity;
            return new WorkerRecordItem(
                sample.Id, sample.Version, sample.State, pool.IsDraining(id), sample.ConnectedSince, sample.DroppedAt,
                [.. openOn(id).Select(run => new WorkerRecordRun(run.Nonce, run.Member.Team, run.Member.Name))],
                new WorkerRecordCapacity(
                    capacity.Cpus, capacity.MemoryLimitBytes, capacity.MemoryInUseBytes, capacity.Bound, capacity.NotMeasured.Count > 0));
        })]);

    /// <summary>Never throws: a control that cannot record it still serves its workers, and logs why.</summary>
    public void Write(string dataRoot, ILogger? log = null)
    {
        var path = Path.Combine(dataRoot, FileName);
        try
        {
            var temp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.LogWarning("Workers: could not record the workers in {Path}: {Error}", path, exception.Message);
        }
    }

    /// <summary>The last record, or null when there is none or it cannot be read.</summary>
    public static WorkersRecord? Read(string dataRoot)
    {
        try
        {
            var path = Path.Combine(dataRoot, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<WorkersRecord>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
