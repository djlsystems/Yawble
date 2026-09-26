using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// Which Agent presets a team actually REFERENCES - the scope the ribbon badge counts, and the one
/// thing that keeps the badge worth reading.
///
/// TWO SURFACES, TWO SCOPES, and they are different on purpose. The Agents screen lists the state
/// for EVERY preset: a seeded `codex` that this tenant never uses, reading "not found on this
/// machine", is information rather than a problem. The badge counts only what is IN USE. A tenant
/// with eight seeded presets and two CLIs installed would otherwise carry a permanent badge of six,
/// and a badge that is always lit is wallpaper - it stops being read, and the one time it matters
/// nobody notices. Warn about what is in use, list what exists.
///
/// THREE ROUTES, and all three count. `team_members.agent` is what an existing member runs;
/// `teams.member_agents` is the ordered allowlist a team's NEXT hire comes from; the tenant's
/// `tenant_interactive_agent_settings.interactive_agent` is what every Concierge launches. Any one of them is a preset this tenant will try to run.
/// Checking only the first would let `PUT /api/agents` defend the hiring PROMPT and not the hiring
/// AGENT, the two halves of one dialog section: removing the Agent would cost nothing visible and
/// refuse the team's next hire.
///
/// The scalar `teams.member_agent` is deliberately NOT read - current code reads
/// <see cref="PersistedTeam.MemberAgents"/>, so counting the scalar here would reintroduce a column
/// the rest of the product does not consult.
/// </summary>
public static class AgentReferences
{
    /// <summary>
    /// Every preset name any team references, compared the way a stored one is looked up.
    ///
    /// ORDINAL-IGNORE-CASE, matching `AgentCatalog.Definition`, `team_members.agent`'s own reader
    /// and the `COLLATE NOCASE` the rename route uses. A case-sensitive set here would silently
    /// leave a team that spells its Agent `Claude-Headless` out of the count, which presents as a
    /// badge that is missing rather than as anything failing.
    ///
    /// A blank entry is dropped rather than added: `member_agents` is nullable and a row repaired
    /// by hand can hold an empty string, and an empty name matches no preset - so keeping it would
    /// only ever be a set member nothing can join to.
    /// </summary>
    public static IReadOnlySet<string> Of(
        IEnumerable<PersistedTeam> teams, IEnumerable<PersistedMember> members, string? concierge)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Add(referenced, concierge);

        foreach (var member in members)
        {
            Add(referenced, member.Agent);
        }

        foreach (var team in teams)
        {
            foreach (var agent in team.MemberAgents ?? [])
            {
                Add(referenced, agent);
            }

        }

        return referenced;
    }

    /// <summary>Reads all three routes off the store in one place, so a caller cannot ask two of
    /// the three and look correct.</summary>
    public static async Task<IReadOnlySet<string>> OfAsync(ITeamStore teams, CancellationToken ct) =>
        Of(
            await teams.TeamsAsync(ct),
            await teams.MembersAsync(ct),
            (await teams.ConciergeSettingsAsync(ct)).Agent);

    private static void Add(HashSet<string> into, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name)) into.Add(name.Trim());
    }
}
