using Yawble.Core.Teams;

namespace Yawble.Core.Bus;

/// <summary>Whether a team is quiet enough to archive or delete, and if not, what is still working (in words).</summary>
public sealed record TeamQuiet(bool Quiet, IReadOnlyList<string> Working, IReadOnlyList<long> OpenWorkflows);

/// <summary>
/// The person's team actions: pause and resume, archive and unarchive,
/// delete, and the team's environment variables. A paused team's work waits in place; an archived one is put away whole
/// and does no work; a deleted one is gone (the message log keeps its history).
/// </summary>
public sealed partial class BusEngine
{
    /// <summary>Pauses a team: it takes no new runs (a run already going finishes); queued work waits; triggers skip.</summary>
    public Task PauseTeamAsync(string team, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var id = TeamId(team);
            _store.InTransaction(() =>
            {
                if (_store.Teams().Single(t => t.Id == id).Paused) return;
                _store.SetPaused(id, true);
                _store.DequeueTeamRuns(id);
            });
            Log($"team {team}: paused");
            return true;
        }, ct);

    /// <summary>Resumes a paused team: its waiting work queues again, oldest first. Refused while archived.</summary>
    public Task ResumeTeamAsync(string team, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var row = TeamRow(team);
            if (row.Archived) throw new BusRefusal("archived", $"'{team}' is archived; unarchive it first (it comes back paused), then resume it");
            _store.SetPaused(row.Id, false);
            Log($"team {team}: resumed");
            Signal();
            return true;
        }, ct);

    public Task<TeamQuiet> TeamQuietAsync(string team, CancellationToken ct = default) =>
        WithGate(() => Quiet(TeamId(team)), ct);

    /// <summary>
    /// Archives a quiet team: kept whole (members, settings, repository, history), it does no work and takes no
    /// instructions, and it is paused. Refused while any member runs, has a session, or has work waiting.
    /// </summary>
    public Task ArchiveTeamAsync(string team, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var id = TeamId(team);
            var quiet = Quiet(id);
            if (!quiet.Quiet) throw new BusRefusal("not_quiet", NotQuiet(team, quiet));
            _store.InTransaction(() =>
            {
                _store.SetArchived(id, true);
                _store.DequeueTeamRuns(id);
            });
            Log($"team {team}: archived");
            return true;
        }, ct);

    /// <summary>Unarchives a team. It comes back paused: nothing starts until the person resumes it.</summary>
    public Task UnarchiveTeamAsync(string team, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var id = TeamId(team);
            _store.InTransaction(() =>
            {
                _store.SetArchived(id, false);
                _store.SetPaused(id, true);
            });
            Log($"team {team}: unarchived (paused)");
            return true;
        }, ct);

    /// <summary>
    /// Deletes a quiet team: its members, their subscriptions, queue and settings, its triggers and skills, its Board
    /// cards, and its folder in the app's data (the members' clones and workspaces, and the repository when the app made
    /// it). A repository folder the person picked is never touched. The message log keeps the history.
    /// </summary>
    /// <returns>What could not be removed from disk (in use, say), in words; empty when everything went.</returns>
    public Task<IReadOnlyList<string>> DeleteTeamAsync(string team, CancellationToken ct = default) =>
        WithGate<IReadOnlyList<string>>(() =>
        {
            var row = TeamRow(team);
            var quiet = Quiet(row.Id);
            if (!quiet.Quiet) throw new BusRefusal("not_quiet", NotQuiet(row.Name, quiet));
            foreach (var m in _store.TeamMembers(row.Id)) _statusLines.Remove(m.Id);
            var folder = Repos?.TeamRoot(row.Name);
            Integrations?.ForgetTeam(row.Id, row.Name);
            _store.InTransaction(() => _store.DeleteTeam(row.Id));
            Board?.ForgetTeam(row.Name);
            var left = new List<string>();
            if (folder is not null && Directory.Exists(folder))
            {
                try { DeleteTree(folder); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left.Add($"{folder}: {e.Message}"); }
            }
            Log($"team {row.Name}: deleted");
            return left;
        }, ct);

    /// <summary>Removes a folder whose git objects are read-only (Windows refuses to delete those as they are).</summary>
    private static void DeleteTree(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(folder, recursive: true);
    }

    public Task<IReadOnlyDictionary<string, string>> TeamEnvAsync(string team, CancellationToken ct = default) =>
        WithGate(() => TeamEnv.Parse(_store.TeamEnvJson(TeamId(team))), ct);

    /// <summary>Replaces the team's environment variables (whole). Refused, with every problem named, when any is invalid.</summary>
    public Task<IReadOnlyDictionary<string, string>> SetTeamEnvAsync(string team, IReadOnlyList<KeyValuePair<string, string>> entries, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var id = TeamId(team);
            var problems = TeamEnv.Problems(entries);
            if (problems.Count != 0) throw new BusRefusal("bad_env", "nothing was saved: " + string.Join("; ", problems));
            _store.SetTeamEnv(id, TeamEnv.Serialize(entries));
            return TeamEnv.Parse(_store.TeamEnvJson(id));
        }, ct);

    // ------------------------------------------------------------------ standing approvals (0024)

    public Task<IReadOnlyList<TeamApproval>> TeamApprovalsAsync(string team, CancellationToken ct = default) =>
        WithGate(() => _store.Approvals(TeamId(team)), ct);

    /// <summary>Something the person approves for the team once and for all; the Manager is told it in every run.</summary>
    public Task<TeamApproval> AddTeamApprovalAsync(string team, string text, string? by, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var id = TeamId(team);
            var t = (text ?? "").Trim();
            if (t.Length == 0) throw new BusRefusal("bad_params", "say what is approved");
            if (t.Length > 500) throw new BusRefusal("bad_params", "keep a standing approval under 500 characters");
            var added = _store.AddApproval(id, t, by);
            Log($"team {team}: standing approval {added}: {t}");
            return _store.Approvals(id).Single(a => a.Id == added);
        }, ct);

    public Task RemoveTeamApprovalAsync(string team, long id, CancellationToken ct = default) =>
        WithGate(() =>
        {
            _store.RemoveApproval(TeamId(team), id);
            return true;
        }, ct);

    // ------------------------------------------------------------------ helpers

    private TeamRow TeamRow(string team) =>
        _store.Teams().SingleOrDefault(t => string.Equals(t.Name, team, StringComparison.OrdinalIgnoreCase))
        ?? throw new BusRefusal("no_such_team", $"there is no team '{team}'");

    /// <summary>Running, in a session, or with instructions not yet started: each member named (the archive's check).</summary>
    private TeamQuiet Quiet(long teamId)
    {
        Pump(); // an instruction just told, not yet handed to its member, is work waiting too
        var working = new List<string>();
        var unstarted = _store.Pending(started: false);
        foreach (var m in _store.TeamMembers(teamId))
        {
            var what = new List<string>();
            if (_active.Values.Any(a => a.Context.Member.Id == m.Id)) what.Add("is running");
            if (HasSession(m.Id)) what.Add("has a terminal session");
            var waiting = unstarted.Count(p => string.Equals(p.Subscriber, m.Qualified, StringComparison.OrdinalIgnoreCase));
            if (waiting > 0) what.Add($"has {waiting} instruction{(waiting == 1 ? "" : "s")} waiting");
            if (what.Count > 0) working.Add($"{m.Name} {string.Join(" and ", what)}");
        }
        var team = _store.Teams().Single(t => t.Id == teamId).Name;
        var open = _store.WorkflowsOf(team, 200).Select(w => w.Id).Where(w => !_store.WorkflowEnded(w)).ToArray();
        return new TeamQuiet(working.Count == 0, working, open);
    }

    private static string NotQuiet(string team, TeamQuiet q) =>
        $"'{team}' is not quiet: {string.Join("; ", q.Working)}. Wait for that work to finish, or stop it, then try again. Nothing was changed.";

    /// <summary>A paused or archived team's member: its work waits and it takes no new runs.</summary>
    private bool Held(MemberRef member, IReadOnlySet<long> held) => held.Contains(member.TeamId);
}
