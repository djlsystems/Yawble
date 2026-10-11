namespace Harness.Contracts;

/// <summary>
/// Where a skill comes from. A built-in is compiled into the host and indexed read-only on every
/// start; a custom skill is the person's, created and edited in the Skills dialog and stored only in
/// the database; a plugin skill ships with an installed plugin, is indexed read-only whenever the
/// plugins are loaded or rescanned, and is named <c>plugin-&lt;id&gt;</c> or
/// <c>plugin-&lt;id&gt;-&lt;name&gt;</c>.
/// </summary>
public enum SkillKind
{
    BuiltIn,
    Custom,
    Plugin,
}

/// <summary>Which kinds a listing returns. The Skills dialog shows Custom by default.</summary>
public enum SkillKindFilter
{
    Custom,
    BuiltIn,
    All,
    Plugin,
}

/// <summary>
/// Who a skill is offered to. Every skill declares at least one; `any` offers it to every role.
/// The role a caller has comes from its credential, never from what it asks for.
/// </summary>
public static class SkillRoles
{
    public const string Concierge = "concierge";
    public const string Manager = "manager";
    public const string Member = "member";
    public const string Any = "any";

    public static IReadOnlyList<string> All { get; } = [Concierge, Manager, Member, Any];

    /// <summary>
    /// The roles as stored: lowercased, known, de-duplicated, in the order of <see cref="All"/>.
    /// Throws with a sentence naming the offender, or when there are none.
    /// </summary>
    public static IReadOnlyList<string> Normalise(IEnumerable<string>? roles)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in roles ?? [])
        {
            var role = (raw ?? "").Trim().ToLowerInvariant();
            if (role.Length == 0) continue;

            if (!All.Contains(role))
            {
                throw new ArgumentException(
                    $"'{raw}' is not a role. A skill's roles are any of: {string.Join(", ", All)}.");
            }

            wanted.Add(role);
        }

        if (wanted.Count == 0)
        {
            throw new ArgumentException(
                $"A skill needs at least one role: any of {string.Join(", ", All)}.");
        }

        return [.. All.Where(wanted.Contains)];
    }

    /// <summary>Whether a skill carrying <paramref name="roles"/> is offered to <paramref name="role"/>.</summary>
    public static bool Offers(IReadOnlyCollection<string> roles, string role) =>
        roles.Contains(Any) || roles.Contains(role);
}

/// <summary>One skill as the index holds it.</summary>
/// <param name="Id">The index row, and the cursor a page is read by. A built-in's id is reassigned
/// on every start, because its row is rebuilt from the build.</param>
/// <param name="UpdatedBy">Who last wrote a custom skill; null for a built-in, which only a code
/// change writes.</param>
/// <param name="Source">The id of the plugin that ships a plugin skill; null for every other kind.</param>
/// <param name="Team">The stored id of the team a TEAM SKILL belongs to; null for every instance-wide
/// skill. A team skill is always custom, and is offered only to that team's members of its roles.</param>
public sealed record Skill(
    long Id,
    string Name,
    string Description,
    IReadOnlyList<string> Roles,
    SkillKind Kind,
    string Body,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy,
    string? Source = null,
    string? Team = null)
{
    /// <summary>
    /// Whether a person may edit, rename or delete it: only a custom skill. A built-in changes with
    /// the product and a plugin skill with its plugin, and neither name may be taken by a custom one.
    /// </summary>
    public bool IsLocked => Kind != SkillKind.Custom;
}

/// <summary>The skill names a plugin may ship, and nothing else may take.</summary>
public static class PluginSkillNames
{
    public const string Prefix = "plugin-";

    /// <summary>The plugin's own skill: <c>plugin-&lt;id&gt;</c>.</summary>
    public static string Main(string pluginId) => Prefix + pluginId;

    /// <summary>
    /// The name a plugin skill is stored under, whatever its file says: <c>plugin-&lt;id&gt;</c>
    /// when the file names the plugin itself (<c>&lt;id&gt;</c> or <c>plugin-&lt;id&gt;</c>), and
    /// <c>plugin-&lt;id&gt;-&lt;name&gt;</c> otherwise - so a plugin cannot shadow a built-in or
    /// another plugin's main skill by what it writes.
    /// </summary>
    public static string Forced(string pluginId, string declared)
    {
        var name = declared.Trim().ToLowerInvariant();
        var main = Main(pluginId);

        if (name == pluginId || name == main) return main;
        if (name.StartsWith(main + "-", StringComparison.Ordinal)) name = name[(main.Length + 1)..];

        return $"{main}-{name}";
    }

    /// <summary>The sentence for a person trying to change a plugin's skill.</summary>
    public static string Locked(string name, string verb) =>
        $"'{name}' is a plugin's skill. It changes only with its plugin, so it cannot be {verb}.";

    /// <summary>Whether <paramref name="name"/> is in the namespace reserved for plugin skills.</summary>
    public static bool IsReserved(string name) => name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="name"/> is one plugin <paramref name="pluginId"/> may ship.</summary>
    public static bool Belongs(string name, string pluginId) =>
        name == Main(pluginId) || name.StartsWith(Main(pluginId) + "-", StringComparison.Ordinal);
}

/// <summary>What a person writes for a custom skill.</summary>
public sealed record SkillDraft(
    string Name,
    string Description,
    IReadOnlyList<string> Roles,
    string Body);

