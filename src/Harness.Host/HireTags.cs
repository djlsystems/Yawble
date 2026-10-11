namespace Harness.Host;

/// <summary>
/// The work tags a hire asks for, and what the caller is told when none of the team's allowed
/// agents carries the one it asked for.
///
/// The hire falls back to the first allowed agent, and the fallback is said in the hire's REPLY
/// (<see cref="Substituted"/>), not only in the <c>X-Harness-Hiring-Notice</c> header, which the
/// <c>member</c> tool never shows - otherwise a team with no developer-tagged agent would quietly
/// hire a researcher and the Manager would believe it got what it asked for.
/// </summary>
public static class HireTags
{
    /// <summary>The tags the Manager hires with - the <c>member</c> tool's <c>for</c>.</summary>
    public static readonly IReadOnlyList<string> Roles = ["developer", "tester", "researcher"];

    /// <summary>The sentence the hire's reply carries when its tag was not matched.</summary>
    public static string Substituted(string tag, string agent) =>
        $"no agent on this team is tagged {tag}; hired {agent} instead";

    /// <summary>The roles in <see cref="Roles"/> that no tag list here carries, in role order.</summary>
    public static IReadOnlyList<string> Uncovered(IEnumerable<IEnumerable<string>> tagsOfAllowedAgents)
    {
        var covered = new HashSet<string>(
            tagsOfAllowedAgents.SelectMany(tags => tags), StringComparer.OrdinalIgnoreCase);

        return Roles.Where(role => !covered.Contains(role)).ToArray();
    }
}
