namespace Harness.Host;

/// <summary>
/// THE RUN-STEP ROWS (<c>run_steps</c>) the Monitor replays: what <see cref="AgentStepFollow"/> read
/// from every agent run's transcript. A consumer depends on this, not on the SQLite class, so a
/// provider is a different implementation and never a different caller. Kind, tool name and time only;
/// see <see cref="AgentStepStore"/>.
/// </summary>
public interface IAgentStepStore
{
    /// <summary>Writes the steps with no row yet and sets the count of those that have one, in one
    /// transaction; each new step is given its <see cref="NewAgentStep.Id"/>.</summary>
    Task WriteAsync(
        string team, string member, long delivery, long? workflow,
        IReadOnlyCollection<NewAgentStep> added, IReadOnlyCollection<NewAgentStep> counted, CancellationToken ct = default);

    /// <summary>The rows after <paramref name="after"/> within <paramref name="from"/> ..
    /// <paramref name="to"/>, oldest first, at most <paramref name="take"/>, scoped by each team's
    /// creation in <paramref name="since"/>.</summary>
    Task<IReadOnlyList<AgentStep>> ReadAsync(
        DateTimeOffset from, DateTimeOffset to, long after, int take,
        IReadOnlyDictionary<string, DateTimeOffset> since, CancellationToken ct = default);

    /// <summary>Removes every row of <paramref name="team"/>; answers how many went.</summary>
    Task<int> DeleteForTeamAsync(string team, CancellationToken ct = default);

    /// <summary>Removes the rows of these members of <paramref name="team"/>; answers how many went.</summary>
    Task<int> DeleteForMembersAsync(string team, IReadOnlyCollection<string> members, CancellationToken ct = default);
}
