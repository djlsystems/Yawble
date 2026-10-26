using Harness.Contracts;
using Harness.Host.SqlServer;
using Harness.SqlServer;
using Harness.SqlServer.Host;
using Harness.SqlServer.Identity;

namespace Harness.Host;

internal sealed partial class HostComposition
{
    /// <summary>
    /// THE SQL SERVER DATABASE'S SCHEMA, moved forward by its own migrator as SQLite's file is by
    /// SchemaMigrator, before any store is constructed and before the start steps write a row. No
    /// messages.db is created, migrated or opened on this path.
    ///
    /// <para>
    /// THE STORE GUARD FIRST: a build that lacks any store's SQL Server version is refused before it
    /// writes anything at all, schema included, so a start either serves or leaves the database as it
    /// found it. Then every refusal - unreachable, unreadable, the wrong collation, row versioning off, a
    /// newer build's steps, a failed step - is one printed line and a non-zero exit, never a trace.
    /// </para>
    ///
    /// <para>
    /// NO BACKUP IS TAKEN FIRST: the database is not a file this Host can copy, and the server's own
    /// point-in-time restore is the backup. Said once, when a step was applied, where the SQLite start
    /// says where its copy went.
    /// </para>
    /// </summary>
    private async Task<bool> SqlServerSchemaAsync()
    {
        var connection = databaseOptions.Connection!;

        try
        {
            StoreGuard.Check(StoreContracts.Required, SqlServerStoreRegistry.Registered);
        }
        catch (StoresMissingException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Environment.ExitCode = 1;
            return false;
        }

        // ONE CONTROL PROCESS PER DATABASE, taken here as the data root's lock is taken before the SQLite
        // migration: after the operator commands, which never take it, and before the schema moves, so a
        // second Host never migrates underneath a serving one. See SqlServerControlLock.
        try
        {
            controlLock = await SqlServerControlLock.AcquireAsync(
                connection,
                say: line => Console.Error.WriteLine(line),
                lost: sentence =>
                {
                    Console.Error.WriteLine(sentence);
                    Environment.ExitCode = 1;
                    controlLost = true;
                    app?.Lifetime.StopApplication();
                });
        }
        catch (Exception exception) when (SqlServerStartRefusal.Sentence(exception, connection) is { } sentence)
        {
            Console.Error.WriteLine(sentence);
            Environment.ExitCode = 1;
            return false;
        }

        var schema = new SqlServerMigrator(connection);

        try
        {
            await schema.ApplyAsync(SqlServerSchemaModules.All);

            if (schema.Applied > 0)
            {
                Console.WriteLine($"Schema updated. No backup was taken first: {new SqlServerDatabaseMaintenance().BackupElsewhere}");
            }
        }
        catch (Exception exception) when (SqlServerStartRefusal.Sentence(exception, connection) is { } sentence)
        {
            Console.Error.WriteLine(sentence);

            // RECORDED AS WELL AS PRINTED when the database answered, as the SQLite start records its
            // refusal: a step from a newer build, or a step that failed, finds diagnostic_events there
            // on any database that has started before. A write that cannot land is swallowed by the log.
            if (SqlServerStartRefusal.IsRecorded(exception))
            {
                await new SqlServerDiagnosticsLog(connection, DiagnosticRetention.ForDataRoot(dataRoot))
                    .WriteAsync(
                        DiagnosticSeverity.Error,
                        DiagnosticKinds.DatabaseMigrationFailed,
                        DiagnosticSources.Database,
                        exceptionType: exception.GetType().FullName,
                        message: exception.Message,
                        detail: exception is Harness.Messaging.MigrationFailedException failed
                            ? $$"""{"step":"{{failed.StepId}}"}"""
                            : null);
            }

            Environment.ExitCode = 1;
            return false;
        }

        return true;
    }
}
