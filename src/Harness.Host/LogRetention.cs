using System.Globalization;
using System.Text.Json;
using Harness.Contracts;
using Harness.Messaging;

namespace Harness.Host;

/// <summary>What one retention pass did. <paramref name="Cutoff"/> is null when the setting is 0.</summary>
public sealed record LogRetentionPass(
    int Days, DateTimeOffset? Cutoff, int Chosen, int Purged, int Retained, int FileChanges, int RunSteps = 0)
{
    /// <summary>Whether the pass removed anything, and so wrote its <c>log.pruned</c> row.</summary>
    public bool Removed => Purged > 0 || FileChanges > 0 || RunSteps > 0;
}

/// <summary>
/// THE MESSAGE LOG KEEPS <c>log.retentionDays</c> OF FINISHED WORK, and this pass removes the rest.
///
/// <para>
/// It chooses with <see cref="SqliteLogRetention"/> and deletes with <see cref="IMessageLog.DeleteAsync"/>,
/// the one delete the log has, so a row a survivor cites or a run is still queued for is retained
/// there, and the recorded file changes and agent steps older than the oldest kept row go in the same transaction.
/// Nothing else is touched: the ledgers refuse a delete, and the tenant log, admission holds, stored
/// landed and backlog stats are other tables.
/// </para>
///
/// <para>
/// BOUNDED: at most <see cref="Batch"/> rows are chosen a pass, so it never holds the database for
/// long. Whole workflows go first, each with its root, declarations and closes, while the batch has
/// room for all of one. The room left takes the other rows of workflows too large to go whole, or
/// held by a dispatch or an outcome link, walked from a position kept in memory that continues on
/// the next pass and starts again from the beginning once a pass reaches the end; a restart starts
/// it again too, which costs a pass and loses nothing.
/// </para>
///
/// <para>
/// The setting is read through a delegate at every pass, never captured. Each pass that removes
/// anything appends one <c>log.pruned</c> tenant row and logs one line; a pass that removes nothing
/// writes nothing.
/// </para>
/// </summary>
public sealed class LogRetention(
    ILogRetention rows,
    IMessageLog log,
    ITenantLog tenant,
    Func<int> retentionDays,
    ILogger<LogRetention> logger,
    int batch = LogRetention.DefaultBatch,
    Action? purged = null)
{
    public const string SettingName = "log.retentionDays";

    public const int DefaultDays = 90;

    /// <summary>The least a setting other than 0 may be.</summary>
    public const int MinimumDays = 7;

    public const int DefaultBatch = 5000;

    public int Batch { get; } = Math.Max(1, batch);

    private long _after;

    public async Task<LogRetentionPass> RunAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var days = retentionDays();
        if (days <= 0) return new LogRetentionPass(days, null, 0, 0, 0, 0);

        var cutoff = now.ToUniversalTime().AddDays(-days);

        // WHOLE WORKFLOWS FIRST, then the room left for the rest. The other way round, a batch full of
        // rows a kept declaration cites - retained on every pass - would leave no room for the
        // workflow that would take them with it.
        var whole = await rows.WholeAsync(cutoff, Batch, ct);
        var first = whole.Count == 0 ? default : await log.DeleteAsync(whole, ct);

        var room = Batch - whole.Count;
        IReadOnlyList<long> body = [];
        var second = default(PurgeReport);
        if (room > 0)
        {
            body = await rows.BodyAsync(cutoff, _after, room, ct);
            if (body.Count > 0) second = await log.DeleteAsync(body, ct);
            _after = body.Count < room ? 0 : body[^1];
        }

        // WHAT HOLDS ROWS IN MEMORY IS TOLD: the board projection appends from the log's position and
        // cannot see a row go.
        if (whole.Count > 0 || body.Count > 0) purged?.Invoke();

        var pass = new LogRetentionPass(
            days,
            cutoff,
            body.Count + whole.Count,
            first.Purged + second.Purged,
            first.Retained + second.Retained,
            first.FileChanges + second.FileChanges,
            first.RunSteps + second.RunSteps);

        if (!pass.Removed) return pass;

        await tenant.WriteAsync(
            null,
            null,
            TenantActions.LogPruned,
            SettingName,
            null,
            JsonSerializer.Serialize(new
            {
                setting = SettingName,
                days,
                cutoff = cutoff.ToString("O", CultureInfo.InvariantCulture),
                purged = pass.Purged,
                retained = pass.Retained,
                fileChanges = pass.FileChanges,
                runSteps = pass.RunSteps,
            }),
            ct);

        logger.LogInformation(
            "Log retention ({Setting} {Days}) removed {Purged} rows older than {Cutoff}, retained {Retained} that kept rows cite or runs are queued for, and removed {FileChanges} recorded file changes and {RunSteps} recorded agent steps.",
            SettingName, days, pass.Purged, cutoff, pass.Retained, pass.FileChanges, pass.RunSteps);

        return pass;
    }
}

/// <summary>
/// Runs <see cref="LogRetention"/> once shortly after start and then once a day, off every request's
/// path. A failed pass is logged and tried again at the next one; it never stops the Host.
/// </summary>
public sealed class LogRetentionLoop(
    LogRetention retention,
    ILogger<LogRetentionLoop> logger,
    TimeSpan? firstDelay = null,
    TimeSpan? interval = null,
    TimeProvider? clock = null)
    : BackgroundService
{
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(firstDelay ?? FirstDelay, _clock, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await retention.RunAsync(_clock.GetUtcNow(), stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "The log retention pass failed; it is tried again at the next pass.");
                }

                await Task.Delay(interval ?? Interval, _clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
