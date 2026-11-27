using Yawble.Core.Teams;

namespace Yawble.Core.Bus;

/// <summary>Whether a team is quiet enough to archive or delete, and if not, what is still working (in words).</summary>
public sealed record TeamQuiet(bool Quiet, IReadOnlyList<string> Working, IReadOnlyList<long> OpenDeliverables);

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
    /// <remarks>
    /// The team leaves the database inside the gate, in one transaction; its folder is removed after the gate is
    /// released, so a large folder holds up nobody else. The order is the database first: a core that stops in between
    /// leaves a folder without a team, never a team without its folder. Until the folder is gone a new team may not
    /// take the name (its folder would be the one being removed).
    /// </remarks>
    /// <returns>What could not be removed from disk (in use, say), in words; empty when everything went.</returns>
    public async Task<IReadOnlyList<string>> DeleteTeamAsync(string team, CancellationToken ct = default)
    {
        var (name, folder) = await WithGate(() =>
        {
            var row = TeamRow(team);
            var quiet = Quiet(row.Id);
            if (!quiet.Quiet) throw new BusRefusal("not_quiet", NotQuiet(row.Name, quiet));
            foreach (var m in _store.TeamMembers(row.Id)) _statusLines.Remove(m.Id);
            var root = Repos?.TeamRoot(row.Name);
            Integrations?.ForgetTeam(row.Id, row.Name);
            _store.InTransaction(() => _store.DeleteTeam(row.Id));
            Board?.ForgetTeam(row.Name);
            if (root is not null) _teamFoldersGoing.Add(row.Name);
            return (row.Name, root);
        }, ct);
        var left = new List<string>();
        if (folder is not null)
        {
            // The team is gone from the database: its folder goes whatever becomes of the caller's token.
            try
            {
                await Task.Run(() =>
                {
                    if (!Directory.Exists(folder)) return;
                    try { RemoveFolder(folder); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left.Add($"{folder}: {e.Message}"); }
                }, CancellationToken.None);
            }
            finally
            {
                await WithGate(() => { _teamFoldersGoing.Remove(name); }, CancellationToken.None);
            }
        }
        Log($"team {name}: deleted");
        return left;
    }

    /// <summary>The deleted teams whose folders are being removed now, by name. Under the gate.</summary>
    private readonly HashSet<string> _teamFoldersGoing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Removes a folder and everything in it: a deleted team's folder, a reset member's working copy (tests set a fake that waits).</summary>
    internal Action<string> RemoveFolder { get; set; } = DeleteTree;

    /// <summary>Under the gate: refuses a new team the name of one whose folder is still being removed.</summary>
    private void RefuseWhileFolderGoes(string name)
    {
        if (_teamFoldersGoing.Contains(name))
            throw new BusRefusal("busy", $"the deleted team '{name}' is still being removed from this computer. Try again in a moment.");
    }

    /// <summary>Removes a folder whose git objects are read-only (Windows refuses to delete those as they are).</summary>
    private static void DeleteTree(string folder)
    {
        foreach (var file in Ops.FolderWalk.Files(folder).Select(f => f.FullName))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(folder, recursive: true);
    }

    /// <summary>
    /// The team's environment variables: every name with its value. A value this computer cannot read (its key is not
    /// in this computer's credential store) comes as <see cref="TeamEnv.UnreadableValue"/>.
    /// </summary>
    public Task<IReadOnlyDictionary<string, string>> TeamEnvAsync(string team, CancellationToken ct = default) =>
        WithGate(() => TeamEnvVault.Read(_options.EnvVault, _store.TeamEnvJson(TeamId(team))).Shown, ct);

    /// <summary>Replaces the team's environment variables (whole). Refused, with every problem named, when any is invalid.</summary>
    /// <param name="hiddenValues">The caller saw the values hidden (a Cloud Link viewer): an entry that still holds
    /// <see cref="TeamEnv.HiddenValue"/> keeps its stored value (<see cref="TeamEnv.KeepHidden"/>).</param>
    /// <remarks>The values are stored sealed (<see cref="TeamEnvVault"/>). An entry that holds
    /// <see cref="TeamEnv.UnreadableValue"/> leaves the stored, unreadable value of that name as it is.</remarks>
    public Task<IReadOnlyDictionary<string, string>> SetTeamEnvAsync(string team, IReadOnlyList<KeyValuePair<string, string>> entries, CancellationToken ct = default,
        bool hiddenValues = false) =>
        WithGate(() =>
        {
            var id = TeamId(team);
            var stored = _store.TeamEnvJson(id);
            if (hiddenValues)
            {
                var (kept, missing) = TeamEnv.KeepHidden(entries, TeamEnvVault.Read(_options.EnvVault, stored).Shown);
                if (missing.Count != 0) throw new BusRefusal("bad_env", "nothing was saved: " + string.Join("; ", missing));
                entries = kept;
            }
            var problems = TeamEnv.Problems(entries);
            if (problems.Count != 0) throw new BusRefusal("bad_env", "nothing was saved: " + string.Join("; ", problems));
            var (json, unsealed) = TeamEnvVault.Write(_options.EnvVault, entries, stored);
            if (json is null) throw new BusRefusal("bad_env", "nothing was saved: " + string.Join("; ", unsealed));
            _store.SetTeamEnv(id, json);
            return TeamEnvVault.Read(_options.EnvVault, _store.TeamEnvJson(id)).Shown;
        }, ct);

    /// <summary>
    /// Reads, or sets when <paramref name="on"/> is given, the team's "Allow localhost (macOS)" (0054; off by default):
    /// whether its macOS runs may open local ports and reach any service on the Mac's loopback. The value now.
    /// </summary>
    public Task<bool> TeamLocalhostAsync(string team, bool? on, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var id = TeamId(team);
            if (on is { } v && v != _store.TeamAllowsLocalhost(id))
            {
                _store.SetTeamAllowsLocalhost(id, v);
                Log($"team {team}: Allow localhost (macOS) turned {(v ? "on" : "off")}");
            }
            return _store.TeamAllowsLocalhost(id);
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
            if (_membersResetting.Contains(m.Id)) what.Add("is being reset");
            var waiting = unstarted.Count(p => string.Equals(p.Subscriber, m.Qualified, StringComparison.OrdinalIgnoreCase));
            if (waiting > 0) what.Add($"has {waiting} instruction{(waiting == 1 ? "" : "s")} waiting");
            if (what.Count > 0) working.Add($"{m.Name} {string.Join(" and ", what)}");
        }
        var team = _store.Teams().Single(t => t.Id == teamId).Name;
        var open = _store.DeliverablesOf(team, -1).Select(w => w.Id).Where(w => !_store.DeliverableEnded(w)).ToArray();
        return new TeamQuiet(working.Count == 0, working, open);
    }

    private static string NotQuiet(string team, TeamQuiet q) =>
        $"'{team}' is not quiet: {string.Join("; ", q.Working)}. Wait for that work to finish, or stop it, then try again. Nothing was changed.";

    /// <summary>A paused or archived team's member: its work waits and it takes no new runs.</summary>
    private bool Held(MemberRef member, IReadOnlySet<long> held) => held.Contains(member.TeamId);
}
