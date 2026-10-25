using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using ModelContextProtocol.AspNetCore;
using Harness.Containers;
using Harness.Backlog;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Host.Capacity;
using Harness.Host.Solutions;
using Harness.Identity;
using Harness.Kanban;
using Harness.Messaging;
using Harness.Skills;

namespace Harness.Host;

internal sealed partial class HostComposition
{
    internal async Task<bool> OperatorAsync()
    {
        // WHERE EVERY COPY OF THE DATABASE GOES, and how many of each kind survive: read once, here, ahead
        // of the operator commands, so `migrate` from a recovery switch, the startup migration and the
        // daily backup all use one folder and one rule. See BackupSettings.
        backupSettings = BackupSettings.From(builder.Configuration, dataRoot);

        // An operator command NEVER serves. Returning here is what makes recovery something a person at the
        // machine does rather than a capability of the running system - see OperatorCommands.
        //
        // INSIDE the same handling the migration below gets, because every one of its failures is one of
        // those three: --backup with a blocked destination throws BackupFailedException, --restore's own
        // pre-restore copy the same, and --reset-password migrates before it can read an account. Left
        // outside, an operator running the recovery switch would meet a thirty-line stack trace, one seam
        // EARLIER than the catch that prevents it.
        try
        {
            var outcome = await OperatorCommands.TryRunAsync(args, dataRoot, Console.Out, backups: backupSettings);

            if (outcome != OperatorOutcome.NotAnOperatorCommand)
            {
                // A REFUSAL exits non-zero, for the same reason the catch below does: the scripts wrapping
                // these commands read $LASTEXITCODE, and a supervisor told "served and shut down cleanly"
                // restarts straight back into the same refusal.
                if (outcome == OperatorOutcome.Refused) Environment.ExitCode = 1;

                return false;
            }
        }
        catch (Exception exception)
            when (exception is SchemaFromTheFutureException
                      or BackupFailedException
                      or MigrationFailedException)
        {
            Console.Error.WriteLine(exception.Message);

            // Non-zero for the same reason as below: a refusal that exits 0 is a refusal no script wrapping
            // it - scripts/backup.ps1 and scripts/restore.ps1 both check $LASTEXITCODE - can see.
            Environment.ExitCode = 1;

            return false;
        }

        // AFTER the operator commands and BEFORE the migration. Both halves of that placement matter.
        //
        // After, because an operator command is not a host: --backup uses SQLite's own VACUUM INTO and is
        // deliberately safe to run against a LIVE database, so taking the root would refuse the one command
        // whose whole point is that it does not need to.
        //
        // Before, because the migration takes a backup and rewrites the schema, and doing that underneath a
        // host already serving the same file is the corruption this lock exists to prevent - not something
        // to discover one step later.
        //
        DataRootLock taken;

        try
        {
            taken = DataRootLock.Acquire(dataRoot);
        }
        catch (DataRootInUseException exception)
        {
            Console.Error.WriteLine(exception.Message);

            // Set rather than returned, and non-zero for the reason every other refusal here is: a
            // supervisor told "served and shut down cleanly" restarts straight back into it.
            Environment.ExitCode = 1;

            return false;
        }

        // `using var` at this scope, so the root is released on every path out of the file below: a schema
        // refusal, an exception, or an ordinary shutdown after app.Run returns.
        held = taken;

        // THE SCHEMA, moved forward by the provider's own migrator before any store is constructed: SQLite's
        // file (`SqliteSchemaAsync`, below) or the SQL Server database (`SqlServerSchemaAsync`). A refusal
        // has printed its sentence and set the exit code.
        if (!await (databaseOptions.Provider == DatabaseProvider.SqlServer ? SqlServerSchemaAsync() : SqliteSchemaAsync()))
        {
            return false;
        }

        // THE LEDGER'S START AND THE INSTANCE'S ID, before anything appends: the one-time backfill of
        // `usage_ledger` and `workflow_ledger` from the log, and `instance.id`. See LedgerStart. A failure
        // is printed and the host starts anyway - the backfill's own row is what marks it done, so the next
        // start tries again, and rows recovered then are keyed so none is counted twice.
        ledgerStart = null;
        var (ledgerRows, outcomeRows) = StartRowsFor(databaseOptions, database);
        if (ledgerRows is not null)
        try
        {
            var ledger = ledgerStart = await ledgerRows.RunAsync();

            if (ledger.BackfilledRuns is { } recovered)
            {
                Console.WriteLine(
                    $"Usage ledger started: {recovered} run(s) and {ledger.BackfilledWorkflows} workflow close(s) recovered from the log.");
            }
        }
        // ANY FAILURE, not only SQLite's: a backfill that met a stamp it could not parse must not stop the
        // Host from starting any more than a locked database does. Rows are keyed, so the retry counts none twice.
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"The usage ledger's start did not finish and is tried again at the next start: {exception.Message}");
        }

        // BACKLOG ITEMS TAKE THEIR DISPATCH WORKFLOW'S OUTCOME, once, for the items dispatched before the
        // link writer did it for them. One transaction; a failure leaves it to the next start.
        try
        {
            if (outcomeRows is not null && await outcomeRows.RunAsync() is { } inherited)
            {
                Console.WriteLine($"Backlog outcomes: {inherited} item(s) took the outcome their dispatch workflow serves.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"The backlog outcome back-fill did not finish and is tried again at the next start: {exception.Message}");
        }

        return true;
    }

    // THE SQLITE FILE'S SCHEMA, moved here unchanged from OperatorAsync: the migration, its backup line and
    // its refusals.
    private async Task<bool> SqliteSchemaAsync()
    {
        // BEFORE any store is constructed. No store constructor creates schema: an order set by the
        // declaration sequence below would work only while every statement was CREATE TABLE IF NOT
        // EXISTS, and a step that must run once could not live there.
        //
        // Same ordering rule as LoadLabelsAsync further down, and for the same reason: a component that
        // reaches a half-built schema fails in a way that reads as a data problem rather than a startup one.
        //
        // Message steps before auth steps. This expression is the only place that order is decided.
        //
        // All three refusals are CAUGHT and printed. An uncaught exception here scrolls thirty lines of
        // stack trace and reads as "the host crashed" - which is exactly the failure port-free.ps1 exists to
        // prevent, one layer down. The operator needs the sentence, not the trace. MigrationFailedException
        // is the one that matters most in practice: a step whose SQL fails is the ORDINARY migration
        // failure.
        // HOISTED OUT OF THE `try` so the two facts it produces outlive the block. The migrator
        // RETURNS rather than prints, and both of its answers are things this instance could not say about
        // itself afterwards: the backup's path, which a terminal would lose at exactly the moment a
        // migration went wrong, and what `PRAGMA journal_mode` actually replied. Both are recorded to the diagnostics log further down, once the store that
        // holds them exists - which is only true AFTER this migration has created its table.
        //
        // The copies taken before a schema step go to the backups folder and are pruned after each one is
        // taken and by every daily backup: the last `Backups:KeepBeforeStepDays` days (default 30, 0 for no
        // age rule) plus the newest `Backups:KeepBeforeStep` (default 10).
        migrator = backupSettings.MigratorFor(database);
        schemaBackup = null;

        try
        {
            schemaBackup = await migrator.ApplyAsync(SchemaModules.All);

            // One line, and only when the schema actually moved - ApplyAsync answers null when nothing was
            // pending, which is every ordinary restart. A backup taken silently is one nobody can find at
            // the moment they need it, and this copy exists precisely for the moment a migration went wrong.
            if (schemaBackup is not null)
            {
                Console.WriteLine($"Schema updated. Backup written to {schemaBackup}");
            }

            if (migrator.LastPrune is { } pruned) Console.WriteLine(pruned.Describe());
        }
        catch (Exception exception)
            when (exception is SchemaFromTheFutureException
                      or BackupFailedException
                      or MigrationFailedException)
        {
            Console.Error.WriteLine(exception.Message);

            // RECORDED AS WELL AS PRINTED, and this is the one diagnostics write that has to be made by
            // hand: the host returns three lines below, so nothing downstream of here will ever run, and
            // the store registered with the container does not exist yet.
            //
            // IT DEGRADES RATHER THAN REFUSING, and the shape of that is worth stating. On an instance that
            // has booted before, `diagnostic_events` is already there and this row lands - which is the
            // case an operator meets, because a migration that fails is a migration that had something to
            // apply. On a database so fresh that even this table's own step has not run, the write finds no
            // table and is swallowed, like every other diagnostics write that cannot land. A store that
            // could not record the failure of the migration that creates it is the honest limit of a table
            // observing its own schema, and it is why capturing the host's stdout to a file ships beside
            // this rather than being replaced by it.
            await new SqliteDiagnosticsLog(database, DiagnosticRetention.ForDataRoot(dataRoot))
                .WriteAsync(
                    DiagnosticSeverity.Error,
                    DiagnosticKinds.DatabaseMigrationFailed,
                    DiagnosticSources.Database,
                    exceptionType: exception.GetType().FullName,
                    message: exception.Message,
                    detail: exception is MigrationFailedException failed
                        ? $$"""{"step":"{{failed.StepId}}"}"""
                        : null);

            // NON-ZERO, because a refusal that exits 0 is a refusal the machine cannot see: the host has
            // just said it will not serve, and 0 tells a supervisor that it served and shut
            // down cleanly. A supervisor reading 0 restarts straight back into the same refusal, forever.
            // Set rather than returned: these are top-level statements, and a `return 1;` here would make
            // every other exit path in the file have to carry a value too.
            Environment.ExitCode = 1;

            return false;
        }

        return true;
    }

    // THE START STEPS' ROWS, the provider's own, and none while the store guard would refuse the start:
    // see StartRows, which asks the guard's question of the registry the stores are registered from.
    private static (ILedgerStartRows? Ledger, IOutcomeStartRows? Outcome) StartRowsFor(DatabaseOptions options, string database) =>
        StartRows.For(options, database, SqlServerStoreRegistry.Registered);
}
