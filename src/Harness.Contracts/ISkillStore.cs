namespace Harness.Contracts;

/// <summary>
/// Where a skill comes from. A built-in is compiled into the host and indexed read-only on every
/// start; a custom skill is the person's, created and edited in the Skills dialog and stored only in
/// the database.
/// </summary>
public enum SkillKind
{
    BuiltIn,
    Custom,
}

/// <summary>Which kinds a listing returns. The Skills dialog shows Custom by default.</summary>
public enum SkillKindFilter
{
    Custom,
    BuiltIn,
    All,
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
public sealed record Skill(
    long Id,
    string Name,
    string Description,
    IReadOnlyList<string> Roles,
    SkillKind Kind,
    string Body,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy);

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
    /// One page, newest row first: rows with an id below <paramref name="before"/> (all when null),
    /// at most <paramref name="take"/>. <paramref name="query"/> narrows by full-text search over
    /// name, description and body; <paramref name="role"/> narrows to what that role is offered.
    /// </summary>
    Task<IReadOnlyList<Skill>> ListAsync(
        SkillKindFilter kind,
        string? query,
        string? role,
        long? before,
        int take,
        CancellationToken ct = default);

    Task<Skill?> GetAsync(string name, CancellationToken ct = default);

    Task<Skill> CreateCustomAsync(SkillDraft draft, string? by, CancellationToken ct = default);

    /// <summary>Rewrites custom skill <paramref name="name"/>; <paramref name="draft"/>'s name may
    /// differ, which renames it. Null when there is no custom skill by that name.</summary>
    Task<Skill?> UpdateCustomAsync(string name, SkillDraft draft, string? by, CancellationToken ct = default);

    Task<bool> DeleteCustomAsync(string name, CancellationToken ct = default);
}
