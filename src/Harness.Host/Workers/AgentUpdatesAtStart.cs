using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// THE START'S CLI UPDATE, IN CONTROL, THROUGH THE ONE UPDATE GATE. A worker's start installs only the
/// CLIs that are missing: replacing one in place while another worker runs members from the shared
/// install would leave a member launched in that moment with no program. So the update a start used to
/// make is asked of control's gate instead, when the first worker joins after control started - once
/// per command, as a person's request from the Agents screen is: it waits for runs, holds launches,
/// and runs once on one worker. <c>HARNESS_UPDATE_AGENTS=0</c> turns it off, as it does the start's.
/// </summary>
public static class AgentUpdatesAtStart
{
    /// <summary>What control logs when it asks.</summary>
    public const string AskedText = "A worker joined after control started: each agent CLI's update is asked of the update gate once.";

    /// <summary>
    /// Wires the ask to the first join <paramref name="onJoined"/> reports, when <paramref name="control"/>
    /// and updates are on; returns whether it did. <paramref name="agents"/> names one preset per command
    /// to update, and <paramref name="request"/> is the gate's request for one.
    /// </summary>
    public static bool Wire(
        bool control, string? updateAgents, Action<Action<Contracts.WorkerId>> onJoined, Func<IReadOnlyList<string>> agents,
        Action<string> request, ILogger? log = null)
    {
        if (!control || updateAgents?.Trim() == "0") return false;

        var asked = 0;
        onJoined(worker =>
        {
            if (Interlocked.Exchange(ref asked, 1) != 0) return;

            log?.LogInformation(AskedText);
            foreach (var agent in agents())
            {
                try
                {
                    request(agent);
                }
                catch (Exception exception)
                {
                    log?.LogWarning("Asking the update of {Agent} after worker {Worker} joined failed: {Message}", agent, worker, exception.Message);
                }
            }
        });

        return true;
    }

    /// <summary>One preset per launched command, the first the catalog lists: an update is per command, not per preset.</summary>
    public static IReadOnlyList<string> OnePerCommand(AgentCatalog catalog) =>
        [.. catalog.Definitions
            .Where(d => d.Launch is not null)
            .GroupBy(d => d.Launch!.FileName, StringComparer.Ordinal)
            .Select(g => g.First().Name)];
}
