using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>One open workflow of a team, as the archive confirmation lists it.</summary>
public sealed record ArchiveOpenWorkflow(long Workflow, string Title);

/// <summary>
/// What <c>GET /api/teams/{team}/archive-check</c> answers: whether the team is quiet, and when it
/// is not the sentence an archive or a delete is refused with; and its open workflows, which do not
/// block either but which the confirmation lists.
/// </summary>
public sealed record ArchiveCheck(bool Quiet, string? Reason, IReadOnlyList<ArchiveOpenWorkflow> OpenWorkflows);

/// <summary>
/// ARCHIVING A TEAM: kept, hidden, doing no work, and still a template to clone from.
///
/// <para>
/// Archived is a flag on the team (`teams.archived_at`, `archived_by`), never a second kind of
/// team: every row, member, setting, document, repository, site, workflow and ledger row stays, so
/// Delete, Clone and every reader keep working on it. Archiving also pauses the team, so the pump
/// passes it over and it takes no slot; unarchiving clears the flag and leaves it paused.
/// </para>
///
/// <para>
/// <b>QUIET FIRST.</b> Archive and delete are refused unless no member has a run going and none has
/// an instruction accepted and not started (<see cref="NotQuietAsync"/>). Open workflows do not
/// block. The refusal names what is still going, member by member.
/// </para>
///
/// <para>
/// <b>AN ARCHIVED TEAM DOES NO WORK.</b> Every trigger of it skips its fire with the reason
/// <see cref="MessageTypes.ScheduleSkippedArchivedReason"/> and a tenant row (the sweep and Run now
/// through <see cref="TriggerSweep"/>, events and folder changes through
/// <see cref="SkipIfArchivedAsync"/>); a tell, a backlog dispatch, a planned card and a site action
/// are refused with <see cref="Refusal"/>.
/// </para>
/// </summary>
public sealed class TeamArchive(
    TeamRegistry teams,
    ContainerHost host,
    IPendingDeliveries pending,
    IMessageLog log,
    ITriggerStore triggers,
    TenantLogging tenant)
{
    /// <summary>
    /// The sentence everything an archived team is asked to do is refused with. It names archiving
    /// and Unarchive, so a caller knows the way back.
    /// </summary>
    public static string Refusal(string label) =>
        $"'{label}' is archived, and an archived team does no work. Nothing was sent. "
        + "Unarchive it first (it comes back paused), then Resume it.";

    /// <summary>Archiving a team that is already archived.</summary>
    public static string AlreadyArchived(string label) => $"'{label}' is already archived. Nothing was changed.";

    /// <summary>Unarchiving a team that is not archived.</summary>
    public static string NotArchived(string label) => $"'{label}' is not archived. Nothing was changed.";

    /// <summary>Whether <paramref name="team"/> is archived. False for an unknown team.</summary>
    public bool IsArchived(string team) => teams.IsArchived(team);

    /// <summary>
    /// The sentence a not-quiet team's archive and delete are refused with, member by member - for
    /// example "Developer is running; Manager has 2 instructions queued" - or null when the team is
    /// quiet: no member has a run going and none has an instruction accepted and not started.
    /// </summary>
    public async Task<string?> NotQuietAsync(string stored, CancellationToken ct = default)
    {
        var rows = await pending.ForTeamAsync(stored, ct);
        var parts = new List<string>();

        foreach (var id in teams.ContainerIdsOf(stored))
        {
            var snapshot = host.Find(id)?.Snapshot();
            var mine = rows
                .Where(row => ContainerId.TryParse(row.Subscriber, out var subscriber) && subscriber.Equals(id))
                .ToList();

            // RUNNING is the run loop's state, or a delivery it has started: either is a run going.
            var running = snapshot?.State == ContainerState.Running || mine.Any(row => row.Started);
            var queued = mine.Count(row => !row.Started);
            var name = snapshot?.Name ?? id.Name;

            var queuedWords = queued == 1 ? "1 instruction queued" : $"{queued} instructions queued";
            if (running && queued > 0) parts.Add($"{name} is running and has {queuedWords}");
            else if (running) parts.Add($"{name} is running");
            else if (queued > 0) parts.Add($"{name} has {queuedWords}");
        }

        if (parts.Count == 0) return null;

        return $"'{teams.LabelFor(stored)}' is not quiet: {string.Join("; ", parts)}. "
            + "Wait for that work to finish, or stop it, then try again. Nothing was changed.";
    }

    /// <summary>What the archive confirmation shows: quiet or the reason, and the open workflows.</summary>
    public async Task<ArchiveCheck> CheckAsync(string stored, CancellationToken ct = default)
    {
        var reason = await NotQuietAsync(stored, ct);

        // THE SAME FLOOR THE ROLLUP COMPUTES, so a same-name predecessor's workflows are not listed.
        var floor = teams.ContainerIdsOf(stored)
            .Select(host.Find)
            .Where(container => container is not null)
            .Select(container => container!.Snapshot().SinceSeq)
            .DefaultIfEmpty(0)
            .Min();

        var workflows = await log.WorkflowsForTeamAsync(stored, floor, ct);
        var open = workflows.Workflows
            .Where(w => w.EndedAt is null && w.Correlation is not null)
            .Select(w => new ArchiveOpenWorkflow(
                w.Correlation!.Value,
                string.IsNullOrWhiteSpace(w.Subject) ? $"Workflow {w.Correlation.Value}" : w.Subject!))
            .ToList();

        return new ArchiveCheck(reason is null, reason, open);
    }

    /// <summary>
    /// Whether an event or folder trigger's fire is skipped because its team is archived, and when it
    /// is, the skip recorded as a waiting team's is: a `schedule.skipped` row caused by the event, the
    /// trigger's outcome `skipped`, and a `tenant_events` row. The caller fires nothing.
    /// </summary>
    public async Task<bool> SkipIfArchivedAsync(
        TriggerRow row, ContainerId member, DateTimeOffset now, long? cause, CancellationToken ct = default)
    {
        if (!teams.IsArchived(member.Team)) return false;

        const string reason = MessageTypes.ScheduleSkippedArchivedReason;
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

/// <summary>
/// WHO MAY ARCHIVE AND UNARCHIVE A TEAM: a person, or the person's own tenant Concierge holding
/// <see cref="Permits.Archive"/> while the tenant setting <c>concierge.mayArchive</c> is on. The
/// setting is read through <paramref name="mayArchive"/> on every call, never captured, so turning it
/// off refuses the next call of a Concierge session already running. The shape of
/// <see cref="ConciergeMergeGate"/>, for the same reasons.
/// </summary>
public sealed class ConciergeArchiveGate(Func<bool> mayArchive)
{
    /// <summary>The Concierge's refusal while the setting is off: it names the setting and who turns it on.</summary>
    public const string SettingOff =
        "Archiving a team is a person's step here: the setting " + TenantSettings.ConciergeMayArchiveName
        + " is off, and only a person turns it on, in " + TenantSettings.ConciergeTab + ". Nothing was changed.";

    /// <summary>Who is archiving, read once per request.</summary>
    /// <param name="Refusal">Non-null when the caller may not; answer it and change nothing.</param>
    /// <param name="ActorEmail">The person's email: the claim, or for the Concierge its owner's.</param>
    /// <param name="ViaConcierge">Whether the Concierge is acting for the person.</param>
    public sealed record Caller(IResult? Refusal, string? ActorEmail, bool ViaConcierge);

    public async Task<Caller> CheckAsync(HttpContext context, IUserStore users, CancellationToken ct)
    {
        var email = context.User.FindFirstValue(ClaimTypes.Email);

        // A PERSON: always.
        if (PrincipalClaims.From(context.User) is not { Kind: not PrincipalKind.User } principal)
        {
            return new Caller(null, email, false);
        }

        // Repeats the route marker's check rather than trusting it.
        if (!ConciergeLaunchFactory.IsConcierge(principal) || !principal.May(Permits.Archive))
        {
            return new Caller(
                Results.Json(new { error = PermitGate.HumansOnlyMessage }, statusCode: StatusCodes.Status403Forbidden),
                email, false);
        }

        var person = email ?? (await users.FindByIdAsync(principal.OwnerUserId!, ct))?.Email;
        if (!mayArchive())
        {
            return new Caller(
                Results.Json(new { error = SettingOff, setting = TenantSettings.ConciergeMayArchiveName },
                    statusCode: StatusCodes.Status403Forbidden),
                person, true);
        }

        return new Caller(null, person, true);
    }
}
