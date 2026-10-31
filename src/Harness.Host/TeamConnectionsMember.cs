using System.Globalization;
using System.Text;
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
    /// Whether <paramref name="label"/> is the reserved name: its confusables SKELETON equals that of
    /// <c>teamconnections</c>. The skeleton (UTS #39, with <see cref="Confusables"/>): NFKD; drop the
    /// combining marks; case-fold; map each character through the table (looked up as written and,
    /// when it has no entry, case-folded, so a capital look-alike such as Greek Nu counts as its own
    /// letter); then keep only letters. So <c>TEAM-CONNECTIONS</c>, <c>Team_Connections</c>,
    /// <c>Team - Connections</c>, <c>Team Connections 2</c>, <c>T\u0435am Connections</c> (Cyrillic ie),
    /// <c>Team C\u0585nnections</c> (Armenian oh), <c>Team Connect\u0131ons</c> (dotless i) and
    /// <c>T\u00E9am Connections</c> are refused, though <see cref="ContainerId.DeriveName"/> keeps '-'
    /// and '_' and would give them another identifier; <c>Team Connection</c>, <c>Connections</c>,
    /// <c>Team</c> and a name in another script are not. A letter the table does not map stays itself.
    /// </summary>
    public static bool IsReserved(string? label) =>
        label is not null && Skeleton(label) == ReservedSkeleton;

    private static string Skeleton(string label)
    {
        var skeleton = new StringBuilder();
        foreach (var rune in label.Normalize(NormalizationForm.FormKD).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark) continue;
            var folded = Rune.ToLowerInvariant(rune);
            var mapped = Confusables.GetValueOrDefault(rune.Value) ?? Confusables.GetValueOrDefault(folded.Value) ?? folded.ToString();
            foreach (var letter in mapped.EnumerateRunes())
                if (Rune.IsLetter(letter)) skeleton.Append(Rune.ToLowerInvariant(letter).ToString());
        }
        return skeleton.ToString();
    }

    /// <summary>The embedded copy of <c>Confusables/team-connections-confusables.txt</c>.</summary>
    public const string ConfusablesResource = "Harness.Host.team-connections-confusables.txt";

    /// <summary>
    /// Unicode's confusables (UTS #39 <c>confusables.txt</c>) whose target is made of the letters of
    /// <c>teamconnections</c>: source code point to target text. Generated from the Unicode file by
    /// <c>Confusables/generate.py</c> and checked in with its Unicode version; read from this assembly
    /// only, never downloaded.
    /// </summary>
    public static IReadOnlyDictionary<int, string> Confusables { get; } = LoadConfusables();

    // After Confusables: static initializers run in the order they are written.
    private static readonly string ReservedSkeleton = Skeleton("teamconnections");

    /// <summary>The <c>source ; target ; type # comment</c> lines of a confusables file; a <c>#</c> line a comment.</summary>
    public static IReadOnlyDictionary<int, string> ParseConfusables(string text)
    {
        var table = new Dictionary<int, string>();
        foreach (var line in text.Split('\n'))
        {
            var data = line.Split('#', 2)[0];
            if (string.IsNullOrWhiteSpace(data)) continue;
            var fields = data.Split(';');
            var target = string.Concat(fields[1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(hex => char.ConvertFromUtf32(Convert.ToInt32(hex, 16))));
            table[Convert.ToInt32(fields[0].Trim(), 16)] = target;
        }
        return table;
    }

    private static IReadOnlyDictionary<int, string> LoadConfusables()
    {
        using var stream = typeof(TeamConnectionsMember).Assembly.GetManifestResourceStream(ConfusablesResource)
            ?? throw new InvalidOperationException($"{ConfusablesResource} is not embedded in the Host.");
        using var reader = new StreamReader(stream);
        return ParseConfusables(reader.ReadToEnd());
    }

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
