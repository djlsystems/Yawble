using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// TEAM CONNECTIONS: the member every team has, running the built-in plugin of the same name
/// (<c>plugins/team-connections/</c>). It holds every connection assigned to its team with no binding -
/// the person's assignment (Team settings → Connections) is the consent - and answers a member's
/// <c>tell</c> with <c>list</c> (each connection's name, kind and health, never a value) and
/// <c>test &lt;name&gt;</c> (the connection's own Test connection, <see cref="Connections.TestAsync"/>).
///
/// <list type="bullet">
/// <item>Made by the platform: at a team's creation or clone, and at start for a team without one
/// (<see cref="TeamRegistry.EnsureTeamConnectionsAsync"/>). Never hired, removed, renamed, repointed
/// or set up by a person, a Manager or the Concierge (<see cref="BuiltInMemberException"/>), and no
/// other member may take its name.</item>
/// <item>Shown on the board, in status and in its Manager's roster only while its team has at least one
/// connection assigned (<see cref="TeamRegistry.Shows"/>); hidden, its runs and history are kept.</item>
/// </list>
/// </summary>
public static class TeamConnectionsMember
{
    /// <summary>The built-in plugin it runs.</summary>
    public const string PluginId = "team-connections";

    /// <summary>What it is called, exactly; reserved on every team.</summary>
    public const string Label = "Team Connections";

    /// <summary>Its identifier, derived from <see cref="Label"/> as any member's is.</summary>
    public static readonly string Name = ContainerId.DeriveName(Label)!;

    /// <summary>What it runs: <c>plugin:team-connections</c>.</summary>
    public static readonly string Agent = MemberRef.ForPlugin(PluginId);

    /// <summary>Whether <paramref name="id"/> is a team's Team Connections member.</summary>
    public static bool Is(ContainerId id) => string.Equals(id.Name, Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="label"/> is the reserved name. The label is FOLDED first: NFKD, so a
    /// fullwidth or accented Latin letter becomes its ASCII letter plus marks, then each single character
    /// in <see cref="LookAlikes"/> (a Cyrillic or Greek letter drawn like an ASCII one) becomes that
    /// letter. Then its ASCII letters, case-insensitively and ignoring everything else (spaces, hyphens,
    /// underscores, dots, digits, punctuation, marks, and any non-ASCII character the table does not map),
    /// must be exactly <c>teamconnections</c>. So <c>TEAM-CONNECTIONS</c>, <c>Team_Connections</c>,
    /// <c>Team - Connections</c>, <c>T\u0435am Connections</c> (Cyrillic ie) and <c>T\u00E9am Connections</c>
    /// are refused, though <see cref="ContainerId.DeriveName"/> keeps '-' and '_' and drops the rest and
    /// would give them another identifier; <c>Team Connection</c>, <c>Connections</c>, <c>Team</c> and a
    /// name in another script are not. An unmapped character is dropped, never a wildcard.
    /// </summary>
    public static bool IsReserved(string? label)
    {
        if (label is null) return false;
        var letters = new System.Text.StringBuilder();
        foreach (var c in label.Normalize(System.Text.NormalizationForm.FormKD))
        {
            var folded = LookAlikes.GetValueOrDefault(c, c);
            if (char.IsAsciiLetter(folded)) letters.Append(folded);
        }
        return string.Equals(letters.ToString(), ReservedLetters, StringComparison.OrdinalIgnoreCase);
    }

    private const string ReservedLetters = "teamconnections";

    /// <summary>
    /// Single Cyrillic and Greek characters drawn like an ASCII letter, both cases: a fixed table, not a
    /// confusables file. NFKD has already handled fullwidth and accented Latin.
    /// </summary>
    private static readonly IReadOnlyDictionary<char, char> LookAlikes = new Dictionary<char, char>
    {
        // Cyrillic: а е о с і ѕ ј р х у and А В Е К М Н О Р С Т Х І Ѕ Ј
        ['\u0430'] = 'a', ['\u0435'] = 'e', ['\u043E'] = 'o', ['\u0441'] = 'c', ['\u0456'] = 'i', ['\u0455'] = 's',
        ['\u0458'] = 'j', ['\u0440'] = 'p', ['\u0445'] = 'x', ['\u0443'] = 'y',
        ['\u0410'] = 'A', ['\u0412'] = 'B', ['\u0415'] = 'E', ['\u041A'] = 'K', ['\u041C'] = 'M', ['\u041D'] = 'H',
        ['\u041E'] = 'O', ['\u0420'] = 'P', ['\u0421'] = 'C', ['\u0422'] = 'T', ['\u0425'] = 'X', ['\u0406'] = 'I',
        ['\u0405'] = 'S', ['\u0408'] = 'J',
        // Greek: α ε ι ο ϲ κ ν and Α Β Ε Ι Κ Μ Ν Ο Ρ Τ Χ Ϲ
        ['\u03B1'] = 'a', ['\u03B5'] = 'e', ['\u03B9'] = 'i', ['\u03BF'] = 'o', ['\u03F2'] = 'c', ['\u03BA'] = 'k',
        ['\u03BD'] = 'v',
        ['\u0391'] = 'A', ['\u0392'] = 'B', ['\u0395'] = 'E', ['\u0399'] = 'I', ['\u039A'] = 'K', ['\u039C'] = 'M',
        ['\u039D'] = 'N', ['\u039F'] = 'O', ['\u03A1'] = 'P', ['\u03A4'] = 'T', ['\u03A7'] = 'X', ['\u03F9'] = 'C',
    };

    /// <summary>Whether <paramref name="agent"/> names the built-in plugin.</summary>
    public static bool Runs(string? agent) =>
        agent is not null && MemberRef.IsPlugin(agent.Trim(), out var id) && id == PluginId;

    public static string NameRefused =>
        $"'{Label}' is the platform's own member on every team, and no other member may take its name.";

    public static string ChangeRefused =>
        $"{Label} is the platform's own member: nobody hires, removes, renames, repoints or sets it up. "
        + "It holds every connection assigned to its team; assign or remove those in Team settings → Connections.";

    /// <summary>
    /// The teams with at least one connection assigned, read now: what <see cref="TeamRegistry.Shows"/>
    /// keeps. The store's team ids are the teams' stored ids.
    /// </summary>
    public static async Task<IReadOnlySet<string>> TeamsWithConnectionsAsync(IConnectionStore store, CancellationToken ct = default) =>
        (await store.AllAssignmentsAsync(ct)).SelectMany(g => g).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A change to the platform's own member (<see cref="TeamConnectionsMember"/>), or a hire of a built-in
/// plugin, or a member taking its name: refused 409, whoever asks.
/// </summary>
public sealed class BuiltInMemberException(string message) : InvalidOperationException(message);
