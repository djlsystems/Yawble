using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Harness.Contracts;

namespace Harness.Skills;

/// <summary>
/// WHAT EVERY SKILL STORE DECIDES THE SAME WAY, whichever database holds the index: a legal name, a
/// draft made clean, the reserved plugin namespace, the free name a displaced skill moves to, the words
/// a search looks for, how a row reads, and every refusal's sentence. One copy, so the SQLite store and
/// the SQL Server one refuse the same writes with the same words.
/// </summary>
public static partial class SkillRules
{
    /// <summary>The longest legal name, which <see cref="FreeNames"/> keeps a moved name within.</summary>
    public const int MaxName = 48;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,47}$")]
    private static partial Regex LegalName();

    /// <summary>The stored spellings of the three kinds.</summary>
    public const string BuiltIn = "builtin";
    public const string Custom = "custom";
    public const string Plugin = "plugin";

    public static bool IsLegalName(string value) => LegalName().IsMatch(value);

    public static void RequireLegalName(string value)
    {
        if (!IsLegalName(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a legal skill name. Use lowercase kebab-case (letters, digits, '-') and 48 characters or fewer.");
        }
    }

    /// <summary>The kind <paramref name="kind"/> lists, as stored; null for every kind.</summary>
    public static string? KindOf(SkillKindFilter kind) => kind switch
    {
        SkillKindFilter.All => null,
        SkillKindFilter.BuiltIn => BuiltIn,
        SkillKindFilter.Plugin => Plugin,
        _ => Custom,
    };

    /// <summary>A page is 1 to 200 rows.</summary>
    public static int Take(int take) => Math.Clamp(take, 1, 200);

    /// <summary>The draft trimmed and checked: a legal name, a one-line description, a body, known roles.</summary>
    public static SkillDraft Validate(SkillDraft draft)
    {
        var name = (draft.Name ?? "").Trim();
        RequireLegalName(name);

        var description = (draft.Description ?? "").Trim();
        if (description.Length == 0)
        {
            throw new ArgumentException("A skill needs a one-line description.");
        }

        if (description.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("A skill's description is one line.");
        }

        var body = draft.Body ?? "";
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("A skill needs a body.");
        }

        return new SkillDraft(name, description, SkillRoles.Normalise(draft.Roles), body);
    }

    /// <summary>A custom skill may not take a name in the plugins' namespace, installed or not.</summary>
    public static void RefuseReserved(string name)
    {
        if (PluginSkillNames.IsReserved(name))
        {
            throw new SkillRefusedException(
                $"'{name}' begins '{PluginSkillNames.Prefix}', which is reserved for the skills installed plugins ship. "
                + "A custom skill needs a name of its own.");
        }
    }

    /// <summary>A plugin may ship only its own namespace's names.</summary>
    public static void RequireBelongs(string name, string source)
    {
        if (!PluginSkillNames.Belongs(name, source))
        {
            throw new ArgumentException(
                $"'{name}' is not a name plugin '{source}' may ship: it must be {PluginSkillNames.Main(source)} or begin {PluginSkillNames.Main(source)}-.");
        }
    }

    /// <summary>
    /// What an instance-wide custom skill's name may not be, by the kind of the instance-wide row
    /// already holding it (null: nothing holds it, so nothing is refused).
    /// </summary>
    public static SkillRefusedException? CustomNameRefusal(string? holder, string name) => holder switch
    {
        BuiltIn => new SkillRefusedException($"'{name}' is a built-in skill's name. A custom skill needs a name of its own."),
        Custom => new SkillRefusedException($"A custom skill named '{name}' already exists."),
        _ => null,
    };

    /// <summary>What a team skill's name may not be, by the kind of the instance-wide row holding it.</summary>
    public static SkillRefusedException? TeamNameRefusal(string? holder, string name) => holder switch
    {
        BuiltIn => new SkillRefusedException($"'{name}' is a built-in skill's name. A team skill needs a name of its own."),
        Custom => new SkillRefusedException($"'{name}' is an instance-wide custom skill's name. A team skill needs a name of its own."),
        Plugin => new SkillRefusedException($"'{name}' is a plugin's skill name. A team skill needs a name of its own."),
        _ => null,
    };

    /// <summary>A custom skill a person cannot change, by its kind; null for a custom one.</summary>
    public static SkillRefusedException? LockedRefusal(string kind, string name, string pluginVerb, string builtInWhat) => kind switch
    {
        BuiltIn => new SkillRefusedException(
            $"'{name}' is a built-in skill. Built-in skills change only with the product, so it cannot be {builtInWhat}."),
        Plugin => new SkillRefusedException(PluginSkillNames.Locked(name, pluginVerb)),
        _ => null,
    };

    /// <summary>A team already holding the name.</summary>
    public static SkillRefusedException TeamHolds(string team, string name) =>
        new($"Team {team} already has a skill named '{name}'.");

    /// <summary>An instance-wide name a team's skill already holds.</summary>
    public static SkillRefusedException TeamHoldsInstanceName(string team, string name) =>
        new($"'{name}' is team {team}'s skill name. An instance-wide skill needs a name no team's skill holds.");

    /// <summary>A rebuilt built-in that moved a skill out of its way.</summary>
    public static string Moved(string name, string free, string? team = null) =>
        team is null ? $"{name} -> {free}" : $"{name} -> {free} (team {team})";

    /// <summary>A custom skill the plugin's namespace took back.</summary>
    public static string PluginRelabelled(string source, string name, string free) =>
        $"a custom skill held plugin '{source}''s skill name and was relabelled: {name} -> {free}.";

    /// <summary>A plugin's skill another skill already holds the name of.</summary>
    public static string PluginNotIndexed(string source, string name) =>
        $"plugin '{source}''s skill {name} was not indexed: another skill already holds that name.";

    /// <summary>
    /// The names a skill displaced from <paramref name="preferred"/> may move to, in the order they are
    /// tried: the name itself within <see cref="MaxName"/>, then with <c>-2</c>, <c>-3</c>... The caller
    /// takes the first no row holds.
    /// </summary>
    public static IEnumerable<string> FreeNames(string preferred)
    {
        yield return preferred.Length > MaxName ? preferred[..MaxName] : preferred;

        for (var suffix = 2; ; suffix++)
        {
            var tail = $"-{suffix}";
            yield return (preferred.Length + tail.Length > MaxName ? preferred[..(MaxName - tail.Length)] : preferred) + tail;
        }
    }

    /// <summary>
    /// THE WORDS A SEARCH LOOKS FOR: the query split on white space. Any one of them found is a match.
    /// Empty for a query of white space alone, which the stores read as no search at all.
    /// </summary>
    public static IReadOnlyList<string> SearchWords(string query) =>
        query.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The roles as stored: the role names, space-separated.</summary>
    public static string StoredRoles(IReadOnlyList<string> roles) => string.Join(' ', roles);

    /// <summary>Every column <see cref="Read"/> reads, in its order, through the alias <c>s</c>.</summary>
    public const string Columns =
        "s.id, s.name, s.description, s.roles, s.kind, s.body, s.updated_at, s.updated_by, s.source, s.team";

    public static Skill Read(DbDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3).Split(' ', StringSplitOptions.RemoveEmptyEntries),
            reader.GetString(4) switch
            {
                BuiltIn => SkillKind.BuiltIn,
                Plugin => SkillKind.Plugin,
                _ => SkillKind.Custom,
            },
            reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
}