/// <summary>A write refused because of what already exists: a built-in, or a name taken.</summary>
public sealed class SkillRefusedException(string message) : Exception(message);

public interface ISkillStore
{
    /// <summary>
    /// Replaces every built-in row with <paramref name="builtIns"/>, in one transaction. Called on
    /// every start: the build is the only source of a built-in. A custom skill holding a built-in's
    /// name is moved out of the way and reported, so the built-in always wins.
    /// </summary>
    Task<IReadOnlyList<string>> ReplaceBuiltInsAsync(
        IReadOnlyList<SkillDraft> builtIns, DateTimeOffset builtAt, CancellationToken ct = default);

    /// <summary>
    /// Replaces every skill plugin <paramref name="source"/> ships with <paramref name="drafts"/>,
    /// in one transaction - deleted and inserted per source, as <see cref="ReplaceBuiltInsAsync"/>
    /// does for the build. Empty removes them. Each name must be one the plugin may ship
    /// (<see cref="PluginSkillNames.Belongs"/>). A custom skill holding such a name is moved out of
    /// the way, as for a built-in; a name another plugin already holds is not taken. Returns one
    /// sentence per skill moved or not taken.
    /// </summary>
    Task<IReadOnlyList<string>> ReplacePluginSkillsAsync(
        string source, IReadOnlyList<SkillDraft> drafts, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>Every plugin id that has skills in the index.</summary>
    Task<IReadOnlyList<string>> PluginSourcesAsync(CancellationToken ct = default);

    /// <summary>
    /// One page, newest row first: rows with an id below <paramref name="before"/> (all when null),
    /// at most <paramref name="take"/>. <paramref name="query"/> narrows by full-text search over
    /// name, description and body; <paramref name="role"/> narrows to what that role is offered
    /// INSTANCE-WIDE, leaving out every team skill (<see cref="ListOfferedAsync"/> adds a team's).
    /// With no role, team skills are listed beside the rest, each carrying its team.
    /// </summary>
    Task<IReadOnlyList<Skill>> ListAsync(
        SkillKindFilter kind,
        string? query,
        string? role,
        long? before,
        int take,
        CancellationToken ct = default);

    /// <summary>
    /// What a member of <paramref name="team"/> in <paramref name="role"/> is offered: the
    /// instance-wide skills of that role, and that team's own skills of that role. With no team (the
    /// Concierge) no team skill is offered. <paramref name="query"/> narrows as in <see cref="ListAsync"/>.
    /// </summary>
    Task<IReadOnlyList<Skill>> ListOfferedAsync(
        string role, string? team, string? query, int take, CancellationToken ct = default);

    /// <summary>The instance-wide skill named <paramref name="name"/>; never a team skill.</summary>
    Task<Skill?> GetAsync(string name, CancellationToken ct = default);

    /// <summary>Every skill named <paramref name="name"/>: the instance-wide one, or the team skills
    /// of that name (one per team at most). Empty when there is none.</summary>
    Task<IReadOnlyList<Skill>> FindAllAsync(string name, CancellationToken ct = default);

    /// <summary>Team <paramref name="team"/>'s own skills, by name.</summary>
    Task<IReadOnlyList<Skill>> ListTeamAsync(string team, CancellationToken ct = default);

    /// <summary>
    /// Creates team <paramref name="team"/>'s skill, or - with <paramref name="replace"/> - rewrites
    /// the one of that name it already has; <paramref name="audit"/> is appended to
    /// <c>tenant_events</c> in the same transaction, so the skill and its record land together or
    /// not at all. Refused (<see cref="SkillRefusedException"/>) when the name is an instance-wide
    /// skill's (a built-in's, a custom one's), begins <c>plugin-</c>, or - without replace - is
    /// already this team's. The caller names the team by its STORED id.
    /// </summary>
    Task<Skill> PutTeamSkillAsync(
        string team, SkillDraft draft, bool replace, string? by, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>
    /// Rewrites team <paramref name="team"/>'s skill <paramref name="name"/>; a different name in
    /// <paramref name="draft"/> renames it. Null when the team has no skill by that name.
    /// </summary>
    Task<Skill?> UpdateTeamSkillAsync(
        string team, string name, SkillDraft draft, string? by, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>Deletes team <paramref name="team"/>'s skill <paramref name="name"/>, with its tenant row.
    /// False when the team has no skill by that name.</summary>
    Task<bool> DeleteTeamSkillAsync(string team, string name, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>
    /// A team deletion's step: every skill of <paramref name="team"/>, one tenant row per skill
    /// (<paramref name="audit"/> given its name), in one transaction. Returns the names removed.
    /// </summary>
    Task<IReadOnlyList<string>> DeleteTeamSkillsAsync(
        string team, Func<string, TriggerAudit> audit, CancellationToken ct = default);

    Task<Skill> CreateCustomAsync(SkillDraft draft, string? by, CancellationToken ct = default);

    /// <summary>Rewrites custom skill <paramref name="name"/>; <paramref name="draft"/>'s name may
    /// differ, which renames it. Null when there is no custom skill by that name.</summary>
    Task<Skill?> UpdateCustomAsync(string name, SkillDraft draft, string? by, CancellationToken ct = default);

    Task<bool> DeleteCustomAsync(string name, CancellationToken ct = default);
}
