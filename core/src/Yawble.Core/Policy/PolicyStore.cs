using Microsoft.Data.Sqlite;
using Yawble.Core.Sandboxing;

namespace Yawble.Core.Policy;

/// <summary>One host on a team's network allow list (team_network_allows, 0041).</summary>
public sealed record TeamHost(string Host, string AddedBy, string? AddedVia, string AddedAt);

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
        using var cmd = c.CreateCommand();
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
        using var cmd = c.CreateCommand();
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

    /// <summary>The effective policy for a member's next run (changes never reach a running sandbox): with its team's network allow list.</summary>
    public MergeResult Effective(long memberId, RunLayout layout, long? machineMemoryMb = null)
    {
        var m = GetMember(memberId);
        return PolicyMerger.Merge(GetGlobal(), m.Override, m.Loosen, layout, canonicalize, machineMemoryMb, TeamHostsOfMember(memberId));
    }

    // ---------------------------------------------------------------- the team's network allow list (0041)

    /// <summary>The team's network allow list, oldest first.</summary>
    public IReadOnlyList<TeamHost> TeamHosts(long teamId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
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
        using var cmd = c.CreateCommand();
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
    /// Adds a host to the team's network allow list, in one write. Checked first: the floor rules (no vendor host, no
    /// wildcard, no address, a lower-case DNS name), then each member's merged policy with the host and the sandbox dry run
    /// (<paramref name="members"/>: each current member's run layout). Any refusal throws and nothing is written.
    /// False when the host was on the list already.
    /// </summary>
    public bool AddTeamHost(long teamId, string host, IReadOnlyList<(long MemberId, RunLayout Layout)> members, string addedBy, string? addedVia)
    {
        if (FloorRules.HostProblem(host) is { } problem) throw new PolicyException([$"network.allowHosts: {problem}"]);
        var current = TeamHosts(teamId).Select(t => t.Host).ToList();
        if (current.Contains(host, StringComparer.Ordinal)) return false;
        var global = GetGlobal();
        var errors = new List<string>();
        foreach (var (memberId, layout) in members)
        {
            var m = GetMember(memberId);
            var merged = PolicyMerger.Merge(global, m.Override, m.Loosen, layout, canonicalize, null, [.. current, host]);
            errors.AddRange(merged.Violations);
            if (merged.Violations.Count == 0 && dryRun is not null)
                errors.AddRange(dryRun(global, m.Override, m.Loosen with { Network = new NetworkPatch([.. m.Loosen.Network?.AllowHosts ?? [], .. current, host]) }, layout));
        }
        if (errors.Count > 0) throw new PolicyException(errors.Distinct().ToList());
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO team_network_allows (team_id, host, added_by, added_via, added_at) VALUES ($t, $h, $by, $via, $at) ON CONFLICT (team_id, host) DO NOTHING;";
        cmd.Parameters.AddWithValue("$t", teamId);
        cmd.Parameters.AddWithValue("$h", host);
        cmd.Parameters.AddWithValue("$by", addedBy);
        cmd.Parameters.AddWithValue("$via", (object?)addedVia ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", Now());
        return cmd.ExecuteNonQuery() > 0;
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
        var c = new SqliteConnection(connectionString);
        c.Open();
        return c;
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
}
