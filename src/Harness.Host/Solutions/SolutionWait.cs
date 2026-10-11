using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host.Solutions;

/// <summary>
/// A SOLUTION TEAM WAITING FOR A MISSING REQUIRED INPUT SPENDS NOTHING. While
/// <see cref="SolutionInstaller.MissingAsync"/> names anything for a team - what
/// `GET /api/teams/{team}/solution` reports as `missing` - every trigger of the team skips its fire:
/// the sweep's schedules and a person's Run now through <see cref="TriggerSweep"/>, events and
/// folder changes through <see cref="SkipIfWaitingAsync"/>. Each skip is a `schedule.skipped` row
/// with the reason "waiting for …" and a tenant row, as a capped fire's is, and an event the skip
/// passes over is not redelivered. Read live, so providing the input unblocks the team at once.
///
/// THE WAIT ENDING CLEARS ITS MARKS. A team seen waiting and then seen with nothing missing has the
/// blocked and failed marks of its members cleared (<see cref="MemberRuntime.ClearTroubleMarks"/>):
/// while it waited no trigger could run, so a mark it carried is the wait's - a run that could not
/// reach the input - and nothing else would ever wake that member to clear it. Seen on every fire,
/// on the team's solution read, after an upload, and on the sweep's heartbeat. The set of teams
/// seen waiting is memory only: after a restart a team still waiting is seen again on its first
/// read.
/// </summary>
public sealed class SolutionWait(
    ITeamSolutionStore solutions,
    Func<SolutionInstaller> installer,
    ContainerHost host,
    IMessageLog log,
    ITriggerStore triggers,
    TenantLogging tenant)
{
    private readonly ConcurrentDictionary<string, byte> _waiting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The skip reason while <paramref name="team"/> waits for a missing required input, naming it in
    /// words - "waiting for a file in Resume/" - or null for a team not installed from a package or
    /// missing nothing. A team found no longer waiting has its wait's marks cleared.
    /// </summary>
    public async Task<string?> WaitingForAsync(string team, CancellationToken ct = default)
    {
        if (await solutions.FindAsync(team, ct) is not { } row) return null;

        var missing = await installer().MissingAsync(row.Team, row, ct);
        if (missing.Count > 0)
        {
            _waiting[row.Team] = 0;
            return ReasonOf(missing);
        }

        if (_waiting.TryRemove(row.Team, out _))
        {
            foreach (var snapshot in host.Snapshots().Where(s => string.Equals(s.Team, row.Team, StringComparison.OrdinalIgnoreCase)))
            {
                host.Find(new ContainerId(snapshot.Team, snapshot.Name))?.ClearTroubleMarks();
            }
        }

        return null;
    }

    /// <summary>Every solution team read once: the heartbeat that ends a wait provided by a path
    /// with no hook of its own (a connection bound, a file an agent wrote).</summary>
    public async Task ObserveAllAsync(CancellationToken ct = default)
    {
        foreach (var row in await solutions.ListAsync(ct)) await WaitingForAsync(row.Team, ct);
    }

    /// <summary>"waiting for a file in Resume/ and Scout's adzuna connection".</summary>
    public static string ReasonOf(IReadOnlyList<SolutionMissing> missing) =>
        "waiting for " + string.Join(" and ", missing.Select(m => m.Kind == "document"
            ? $"a file in {m.Name}"
            : $"{m.Member}'s {m.Name} connection"));

    /// <summary>
    /// Whether an event or folder trigger's fire is skipped because its team waits for a missing
    /// input, and when it is, the skip recorded: a `schedule.skipped` row caused by the event, the
    /// trigger's outcome `skipped`, and a `tenant_events` row. The caller fires nothing.
    /// </summary>
    public async Task<bool> SkipIfWaitingAsync(
        TriggerRow row, ContainerId member, DateTimeOffset now, long? cause, CancellationToken ct = default)
    {
        if (await WaitingForAsync(member.Team, ct) is not { } reason) return false;

        var source = $"trigger:{row.Id}";
        var skipped = await log.AppendAsync(
            new NewMessage(
                MessageTypes.ScheduleSkipped,
                JsonSerializer.Serialize(new { member = member.ToString(), reason }),
                source,
                cause),
            ct);

        await triggers.RecordSkipAsync(row.Id, "skipped", skipped.Seq, ct);
        await tenant.WriteAsAsync(
            source,
            actorEmail: null,
            TenantActions.ScheduleSkipped,
            row.Id,
            row.Name,
            new
            {
                team = member.Team,
                container = member.Name,
                reason,
                skippedAt = now.ToString("O", CultureInfo.InvariantCulture),
            },
            ct);

        return true;
    }
}
