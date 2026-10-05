using System.ComponentModel;
using System.Text.Json.Serialization;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// THE TEAM'S TOKENS, RUN BY RUN: <c>GET /api/teams/{team}/tokens/runs</c>. One row per finished run
/// from <c>usage_ledger</c>, so the Tokens dialog can say WHEN the team spent and who, not only how
/// much.
///
/// <para>
/// A RUN'S TOKENS ARE PLACED AT ITS END, the instant they were measured, and never spread over its
/// duration: that would be an estimate. A run that reported no usage carries no figures - never 0 -
/// and a combined total carries <c>combined</c> with no in/out split, exactly as the ledger keeps them.
/// </para>
///
/// <para>
/// SCOPED BY THE TEAM'S CREATION, NOT BY ITS MEMBERS' FLOOR: Reset's "Delete memory" raises every
/// named member's floor, and the ledger keeps every run through it. A team re-created under the same
/// name reads none of its predecessor's runs: they ended at log positions below its creation.
/// </para>
/// </summary>
public static class TeamTokenRuns
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/teams/{team}/tokens/runs", async (
                [Description(Describe.Team)] string team,
                [Description("Start of the period, a UTC instant. Give it with `to`, or neither for the "
                    + "team's workflow window.")] DateTimeOffset? from,
                [Description("End of the period, a UTC instant, at most a year after `from`.")] DateTimeOffset? to,
                TeamRegistry teams, ITeamStore store, IMessageLog log, IUsageLedger ledger,
                TimeProvider clock, CancellationToken ct) =>
            {
                if (teams.ExistingName(team) is not { } stored)
                {
                    return Results.NotFound(new { error = $"No team '{team}'." });
                }

                if (TeamActivity.Refusal(from, to) is { } refusal) return Results.BadRequest(new { error = refusal });

                return Results.Ok(await ReadAsync(stored, from, to, teams, store, log, ledger, clock.GetUtcNow(), ct));
            })
            .WithTags("Activity")
            .RequirePermit(Permits.Read)
            .WithSummary("This team's runs and the tokens each used, at the time each ended")
            .WithDescription(
                "One row per finished run from the usage ledger (a batched run is one row), at its "
                + "`endedAt`: when its tokens were measured. Nothing is spread over a run's duration. "
                + "`measured: false` is a run that reported no usage and carries no figures - never 0. "
                + "A combined total carries `combined` and `billable` and no in/out split. `current` is "
                + "false for a member since removed.\n\n"
                + "With no `from` and `to`, every run since the team's creation, surviving a Reset that "
                + "deletes memory, and the window to draw them in (`window: \"workflows\"`): from the root "
                + "of the team's earliest workflow to the latest activity of any of them, never to the "
                + "clock - the Activity tile's own window. A team with no workflow answers `\"none\"` "
                + "and no `from` or `to`. With both, the runs that ended in that period "
                + "(`\"requested\"`), at most one year.");
    }

    public static async Task<TeamTokenRunsAnswer> ReadAsync(
        string team, DateTimeOffset? from, DateTimeOffset? to,
        TeamRegistry teams, ITeamStore store, IMessageLog log, IUsageLedger ledger,
        DateTimeOffset now, CancellationToken ct)
    {
        var created = await store.CreatedAtAsync(team, ct);
        var floor = created is { } at ? await log.LastSeqBeforeAsync(at, ct) : 0;

        // THE ACTIVITY TILE'S WINDOW when none is asked for: the team's earliest workflow root to
        // the latest activity of any of its workflows, never the clock. The runs are every run since
        // the team's creation all the same, so the dialog's totals keep the whole history.
        var (window, start, end) = from is { } f && to is { } t
            ? ("requested", (DateTimeOffset?)f, (DateTimeOffset?)t)
            : await log.WorkflowStretchForTeamAsync(team, floor, ct) is { } stretch
                ? ("workflows", stretch.From, stretch.To)
                : ("none", null, null);

        var runs = await ledger.ReadTeamRunsEndedAsync(team, floor, from, to, ct);
        var current = teams.ContainerIdsOf(team).Select(id => id.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new TeamTokenRunsAnswer(start, end, now, window, [.. runs.Select(run => new TeamTokenRun(
            run.Member,
            current.Contains(run.Member),
            run.EndedAt,
            run.Measured,
            run.Measured ? run.Billable : null,
            run.Measured ? run.TokensIn : null,
            run.Measured ? run.TokensCachedIn : null,
            run.Measured ? run.TokensCacheCreation : null,
            run.Measured ? run.TokensOut : null,
            run.Measured ? run.TokensCombined : null))]);
    }
}

/// <summary>The answer of <c>GET /api/teams/{team}/tokens/runs</c>; see <see cref="TeamTokenRuns"/>.
/// <see cref="From"/> and <see cref="To"/> are null for a team with no workflow.</summary>
public sealed record TeamTokenRunsAnswer(
    DateTimeOffset? From,
    DateTimeOffset? To,
    DateTimeOffset ServerNow,
    string Window,
    IReadOnlyList<TeamTokenRun> Runs);

/// <summary>One finished run at its end. A figure the run did not report is absent, never 0.</summary>
public sealed record TeamTokenRun(
    string Member,
    bool Current,
    DateTimeOffset EndedAt,
    bool Measured,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Billable,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TokensIn,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TokensCachedIn,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TokensCacheCreation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TokensOut,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Combined);
