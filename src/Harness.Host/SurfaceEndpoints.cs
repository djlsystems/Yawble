using System.Globalization;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

public static class SurfaceEndpoints
{
    public static void Map(WebApplication app, string dataRoot)
    {
        app.MapGet("/api/wip", (WipLedger wip, TenantSettings settings, RunMemoryLimits memory) =>
            {
                var view = wip.View();
                var figures = WipRecord.Of(settings, memory);
                return Results.Ok(new { view.Max, view.Running, view.Waiting, figures.Limit, figures.RunMemory });
            })
            .RequirePermit(Permits.Read)
            .WithTags("Admission")
            .WithSummary("Who holds the instance-wide run slots, and who is waiting")
            .WithDescription(
                "`limit` says where `max` comes from: `bound` is `setting` (a Tenant Settings row), "
                + "`configuration` (appsettings), or the default's `cpu` or `memory` bound - the smaller "
                + "of max(1, CPUs - 1) and the container's memory limit / `wip.memoryPerRunMb` - with "
                + "both bounds and a `reason` sentence. Each waiting hold carries the `reason` it waits for: "
                + "\"waiting for a slot\", or \"waiting for memory: ...\" while measured headroom holds it "
                + "(`admission.memoryPercent`, `admission.memoryPressurePercent`).\n\n"
                + "`runMemory` is what the Host applies to each run's own memory, from the decision it logged at "
                + "start: `mechanism` `cgroup` (a child cgroup per run), `rlimit` (RLIMIT_DATA on each process) or "
                + "`none`; `perRunMb` the figure the next run is held to, null when nothing is enforced; `detail` "
                + "the Host's sentence. Read, never estimated.");

        app.MapGet("/api/agents/auth", async (
                AgentAuthProbe probe, AgentLaunchChecks launches, ITeamStore teams, CancellationToken ct) =>
                Results.Ok(AgentLaunchChecks.Attach(
                    AgentAuthReport.MarkReferenced(await probe.ReportsAsync(ct), await AgentReferences.OfAsync(teams, ct)),
                    await launches.ReportsAsync(ct))))
            .RequirePermit(Permits.Read)
            .WithTags("Agents")
            .WithSummary("Whether each agent CLI is installed and authenticated")
            .WithDescription(
                "One report per preset. `referenced` is true when a team's member, a team's hiring "
                + "allowlist or the Concierge uses the preset; a warning about sign-in belongs only "
                + "on those.\n\n"
                + "`launch` says whether the preset's CLI STARTS THE WAY A MEMBER RUN STARTS IT: its declared free "
                + "invocation (`launchCheck`, a version flag) run through a member's own launch - the agent user, "
                + "the run's memory limit, the preset's isolation environment and update-off. `result` is `ok`, "
                + "`failed` (with `exitCode` and the redacted `stderrTail`) or `not checked` (no free invocation "
                + "declared, an interactive preset, a CLI not installed or being updated); `detail` says which. "
                + "No prompt is sent and nothing is spent. Cached for 30 seconds.");

        app.MapGet("/api/agents/tools", (AgentToolPreflight preflight) =>
                Results.Ok(new
                {
                    at = preflight.Current?.At,
                    running = preflight.Running,
                    presets = preflight.Current?.Presets ?? [],
                }))
            .HumansOnly()
            .WithTags("Agents")
            .WithSummary("What each preset's CLI would load, and whether a member gets only the platform's tools")
            .WithDescription(
                "The Host's last pre-flight, taken at start and after every catalog save: each CLI's own "
                + "listing (`claude mcp list`, `codex mcp list`/`features list`, `grok inspect`, `copilot mcp "
                + "list`), run as the agent user - a member's preset WITH its isolation, the Concierge's without. "
                + "No model is called.\n\n"
                + "`verdict` per preset: `isolated`, `foreignFound` (named in `foreign`), `notVerified` (a "
                + "headless preset with no isolation declaration), `notMeasured` (the CLI could not be listed; "
                + "never read as isolated), `concierge` (its `loaded` list is information, not a warning) or "
                + "`notAModel`. `gaps` are the preset's recorded surfaces no launch switch reaches. `at` is null "
                + "and `presets` empty until the first pass ends; `running` is true while one runs.");

        app.MapPut("/api/me/steering", (
            SteeringBody body,
            HttpContext context,
            TeamPaths paths) =>
        {
            if (PrincipalClaims.From(context.User) is not { Kind: PrincipalKind.User } principal)
            {
                return Results.Json(
                    new { error = "A person has to do this." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            SteeringFile.Write(dataRoot, principal.Id, body.CorrelationId);

            // A running Concierge reads this file. Do not create a workspace to hold it.
            var existing = ConciergeWorkspaces.TryExisting(paths, principal.Id);
            if (existing is not null)
            {
                var causation = body.CorrelationId?.ToString(CultureInfo.InvariantCulture);
                try
                {
                    File.WriteAllText(Path.Combine(existing, "STEERING.md"), SteeringFile.Note(causation));
                }
                catch (IOException)
                {
                    // The data-root copy still stands for the next launch.
                }
            }

            return Results.NoContent();
        })
            .HumansOnly()
            .WithTags("Concierge")
            .WithSummary("Point the Concierge at the workflow the person is looking at");
    }

    public sealed record SteeringBody(long? CorrelationId);
}
