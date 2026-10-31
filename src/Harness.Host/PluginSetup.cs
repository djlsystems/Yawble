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
///
/// A CONNECTION THAT NEEDS A PERSON holds up every member using it the same way: a bound connection
/// marked <c>needs-reconnect</c> - by a run that reported it refused or asking for a sign-in, by a refused
/// refresh, or by a Test connection - is said on the member's mark, and the member is
/// remembered as held by it (<see cref="Holds"/>), which Needs you lists once per connection. When the
/// connection is put right (<see cref="ConnectionChangedAsync"/>), each member it held is read again, and
/// its Manager is told, once, in each still-open workflow a run of the member failed held up by it
/// (<see cref="PluginReadyNotice.SendAsync"/>).
/// </summary>
public sealed class PluginSetup(
    ContainerHost host, PluginCatalog plugins, IPluginMemberSettingsStore settings, ISecretStore secrets,
    PluginMemberSecretStore memberSecrets, IMessageLog? log = null, IConnectionStore? connections = null)
{
    /// <summary>One connection holding members up: what it is called, what it says, since when, and whom.</summary>
    public sealed record ConnectionHold(string Id, string Name, string Sentence, DateTimeOffset Since, IReadOnlyList<ContainerId> Members);

    private readonly Lock _gate = new();

    /// <summary>Each member held up, and the connections holding it: (id, name, the sentence the member reads).</summary>
    private readonly Dictionary<ContainerId, IReadOnlyList<(string Id, string Name, string Sentence)>> _held = [];

    /// <summary>
    /// The connections a Team Connections run of each member failed held up by (CON 6), kept until each
    /// no longer needs a person - a later run that succeeds at something else does not put it right.
    /// </summary>
    private readonly Dictionary<ContainerId, HashSet<string>> _heldByRun = [];

    /// <summary>When each holding connection was first seen holding, on this Host's run.</summary>
    private readonly Dictionary<string, DateTimeOffset> _since = new(StringComparer.Ordinal);

    /// <summary>
    /// EVERY CONNECTION HOLDING A MEMBER UP, once each, with the members it holds: what
    /// <c>GET /api/needs-you</c> lists as one <c>connection</c> row. Read from memory.
    /// </summary>
    public IReadOnlyList<ConnectionHold> Holds()
    {
        lock (_gate)
        {
            return [.. _held
                .SelectMany(pair => pair.Value.Select(c => (Member: pair.Key, Connection: c)))
                .GroupBy(h => h.Connection.Id, StringComparer.Ordinal)
                .Select(g => new ConnectionHold(
                    g.Key, g.First().Connection.Name, g.First().Connection.Sentence,
                    _since.GetValueOrDefault(g.Key, DateTimeOffset.UtcNow),
                    [.. g.Select(h => h.Member).Distinct().OrderBy(m => m.Team, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Name, StringComparer.Ordinal)]))
                .OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>
    /// A CONNECTION'S HEALTH MAY HAVE MOVED (<see cref="Connections.HealthChanged"/>): every member using it,
    /// and every member it held, is read again - marked, or cleared and its Manager told.
    /// </summary>
    public async Task ConnectionChangedAsync(string connectionId, CancellationToken ct = default)
    {
        var members = new HashSet<ContainerId>();

        lock (_gate)
        {
            foreach (var (member, holding) in _held)
            {
                if (holding.Any(c => c.Id == connectionId)) members.Add(member);
            }
        }

        if (connections is not null)
        {
            foreach (var use in await connections.UsedByAsync(connectionId, ct))
            {
                members.Add(new ContainerId(use.Team, use.Member));
            }
        }

        foreach (var member in members.OrderBy(m => m.Team, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Name, StringComparer.Ordinal))
        {
            await RefreshAsync(member, ct);
        }
    }

    /// <summary>
    /// A MEMBER'S RUN ENDED (<c>onTerminal</c>): a Team Connections run that failed held up by a connection
    /// - a website's refused sign-in, expired session, CAPTCHA or device approval, or one that already
    /// needed a person (CON 6) - is a connection hold from now on, as a bound member's is (CON 4).
    /// </summary>
    public async Task RunEndedAsync(Message terminal, CancellationToken ct = default)
    {
        if (terminal.Type != MessageTypes.Failed
            || new ContainerMarks(null, null, terminal).HeldByConnection is not { } heldBy
            || !ContainerId.TryParse(terminal.Source, out var member)
            || !TeamConnectionsMember.Is(member))
        {
            return;
        }

        lock (_gate)
        {
            if (!_heldByRun.TryGetValue(member, out var ids)) _heldByRun[member] = ids = new(StringComparer.Ordinal);
            ids.Add(heldBy);
        }

        await RefreshAsync(member, ct);
    }

    /// <summary>
    /// The connections bound on the member that need a person, as the member's mark says them. TEAM
    /// CONNECTIONS binds none - it uses the team's assigned connections (CON 6) - so for it, each
    /// connection a run of it failed held up by (<see cref="RunEndedAsync"/>, and after a restart the
    /// failed mark's <see cref="MemberRuntime.HeldByConnection"/>) while that still needs a person.
    /// </summary>
    private async Task<IReadOnlyList<(string Id, string Name, string Sentence)>> HoldingAsync(
        PluginMemberSettings bound, MemberRuntime runtime, CancellationToken ct)
    {
        if (connections is null) return [];

        var byRun = new List<string>();
        if (TeamConnectionsMember.Is(runtime.Id))
        {
            lock (_gate)
            {
                if (_heldByRun.TryGetValue(runtime.Id, out var ids)) byRun.AddRange(ids);
            }

            if (runtime.HeldByConnection is { } heldBy) byRun.Add(heldBy);
        }

        var holding = new List<(string, string, string)>();
        foreach (var id in bound.Connections.Values.Concat(byRun).Distinct(StringComparer.Ordinal))
        {
            if (await connections.GetAsync(id, ct) is { Status: ConnectionRecord.NeedsReconnect } connection)
            {
                holding.Add((connection.Id, connection.Name, Connections.NeedsReconnect(connection, connection.StatusReason)));
            }
            else if (byRun.Contains(id))
            {
                // Put right, or removed: it holds this member no more.
                lock (_gate)
                {
                    if (_heldByRun.TryGetValue(runtime.Id, out var ids) && ids.Remove(id) && ids.Count == 0) _heldByRun.Remove(runtime.Id);
                }
            }
        }

        return holding;
    }

    /// <summary>Records what holds <paramref name="member"/> up now; answers the connections that held it and no longer do.</summary>
    private IReadOnlyList<string> Hold(ContainerId member, IReadOnlyList<(string Id, string Name, string Sentence)> holding)
    {
        lock (_gate)
        {
            var before = _held.TryGetValue(member, out var was) ? was.Select(c => c.Id).ToList() : [];

            if (holding.Count == 0) _held.Remove(member);
            else _held[member] = holding;

            foreach (var (id, _, _) in holding) _since.TryAdd(id, DateTimeOffset.UtcNow);

            var still = _held.Values.SelectMany(h => h).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in _since.Keys.Where(id => !still.Contains(id)).ToList()) _since.Remove(gone);

            return [.. before.Where(id => holding.All(c => c.Id != id))];
        }
    }

    /// <summary>The words a member's mark carries for <paramref name="missing"/>, or null for none.</summary>
    public static string? Words(IReadOnlyList<string> missing) =>
        missing.Count == 0 ? null : "Waits for a person to " + string.Join("; and to ", missing) + ".";

    /// <summary>The mark's words: what is missing, then each connection that needs a person, in its own sentence.</summary>
    private static string? Words(IReadOnlyList<string> missing, IReadOnlyList<(string Id, string Name, string Sentence)> holding)
    {
        var words = (string?[])[Words(missing), .. holding.Select(c => c.Sentence)];
        var said = words.Where(w => w is not null).ToList();
        return said.Count == 0 ? null : string.Join(" ", said);
    }

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
            var holding = await HoldingAsync(bound, runtime, ct);
            var released = Hold(member, holding);

            runtime.MarkNeedsPerson(Words(missing, holding));
            if (missing.Count == 0 && holding.Count == 0)
            {
                runtime.ClearNeedsPersonMarks();
                await CloseWaitsAsync(member, ct);
            }

            // A CONNECTION PUT RIGHT tells the Manager, once, in each open workflow a run of this member
            // failed held up by it. A workflow that has ended is not woken.
            if (log is not null)
            {
                foreach (var connection in released)
                {
                    await PluginReadyNotice.SendAsync(log, member, runtime.Snapshot().Name, ct, heldByConnection: connection);
                }
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
