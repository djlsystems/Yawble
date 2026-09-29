using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>What a team deletion actually removed, so the caller can say it rather than guess.</summary>
public sealed record TeamDeleted(
    string Team,
    int Containers,

    // THERE IS NO SESSION COUNT. A Concierge is keyed on the person, so no team-scoped operation
    // can address one, and a hard-coded `0` would put an audit row in `tenant_events` asserting
    // nobody was connected while a person was typing into a session. The honest report is silence.
    int PendingDeliveries,
    int Schedules,
    IReadOnlyList<string> Directories,
    IReadOnlyList<string> Failures,

    /// <summary>
    /// EVERY PATH UNDER THE ROOT THAT COULD NOT BE REMOVED, one by one. Empty when the root went.
    /// When it is not, the root keeps its marker, is recorded as a removal unfinished, and is
    /// retried at the Host's next start or when a person asks (<c>POST /api/removals/retry</c>).
    /// </summary>
    IReadOnlyList<string> Remaining,

    /// <summary>The root recorded as a removal unfinished - the path a retry names - or null when
    /// the root went, was refused, or was never there.</summary>
    string? RemovalUnfinished,

    /// <summary>
    /// THE TEAM'S DOCUMENTS, WHICH THIS DELETION KEPT. Said out loud rather than left to be
    /// noticed, because keeping them is the point: documents live outside the root step 5
    /// removes, and a team is deleted as soon as its work is merged - exactly when its reports
    /// become the only record of how that work was checked, and sometimes its ENTIRE deliverable.
    ///
    /// A PATH, not a boolean, so the sentence the caller shows can name the folder a person can go
    /// and open. Null when there was nothing to keep: a folder holding only the platform's marker
    /// is removed with the team, so a team that wrote no documents leaves no folder behind.
    /// </summary>
    string? DocumentsKept)
{
    /// <summary>
    /// THE TEAM'S LOCAL REPOSITORIES, WHICH THIS DELETION KEPT (B001F): the <c>local:&lt;name&gt;</c>
    /// references it had. They live under <c>&lt;dataRoot&gt;/repos</c>, never under the root, and are
    /// listed as unused afterwards, where a person may delete them. Filled in by the route.
    /// </summary>
    public IReadOnlyList<string> LocalRepositoriesKept { get; init; } = [];
}

/// <summary>
/// Deleting this team would discard commits no remote-tracking ref in the clone can reach, and the
/// caller did not confirm that exact loss.
/// </summary>
public sealed class TeamDeletionConfirmationRequiredException(
    string confirmation,
    IReadOnlyList<string> losses)
    : Exception(
        "Deleting this team would discard commits that are on no remote: "
        + $"{string.Join("; ", losses)}. Repeat the delete with this exact confirmation: "
        + $"{confirmation}")
{
    public string Confirmation { get; } = confirmation;
    public IReadOnlyList<string> Losses { get; } = losses;
}

