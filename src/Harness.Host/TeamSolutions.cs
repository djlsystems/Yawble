namespace Harness.Host;

/// <summary>
/// The solution package a team was installed from: its id, name and version, and the ids of the
/// plugins the package installed. Rides <see cref="TeamSummary.Solution"/>, so the team delete dialog
/// can say those plugins stay installed (deleting a team never removes a plugin) and where to remove
/// them. Nothing here is a secret or a path.
/// </summary>
public sealed record TeamSolution(string Id, string Name, string Version, IReadOnlyList<string> Plugins);

/// <summary>
/// WHICH PACKAGE A TEAM CAME FROM, for <see cref="TeamRegistry"/> to put on each team's summary. The
/// install wizard's <c>team_solutions</c> record implements this and replaces
/// <see cref="NoTeamSolutions"/> in <c>Program.cs</c>. Read on every summary, so an implementation
/// answers from memory - no database round trip per call - and null for a team made by hand.
/// </summary>
public interface ITeamSolutions
{
    TeamSolution? For(string team);
}

/// <summary>No team came from a package: the answer until <c>team_solutions</c> exists.</summary>
public sealed class NoTeamSolutions : ITeamSolutions
{
    public static readonly NoTeamSolutions Instance = new();

    public TeamSolution? For(string team) => null;
}
