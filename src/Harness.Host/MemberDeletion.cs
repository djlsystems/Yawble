using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>What a member deletion actually removed, so the caller can say it rather than guess.</summary>
public sealed record MemberDeleted(
    string Team,
    string Member,
    string Label,
    int PendingDeliveries,
    int Schedules,
    IReadOnlyList<string> Directories,
    IReadOnlyList<string> Failures,

    /// <summary>Every path in the workspace that could not be removed. When it is not empty the
    /// workspace is recorded as a removal unfinished and retried, as a team root is.</summary>
    IReadOnlyList<string> Remaining);

/// <summary>
/// Deletes one member, and everything that names it.
///
/// <see cref="TeamDeletion"/>'s sibling and deliberately its shape: its own class rather than a
/// method on <see cref="TeamRegistry"/>, because it needs the cursor, subscription, pending-delivery
/// and principal stores, none of which the registry has or should acquire. It does NOT take
/// <c>ITeamStore</c>, and the asymmetry with <see cref="TeamDeletion"/> is the point: the row, this
/// registry's memory of the member and the manager's prompt have to move together, so they move
/// inside <see cref="TeamRegistry.RemoveContainerAsync"/> rather than being three things a caller
/// here could do two of.
///
/// THE ORDER IS THE DESIGN, and it is the team's minus sessions - a Concierge belongs to a
/// team and a person, never to a member - plus one step that has no team-level equivalent:
///
/// 1. <b>The manager is refused</b>, before anything is touched. There is no team without a door
///    into it, and a team whose manager could be deleted is one nobody can address with every other
///    member still running.
/// 2. <b>The container</b>, which owns a child process. <see cref="ContainerHost.RemoveAsync"/>
///    takes it out of the dictionary before disposing it, because the delivery pump walks that
///    dictionary and would otherwise hand work to something being torn down.
/// 3. <b>The rows only it owns</b> - pending deliveries, schedules, cursor, subscriptions,
///    credential. None of
///    these cascade off <c>team_members</c>, and <c>pending_deliveries</c> is the one that bites:
///    keyed on <c>Team/Name</c> with no foreign key, so a row left behind is inherited by the next
///    member created with that name. For a LIVE team that is worse than it is for a deleted one -
///    somebody re-adds a member of the same name an hour later and its first act is to publish
///    <c>container.failed</c> for work it never accepted.
/// 4. <b>The row, this process's memory of it, and the manager's prompt</b>, which move together
///    inside <see cref="TeamRegistry.RemoveContainerAsync"/>. The re-prompt is the step with no
///    team-level analogue and the only one whose omission leaves nothing to find: the team survives,
///    so its manager goes on holding a roster naming a colleague that is not there.
/// 5. <b>The workspace and repo worktrees last</b>, because they are the only steps that are not
///    database writes and the only ones that can fail halfway - usually a file still held by a
///    child that has not finished exiting. For each team repo, every one of the member's per-card
///    trees goes through <see cref="WorktreeRemoval"/>: <c>git worktree remove</c> without
///    <c>--force</c>, then <c>git worktree prune</c>. A tree it leaves (uncommitted edits, commits
///    not on origin) is named in the result and on the team feed rather than forced. The workspace
///    goes through <see cref="FolderRemoval"/>, as a team root does: agent content removed as the
///    agent, links never followed, and whatever remains named and recorded to be retried.
///
/// <b>The transcripts are NOT removed, and that is the one place this deliberately differs from
/// deleting a team.</b> Messages already in the log carry <c>payload.transcript</c> paths into
/// <c>transcripts/&lt;team&gt;/&lt;member&gt;</c>, and the log is append-only and untouched here, so
/// deleting them would leave it naming files that are not there. When a whole TEAM goes there is
/// nothing left to read those messages from a board with, which is why that case takes them.
///
/// <b>NOTHING ON THE MESSAGE LOG IS REMOVED</b>, for the reason nothing is removed by a team
/// deletion: what a member did is the history of what happened rather than a property of the member.
///
/// <b>ONE ROW IS APPENDED.</b> <c>agentContainer.removed</c> is published after the row is
/// gone and before the filesystem steps; cards are a PROJECTION of this log, so without it the
/// projector cannot see a deletion at all and every card the member held goes on naming somebody who
/// is not there. It removes nothing and nobody subscribes to it.
/// </summary>
public sealed class MemberDeletion(
    TeamRegistry teams,
    ContainerHost host,
    ICursors cursors,
    EffectiveSubscriptions effective,
    IPendingDeliveries pending,
    ITriggerStore schedules,
    IPrincipalStore principals,
    TeamPaths paths,

    /// <summary>
    /// The log this deletion says so on. REQUIRED, not defaulted, for the reason every other
    /// required seam here is: a deletion that could silently append nothing reinstates exactly the
    /// defect step 4a exists to remove, and would do it invisibly - the cards would simply go on
    /// naming somebody who is gone.
    /// </summary>
    IMessageLog log,
    string gitExecutable = "git",
    AgentLaunchUser? runAs = null,
    FolderRemoval? removal = null)
{
    private readonly FolderRemoval _removal = removal ?? new FolderRemoval(runAs);

    private readonly WorktreeRemoval _worktrees = new(
        new GitRunner(string.IsNullOrWhiteSpace(gitExecutable) ? "git" : gitExecutable, runAs: runAs), log);

    /// <summary>
    /// Deletes <paramref name="name"/> from <paramref name="team"/>. Returns null when there is no
    /// such team or no such member, which is a 404 rather than an error - asking twice is not a
    /// fault. Throws <see cref="ManagerCannotBeDeletedException"/> for the one member that may not
    /// go.
    /// </summary>
    public async Task<MemberDeleted?> DeleteAsync(
        string team, string name, CancellationToken ct = default)
    {
        // The STORED spellings, resolved once and used for everything below. Every row key, path and
        // credential id is built from them, so taking the caller's capitalisation here would delete
        // a directory that is not there and leave the one that is.
        if (teams.ExistingName(team) is not { } stored) return null;
        if (host.Find(new ContainerId(stored, name)) is not { } container) return null;

        var snapshot = container.Snapshot();

        // The member's own identifier as the host holds it, never the caller's spelling - the same
        // rule that makes an instruction type get built from the found container rather than from
        // what was typed.
        var id = new ContainerId(stored, snapshot.Id);

        if (string.Equals(id.Name, TeamRegistry.DefaultManagerName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ManagerCannotBeDeletedException(teams.LabelFor(stored));
        }

        var failures = new List<string>();
        var swept = 0;
        var removedSchedules = 0;

        // 2 and 3. The live thing first, then the rows only it owned.
        try
        {
            await host.RemoveAsync(id);

            swept = await pending.RemoveAllAsync(id, ct);
            removedSchedules = await schedules.DeleteForContainerAsync(id.Team, id.Name, ct);
            await cursors.ForgetAsync(id, ct);

            // NOT a derivation - the member row it would derive from is about to be removed below,
            // so "empty" is the only correct answer. Routed through EffectiveSubscriptions anyway:
            // that class is the only one allowed to call ISubscriptions.SetAsync, and a deletion's
            // clear is one more shape of that write, not a second door to it.
            await effective.ClearAsync(id, ct);

            // Its own credential. The id IS the qualified name - see AgentEnvironment, which mints
            // with `id.ToString()`. A live credential for a container nobody can see must not
            // outlive it.
            await principals.RevokeAsync(id.ToString(), ct);
        }
        catch (Exception ex)
        {
            failures.Add($"{id}: {ex.Message}");
        }

        // 4. The row, the registry's memory, and the manager's prompt - one call, because a caller
        // that could do the first without the third is the defect this exists to prevent.
        await teams.RemoveContainerAsync(stored, id.Name, ct);

        // 4a. SAY SO ON THE LOG, and this is the one step that is not a removal.
        //
        // WITHOUT THIS ROW a deleted member's cards are unfixable: cards are a PROJECTION of the
        // log, so with no row the projector cannot see a deletion and no amount of client work would show it. A card still naming somebody who is
        // gone is a board lying about who is on the work.
        //
        // NOT BY JOINING THE PROJECTION TO `team_members`, which is the obvious alternative and the
        // wrong one: that is the coupling `ILedger` already forbids - `SumUsageForTeamAsync` takes
        // one floor for a whole team precisely to avoid joining `messages` to `team_members` - and
        // it would make a board read depend on a store the projector has no business knowing about.
        //
        // ONE ROW, NOT ONE PER CARD. The projector unassigns every card currently naming this
        // member; a row per card would put the projection's own output into its input.
        //
        // AFTER the row is gone and BEFORE the filesystem steps, so the log never claims a removal
        // that did not happen. Its team comes from `Source` - the member's qualified `Team/Name` -
        // which is the existing rule for container events, so no payload field names a team.
        // Nothing subscribes to it and nothing should, exactly as with `container.progress`.
        await log.AppendAsync(
            new NewMessage(MessageTypes.ContainerRemoved, "{}", id.ToString()),
            ct);

        // 5. The workspace and repo worktrees. The transcripts stay - see this class's own summary.
        var removed = new List<string>();
        var member = new ContainerId(stored, id.Name);
        var workspace = paths.WorkspaceFor(member);

        var remaining = new List<string>();

        if (Directory.Exists(workspace))
        {
            var report = await _removal.RemoveWorkspaceAsync(workspace, stored, id.Name, ct);

            if (report.Complete)
            {
                removed.Add(workspace);
            }
            else
            {
                // Named, not swallowed, and recorded to be retried. The member is gone from every
                // list either way.
                remaining.AddRange(report.Remaining);
                failures.Add(
                    $"{workspace}: removal unfinished, {report.Remaining.Count} path(s) remain; it is "
                    + "retried at the next start or on request: " + string.Join(", ", report.Remaining)
                    + Why(report));
            }
        }

        foreach (var url in teams.ReposFor(stored))
        {
            var repo = RepoUrls.DeriveName(url);

            // Every per-card tree this member has, under the one no-force rule: a tree with
            // uncommitted edits or commits not on origin is left on disk, named in the result and
            // on the team feed. The pass also prunes the clone's worktree records.
            var mainClone = Path.Combine(paths.ReposFor(stored), repo, "main");
            var trees = paths.WorktreesFor(member, repo);

            var pass = Directory.Exists(mainClone)
                ? await _worktrees.RemoveAsync(mainClone, trees, ct)
                : new WorktreeRemovalReport([], trees
                    .Select(tree => new WorktreeLeft(tree, "There is no main clone to remove it from."))
                    .ToList());
            removed.AddRange(pass.Removed);

            foreach (var tree in pass.Left)
            {
                failures.Add($"{tree.Path}: {tree.Reason}");
                await _worktrees.ReportLeftAsync(member, repo, tree, causation: null, ct);
            }
        }

        return new MemberDeleted(stored, id.Name, snapshot.Name, swept, removedSchedules, removed, failures, remaining);
    }

    /// <summary>Why what remains is still there, when the removal said (no worker for the agent's pass), as one more sentence.</summary>
    private static string Why(FolderRemovalReport report) =>
        report.Reasons?.Values.Distinct(StringComparer.Ordinal).ToList() is { Count: > 0 } reasons
            ? ". " + string.Join(" ", reasons)
            : "";
}

/// <summary>
/// The one member that may not be deleted. A team without its manager is a team nobody can address,
/// with every other member still running and still able to publish - so this is a 409 rather than a
/// 403: nothing about the caller would make it allowed.
/// </summary>
public sealed class ManagerCannotBeDeletedException(string teamLabel)
    : InvalidOperationException(
        $"A team's manager cannot be deleted. Delete the team '{teamLabel}' itself to remove it.")
{
    public string TeamLabel { get; } = teamLabel;
}
