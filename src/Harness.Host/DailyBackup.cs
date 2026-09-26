using System.Globalization;
using Harness.Messaging;

namespace Harness.Host;

/// <summary>
/// A daily <c>VACUUM INTO</c> copy of the database, keeping the newest <see cref="Keep"/>.
///
/// <para>
/// <b>ONLY ITS OWN FILES ARE EVER DELETED.</b> The directory is shared with the pre-migration
/// backups (<c>messages-*-before-*.db</c>) and <c>--backup</c>'s manual ones
/// (<c>messages-*-manual.db</c>), and both are kept forever on purpose - see
/// <c>SchemaMigrator.BackUpBeforeAsync</c>. Retention matches the <c>-daily.db</c> suffix and
/// nothing else.
/// </para>
///
/// <para>
/// <b>DUE IS READ FROM THE DIRECTORY, NOT A CLOCK IN MEMORY.</b> A host restarted twice an hour
/// would otherwise take a copy on every start and retention would keep seven hours, not seven days.
/// </para>
/// </summary>
public sealed class DailyBackup(string databasePath, string directory, int keep = DailyBackup.DefaultKeep)
{
    public const int DefaultKeep = 7;

    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private const string Prefix = "messages-";
    private const string Suffix = "-daily.db";
    private const string StampFormat = "yyyyMMdd-HHmmss";

    public string Directory { get; } = directory;

    public int Keep { get; } = Math.Max(1, keep);

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
            await SchemaMigrator.BackUpAsync(databasePath, destination, ct);
        }

        Prune();

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
