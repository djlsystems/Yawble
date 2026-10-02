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

    /// <summary>
    /// The workers now, from the pool, the ledger's figures and the runs on each. A worker's runs are
    /// every run open on it and every slot placed on it that has not been sent there yet - still
    /// starting, or held at the update gate - named by team and member with an empty run until it is
    /// open. Whether a worker drains is read BEFORE its slots: a record that says draining then holds
    /// every placement made on it before the drain, and the ledger places nothing on a draining worker
    /// after, so a draining worker with no runs here has none coming.
    /// </summary>
    public static WorkersRecord Of(WorkerPool pool, WipLedger wip, Func<WorkerId, IReadOnlyCollection<RunId>> openOn, string version) =>
        new(DateTimeOffset.UtcNow, [.. WorkersView.Of(pool, wip, version).Select(sample =>
        {
            var id = new WorkerId(sample.Id);
            var capacity = sample.Capacity;
            var draining = pool.IsDraining(id);
            return new WorkerRecordItem(
                sample.Id, sample.Version, sample.State, draining, sample.ConnectedSince, sample.DroppedAt,
                RunsOn(id, wip, openOn),
                new WorkerRecordCapacity(
                    capacity.Cpus, capacity.MemoryLimitBytes, capacity.MemoryInUseBytes, capacity.Bound, capacity.NotMeasured.Count > 0));
        })]);

    private static WorkerRecordRun[] RunsOn(WorkerId worker, WipLedger wip, Func<WorkerId, IReadOnlyCollection<RunId>> openOn)
    {
        var held = wip.HoldsOn(worker);
        var open = openOn(worker);
        return [.. open.Select(run => new WorkerRecordRun(run.Nonce, run.Member.Team, run.Member.Name)),
            .. held.Where(hold => !open.Any(run => SameMember(run, hold))).Select(hold => new WorkerRecordRun(string.Empty, hold.Team, hold.Member))];
    }

    private static bool SameMember(RunId run, WipHold hold) =>
        string.Equals(run.Member.Team, hold.Team, StringComparison.OrdinalIgnoreCase)
        && string.Equals(run.Member.Name, hold.Member, StringComparison.OrdinalIgnoreCase);

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
