namespace Yawble.Core.Bus;

/// <summary>What a team reset clears, for the members picked (and the team's documents, team-wide).</summary>
public sealed record TeamResetOptions(bool DropWaiting, bool ClearHome, bool RemoveClone, bool ClearDocuments);

/// <summary>What a reset did, in words, and what it could not remove.</summary>
public sealed record TeamResetResult(IReadOnlyList<string> Done, IReadOnlyList<string> Left);

/// <summary>
/// A team reset: the members stay as they are; what they carry is
/// cleared. For each member picked: its waiting instructions, its home folder (the CLI's own session history and
/// memory) and temp folder, and its working copy (made again from its branch in the team repository at its next
/// run, so committed work stays and uncommitted changes go). The team's documents too, when asked. Refused unless
/// every member picked is quiet, and nothing is cleared then.
/// </summary>
public sealed partial class BusEngine
{
    /// <remarks>
    /// The checks and the dropped instructions happen inside the gate; the folders are cleared after the gate is
    /// released, so a large working copy holds up nobody else. Until they are cleared the picked members take no run
    /// and no terminal session (their folders are going), and a second reset or a delete of the team is refused.
    /// </remarks>
    public async Task<TeamResetResult> ResetTeamAsync(string team, IReadOnlyList<string> members, TeamResetOptions options, CancellationToken ct = default)
    {
        var done = new List<string>();
        var left = new List<string>();
        var folders = options.ClearHome || options.RemoveClone || options.ClearDocuments;
        var (name, picked, repos) = await WithGate(() =>
        {
            var row = TeamRow(team);
            Pump();
            var all = _store.TeamMembers(row.Id);
            var picked = members.Count == 0 ? all.ToList()
                : members.Select(n => all.FirstOrDefault(m => string.Equals(m.Name, n.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new BusRefusal("no_such_member", $"'{row.Name}' has no member named '{n}'")).ToList();
            var unstarted = _store.Pending(started: false);
            var busy = picked.Where(m => _active.Values.Any(a => a.Context.Member.Id == m.Id) || HasSession(m.Id)).Select(m => m.Name).ToList();
            if (busy.Count > 0)
                throw new BusRefusal("not_quiet", $"'{row.Name}' cannot be reset while {string.Join(", ", busy)} {(busy.Count == 1 ? "is" : "are")} working. Nothing was changed.");
            var going = picked.Where(m => _membersResetting.Contains(m.Id)).Select(m => m.Name).ToList();
            if (going.Count > 0)
                throw new BusRefusal("busy", $"{string.Join(", ", going)} {(going.Count == 1 ? "is" : "are")} still being reset. Try again in a moment. Nothing was changed.");
            if (!options.DropWaiting)
            {
                var waiting = picked.Where(m => unstarted.Any(p => string.Equals(p.Subscriber, m.Qualified, StringComparison.OrdinalIgnoreCase))).Select(m => m.Name).ToList();
                if (waiting.Count > 0 && (options.RemoveClone || options.ClearHome))
                    throw new BusRefusal("not_quiet", $"{string.Join(", ", waiting)} {(waiting.Count == 1 ? "has" : "have")} instructions waiting: drop them too, or wait. Nothing was changed.");
            }
            if (options.DropWaiting)
            {
                var dropped = _store.InTransaction(() => picked.Sum(m => _store.DropWaiting(m.Id, m.Qualified)));
                done.Add($"dropped {dropped} waiting instruction{(dropped == 1 ? "" : "s")}");
            }
            foreach (var m in picked) _statusLines.Remove(m.Id);
            if (Repos is not null && folders)
                foreach (var m in picked) _membersResetting.Add(m.Id);
            return (row.Name, picked, Repos);
        }, ct);
        if (repos is not null && folders)
        {
            // The checks passed and the members are held: their folders are cleared whatever becomes of the caller's token.
            try
            {
                await Task.Run(() => ClearFolders(repos, name, picked, options, left), CancellationToken.None);
            }
            finally
            {
                await WithGate(() => { foreach (var m in picked) _membersResetting.Remove(m.Id); }, CancellationToken.None);
                Signal(); // work that arrived for a held member is scheduled
            }
            if (options.ClearHome) done.Add($"cleared the home and temp folders of {string.Join(", ", picked.Select(m => m.Name))}");
            if (options.RemoveClone) done.Add($"removed the working copies of {string.Join(", ", picked.Select(m => m.Name))} (made again from their branches at their next run)");
            if (options.ClearDocuments) done.Add("cleared the team's documents");
        }
        else if (folders)
        {
            left.Add("the team's folders are not here (no app data folder)");
        }
        Log($"team {name}: reset ({string.Join("; ", done)})");
        return new TeamResetResult(done, left);
    }

    /// <summary>The members whose folders a reset is clearing now, by id. Under the gate.</summary>
    private readonly HashSet<long> _membersResetting = [];

    /// <summary>The file work of a reset, outside the gate: what could not be removed goes to <paramref name="left"/>.</summary>
    private void ClearFolders(Teams.TeamRepos repos, string team, IReadOnlyList<MemberRef> picked, TeamResetOptions options, List<string> left)
    {
        foreach (var m in picked)
        {
            var root = repos.MemberRoot(team, m.Name);
            if (options.ClearHome)
            {
                Empty(Path.Combine(root, "home"), left);
                Empty(Path.Combine(root, "tmp"), left);
            }
            // The member's clones: the main one and those of the team's other writable repositories (repos/).
            foreach (var folder in new[] { "repo", Teams.TeamRepos.ExtraClonesFolder })
            {
                if (!options.RemoveClone || !Directory.Exists(Path.Combine(root, folder))) continue;
                try { RemoveFolder(Path.Combine(root, folder)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left.Add($"{m.Name}'s working copy: {e.Message}"); }
            }
        }
        if (options.ClearDocuments) Empty(Path.Combine(repos.TeamRoot(team), Teams.TeamDocuments.Folder), left);
    }

    /// <summary>Removes everything inside a folder, keeping the folder.</summary>
    private static void Empty(string folder, List<string> left)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            try
            {
                if (entry is DirectoryInfo d) DeleteTree(d.FullName);
                else
                {
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                    entry.Delete();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                left.Add($"{entry.FullName}: {e.Message}");
            }
        }
    }
}
