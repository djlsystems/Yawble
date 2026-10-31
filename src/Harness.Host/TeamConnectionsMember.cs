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
    /// Whether <paramref name="label"/> is the reserved name: its ASCII letters, case-insensitively and
    /// ignoring everything else (spaces, hyphens, underscores, dots, digits, punctuation), are exactly
    /// <c>teamconnections</c>. So <c>TEAM-CONNECTIONS</c>, <c>Team_Connections</c> and <c>Team - Connections</c>
    /// are refused, though <see cref="ContainerId.DeriveName"/> keeps '-' and '_' and would give them
    /// another identifier; <c>Team Connection</c>, <c>Connections</c> and <c>Team</c> are not.
    /// </summary>
    public static bool IsReserved(string? label) =>
        label is not null
        && string.Equals(new string(label.Where(char.IsAsciiLetter).ToArray()), ReservedLetters, StringComparison.OrdinalIgnoreCase);

    private const string ReservedLetters = "teamconnections";

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
