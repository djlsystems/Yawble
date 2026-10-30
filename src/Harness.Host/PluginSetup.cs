using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WHETHER A PLUGIN MEMBER WAITS FOR A PERSON, on its card: <see cref="ContainerSnapshot.NeedsPerson"/>
/// read from its settings by <see cref="PluginReadyNotice.Missing"/> - a required connection slot with
/// nothing bound, a required secret with no value - and set on the member's runtime, which shows it as
/// Needs you where its status shows. Read at restore, after a hire, after every change a person makes
/// to the member's settings or values, and after a connection is taken from it (removed from its team,
/// or deleted). Best effort: a read that fails leaves the mark as it was, and the run still refuses
/// <c>needs-person</c> on its own.
///
/// NOTHING MISSING CLEARS THE WAIT AT ONCE, not at the next run: a run that already failed
/// <c>needs-person</c>, and the block the Host wrote when a connection was taken from the member, are
/// cleared with the mark (<see cref="MemberRuntime.ClearNeedsPersonMarks"/>). That block, written while
/// the member was idle, opened a workflow of its own; it is closed here, or the team's tab would go on
/// reading it as blocked. The restart's read clears both again from the log's classes.
/// </summary>
public sealed class PluginSetup(
    ContainerHost host, PluginCatalog plugins, IPluginMemberSettingsStore settings, ISecretStore secrets,
    PluginMemberSecretStore memberSecrets, IMessageLog? log = null)
{
    /// <summary>The words a member's mark carries for <paramref name="missing"/>, or null for none.</summary>
    public static string? Words(IReadOnlyList<string> missing) =>
        missing.Count == 0 ? null : "Waits for a person to " + string.Join("; and to ", missing) + ".";

    /// <summary>Reads one member's settings and sets or clears its mark. Answers what is missing.</summary>
    public async Task<IReadOnlyList<string>> RefreshAsync(ContainerId member, CancellationToken ct = default)
    {
        if (host.Find(member) is not { } runtime) return [];

        var agent = runtime.Snapshot().Agent;
        if (!MemberRef.IsPlugin(agent, out var pluginId) || plugins.For(pluginId) is not { } plugin) return [];

        try
        {
            var bound = await settings.ForAsync(member, ct);
            var valued = (await memberSecrets.StatesAsync(member, ct)).Keys.ToHashSet(StringComparer.Ordinal);
            var missing = PluginReadyNotice.Missing(plugin.Manifest, bound, secrets, valued);

            runtime.MarkNeedsPerson(Words(missing));
            if (missing.Count == 0)
            {
                runtime.ClearNeedsPersonMarks();
                await CloseWaitsAsync(member, ct);
            }

            return missing;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// Closes each still-open workflow a Host-written needs-person block of this member opened, saying
    /// a person set what it waited for - whether or not the member has run since. Its own roots only: a
    /// block written inside another workflow is that workflow's, and is never closed from here.
    /// </summary>
    private async Task CloseWaitsAsync(ContainerId member, CancellationToken ct)
    {
        if (log is null) return;

        var waits = (await log.ReadMemberBeforeAsync(member, 0, long.MaxValue, [MessageTypes.Blocked], RecentBlocks, ct))
            .Where(row => row.Seq == row.CorrelationId && new ContainerMarks(row, null, null).BlockedClass == FailureClasses.NeedsPerson)
            .Select(row => row.CorrelationId)
            .ToList();
        if (waits.Count == 0) return;

        foreach (var correlation in (await log.OpenWorkflowsAmongAsync(waits, ct)).Order())
        {
            await log.AppendAsync(
                new NewMessage(
                    MessageTypes.WorkflowClosed,
                    JsonSerializer.Serialize(new { team = member.Team, reason = $"A person set what {member.Name} was waiting for." }),
                    "host",
                    correlation),
                ct);
        }
    }

    /// <summary>How many of a member's newest <c>blocked</c> rows are read for a wait to close.</summary>
    private const int RecentBlocks = 20;

    /// <summary>Every plugin member of <paramref name="team"/>.</summary>
    public async Task RefreshTeamAsync(string team, CancellationToken ct = default)
    {
        foreach (var snapshot in host.Snapshots().Where(s => string.Equals(s.Team, team, StringComparison.OrdinalIgnoreCase)))
        {
            await RefreshAsync(new ContainerId(snapshot.Team, snapshot.Id), ct);
        }
    }

    /// <summary>Every plugin member on the Host: the restore's read.</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        foreach (var snapshot in host.Snapshots().Where(s => MemberRef.IsPlugin(s.Agent, out _)))
        {
            await RefreshAsync(new ContainerId(snapshot.Team, snapshot.Id), ct);
        }
    }
}
