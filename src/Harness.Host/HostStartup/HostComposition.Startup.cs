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
    internal async Task<bool> StartupAsync()
    {
        app.Lifetime.ApplicationStopped.Register(pluginEvents.Dispose);
        admissionHoldWriter.Logger = app.Services.GetRequiredService<ILogger<AdmissionHoldWriter>>();
        // Made now, not on first use: it follows every run from the first one on.
        app.Services.GetRequiredService<AgentStepFollow>();

        {
            var runAs = app.Services.GetRequiredService<AgentLaunchUser>();
            if (runAs.Refuses)
            {
                // Loud, because every member, Concierge and host git will refuse.
                app.Logger.LogError(
                    "Agent launch: {Mode} - no agent child, Concierge or host git will run: {Reason}", runAs.Mode, runAs.Reason);
            }
            else
            {
                app.Logger.LogInformation("Agent launch: {Mode} - {Reason}", runAs.Mode, runAs.Reason);
            }

            AgentLaunchRecord.Write(dataRoot, runAs, app.Logger);

            var memoryLimits = app.Services.GetRequiredService<RunMemoryLimits>();
            var runLimit = memoryLimits.Limit();
            var heavyNow = tenantSettings.HeavyRunMemoryLimit(0, 0);
            if (memoryLimits.NotEnforced)
            {
                // Under rlimit with nothing set, runs get no limit and no heavy raise: the line says so and nothing more.
                app.Logger.LogInformation("{RunMemoryLimits}", memoryLimits.LogLine);
            }
            else
            {
                app.Logger.LogInformation("{RunMemoryLimits} (each run now: {Limit}; a heavy run with no other running: {Heavy})", memoryLimits.LogLine,
                    runLimit.Mb is { } mb ? $"{mb} MB, as {runLimit.Source}" : $"no limit, as {runLimit.Source}",
                    heavyNow.Mb is { } heavyMb ? $"{heavyMb} MB, as {heavyNow.Source}" : $"no limit, as {heavyNow.Source}");
            }

            // For --doctor, which reads the Host's own figures rather than working them out as itself.
            WipRecord.Keep(dataRoot, tenantSettings, memoryLimits, app.Logger);

            var memberTemp = app.Services.GetRequiredService<MemberTempRoot>();
            app.Logger.LogInformation("Member temporary folders: {Path} - {Reason}", memberTemp.Path, memberTemp.Reason);
        }

        // THE ADDRESS EVERY MEMBER IS TOLD TO CALL, printed for the reason the dev server prints its proxy
        // target: when this is wrong it is wrong SILENTLY - a worktree's members write their progress to
        // another instance and nothing else says so. One line, at the one
        // moment somebody is looking at this console.
        Console.WriteLine($"Members call back on {memberBaseAddress}.");

        // AWAITED, before anything can serve a request and before PumpService takes a pass. A registry that
        // has not been restored is not wrong, only empty: it answers "no such team" for every team its own
        // database still holds, which presents as "my teams are gone" rather than as a startup problem.
        //
        // CAUGHT the same way the migration above is. RestoreAsync converts what would otherwise be
        // PER-REQUEST failures - an unwritable Directory.CreateDirectory, an AgentEnvironment.ForContainerAsync that
        // cannot mint a credential - into a startup failure, and an uncaught one would be a thirty-line
        // trace instead of a sentence. TeamRestorationFailedException is thrown
        // from RestoreAsync itself, where the team being restored is known.
        // BUILT-IN SKILLS FROM THE BUILD, on every start, BEFORE any team is restored. The index
        // is rebuilt from what this build ships, so changing a built-in in code reaches every agent on the
        // next start with no manual step. A volume that still holds a skill-files folder has those files
        // moved to the backup and the custom ones imported; without such a folder this does nothing.
        // All of it precedes the restore because every restored prompt lists its role's skills.
        var skills = app.Services.GetRequiredService<ISkillStore>();
        var paths = app.Services.GetRequiredService<TeamPaths>();
        var skillDirectory = app.Services.GetRequiredService<SkillDirectory>();

        foreach (var moved in await skills.ReplaceBuiltInsAsync(BuiltInSkills.Drafts(), DateTimeOffset.UtcNow))
        {
            Console.WriteLine(
                $"WARNING: a custom skill held a built-in skill's name and was relabelled: {moved}.");
        }

        // THE INSTALLED PLUGINS' SKILLS, now and after every rescan however it is started: locked rows of
        // kind `plugin`, named `plugin-<id>[-<name>]`, never in a prompt's list. See `PluginSkills`.
        await pluginCatalog.Attach(async scan =>
        {
            foreach (var line in await PluginSkills.SyncAsync(skills, scan))
            {
                Console.WriteLine($"WARNING: {line}");
            }
        });

        var tenantSkillMigration = await SkillMigration.RunAsync(
            [("skills", paths.TenantSkills), ("skill-drafts", paths.TenantSkillDrafts)],
            paths.SkillBackups,
            skills);

        Program.ReportSkillMigration(tenantSkillMigration, paths.SkillBackups);

        await skillDirectory.RefreshAsync(skills);

        RestoreReport restoreReport;
        TeamRegistry registry;

        try
        {
            registry = app.Services.GetRequiredService<TeamRegistry>();
            restoreReport = await registry.RestoreAsync();
        }
        catch (TeamRestorationFailedException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Environment.ExitCode = 1;

            return false;
        }

        // A MANAGER'S ROSTER NAMES ITS PLUGIN MEMBERS' DESCRIPTIONS AND SKILLS, so a rescan that changes
        // them re-prompts every member, the same as a skill write does. Attached after the restore, which
        // composed every prompt from the plugins already loaded.
        await pluginCatalog.Attach(_ => registry.RepromptAllAsync());

        // A PLUGIN MEMBER STILL WAITING FOR A PERSON reads Needs you again after a restart: the mark is
        // read from its settings, never stored.
        await app.Services.GetRequiredService<PluginSetup>().RefreshAllAsync();

        // A PLACED TEAM WHOSE VOLUME IS NOT THERE. Said out loud for the reason the blocked moves above
        // are, and it is the louder of the two - but NOT an exit: exiting 1 would take every OTHER team
        // down with it - default-rooted ones included - into a restart loop with no in-product way out.
        //
        // The team is up and visible; its members are marked and refuse at their wake, which is what stops
        // one of them running in the Host's own directory.
        foreach (var root in restoreReport.UnreachableRoots)
        {
            Console.WriteLine(
                $"WARNING: team '{root.TeamId}' is placed at {root.Root}, which could not be reached "
                + $"({root.Reason}). The team is up but its members will refuse to run until the folder is "
                + "reachable and the Host is restarted.");
        }

        // A STORED TEAM THAT COULD NOT BE REBUILT AT ALL, which is the louder of the two and is here for
        // the identical reason. A `teams` row whose id is not a legal container name must not throw out of
        // RestoreAsync, past the catch above - an `ArgumentException`, not `TeamRestorationFailedException` -
        // or it escapes `Main`, the Host never builds an IHost at all, and its supervisor restarts it into
        // the same refusal forever with every other team down too.
        //
        // The row is left exactly where it is, and this is the only thing that says so. Nothing repairs it
        // and nothing deletes it: a team's data is the operator's, and the recovery here is a person
        // reading this line.
        foreach (var team in restoreReport.Unrestorable)
        {
            Console.WriteLine(
                $"WARNING: team '{team.TeamId}' could not be restored and was skipped - {team.Reason} "
                + "Every other team started normally. Its row is still in the database; remove or rename "
                + "it by hand if you no longer want it.");
        }

        // THE REMOVALS A DELETION OR RESET COULD NOT FINISH, retried now that the teams are restored and a
        // live team or member can be told from a dead one. Before any member runs, so nothing is writing in
        // a folder being emptied. Never fatal: a folder that still cannot be removed stays recorded, is said
        // here, and is retried at the next start or on request.
        try
        {
            var retried = await app.Services.GetRequiredService<UnfinishedRemovalRetry>().RetryAsync();

            foreach (var removal in retried)
            {
                Console.WriteLine(removal.Finished
                    ? $"Removal finished at start: {removal.Path}."
                    : $"WARNING: removal still unfinished at start: {removal.Path} - "
                      + (removal.Note ?? $"{removal.Remaining.Count} path(s) remain: {string.Join(", ", removal.Remaining)}"));
            }

            if (retried.Count > 0)
            {
                await app.Services.GetRequiredService<TenantLogging>().WriteAsAsync(
                    null, null, TenantActions.RemovalRetried, null, null,
                    new { atStart = true, retried = retried.Select(RemovalEndpoints.Detail).ToArray() });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.WriteLine($"WARNING: unfinished removals could not be retried at start: {exception.Message}");
        }

        // PREVIEW ENV FILES a Host stopped rather than its sessions ended would otherwise keep. Before any
        // Concierge starts. Never fatal: the removal never throws.
        app.Services.GetRequiredService<PreviewEnvFiles>().RemoveAll();

        // CONCIERGE ATTACHMENTS PAST THEIR RETENTION, which a Host stopped rather than its sessions ended
        // would otherwise keep for good. Before any Concierge starts. Never fatal.
        try
        {
            foreach (var left in await app.Services.GetRequiredService<ConciergeAttachments>().RemoveExpiredAsync(DateTimeOffset.UtcNow))
            {
                Console.WriteLine($"WARNING: an expired Concierge attachment could not be removed at start: {left}");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.WriteLine($"WARNING: expired Concierge attachments could not be removed at start: {exception.Message}");
        }

        // Any per-team skill folders still on disk, now that the teams are registered and their
        // roots resolve. No skill is team-scoped: each folder is migrated like the tenant's and
        // moved to the backup, and a skill imported from one re-prompts everybody so it is listed.
        var teamSkillMigration = await SkillMigration.RunAsync(
            [.. registry.All(withRoot: true).SelectMany(team => new[]
            {
                ($"teams/{team.Id}/skills", paths.SkillsFor(team.Id)),
                ($"teams/{team.Id}/skill-drafts", paths.SkillDraftsFor(team.Id)),
            })],
            paths.SkillBackups,
            skills);

        Program.ReportSkillMigration(teamSkillMigration, paths.SkillBackups);

        if (teamSkillMigration.Imported.Count > 0)
        {
            await skillDirectory.RefreshAsync(skills);
            await registry.RepromptAllAsync();
        }

        // Git prerequisite check
        //
        // COLLECTED AS WELL AS PRINTED. A `PathSearch` miss is a launch failure waiting to happen, and a
        // line in a terminal that scrolls is not evidence anybody can find later. The list is
        // recorded with the rest of the startup facts below, in one pass, rather than here: the point of
        // those rows is that they are read together as "what was this instance configured as".
        missingPrerequisites = new List<string>();

        var gitExecutable = builder.Configuration["GitExecutable"] ?? "git";
        if (SystemCommand.Find(gitExecutable) is null)
        {
            missingPrerequisites.Add(gitExecutable);

            Console.WriteLine(
                "WARNING: git was not found in a root-owned system directory - the Repos panel cannot run until " +
                "git is installed and the Host is restarted.");
        }

        // GitHub CLI prerequisite check
        if (PathSearch.Find("gh") is null)
        {
            missingPrerequisites.Add("gh");

            Console.WriteLine(
                "WARNING: gh was not found on this machine's PATH - agents use it for pull requests; " +
                "the platform does not invoke it.");
        }

        // ANNOUNCED, never inferred. A missing/empty member-agent allowlist refuses hiring rather than
        // running on a guessed catalog name, and is fixed in one click by somebody who knows to make it.
        // There is no prompt to announce: every member is told its job by its role.
        var restoredTeams = registry.All();

        foreach (var pausedTeam in restoredTeams.Where(team => team.Paused))
        {
            Console.WriteLine(
                $"WARNING: team '{pausedTeam.Name}' is paused. No new work will be delivered until someone "
                + "resumes it.");
        }

        // Every team is one somebody can hire into, so there is nothing to exclude and the condition each
        // line reports is one a person can act on.
        var unchosenAllowlists = restoredTeams
            .Where(t => t.MemberAgents is not { Count: > 0 })
            .Select(t => t.Name)
            .ToList();

        if (unchosenAllowlists.Count > 0)
        {
            Console.WriteLine(
                $"No Agent allowlist for new members on {string.Join(", ", unchosenAllowlists)} - "
                + "hiring is refused until one is chosen under Dynamic members in Team Settings.");
        }

        // EXISTING ROWS WARN AND SERVE, never refuse.
        //
        // A guard fails in whichever direction is RECOVERABLE, and there is nothing wrong with the data
        // here - only with what someone may do next. Startup already names every NULL prompt and every
        // headless preset with no usage format, and RestoreAsync skips and reports an illegal teams row
        // rather than dying on it. `AddContainerAsync`, `UpdateContainerAsync` and `PUT /api/agents` all
        // refuse the illegal pair; this is the fourth site, for a pair already sitting in the database.
        var firehoseHolders = Program.FirehoseHolders(loadedCatalog, restoredTeams);

        if (firehoseHolders.Count > 0)
        {
            // Named a real recovery ONLY: there is no `subscribes` editor anywhere (member settings
            // can repoint the Agent and the Prompt, never the subscription - see `UpdateContainer` in
            // this file), and "point it at another Agent" has no seeded target, since nothing seeds a
            // `languageModel: false` preset. The two that actually work are on the Agents screen - open
            // this preset and clear its Language model checkbox, which `PUT /api/agents` always allows
            // in that direction - or delete the member and hire a fresh one: `addMember` on the SPA
            // sends no `subscribes` at all, so a member hired through the UI never carries a firehose
            // subscription to begin with.
            Console.WriteLine(
                $"WARNING: {string.Join(", ", firehoseHolders)} subscribe(s) to a high-volume type on a "
                + "preset that is now a language model - it will be woken by every status line every "
                + "worker writes. On the Agents screen, open that preset and clear its Language model "
                + "checkbox, or delete the member and hire a fresh one - a hire through the UI never "
                + "carries a high-volume subscription.");
        }

        // AFTER restoration, because it walks the containers restoration has just brought back - there is
        // nothing to hand work to before that. Says so when it did anything: work resumed silently is work
        // nobody knows was ever at risk, and an interrupted run is reported as a failure somebody has to
        // read to understand why their instruction came back unfinished.
        // FIRST, what deleted teams left queued: removed and logged, a live team's never touched.
        await app.Services.GetRequiredService<PendingDeliveriesAtStart>().SweepAsync();

        var resumed = await app.Services.GetRequiredService<ContainerHost>().ResumePendingAsync();

        // BOTH numbers, and a line whenever either is non-zero. An interrupted run is reported to the log
        // and is exactly the thing whoever dispatched it needs told about, so a restart that cut runs short
        // must say so even when none of them could be re-offered.
        if (resumed.Reoffered > 0 || resumed.Interrupted > 0 || resumed.ManagersWoken > 0)
        {
            Console.WriteLine(
                $"Resumed {resumed.Reoffered} queued item(s) and reported {resumed.Interrupted} "
                + $"interrupted run(s) from the last stop; told {resumed.ManagersWoken} Manager(s) cut off "
                + "by the restart to carry on.");
        }

        // THE STARTUP AND LIFECYCLE FACTS, IN ONE PASS.
        //
        // Every line below this point is also PRINTED, and printed alone it is lost: the host's stdout is
        // a window with a bounded scrollback. These are the lines that answer "WHAT WAS THIS INSTANCE EVEN CONFIGURED AS" on a cold start, and they
        // are worth a row each precisely because nobody thinks to copy them out until it is too late.
        //
        // BOUNDED BY BOOTS, NOT BY TRAFFIC. That is what makes this family affordable where per-request
        // timings are not: an instance restarted twenty times a day writes a few dozen rows, not a few
        // hundred thousand.
        //
        // HERE rather than at each printing site, because these rows are read TOGETHER. A reader opening
        // the screen after a bad night wants one contiguous block describing the instance, not seven facts
        // scattered between whatever else was happening.
        //
        // AWAITED, before anything can serve. A startup fact recorded after the first request is a fact
        // that is missing from exactly the window somebody is investigating.
        {
            var startup = app.Services.GetRequiredService<DiagnosticsRecorder>();

            // ON SQL SERVER THERE IS NO FILE: the row names the provider, never the connection string,
            // which may hold a password, and there is no journal mode to report.
            var onSqlite = databaseOptions.Provider == DatabaseProvider.Sqlite;

            // FIRST, so the trim cannot remove the rows this boot is about to write, and so a host that
            // stays up for months is not the reason its own log is unbounded. The store trims from the
            // write path as well, throttled - this is the pass that happens even on a quiet instance.
            await startup.TrimAsync();

            await startup.WriteAsync(
                DiagnosticSeverity.Info,
                DiagnosticKinds.StartupInstance,
                DiagnosticSources.Startup,
                message: $"{instance} serving from {dataRoot}",
                detail: new
                {
                    instance,
                    dataRoot,
                    database = onSqlite ? database : "sqlserver",

                    // THE PORT, by way of the address members are told to call - the same fact, and the one
                    // spelling of it that has ever been wrong in a way anybody noticed.
                    membersCallBack = memberBaseAddress,
                    teams = restoredTeams.Count,

                    // SO A READER CAN TELL A QUIET INSTANCE FROM A TRIMMED ONE. A bounded log that does not
                    // say what its bound is looks identical to one that recorded nothing.
                    retentionDays = diagnosticsRetention.MaxAge.TotalDays,
                    retentionRows = diagnosticsRetention.MaxRows,
                });

            // WHAT THE PRAGMA ACTUALLY ANSWERED, rather than firing it blind and throwing it away. Every
            // other guarantee over this file assumes WAL - a reader never blocking a writer, `--backup`
            // being safe against a live database, two stores writing one file - so a file that did not
            // enter it is an error rather than a curiosity, and saying so is the whole value of the row.
            //
            // NULL IS "NOT MEASURED" AND IS NOT A FAILURE, the distinction CredentialUseRunner makes about
            // a probe that could not see: a pragma that could not be read must not convict.
            if (onSqlite)
            await startup.WriteAsync(
                migrator.JournalMode is null ? DiagnosticSeverity.Info
                    : migrator.JournalMode.Equals("wal", StringComparison.OrdinalIgnoreCase)
                        ? DiagnosticSeverity.Info
                        : DiagnosticSeverity.Error,
                DiagnosticKinds.DatabaseJournalMode,
                DiagnosticSources.Database,
                message: migrator.JournalMode ?? "(unknown)",
                detail: new { journalMode = migrator.JournalMode });

            // ONLY WHEN ONE WAS TAKEN. ApplyAsync answers null when nothing was pending, which is every
            // ordinary restart, and a row per boot saying "no backup" would bury the boots where there was
            // one.
            if (schemaBackup is not null)
            {
                await startup.WriteAsync(
                    DiagnosticSeverity.Info,
                    DiagnosticKinds.StartupBackupWritten,
                    DiagnosticSources.Startup,
                    message: schemaBackup);
            }

            foreach (var pausedTeam in restoredTeams.Where(team => team.Paused))
            {
                await startup.WriteAsync(
                    DiagnosticSeverity.Warning,
                    DiagnosticKinds.StartupTeamPaused,
                    DiagnosticSources.Startup,
                    message: pausedTeam.Name,
                    detail: new { team = pausedTeam.Name });
            }

            foreach (var team in unchosenAllowlists)
            {
                await startup.WriteAsync(
                    DiagnosticSeverity.Warning,
                    DiagnosticKinds.StartupNoAgentAllowlist,
                    DiagnosticSources.Startup,
                    message: team,
                    detail: new { team });
            }

            foreach (var preset in unmeasuredHeadlessPresets)
            {
                await startup.WriteAsync(
                    DiagnosticSeverity.Warning,
                    DiagnosticKinds.StartupPresetWithoutUsageFormat,
                    DiagnosticSources.Startup,
                    message: preset,
                    detail: new { preset });
            }

            // A `PathSearch` MISS IS A LAUNCH FAILURE THAT HAS NOT HAPPENED YET, which is why it
            // is here rather than only under `process.*`: by the time a run fails for it, the reason is a
            // child process's own "cannot find the file", and nothing connects that to a host that said so
            // at boot.
            foreach (var executable in missingPrerequisites)
            {
                await startup.WriteAsync(
                    DiagnosticSeverity.Warning,
                    DiagnosticKinds.StartupPrerequisiteMissing,
                    DiagnosticSources.Startup,
                    message: executable,
                    detail: new { executable });
            }
        }

        return true;
    }
}
