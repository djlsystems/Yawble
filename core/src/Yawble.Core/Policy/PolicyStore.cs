using Microsoft.Data.Sqlite;
using Yawble.Core.Sandboxing;

namespace Yawble.Core.Policy;

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

    /// <summary>The effective policy for a member's next run (changes never reach a running sandbox).</summary>
    public MergeResult Effective(long memberId, RunLayout layout, long? machineMemoryMb = null)
    {
        var m = GetMember(memberId);
        return PolicyMerger.Merge(GetGlobal(), m.Override, m.Loosen, layout, canonicalize, machineMemoryMb);
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
