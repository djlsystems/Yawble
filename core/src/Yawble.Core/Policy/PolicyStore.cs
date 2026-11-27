using Microsoft.Data.Sqlite;
using Yawble.Core.Sandboxing;

namespace Yawble.Core.Policy;

/// <summary>One host on a team's network allow list (team_network_allows, 0041).</summary>
public sealed record TeamHost(string Host, string AddedBy, string? AddedVia, string AddedAt);

/// <summary>One folder on a team's folder list (team_folders, 0064). <paramref name="Access"/>: read or readwrite.</summary>
public sealed record TeamFolder(string Path, string Access, string AddedBy, string? AddedVia, string AddedAt)
{
    public bool Write => Access == Integrations.FolderAccess.ReadWrite;

    public TeamFolderGrant Grant => new(Path, Write);
}

public sealed record MemberPolicy(PolicyPatch Override, PolicyPatch Loosen, IReadOnlyList<string> LoosenSummary, DateTimeOffset? LoosenApprovedAt);

/// <summary>
/// Compiles a policy for a sample run and validates it through the sandbox; returns the reasons it was refused.
/// Every save goes through it.
/// </summary>
public delegate IReadOnlyList<string> PolicyDryRun(GlobalPolicy global, PolicyPatch? overridePatch, PolicyPatch? loosen, RunLayout layout);

/// <summary>
/// The policy store: policy_global (one row) and member_policy. Overrides may only tighten; the
/// loosen block is saved only with the person's confirmation of exactly what it loosens. No MCP tool
/// reaches this class, and the database lives on an implicitly denied path.
/// </summary>
public sealed class PolicyStore(string connectionString, PolicyDryRun? dryRun = null, Func<string, string>? canonicalize = null)
{
    /// <summary>The standard dry run: compile a headless sample run and ask the sandbox (<see cref="SandboxRequestValidator"/>).</summary>
    public static PolicyDryRun ThroughSandbox { get; } = (global, o, loosen, layout) =>
    {
        try
        {
            var compiled = SandboxRequestCompiler.Compile(global, o, loosen, SampleRun(layout), HostPathCanonicalizer.Canonicalize);
            var result = SandboxRequestValidator.Validate(compiled.RequestJson);
            return result.Accepted ? [] : [$"the sandbox refused the compiled request: {result.Detail}"];
        }
        catch (PolicyException e)
        {
            return e.Errors;
        }
    };

    public static RunSpec SampleRun(RunLayout layout) => new()
    {
        Layout = layout,
        Credential = layout.Cli switch
        {
            AgentCli.Claude => CredentialMode.ClaudeSubscription,
            AgentCli.Grok => CredentialMode.GrokLogin,
            var other => AgentPresets.HostSignInMode(other),
        },
        Kind = RunKind.Headless,
        Prompt = "policy dry run",
        RunId = "dry-run",
        SessionId = "00000000-0000-0000-0000-000000000000",
        RunToken = new string('d', 43),
        GatewayPort = 47999,
    };

    public GlobalPolicy GetGlobal()
    {
        using var c = Open();
        return ReadGlobal(c, null);
    }

