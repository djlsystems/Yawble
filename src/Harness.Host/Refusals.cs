namespace Harness.Host;

/// <summary>The refusal every route answers for a team or a member that is not there.</summary>
public static class Refusals
{
    public static IResult NoTeam(string team) => Results.NotFound(new { error = $"No team '{team}'." });

    public static IResult NoMember(string name) => Results.NotFound(new { error = $"No member '{name}'." });
}
