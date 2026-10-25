using Harness.Messaging;
using Microsoft.Extensions.Configuration;

namespace Harness.Host;

/// <summary>
/// Where the copies of the database go and how many of each kind survive, read once from
/// configuration (so <c>Backups__Directory</c> and the rest work from the environment too).
///
/// <para>
/// <b>ONE FOLDER FOR THE MIGRATOR AND THE DAILY BACKUP.</b> The before-step copies a migration takes
/// are pruned by every daily run, which only works when both are handed the same directory. Program.cs
/// and the operator commands build their <see cref="SchemaMigrator"/> and <see cref="DailyBackup"/>
/// from here rather than reading the keys a second time. <c>--backup</c>'s manual copy and the copy
/// <c>--restore</c> takes first go to <see cref="Directory"/> too, and <c>--doctor</c>'s backups
/// section reports on it.
/// </para>
///
/// <para>Configuration, not tenant settings: no person acts on these.</para>
/// </summary>
public sealed record BackupSettings(
    string Directory,
    int Keep = DailyBackup.DefaultKeep,
    int KeepBeforeStep = BeforeStepBackups.DefaultKeep,
    int KeepBeforeStepDays = BeforeStepBackups.DefaultKeepDays)
{
    /// <summary>
    /// <c>Backups:Directory</c> (default <c>&lt;dataRoot&gt;/backups</c>), <c>Backups:Keep</c> (daily
    /// copies, default 7), <c>Backups:KeepBeforeStep</c> (default 10) and
    /// <c>Backups:KeepBeforeStepDays</c> (default 30, 0 for no age rule).
    /// </summary>
    public static BackupSettings From(IConfiguration configuration, string dataRoot) => new(
        configuration["Backups:Directory"] is { Length: > 0 } directory
            ? directory
            : DefaultDirectory(dataRoot),
        int.TryParse(configuration["Backups:Keep"], out var keep)
            ? keep
            : DailyBackup.DefaultKeep,
        int.TryParse(configuration["Backups:KeepBeforeStep"], out var keepBeforeStep)
            ? keepBeforeStep
            : BeforeStepBackups.DefaultKeep,
        int.TryParse(configuration["Backups:KeepBeforeStepDays"], out var keepBeforeStepDays)
            ? keepBeforeStepDays
            : BeforeStepBackups.DefaultKeepDays);

    /// <summary>Every default, for a caller with no configuration to read.</summary>
    public static BackupSettings Defaults(string dataRoot) => new(DefaultDirectory(dataRoot));

    public static string DefaultDirectory(string dataRoot) => Path.Combine(dataRoot, "backups");

    /// <summary>A migrator that writes its before-step copy here and prunes by these settings.</summary>
    public SchemaMigrator MigratorFor(string database) => new(
        database, Directory, keepBeforeStep: KeepBeforeStep, keepBeforeStepDays: KeepBeforeStepDays);

    /// <summary>The daily backup over the same folder, so its run prunes the migrator's copies.</summary>
    public DailyBackup DailyBackupFor(string database) =>
        new(database, Directory, Keep, KeepBeforeStep, KeepBeforeStepDays);

    /// <summary>The daily backup of whichever database <paramref name="database"/> keeps, over the same folder.</summary>
    public DailyBackup DailyBackupFor(IDatabaseMaintenance database) =>
        new(database, Directory, Keep, KeepBeforeStep, KeepBeforeStepDays);
}