/// <summary>
/// Deletes a team and everything that names it.
///
/// ITS OWN CLASS, not a method on <see cref="TeamRegistry"/>, for a reason that is about the shape
/// rather than tidiness: this needs the cursor, subscription, pending-delivery, principal and
/// session stores, none of which the registry has or should acquire. Widening that constructor would
/// reach every test that builds one, and would give the registry five dependencies it uses for one
/// operation.
///
/// THE ORDER IS THE DESIGN, and each step is placed against a specific way of getting it wrong:
///
/// 1. <b>Live containers first.</b> They own child processes. A row deleted
///    while a process is still running leaves the process, and nothing afterwards knows to look for
///    it: the team is gone from every list that would have named it.
/// 2. <b>Per-container rows next</b> — cursors, subscriptions, pending deliveries, principals, and
///    schedules. None
///    of these cascade, because none of them reference <c>teams</c>. <c>pending_deliveries</c> is the
///    one that bites: keyed on <c>Team/Name</c> with no foreign key, so a row left behind is
///    inherited by the next container created with that qualified name — a recreated team's
///    <c>Manager</c> publishing <c>container.failed</c> for work it never accepted.
/// 3. <b>The team row</b>, which cascades to <c>team_members</c>. After the
///    per-container rows, not before: once the row is gone, <c>MembersAsync</c> can no longer tell
///    anyone which containers the team had.
/// 4. <b>The registry's memory</b>, so nothing re-derives the team between here and the directories.
/// 5. <b>The directory last</b> — ONE root, never trees enumerated by hand — because it is
///    the only step that is not a database write and the only one that can fail halfway. Failing
///    here leaves files on disk and a team that is otherwise properly gone — recoverable by hand,
///    and named in the result rather than swallowed. Guarded by <see cref="TeamPaths.MarkerFileName"/>:
///    a team root is a path a person can type, and this is the most destructive operation in the
///    product, so it is refused rather than removed when the marker is missing — the same
///    fails-in-the-recoverable-direction argument the pre-migration backup makes, in the opposite
///    direction: a diagnostic must fail open, a delete must fail closed. The removal is
///    <see cref="FolderRemoval"/>'s: the marker goes LAST, content the Host cannot remove is removed
///    as the agent, and whatever still remains is named path by path, keeps the marker, and is
///    recorded so it is retried.
///
/// <b>The root is RESOLVED BEFORE any of the above, not at step 5.</b> <see cref="TeamPaths.RootFor"/>
/// throws for a team nobody registered, and resolving it only at step 5 would mean a wiring fault surfaces AFTER the team row,
/// cursors, subscriptions and principals are already gone: the destructive half completes,
/// <c>paths.Forget</c> never runs, no result is returned, and the caller gets a bare 500 naming
/// nothing — the opposite of this class's own rule that a directory failure is named in the result
/// rather than swallowed. A team we cannot locate is something to discover before deleting a single
/// row, not after.
///
/// <b>The message log is NOT touched, and that is deliberate.</b> It is append-only, and a team's
/// messages are the history of what happened rather than a property of the team. Deleting them would
/// make the ledger a mutable store and would take other teams' causally-linked messages with it.
/// </summary>
public sealed class TeamDeletion(
    TeamRegistry teams,
    ContainerHost host,
    ITeamStore store,
    ICursors cursors,
    ISubscriptions subscriptions,
    EffectiveSubscriptions effective,
    IPendingDeliveries pending,
    ITriggerStore schedules,
    IPrincipalStore principals,
    TeamPaths paths,
    GitRunner git,
    FolderRemoval? removal = null,
    SiteService? sites = null,
    TeamSkills? skills = null,
    ITeamSolutionStore? solutions = null)
{
    private readonly FolderRemoval _removal = removal ?? new FolderRemoval();

    /// <summary>
    /// Deletes <paramref name="team"/>. Returns null when there is no such team, which is a 404
    /// rather than an error - asking twice is not a fault.
    /// </summary>
    public async Task<TeamDeleted?> DeleteAsync(
        string team,
        string? confirmation = null,
        CancellationToken ct = default)
    {
        // The STORED spelling, resolved once. Every path, row key and principal id below is built
        // from it, so taking the caller's capitalisation here would delete a directory that is not
        // there and leave the one that is.
        if (teams.ExistingName(team) is not { } stored) return null;

        // Resolved HERE, before any destructive work, not at step 5. RootFor throws for a team
        // nobody registered, and a throw this late - after the team row, cursors,
        // subscriptions and principals are already gone - would leave the destructive half done,
        // paths.Forget never called, and the caller looking at a bare 500 that names nothing. A
        // team we cannot locate is discovered now, before a single row moves.
        var root = paths.RootFor(stored);

        // BEFORE the first destructive step, because this route deletes the clone itself. A team
        // whose clone or linked worktree still holds commits on no remote can lose its only copy of
        // that work here, even if the ref names all look tidy in the UI.
        var losses = await CommitsOnlyThisMachineCanSeeAsync(stored, ct);
        if (losses.Count > 0)
        {
            var required = ConfirmationFor(stored, losses);
            if (!string.Equals(confirmation?.Trim(), required, StringComparison.Ordinal))
            {
                throw new TeamDeletionConfirmationRequiredException(required, losses);
            }
        }

        var containers = teams.ContainerIdsOf(stored);
        var failures = new List<string>();

        // 1. Containers, and then the rows that only they own.
        var swept = 0;

        foreach (var id in containers)
        {
            try
            {
                await host.RemoveAsync(id);

                swept += await pending.RemoveAllAsync(id, ct);
                await cursors.ForgetAsync(id, ct);

                // NOT a derivation - see MemberDeletion's identical call for why this goes through
                // EffectiveSubscriptions rather than `subscriptions.SetAsync` directly, even though
                // the team row (and this container's) is moments from being gone either way.
                await effective.ClearAsync(id, ct);

                // The container's own credential. Its id IS the qualified name - see
                // AgentEnvironment, which mints with `id.ToString()`. A live credential for a
                // container nobody can see must not outlive the team.
                await principals.RevokeAsync(id.ToString(), ct);
            }
            catch (Exception ex)
            {
                failures.Add($"{id}: {ex.Message}");
            }
        }

        // 2. THE SWEEP BY TEAM, which is not the loop above repeated - it is the half that does not
        // depend on the registry being able to NAME a container.
        //
        // The loop cleans what ContainerIdsOf can enumerate, and three things put a row outside that
        // set: a step that threw mid-loop (the catch above records it and moves on), a container
        // restoration skipped so the registry never knew it, and a deletion by an older build. Every
        // one of those leaves a row keyed on `Team/Name` for the NEXT team with that name to inherit
        // - which is the remnant this whole class exists to prevent.
        //
        // AFTER the loop, not instead of it: the loop is also what stops the live processes and
        // revokes each container's own credential, and it reports per-container failures. This is
        // the backstop, and it is deliberately cheap - four statements, no enumeration.
        //
        // The principal sweep is by the `team` COLUMN, so it takes credentials whose id is not a
        // container name at all: an API key minted against this team, and any other row carrying it.
        // A live credential must never name a team that no longer exists.
        swept += await pending.RemoveAllForTeamAsync(stored, ct);
        await cursors.ForgetTeamAsync(stored, ct);
        await subscriptions.ClearTeamAsync(stored, ct);
        await principals.RevokeForTeamAsync(stored, ct);

        var removedSchedules = await schedules.DeleteForTeamAsync(stored, ct);

        // The team's sites, their files and their data, one tenant row per site, in one
        // transaction. Keyed by the stored spelling, so a same-name successor starts with none.
        if (sites is not null)
        {
            try
            {
                await sites.DeleteTeamAsync(stored, ct);
            }
            catch (Exception ex)
            {
                failures.Add($"sites: {ex.Message}");
            }
        }

        // The team's own skills, one tenant row per skill, in one transaction. Keyed by the stored
        // spelling, so a same-name successor is offered none of them.
        if (skills is not null)
        {
            try
            {
                await skills.DeleteTeamAsync(stored, ct);
            }
            catch (Exception ex)
            {
                failures.Add($"skills: {ex.Message}");
            }
        }

        // Which solution package it came from: a same-name successor was installed from none. The
        // package's plugins stay installed - deleting a team never removes a plugin.
        if (solutions is not null)
        {
            try
            {
                await solutions.DeleteAsync(stored, null, ct);
            }
            catch (Exception ex)
            {
                failures.Add($"solution record: {ex.Message}");
            }
        }

        // 3. The team row. Cascades to team_members.
        await store.DeleteTeamAsync(stored, ct);

        // 4. This process's memory of it. The paused bit lives in ContainerHost rather than the
        // registry, and recreating the same id without clearing it leaves the new team born paused
        // despite a row that says otherwise.
        await host.SetPausedAsync(stored, paused: false);
        teams.Forget(stored);

        // 5. ONE root, resolved at the top of this method, so there is no list of trees for
        // somebody to keep complete.
        var removed = new List<string>();

        // The documents are not in it: they live under the tenant documents root, outside every
        // team root, so removing the root below cannot reach them.
        var kept = TeamPaths.DocumentsFolderIn(paths.DataRoot, stored);

        var remaining = new List<string>();

        if (Directory.Exists(root))
        {
            // REFUSED, not deleted, when the root carries no marker, and reported rather than
            // swallowed: a team root is a path a person can type, and without the marker there is
            // no evidence the platform made this directory. Otherwise emptied around its marker,
            // then the marker, then the root - so anything left keeps the marker, is named path by
            // path, and is recorded to be retried.
            var report = await _removal.RemoveTeamRootAsync(root, stored, ct);

            if (report.Refused is { } refused)
            {
                failures.Add(refused);
            }
            else if (report.Remaining.Count > 0)
            {
                remaining.AddRange(report.Remaining);
                failures.Add(
                    $"{root}: removal unfinished, {report.Remaining.Count} path(s) remain and keep its "
                    + $"{TeamPaths.MarkerFileName} marker; it is retried at the next start or on request: "
                    + string.Join(", ", report.Remaining));
            }
            else
            {
                removed.Add(root);
            }
        }

        // A DOCUMENTS FOLDER HOLDING ONLY ITS MARKER IS NOT KEPT. Every team is given one at
        // creation, so keeping the empty ones listed a "documents kept" folder in the dialog for
        // every team that ever existed, most of them with nothing in it. Only the marker is
        // removed by name, and the folder only when it is then empty: anything else in it - a
        // file, an empty subfolder somebody made - keeps the whole folder, marker included, so
        // the claim guard still recognises it.
        RemoveIfOnlyMarker(kept, stored, failures);

        // This process's memory of the ROOT, distinct from step 4's memory of the team itself - a
        // stale entry here outlives the team it named and would answer a later RootFor(stored) with
        // a path nothing owns any more.
        paths.Forget(stored);

        return new TeamDeleted(
            stored,
            containers.Count,
            swept,
            removedSchedules,
            removed,
            failures,
            remaining,
            remaining.Count > 0 ? root : null,
            Directory.Exists(kept) ? kept : null);
    }

    private static void RemoveIfOnlyMarker(string folder, string team, List<string> failures)
    {
        try
        {
            if (!Directory.Exists(folder)) return;

            var marker = TeamPaths.MarkerIn(folder);
            var entries = Directory.EnumerateFileSystemEntries(folder).ToList();

            if (entries.Count != 1 || !File.Exists(marker)
                || !string.Equals(Path.GetFileName(entries[0]), TeamPaths.MarkerFileName, StringComparison.Ordinal))
            {
                return;
            }

            File.Delete(marker);

            // Not recursive: if anything arrived between the enumeration and here, this throws and
            // the folder stays, which is the recoverable direction.
            Directory.Delete(folder, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Put the marker back if it went: a kept folder without one is refused by the next
            // claim on this id as "not ours".
            if (Directory.Exists(folder)) TeamPaths.EnsureDocumentsFolder(folder, team);

            failures.Add($"{folder}: {ex.Message}");
        }
    }

    private async Task<IReadOnlyList<string>> CommitsOnlyThisMachineCanSeeAsync(
        string team,
        CancellationToken ct)
    {
        var losses = new List<string>();

        foreach (var url in teams.ReposFor(team))
        {
            var repo = RepoUrls.DeriveName(url);
            var clonePath = Path.Combine(paths.ReposFor(team), repo, "main");

            if (!Directory.Exists(clonePath))
            {
                continue;
            }

            // The clone's default branch, as stored. Not known is not `main`: every local
            // branch is probed instead, so nothing on one is lost to a guess.
            var defaultBranch = teams.DefaultBranchFor(team, repo).Branch;
            var mainOnly = await git.CountCommitsNotOnAnyRemoteAsync(
                clonePath, defaultBranch is null ? "--branches" : $"refs/heads/{defaultBranch}", ct);
            if (mainOnly is > 0)
            {
                losses.Add(defaultBranch is null
                    ? $"{repo} local branches ({CommitPhrase(mainOnly.Value)} on no remote)"
                    : $"{repo} {defaultBranch} ({CommitPhrase(mainOnly.Value)} on no remote)");
            }

            var status = await git.StatusAsync(clonePath, defaultBranch, ct: ct);
            foreach (var worktree in status.Worktrees)
            {
                var reference = worktree.Branch is { Length: > 0 }
                    ? $"refs/heads/{worktree.Branch}"
                    : worktree.Sha;

                if (reference is null)
                {
                    continue;
                }

                var count = await git.CountCommitsNotOnAnyRemoteAsync(clonePath, reference, ct);
                if (count is not > 0)
                {
                    continue;
                }

                losses.Add(
                    $"{repo} {WorktreeLabel(worktree)} ({CommitPhrase(count.Value)} on no remote)");
            }
        }

        return losses;
    }

    private string ConfirmationFor(string team, IReadOnlyList<string> losses) =>
        $"Delete {teams.LabelFor(team)} and lose: {string.Join("; ", losses)}";

    private static string WorktreeLabel(GitRunner.WorktreeInfo worktree) =>
        worktree.Branch is { Length: > 0 }
            ? $"worktree {worktree.Branch}"
            : $"detached worktree {ShortSha(worktree.Sha)}";

    private static string ShortSha(string? sha) =>
        string.IsNullOrWhiteSpace(sha)
            ? "unknown"
            : sha[..Math.Min(12, sha.Length)];

    private static string CommitPhrase(int count) =>
        count == 1 ? "1 commit" : $"{count} commits";
}
