using System.Globalization;
using Harness.Messaging;

namespace Harness.Host;

/// <summary>
/// A daily <c>VACUUM INTO</c> copy of the database, keeping the newest <see cref="Keep"/>.
///
/// <para>
/// <b>ONLY ITS OWN FILES ARE EVER DELETED.</b> The directory is shared with the pre-migration
/// backups (<c>messages-*-before-*.db</c>), <c>--backup</c>'s manual ones
/// (<c>messages-*-manual.db</c>) and the copy <c>--restore</c> takes first
/// (<c>messages-*-before-restore.db</c>). Retention matches the <c>-daily.db</c> suffix and nothing
/// else; manual and before-restore copies are kept forever on purpose.
/// </para>
///
/// <para>
/// <b>EACH RUN ALSO PRUNES THE BEFORE-STEP COPIES</b>, by their own rule rather than this one
/// (<see cref="BeforeStepBackups"/>: the last <see cref="KeepBeforeStepDays"/> days plus the newest
/// <see cref="KeepBeforeStep"/>), because a migration alone runs only when a release adds a step.
/// </para>
///
/// <para>
/// <b>DUE IS READ FROM THE DIRECTORY, NOT A CLOCK IN MEMORY.</b> A host restarted twice an hour
/// would otherwise take a copy on every start and retention would keep seven hours, not seven days.
/// </para>
///
/// <para>
/// <b>THE COPY IS THE DATABASE'S OWN</b> (<see cref="IDatabaseMaintenance.CopyToAsync"/>): SQLite's
/// <c>VACUUM INTO</c>. A database this Host does not copy (<see cref="Elsewhere"/> set) takes none, and
/// the runner says what backs it up instead.
/// </para>
/// </summary>
public sealed class DailyBackup(
    IDatabaseMaintenance database,
    string directory,
    int keep = DailyBackup.DefaultKeep,
    int keepBeforeStep = BeforeStepBackups.DefaultKeep,
    int keepBeforeStepDays = BeforeStepBackups.DefaultKeepDays)
{
    /// <summary>The daily backup of the SQLite file at <paramref name="databasePath"/>.</summary>
    public DailyBackup(
        string databasePath,
        string directory,
        int keep = DefaultKeep,
        int keepBeforeStep = BeforeStepBackups.DefaultKeep,
        int keepBeforeStepDays = BeforeStepBackups.DefaultKeepDays)
        : this(new SqliteDatabaseMaintenance(databasePath), directory, keep, keepBeforeStep, keepBeforeStepDays)
    {
    }

    /// <summary>Null when this Host copies the database; otherwise what backs it up instead.</summary>
    public string? Elsewhere => database.BackupElsewhere;

    public const int DefaultKeep = 7;

    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private const string Prefix = "messages-";
    private const string Suffix = "-daily.db";
    private const string StampFormat = "yyyyMMdd-HHmmss";

    public string Directory { get; } = directory;

    public int Keep { get; } = Math.Max(1, keep);

    public int KeepBeforeStep { get; } = Math.Max(1, keepBeforeStep);

    public int KeepBeforeStepDays { get; } = Math.Max(0, keepBeforeStepDays);

    /// <summary>What the last <see cref="TakeAsync"/> did to the before-step copies, for the runner to say.</summary>
    public BeforeStepPrune? LastBeforeStepPrune { get; private set; }

    /// <summary>This backup's daily copies, newest first. The stamp sorts as the time does.</summary>
    public IReadOnlyList<string> Existing() =>
        !System.IO.Directory.Exists(Directory)
            ? []
            : [.. System.IO.Directory.EnumerateFiles(Directory, Prefix + "*" + Suffix)
                .Where(path => StampOf(path) is not null)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)];

    /// <summary>When the newest daily copy was taken, or null when there is none.</summary>
    public DateTimeOffset? Newest() => Existing() is [var newest, ..] ? StampOf(newest) : null;

    public bool IsDue(DateTimeOffset now) => Newest() is not { } newest || now - newest >= Interval;

    /// <summary>Takes a copy now, then prunes. Answers the path written.</summary>
    public async Task<string> TakeAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var destination = Path.Combine(
            Directory,
            Prefix + now.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture) + Suffix);

        // VACUUM INTO refuses a destination that exists. Two copies inside one second is a test, not
        // a deployment, and the one already there is as good.
        if (!File.Exists(destination))
        {
            await database.CopyToAsync(destination, ct);
        }

        Prune();

        LastBeforeStepPrune = BeforeStepBackups.Prune(Directory, now, KeepBeforeStep, KeepBeforeStepDays);

        return destination;
    }

    /// <summary>Deletes every daily copy past the newest <see cref="Keep"/>. Answers what went.</summary>
    public IReadOnlyList<string> Prune()
    {
        var removed = new List<string>();

        foreach (var old in Existing().Skip(Keep))
        {
            File.Delete(old);
            removed.Add(old);
        }

        return removed;
    }

    private static DateTimeOffset? StampOf(string path)
    {
        var name = Path.GetFileName(path);

        if (!name.StartsWith(Prefix, StringComparison.Ordinal)
            || !name.EndsWith(Suffix, StringComparison.Ordinal))
        {
            return null;
        }

        var stamp = name[Prefix.Length..^Suffix.Length];

        return DateTime.TryParseExact(
            stamp, StampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.Zero)
            : null;
    }
}

/// <summary>
/// Takes <see cref="DailyBackup"/> when it is due: once at start if the newest copy is a day old or
/// missing, then checked every <c>checkInterval</c>. A failure is logged and retried on the next
/// check rather than stopping the host.
/// </summary>
public sealed class DailyBackupRunner(
    DailyBackup backup,
    TimeSpan checkInterval,
    ILogger<DailyBackupRunner> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A DATABASE THIS HOST DOES NOT COPY: said once, and nothing is written or pruned.
        if (backup.Elsewhere is { } elsewhere)
        {
            log.LogInformation("No daily database backup is taken: {Elsewhere}", elsewhere);
            return;
        }

        log.LogInformation(
            "Daily database backup into {Directory}, keeping the newest {Keep}.",
            backup.Directory,
            backup.Keep);

        using var timer = new PeriodicTimer(checkInterval);

        do
        {
            try
            {
                if (backup.IsDue(DateTimeOffset.UtcNow))
                {
                    var written = await backup.TakeAsync(DateTimeOffset.UtcNow, stoppingToken);
                    log.LogInformation("Wrote the daily database backup {Path}.", written);

                    if (backup.LastBeforeStepPrune is { } pruned)
                    {
                        log.Log(
                            pruned.Failed.Count == 0 ? LogLevel.Information : LogLevel.Warning,
                            "{Prune}",
                            pruned.Describe());
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                log.LogWarning(exception, "The daily database backup failed; it is tried again at the next check.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