    private static GlobalPolicy ReadGlobal(SqliteConnection c, SqliteTransaction? tx)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT policy_json FROM policy_global WHERE id = 1;";
        return cmd.ExecuteScalar() is string json ? GlobalPolicy.Parse(json) : GlobalPolicy.Default;
    }

    /// <summary>Saves the global policy. <paramref name="sample"/> is a representative member run for the sandbox dry run.</summary>
    public void SaveGlobal(GlobalPolicy policy, RunLayout? sample = null)
    {
        var errors = new List<string>(PolicyMerger.CheckGlobal(policy));
        if (sample is not null)
        {
            var merged = PolicyMerger.Merge(policy, null, null, sample, canonicalize);
            errors.AddRange(merged.Violations);
            if (errors.Count == 0 && dryRun is not null) errors.AddRange(dryRun(policy, null, null, sample));
        }
        if (errors.Count > 0) throw new PolicyException(errors);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO policy_global (id, policy_json, updated_at) VALUES (1, $j, $t)
            ON CONFLICT (id) DO UPDATE SET policy_json = excluded.policy_json, updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$j", policy.ToJson().ToJsonString());
        cmd.Parameters.AddWithValue("$t", Now());
        cmd.ExecuteNonQuery();
    }

    public MemberPolicy GetMember(long memberId)
    {
        using var c = Open();
        return ReadMember(c, null, memberId);
    }

    private static MemberPolicy ReadMember(SqliteConnection c, SqliteTransaction? tx, long memberId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT override_json, loosen_json, loosen_summary, loosen_approved_at FROM member_policy WHERE member_id = $m;";
        cmd.Parameters.AddWithValue("$m", memberId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new MemberPolicy(PolicyPatch.Empty, PolicyPatch.Empty, [], null);
        return new MemberPolicy(
            PolicyPatch.Parse(r.IsDBNull(0) ? null : r.GetString(0), PatchKind.Override),
            PolicyPatch.Parse(r.IsDBNull(1) ? null : r.GetString(1), PatchKind.Loosen),
            r.IsDBNull(2) ? [] : r.GetString(2).Split('\n', StringSplitOptions.RemoveEmptyEntries),
            r.IsDBNull(3) ? null : DateTimeOffset.Parse(r.GetString(3), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Saves a member's override. Refused when any part of it would widen the current global policy.</summary>
    public void SaveOverride(long memberId, PolicyPatch overridePatch, RunLayout layout)
    {
        var global = GetGlobal();
        var current = GetMember(memberId);
        var merged = PolicyMerger.Merge(global, overridePatch, null, layout, canonicalize);
        var errors = merged.Violations.Concat(merged.Widening).ToList();
        if (errors.Count == 0 && dryRun is not null) errors.AddRange(dryRun(global, overridePatch, current.Loosen, layout));
        if (errors.Count > 0) throw new PolicyException(errors);
        Upsert(memberId, "override_json = $o", ("$o", overridePatch.IsEmpty ? DBNull.Value : overridePatch.ToJson().ToJsonString()));
    }

    /// <summary>What saving this loosen block would loosen, exactly: the list the person must confirm.</summary>
    public IReadOnlyList<string> PreviewLoosen(long memberId, PolicyPatch loosen, RunLayout layout)
    {
        var merged = PolicyMerger.Merge(GetGlobal(), GetMember(memberId).Override, loosen, layout, canonicalize);
        if (merged.Violations.Count > 0) throw new PolicyException(merged.Violations);
        return merged.Policy.Loosened;
    }

    /// <summary>
    /// Saves the loosen block. <paramref name="confirmed"/> must be exactly the list from
    /// <see cref="PreviewLoosen"/> that the person confirmed; anything else is refused.
    /// </summary>
    public void SaveLoosen(long memberId, PolicyPatch loosen, RunLayout layout, IReadOnlyList<string> confirmed)
    {
        var global = GetGlobal();
        var current = GetMember(memberId);
        var merged = PolicyMerger.Merge(global, current.Override, loosen, layout, canonicalize);
        var errors = new List<string>(merged.Violations);
        if (!merged.Policy.Loosened.SequenceEqual(confirmed, StringComparer.Ordinal))
            errors.Add("the confirmation does not match what this loosen block loosens; preview it again and confirm the new list");
        if (errors.Count == 0 && dryRun is not null) errors.AddRange(dryRun(global, current.Override, loosen, layout));
        if (errors.Count > 0) throw new PolicyException(errors);
        var empty = loosen.IsEmpty;
        Upsert(memberId, "loosen_json = $l, loosen_summary = $s, loosen_approved_at = $a",
            ("$l", empty ? DBNull.Value : loosen.ToJson().ToJsonString()),
            ("$s", empty ? DBNull.Value : string.Join('\n', confirmed)),
            ("$a", empty ? DBNull.Value : Now()));
    }

    /// <summary>
    /// The effective policy for a member's next run (changes never reach a running sandbox): with its team's network allow
    /// list and its team's folder list.
    /// </summary>
    public MergeResult Effective(long memberId, RunLayout layout, long? machineMemoryMb = null)
    {
        var m = GetMember(memberId);
        return PolicyMerger.Merge(GetGlobal(), m.Override, m.Loosen, layout, canonicalize, machineMemoryMb, TeamHostsOfMember(memberId), TeamFoldersOfMember(memberId));
    }

    /// <summary>What every member of a team shares in its effective policy: the global policy, the team's network allow list and the team's folder list.</summary>
    public sealed record TeamShared(GlobalPolicy Global, IReadOnlyList<string> Hosts, IReadOnlyList<TeamFolderGrant> Folders);

    public TeamShared SharedOf(long teamId) => new(GetGlobal(), [.. TeamHosts(teamId).Select(h => h.Host)], [.. TeamFolders(teamId).Select(f => f.Grant)]);

    /// <summary>
    /// <see cref="Effective(long, RunLayout, long?)"/> for a member of the team <paramref name="shared"/> was read for:
    /// a reader of many members of one team reads the shared part once.
    /// </summary>
    public MergeResult Effective(long memberId, RunLayout layout, long? machineMemoryMb, TeamShared shared)
    {
        var m = GetMember(memberId);
        return PolicyMerger.Merge(shared.Global, m.Override, m.Loosen, layout, canonicalize, machineMemoryMb, shared.Hosts, shared.Folders);
    }

    // ---------------------------------------------------------------- the team's network allow list (0041)

    /// <summary>The team's network allow list, oldest first.</summary>
    public IReadOnlyList<TeamHost> TeamHosts(long teamId)
    {
        using var c = Open();
        return ReadTeamHosts(c, null, teamId);
    }

    private static List<TeamHost> ReadTeamHosts(SqliteConnection c, SqliteTransaction? tx, long teamId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT host, added_by, added_via, added_at FROM team_network_allows WHERE team_id = $t ORDER BY added_at, host;";
        cmd.Parameters.AddWithValue("$t", teamId);
        using var r = cmd.ExecuteReader();
        var list = new List<TeamHost>();
        while (r.Read()) list.Add(new TeamHost(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3)));
        return list;
    }

    /// <summary>The team's current members' ids (the checks of a team host run against each one's layout).</summary>
    public IReadOnlyList<long> TeamMemberIds(long teamId)
    {
        using var c = Open();
        return ReadTeamMemberIds(c, null, teamId);
    }

    private static List<long> ReadTeamMemberIds(SqliteConnection c, SqliteTransaction? tx, long teamId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id FROM members WHERE team_id = $t ORDER BY id;";
        cmd.Parameters.AddWithValue("$t", teamId);
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    /// <summary>The hosts of the member's own team's list (never another team's); empty for a member that is gone.</summary>
    public IReadOnlyList<string> TeamHostsOfMember(long memberId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT a.host FROM team_network_allows a JOIN members m ON m.team_id = a.team_id WHERE m.id = $m ORDER BY a.added_at, a.host;";
        cmd.Parameters.AddWithValue("$m", memberId);
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>
    /// Adds a host, or a domain allow ("*.digicert.com": every subdomain, not the apex), to the team's network allow list,
    /// in one write. Checked first: the floor rules (no vendor host, no wildcard but one leading "*." over a registrable
    /// domain, no address, a lower-case DNS name), then each member's merged policy with the host and the sandbox dry run
    /// (<paramref name="members"/>: each current member's run layout, every member of the team exactly once). Any refusal
    /// throws and nothing is written. False when the host was on the list already.
    /// The write is one database transaction (BEGIN IMMEDIATE) that reads the team's list, the global policy, the team's
    /// member ids and each member's policy again and writes the row only when they are exactly what was checked; so no
    /// policy change, hire or removal can land between the checks and the write, and the row written is one every member's
    /// check accepted against the state it is written in. When a policy changed the checks run again on the new state (up
    /// to <see cref="AddTeamHostAttempts"/> times, then refused). When the team's member ids (read in the snapshot and again
    /// in the transaction) are not exactly the ids in <paramref name="members"/> - a member hired or removed, in either
    /// direction - nothing is written and <see cref="TeamMembersChangedException"/> is thrown: this store cannot get a run
    /// layout, so the caller builds the list again and calls again (<see cref="Integrations.StoreNetworkPolicy.AllowForTeamAsync"/>).
    /// The sandbox dry run (a no-op launch on some hosts) runs before the transaction, never while it holds the database's
    /// write lock; it depends only on what was read, so equal state means an equal answer.
    /// </summary>
    public bool AddTeamHost(long teamId, string host, IReadOnlyList<(long MemberId, RunLayout Layout)> members, string addedBy, string? addedVia)
    {
        // The ask filter's rules too (ASCII names only, no trailing dot, not the app's own hosts), whoever calls this; for
        // a domain allow ("*.digicert.com") they apply to its domain, and the floor rules to the whole pattern.
        if ((Integrations.NetworkAskFilter.Problem(TeamHostFilterName(host)) ?? FloorRules.HostProblem(host)) is { } problem) throw new PolicyException([$"network.allowHosts: {problem}"]);
        var memberIds = members.Select(m => m.MemberId).ToList();
        for (var attempt = 1; ; attempt++)
        {
            TeamHostState checkedState;
            using (var r = Open()) checkedState = ReadTeamHostState(r, null, teamId, memberIds);
            if (checkedState.Hosts.Contains(host, StringComparer.Ordinal)) return false;
            EnsureSameMembers(teamId, memberIds, checkedState.TeamMemberIds);
            CheckTeamHost(checkedState, host, members);

            using var c = Open();
            using var tx = c.BeginTransaction(deferred: false);
            var now = ReadTeamHostState(c, tx, teamId, memberIds);
            if (now.Hosts.Contains(host, StringComparer.Ordinal)) return false; // disposing tx rolls back: nothing written
            EnsureSameMembers(teamId, memberIds, now.TeamMemberIds); // a throw disposes tx: rolled back, nothing written
            if (now.Stored != checkedState.Stored)
            {
                if (attempt >= AddTeamHostAttempts)
                    throw new PolicyException(["the team's policy kept changing while the host was checked; nothing was written, try again"]);
                continue; // checked against an older state: check again on the new one
            }
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO team_network_allows (team_id, host, added_by, added_via, added_at) VALUES ($t, $h, $by, $via, $at) ON CONFLICT (team_id, host) DO NOTHING;";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$h", host);
            cmd.Parameters.AddWithValue("$by", addedBy);
            cmd.Parameters.AddWithValue("$via", (object?)addedVia ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", Now());
            var added = cmd.ExecuteNonQuery() > 0;
            tx.Commit();
            return added;
        }
    }

    /// <summary>The name the ask filter checks: a domain allow's domain ("*.digicert.com" → digicert.com), else the host.</summary>
    private static string TeamHostFilterName(string host) => host.StartsWith("*.", StringComparison.Ordinal) ? host[2..] : host;

    /// <summary>How many times <see cref="AddTeamHost"/> checks again after the policy changed under it before it refuses.</summary>
    public const int AddTeamHostAttempts = 3;

    /// <summary>
    /// Throws <see cref="TeamMembersChangedException"/> unless the team's current member ids are exactly the given ones (as
    /// sets, both directions: a hire and a removal are both a change; a member given twice is not a different team).
    /// </summary>
    private static void EnsureSameMembers(long teamId, IReadOnlyList<long> given, IReadOnlyList<long> current)
    {
        if (!given.ToHashSet().SetEquals(current))
            throw new TeamMembersChangedException(teamId, given.Distinct().Order().ToList(), current);
    }

    /// <summary>
    /// What AddTeamHost and AddTeamFolder check: the team's hosts and folders, the global policy, the team's member ids and
    /// each given member's policy; <c>Stored</c> is the stored text of all of them.
    /// </summary>
    private sealed record TeamHostState(List<string> Hosts, GlobalPolicy Global, List<long> TeamMemberIds, List<MemberPolicy> Members, string Stored, List<TeamFolder> Folders);

    private static TeamHostState ReadTeamHostState(SqliteConnection c, SqliteTransaction? tx, long teamId, IReadOnlyList<long> memberIds)
    {
        var stored = new System.Text.StringBuilder();
        var hosts = ReadTeamHosts(c, tx, teamId).Select(t => t.Host).ToList();
        var teamMemberIds = ReadTeamMemberIds(c, tx, teamId);
        stored.Append(string.Join('\n', hosts)).Append('\0');
        var folders = ReadTeamFolders(c, tx, teamId);
        stored.Append(string.Join('\n', folders.Select(f => f.Access + ' ' + f.Path))).Append('\0');
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT policy_json FROM policy_global WHERE id = 1;";
            stored.Append(cmd.ExecuteScalar() as string).Append('\0');
        }
        var global = ReadGlobal(c, tx);
        var list = new List<MemberPolicy>();
        foreach (var id in memberIds)
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT override_json, loosen_json FROM member_policy WHERE member_id = $m;";
                cmd.Parameters.AddWithValue("$m", id);
                using var r = cmd.ExecuteReader();
                stored.Append(id).Append(':');
                if (r.Read()) stored.Append(r.IsDBNull(0) ? "" : r.GetString(0)).Append('\u0001').Append(r.IsDBNull(1) ? "" : r.GetString(1));
                stored.Append('\0');
            }
            list.Add(ReadMember(c, tx, id));
        }
        return new TeamHostState(hosts, global, teamMemberIds, list, stored.ToString(), folders);
    }

    /// <summary>Each member's merged policy with the host, then the sandbox dry run; throws with every refusal.</summary>
    private void CheckTeamHost(TeamHostState state, string host, IReadOnlyList<(long MemberId, RunLayout Layout)> members)
    {
        var errors = new List<string>();
        for (var i = 0; i < members.Count; i++)
        {
            var (m, layout) = (state.Members[i], members[i].Layout);
            var merged = PolicyMerger.Merge(state.Global, m.Override, m.Loosen, layout, canonicalize, null, [.. state.Hosts, host], [.. state.Folders.Select(f => f.Grant)]);
            errors.AddRange(merged.Violations);
            if (merged.Violations.Count == 0 && dryRun is not null)
                errors.AddRange(dryRun(state.Global, m.Override, m.Loosen with { Network = new NetworkPatch([.. m.Loosen.Network?.AllowHosts ?? [], .. state.Hosts, host]) }, layout));
        }
        if (errors.Count > 0) throw new PolicyException(errors.Distinct().ToList());
    }

    /// <summary>Takes a host off the team's list: the team's next runs no longer have it (a member's own loosen block may still). False when it was not there.</summary>
    public bool RemoveTeamHost(long teamId, string host)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM team_network_allows WHERE team_id = $t AND host = $h;";
        cmd.Parameters.AddWithValue("$t", teamId);
        cmd.Parameters.AddWithValue("$h", host.Trim().ToLowerInvariant());
        return cmd.ExecuteNonQuery() > 0;
    }

    // ---------------------------------------------------------------- the team's folder list (0064)

    /// <summary>The team's folder list, oldest first.</summary>
    public IReadOnlyList<TeamFolder> TeamFolders(long teamId)
    {
        using var c = Open();
        return ReadTeamFolders(c, null, teamId);
    }

    private static List<TeamFolder> ReadTeamFolders(SqliteConnection c, SqliteTransaction? tx, long teamId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT path, access, added_by, added_via, added_at FROM team_folders WHERE team_id = $t ORDER BY added_at, path;";
        cmd.Parameters.AddWithValue("$t", teamId);
        using var r = cmd.ExecuteReader();
        var list = new List<TeamFolder>();
        while (r.Read()) list.Add(new TeamFolder(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4)));
        return list;
    }

    /// <summary>The folders of the member's own team's list (never another team's); empty for a member that is gone.</summary>
    public IReadOnlyList<TeamFolderGrant> TeamFoldersOfMember(long memberId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT f.path, f.access FROM team_folders f JOIN members m ON m.team_id = f.team_id WHERE m.id = $m ORDER BY f.added_at, f.path;";
        cmd.Parameters.AddWithValue("$m", memberId);
        using var r = cmd.ExecuteReader();
        var list = new List<TeamFolderGrant>();
        while (r.Read()) list.Add(new TeamFolderGrant(r.GetString(0), r.GetString(1) == Integrations.FolderAccess.ReadWrite));
        return list;
    }

    /// <summary>
    /// Adds a folder to the team's folder list with read or readwrite access, or changes the access of one that is on it,
    /// in one write. Checked first, for every current member (<paramref name="members"/>: each one's run layout, every
    /// member of the team exactly once): the path is absolute; <see cref="Integrations.FolderAskRules.Problem"/> allows it
    /// (never another team's folders, <paramref name="otherTeamFolders"/>; never secrets, the app's own data, a system
    /// folder, the profile root); the member's merged policy with the folder has no violation and really grants it with
    /// that access (a deny wins); the sandbox dry run accepts it. Any refusal throws and nothing is written. Returns the
    /// folder as it is stored, or null when it was on the list with that access already.
    /// The write follows <see cref="AddTeamHost"/>: one transaction that writes only when the team's lists, the global
    /// policy, the team's member ids and each member's policy are exactly what was checked; a member hired or removed
    /// meanwhile throws <see cref="TeamMembersChangedException"/> and the caller builds the list again.
    /// </summary>
    public string? AddTeamFolder(long teamId, string path, string access, IReadOnlyList<(long MemberId, RunLayout Layout)> members,
        IReadOnlyList<string> otherTeamFolders, string addedBy, string? addedVia)
    {
        if (access is not (Integrations.FolderAccess.Read or Integrations.FolderAccess.ReadWrite))
            throw new PolicyException([$"access is read or readwrite, not '{access}'"]);
        var memberIds = members.Select(m => m.MemberId).ToList();
        for (var attempt = 1; ; attempt++)
        {
            TeamHostState checkedState;
            using (var r = Open()) checkedState = ReadTeamHostState(r, null, teamId, memberIds);
            EnsureSameMembers(teamId, memberIds, checkedState.TeamMemberIds);
            if (members.Count == 0) throw new PolicyException(["the team has no members to check the folder for; nothing was written"]);
            var first = new PolicyPaths(members[0].Layout.Os, members[0].Layout.Home, canonicalize);
            var raw = (path ?? "").Trim();
            if (!Integrations.FolderAskRules.IsAbsolute(raw, first.Os) || first.Resolve(raw) is not { } resolved)
                throw new PolicyException([$"'{raw}' is not an absolute path on this computer; give the folder's full path"]);
            var existing = checkedState.Folders.FirstOrDefault(f => first.Same(f.Path, resolved));
            if (existing is not null && existing.Access == access) return null;
            var stored = existing?.Path ?? resolved;
            CheckTeamFolder(checkedState, stored, access, members, otherTeamFolders, Names(memberIds));

            using var c = Open();
            using var tx = c.BeginTransaction(deferred: false);
            var now = ReadTeamHostState(c, tx, teamId, memberIds);
            EnsureSameMembers(teamId, memberIds, now.TeamMemberIds); // a throw disposes tx: rolled back, nothing written
            if (now.Stored != checkedState.Stored)
            {
                if (attempt >= AddTeamHostAttempts)
                    throw new PolicyException(["the team's policy kept changing while the folder was checked; nothing was written, try again"]);
                continue; // checked against an older state: check again on the new one
            }
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO team_folders (team_id, path, access, added_by, added_via, added_at) VALUES ($t, $p, $a, $by, $via, $at)
                ON CONFLICT (team_id, path) DO UPDATE SET access = excluded.access, added_by = excluded.added_by, added_via = excluded.added_via, added_at = excluded.added_at;
                """;
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$p", stored);
            cmd.Parameters.AddWithValue("$a", access);
            cmd.Parameters.AddWithValue("$by", addedBy);
            cmd.Parameters.AddWithValue("$via", (object?)addedVia ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", Now());
            cmd.ExecuteNonQuery();
            tx.Commit();
            return stored;
        }
    }

    private Dictionary<long, string> Names(IReadOnlyList<long> memberIds)
    {
        var names = new Dictionary<long, string>();
        using var c = Open();
        foreach (var id in memberIds)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT name FROM members WHERE id = $m;";
            cmd.Parameters.AddWithValue("$m", id);
            names[id] = cmd.ExecuteScalar() as string ?? $"member {id}";
        }
        return names;
    }

    /// <summary>The folder for each member: the ask rules, the merged policy with it, that the merge really grants it, then the sandbox dry run; throws with every refusal.</summary>
    private void CheckTeamFolder(TeamHostState state, string folder, string access, IReadOnlyList<(long MemberId, RunLayout Layout)> members,
        IReadOnlyList<string> otherTeamFolders, Dictionary<long, string> names)
    {
        var write = access == Integrations.FolderAccess.ReadWrite;
        var errors = new List<string>();
        for (var i = 0; i < members.Count; i++)
        {
            var (m, layout, name) = (state.Members[i], members[i].Layout, names[members[i].MemberId]);
            var paths = new PolicyPaths(layout.Os, layout.Home, canonicalize);
            if (Integrations.FolderAskRules.Problem(paths, folder, access, layout, state.Global, otherTeamFolders) is { } problem)
            {
                errors.Add(problem);
                continue;
            }
            List<TeamFolderGrant> folders = [.. state.Folders.Where(f => !paths.Same(f.Path, folder)).Select(f => f.Grant), new(folder, write)];
            var merged = PolicyMerger.Merge(state.Global, m.Override, m.Loosen, layout, canonicalize, null, state.Hosts, folders);
            errors.AddRange(merged.Violations);
            if (merged.Violations.Count > 0) continue;
            var granted = Integrations.FolderAskRules.Granted(paths, merged.Policy, layout, folder);
            if (granted is null || (write && granted != Integrations.FolderAccess.ReadWrite))
            {
                errors.Add($"{folder} would {(granted is null ? "not be readable" : "stay read-only")} for {name}: the policy denies it or grants it read-only there (see {name}'s Policy tab and the global policy)");
                continue;
            }
            if (dryRun is null) continue;
            // The dry run compiles a loosen block: the team's folders the merge kept go in as its entries.
            bool Kept(TeamFolderGrant f) => paths.Resolve(f.Path) is { } p && merged.Policy.Readonly.Concat(merged.Policy.Readwrite).Any(g => paths.Same(g, p));
            var kept = folders.Where(Kept).ToList();
            var fs = m.Loosen.Filesystem ?? new FilesystemPatch();
            var loosen = m.Loosen with
            {
                Network = new NetworkPatch([.. m.Loosen.Network?.AllowHosts ?? [], .. state.Hosts]),
                Filesystem = fs with
                {
                    Readonly = [.. fs.Readonly ?? [], .. kept.Where(f => !f.Write).Select(f => f.Path)],
                    Readwrite = [.. fs.Readwrite ?? [], .. kept.Where(f => f.Write).Select(f => f.Path)],
                },
            };
            errors.AddRange(dryRun(state.Global, m.Override, loosen, layout));
        }
        if (errors.Count > 0) throw new PolicyException(errors.Distinct().ToList());
    }

    /// <summary>Takes a folder off the team's list: the team's next runs no longer have it (a member's own loosen block may still). False when it was not there.</summary>
    public bool RemoveTeamFolder(long teamId, string path)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM team_folders WHERE team_id = $t AND path = $p;";
        cmd.Parameters.AddWithValue("$t", teamId);
        cmd.Parameters.AddWithValue("$p", path.Trim());
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// The folders outside its data folder a workflow member's file steps may use: its own loosen block's and its team's
    /// folder list's, read-write ones writable and read-only ones readable. A folder under a path the global policy denies
    /// is left out. The most specific folder comes first, and of one folder granted both ways the writable one.
    /// </summary>
    public IReadOnlyList<(string Path, bool CanWrite)> WorkflowFolders(long memberId, PolicyPaths paths)
    {
        var loosen = GetMember(memberId).Loosen.Filesystem;
        var denied = GetGlobal().Filesystem.Denied.Select(paths.Resolve).OfType<string>().ToList();
        var all = (loosen?.Readwrite ?? []).Select(p => (Path: p, CanWrite: true))
            .Concat((loosen?.Readonly ?? []).Select(p => (Path: p, CanWrite: false)))
            .Concat(TeamFoldersOfMember(memberId).Select(f => (f.Path, CanWrite: f.Write)));
        var list = new List<(string Path, bool CanWrite)>();
        foreach (var (raw, canWrite) in all)
        {
            if (paths.Resolve(raw) is not { } p || denied.Any(d => paths.IsWithin(p, d))) continue;
            if (!list.Any(g => g.CanWrite == canWrite && paths.Same(g.Path, p))) list.Add((p, canWrite));
        }
        return [.. list.OrderByDescending(g => g.Path.Length).ThenByDescending(g => g.CanWrite)];
    }

    private void Upsert(long memberId, string set, params (string Name, object Value)[] values)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO member_policy (member_id, updated_at) VALUES ($m, $t) ON CONFLICT (member_id) DO NOTHING;
            UPDATE member_policy SET {set}, updated_at = $t WHERE member_id = $m;
            """;
        cmd.Parameters.AddWithValue("$m", memberId);
        cmd.Parameters.AddWithValue("$t", Now());
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = Storage.Db.Open(connectionString);
        return c;
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
}
