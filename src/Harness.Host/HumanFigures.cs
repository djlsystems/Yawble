using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WHAT PEOPLE DID, COUNTED FROM <c>intervention_ledger</c>; NEVER PRICED. Three figures, read only:
///
/// <list type="bullet">
/// <item><b>Human Interventions</b>: how many rows fall in the window (<c>occurred_at</c>), every kind, each
/// row once. A Concierge session is one row, so one intervention, however many messages it held.</item>
/// <item><b>Human Attention Time</b>: the sum of <c>duration_seconds</c> over the rows with
/// <c>duration_measured = 1</c> ONLY. A row with no measured duration adds nothing and is never estimated.
/// With no measured row the figure is null - "no measured time" - never 0.</item>
/// <item><b>Waiting on a person</b>: the existing <c>blockedSeconds</c>, time work waited on a person
/// (<see cref="OutcomeFigures.BlockedByWorkflow"/>). It is NOT attention: nothing here adds it to Human
/// Attention Time, and no figure joins the two.</item>
/// </list>
///
/// <para>
/// ONE ROW, ONE BUCKET. For an outcome, a row counts under its <c>outcome_id_at_event</c> followed through
/// <c>merged_into</c> (<see cref="OutcomeFigures.Resolution"/>), as a run follows its link; a row with no
/// outcome, or whose outcome is deleted, counts under Unattributed. For a team, a row counts by its
/// <c>team_id</c>; a Concierge session carries none, so a team's count leaves Concierge sessions out.
/// Only a person's act writes a row - a member, a key of a member's, a trigger, a wake or the Host writes
/// none - so nothing but people's acts is counted here.
/// </para>
///
/// <para>NOTHING IS PRICED: no rate, no labour cost, no money is read or written beside these.</para>
/// </summary>
public static class HumanFigures
{
    /// <summary>
    /// The figures over a set of rows. <see cref="AttentionSeconds"/> is null - no measured time - when
    /// <see cref="MeasuredInterventions"/> is 0; never 0 for "nothing measured".
    /// </summary>
    public sealed record Figures(int Interventions, int MeasuredInterventions, double? AttentionSeconds)
    {
        public static Figures None { get; } = new(0, 0, null);
    }

    /// <summary>One of the weekly buckets (<see cref="OutcomeFigures.WeekStarts"/>): the rows that occurred in it.</summary>
    public sealed record Week(DateTimeOffset WeekStart, int Interventions, int MeasuredInterventions, double? AttentionSeconds);

    /// <summary>
    /// A team's figures over a read's window: its Human Interventions and Human Attention Time (rows by
    /// <c>team_id</c>; Concierge sessions carry none and are not in it), <see cref="Unattributed"/> - those of
    /// its rows serving no outcome - and <see cref="WaitingSeconds"/>, Waiting on a person, kept apart.
    /// </summary>
    public sealed record Team(
        int Interventions, int MeasuredInterventions, double? AttentionSeconds, Figures Unattributed, double WaitingSeconds);

    /// <summary>Whether a row's duration counts: measured, with a duration. Nothing else adds time.</summary>
    public static bool Measured(InterventionEntry row) => row.DurationMeasured && row.DurationSeconds is not null;

    /// <summary>The figures over <paramref name="rows"/>, each row once.</summary>
    public static Figures Of(IEnumerable<InterventionEntry> rows)
    {
        var interventions = 0;
        var measured = 0;
        double seconds = 0;

        foreach (var row in rows)
        {
            interventions++;
            if (!Measured(row)) continue;
            measured++;
            seconds += row.DurationSeconds!.Value;
        }

        return new Figures(interventions, measured, measured == 0 ? null : seconds);
    }

    /// <summary>
    /// THE OUTCOME A ROW COUNTS UNDER: its <c>outcome_id_at_event</c> followed through <c>merged_into</c>; null -
    /// Unattributed - for a row with none, or one naming an outcome that no longer exists.
    /// </summary>
    public static string? OutcomeOf(InterventionEntry row, IReadOnlyDictionary<string, string> resolution) =>
        row.OutcomeIdAtEvent is { } id && resolution.TryGetValue(id, out var holder) ? holder : null;

    /// <summary>The rows counting under <paramref name="outcome"/> (null: Unattributed).</summary>
    public static IEnumerable<InterventionEntry> Serving(
        IEnumerable<InterventionEntry> rows, string? outcome, IReadOnlyDictionary<string, string> resolution) =>
        rows.Where(r => OutcomeOf(r, resolution) == outcome);

    /// <summary>The rows in [<paramref name="from"/>, <paramref name="to"/>), by <c>occurred_at</c>; an open end is unbounded.</summary>
    public static IEnumerable<InterventionEntry> Within(IEnumerable<InterventionEntry> rows, DateTimeOffset? from, DateTimeOffset? to) =>
        rows.Where(r => (from is null || r.OccurredAt >= from) && (to is null || r.OccurredAt < to));

    /// <summary>The rows of <paramref name="team"/>, by <c>team_id</c>: a Concierge session (no team) is in no team's.</summary>
    public static IEnumerable<InterventionEntry> OfTeam(IEnumerable<InterventionEntry> rows, string team) =>
        rows.Where(r => r.TeamId is not null && string.Equals(r.TeamId, team, StringComparison.OrdinalIgnoreCase));

    /// <summary>Intervention frequency: for each of <paramref name="weeks"/>, the rows that occurred in it.</summary>
    public static IReadOnlyList<Week> Weekly(IEnumerable<InterventionEntry> rows, IReadOnlyList<DateTimeOffset> weeks)
    {
        var list = rows.ToList();
        return weeks
            .Select(start =>
            {
                var figures = Of(Within(list, start, start.AddDays(7)));
                return new Week(start, figures.Interventions, figures.MeasuredInterventions, figures.AttentionSeconds);
            })
            .ToList();
    }

    /// <summary>
    /// A team's figures from <paramref name="rows"/> (already the read's window) and its Waiting on a person,
    /// <paramref name="waitingSeconds"/>, which is carried beside them and never added in.
    /// </summary>
    public static Team ForTeam(
        IEnumerable<InterventionEntry> rows, string team, IReadOnlyDictionary<string, string> resolution, double waitingSeconds)
    {
        var mine = OfTeam(rows, team).ToList();
        var all = Of(mine);
        return new Team(all.Interventions, all.MeasuredInterventions, all.AttentionSeconds, Of(Serving(mine, null, resolution)), waitingSeconds);
    }
}
