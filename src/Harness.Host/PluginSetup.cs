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
/// </summary>
public sealed class PluginSetup(
    ContainerHost host, PluginCatalog plugins, IPluginMemberSettingsStore settings, ISecretStore secrets,
    PluginMemberSecretStore memberSecrets)
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
            return missing;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
    }

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
