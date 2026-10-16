using System.Globalization;
using Harness.Contracts;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// An open workflow the person may follow: its team by stored id and by the name a person sees, its
/// state in the log's own words, and its newest row.
/// </summary>
public sealed record SteeredWorkflow(long Workflow, string Team, string TeamLabel, string State, long LatestRow);

/// <summary>
/// WHETHER A WORKFLOW MAY BE THE STEERING, and the stored steering read through that judgement.
///
/// <para>
/// The steering is only an open workflow of a live, active team. A stored workflow that is completed
/// or closed (the log's own predicate, <see cref="IMessageLog.OpenWorkflowsAmongAsync"/>), whose team
/// no longer exists (deleted, or deleted and made again under the same name: its root is at or below
/// the new team's creation) or is archived, or that is not a workflow on the log at all, is treated as
/// none: the stored file is removed, the person's existing Concierge workspace is told "No workflow is
/// selected", and one log line says which workflow and why; the route's refusal of the stored workflow
/// drops it the same way. Otherwise a workflow followed once stayed
/// "what the person is looking at" for good, and a Concierge that trusted it joined new work to a
/// finished workflow of another team.
/// </para>
///
/// <para>
/// The team is the root's (<see cref="MessageTeam.Of"/>), never another row's, as
/// <see cref="WorkflowTeamOwnership"/> reads it.
/// </para>
/// </summary>
public sealed class SteeringWorkflows(
    TeamPaths paths, IMessageLog log, TeamRegistry teams, ITeamStore store, ILogger<SteeringWorkflows> logger)
{
    /// <summary>The workflow as one the person may follow, or why it may not be.</summary>
    public async Task<(SteeredWorkflow? Open, string? Over)> JudgeAsync(long workflow, CancellationToken ct = default)
    {
        var name = $"Workflow {workflow.ToString(CultureInfo.InvariantCulture)}";

        if (workflow <= 0
            || await log.FindAsync(workflow, ct) is not { } root
            || root.CorrelationId != workflow)
        {
            return (null, $"{name} is not a workflow on the log");
        }

        if (MessageTeam.Of(root) is not { } rootTeam || teams.ExistingName(rootTeam) is not { } stored)
        {
            return (null, $"{name}'s team no longer exists");
        }

        // A TEAM DELETED AND MADE AGAIN UNDER THE SAME NAME is another team: a workflow rooted before
        // this one was created was its predecessor's.
        if (await store.CreatedAtAsync(stored, ct) is { } created
            && workflow <= await log.LastSeqBeforeAsync(created, ct))
        {
            return (null, $"{name}'s team no longer exists");
        }

        var label = teams.LabelFor(stored);
        if (teams.IsArchived(stored))
        {
            return (null, $"{name}'s team {label} is archived");
        }

        var timing = await log.WorkflowForTeamAsync(stored, workflow, 0, ct);
        var state = (timing.State ?? "open").ToLowerInvariant();

        if (!(await log.OpenWorkflowsAmongAsync([workflow], ct)).Contains(workflow))
        {
            return (null, state is "completed" or "closed" ? $"{name} is {state}" : $"{name} is over");
        }

        var rows = await log.ReadCorrelationAsync(workflow, ct);
        var latest = rows.Count == 0 ? workflow : rows.Max(row => row.Seq);

        return (new SteeredWorkflow(workflow, stored, label, state, latest), null);
    }

    /// <summary>
    /// The person's stored steering, judged. A workflow that may not be followed is dropped: the file
    /// is removed, the existing workspace's <c>STEERING.md</c> rewritten and one line logged.
    /// </summary>
    public async Task<SteeredWorkflow?> ReadAsync(string userId, CancellationToken ct = default)
    {
        if (SteeringFile.Read(paths.DataRoot, userId) is not { } text) return null;

        var workflow = long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        var (open, over) = await JudgeAsync(workflow, ct);
        if (open is not null) return open;

        Drop(userId, workflow, over!);
        return null;
    }

    /// <summary>
    /// A workflow the route refused: when the person's stored steering names that same workflow it
    /// is dropped as a launch would drop it, so nothing keeps naming it. A stored steering naming
    /// another workflow is left alone.
    /// </summary>
    public void Refused(string userId, long workflow, string over)
    {
        if (SteeringFile.Read(paths.DataRoot, userId) is { } text
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stored)
            && stored == workflow)
        {
            Drop(userId, workflow, over);
        }
    }

    // The stored file removed, the existing workspace told "No workflow is selected", one line logged.
    private void Drop(string userId, long workflow, string over)
    {
        SteeringFile.Write(paths.DataRoot, userId, null);
        WriteNote(userId, null);
        logger.LogInformation(
            "Steering of {User} dropped workflow {Workflow}: {Reason}.", userId, workflow, over);
    }

    /// <summary>
    /// Rewrites the person's existing Concierge workspace's <c>STEERING.md</c>, which a running
    /// Concierge reads. Does not create a workspace to hold it.
    /// </summary>
    public void WriteNote(string userId, SteeredWorkflow? open)
    {
        if (ConciergeWorkspaces.TryExisting(paths, userId) is not { } existing) return;

        try
        {
            File.WriteAllText(Path.Combine(existing, "STEERING.md"), SteeringFile.NoteFor(open));
        }
        catch (IOException)
        {
            // The data-root copy still stands for the next launch.
        }
    }
}
