using System.Globalization;
using System.Text.RegularExpressions;

namespace Harness.Messaging;

/// <summary>
/// What one prune of the before-step copies did: the copies it deleted, how many it kept, and the
/// ones it meant to delete but could not.
/// </summary>
public sealed record BeforeStepPrune(
    IReadOnlyList<string> Deleted,
    int Kept,
    IReadOnlyList<string> Failed,
    int Keep,
    int KeepDays)
{
    /// <summary>
    /// The one log line a prune writes, naming each copy deleted. Said by whoever ran the prune -
    /// this is a module, and the console or logger belongs to its caller.
    /// </summary>
    public string Describe()
    {
        var rule = KeepDays > 0
            ? $"older than {KeepDays} days beyond the newest {Keep}"
            : $"beyond the newest {Keep}";

        var line = Deleted.Count == 0
            ? $"Before-step backups: none deleted {rule}; {Kept} kept."
            : $"Before-step backups: deleted {Deleted.Count} {rule} "
              + $"({string.Join(", ", Deleted.Select(Path.GetFileName))}); {Kept} kept.";

        return Failed.Count == 0
            ? line
            : line + $" Could not delete {Failed.Count}: {string.Join("; ", Failed)}.";
    }
}

/// <summary>
/// Retention for the copies <see cref="SchemaMigrator"/> takes before a schema step
/// (<c>messages-&lt;stamp&gt;-before-&lt;step&gt;[-n].db</c>): every copy taken in the last
/// <c>keepDays</c> days and, beyond that, the newest <c>keep</c>.
///
/// <para>
/// <b>ONLY ITS OWN FILES ARE EVER DELETED.</b> The folder is shared with the daily copies
/// (<c>-daily.db</c>, <c>DailyBackup</c>'s own rule), <c>--backup</c>'s manual ones (<c>-manual.db</c>,
/// kept forever) and folders such as <c>skills-before-builtins</c>. A name that does not parse as a
/// before-step copy is not one, and is left alone.
/// </para>
///
/// <para>
/// <b>AGE IS THE STAMP IN THE NAME, NEVER THE MODIFICATION TIME.</b> A copy restored, synced or
/// touched gets a new mtime, and that must not make a year-old copy look like yesterday's.
/// </para>
/// </summary>
public static partial class BeforeStepBackups
{
    public const int DefaultKeep = 10;

    public const int DefaultKeepDays = 30;

    private const string StampFormat = "yyyyMMdd-HHmmss";

    /// <summary>The before-step copies in <paramref name="directory"/>, newest first.</summary>
    public static IReadOnlyList<(string Path, DateTimeOffset Taken)> Existing(string directory) =>
        !Directory.Exists(directory)
            ? []
            : [.. Directory.EnumerateFiles(directory, "messages-*-before-*.db")
                .Select(path => (Path: path, Taken: StampOf(path)))
                .Where(copy => copy.Taken is not null)
                .Select(copy => (copy.Path, Taken: copy.Taken!.Value))
                .OrderByDescending(copy => copy.Taken)
                .ThenByDescending(copy => Path.GetFileName(copy.Path), StringComparer.Ordinal)];

    /// <summary>
    /// Deletes every before-step copy older than <paramref name="keepDays"/> days (0: no age rule)
    /// and beyond the newest <paramref name="keep"/> (at least 1). <paramref name="justTaken"/>, the
    /// copy a migration has just written, is never deleted whatever the settings. Never throws for a
    /// file: a deletion that fails is named in <see cref="BeforeStepPrune.Failed"/> and the rest go on.
    /// </summary>
    public static BeforeStepPrune Prune(
        string directory,
        DateTimeOffset now,
        int keep = DefaultKeep,
        int keepDays = DefaultKeepDays,
        string? justTaken = null)
    {
        keep = Math.Max(1, keep);
        keepDays = Math.Max(0, keepDays);

        IReadOnlyList<(string Path, DateTimeOffset Taken)> copies;

        try
        {
            copies = Existing(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new BeforeStepPrune([], 0, [$"{directory} ({exception.Message})"], keep, keepDays);
        }

        var deleted = new List<string>();
        var failed = new List<string>();
        var kept = 0;

        for (var index = 0; index < copies.Count; index++)
        {
            var (path, taken) = copies[index];

            var keepIt = index < keep
                || (keepDays > 0 && now - taken < TimeSpan.FromDays(keepDays))
                || (justTaken is not null && SamePath(path, justTaken));

            if (keepIt)
            {
                kept++;
                continue;
            }

            try
            {
                File.Delete(path);
                deleted.Add(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{Path.GetFileName(path)} ({exception.Message})");
            }
        }

        return new BeforeStepPrune(deleted, kept, failed, keep, keepDays);
    }

    /// <summary>When the copy was taken, read from its name (UTC), or null when it is not one.</summary>
    public static DateTimeOffset? StampOf(string path)
    {
        var match = Name().Match(Path.GetFileName(path));

        return match.Success && DateTime.TryParseExact(
            match.Groups["stamp"].Value, StampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.Zero)
            : null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal);

    [GeneratedRegex(@"^messages-(?<stamp>\d{8}-\d{6})-before-[^/\\]+\.db$", RegexOptions.CultureInvariant)]
    private static partial Regex Name();
}
