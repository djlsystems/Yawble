using System.Runtime.CompilerServices;
using Harness.Contracts;
using Harness.Pty;

namespace Harness.Host;

/// <summary>
/// THE CONCIERGE LAUNCH THE HOST RUNS: what <see cref="ConciergeSessionStore"/> is handed as its
/// <see cref="ConciergeLaunch"/>, and the output floor of each session it staged. A class rather than
/// a delegate written inline where the store is registered, so a test drives the same launch the
/// Host does instead of a copy of its wiring.
/// </summary>
/// <remarks>
/// Its services are resolved once, when it is made, and never inside the launch: the store outlives
/// the service provider at shutdown.
/// </remarks>
public sealed class ConciergeLaunches(
    ConciergeLaunchFactory launcher,
    WorkerPtyEngine terminals,
    TeamRegistry teams,
    // The steering, judged at every launch: a workflow that is over, or whose team is gone or
    // archived, is dropped rather than handed on.
    SteeringWorkflows steering,
    // The LOGIN, for the workspace's directory name. Resolved here rather than inside the
    // factory for teamLabel's stated reason - the factory has no store of its own.
    IUserStore accounts,
    // For the agent the Concierge runs when nobody has chosen one - see ConciergeAgentDefault.
    AgentCatalog catalog,
    AgentAuthProbe probe,
    // The MCP servers' tools the session is given (MCP 4): its person's current team's, as it stands at launch.
    McpRunTools mcp)
{
    // The output floor each session must pass, from the preset it launched with: recorded against
    // the staged spec's environment, which the store's own `with` for the size keeps.
    private readonly ConditionalWeakTable<IReadOnlyDictionary<string, string>, OutputFloor> _floors = [];

    /// <summary>
    /// One person's Concierge, staged. Console config belongs to the TEAM, not a global setting: an
    /// embedded console for one app needs its own CLI and key, and another app's needs something
    /// else. The PERSON comes from the key rather than from config, because it is whoever opened
    /// this one. The LABEL is resolved here rather than inside the factory - it has no TeamRegistry
    /// of its own. LabelFor falls back to the identifier for a team nobody has relabelled, so a
    /// never-relabelled team's console reads its identifier.
    /// </summary>
    public async Task<PtySpec> LaunchAsync(
        ConciergeSessionKey key, string team, ConciergeBrowser? browser, CancellationToken ct)
    {
        // THE CONCIERGE, ASKED WITHOUT NAMING A TEAM. ConciergeFor(team) throws when the
        // registry holds nothing, which is the empty instance GET /api/concierge/ws exists
        // to serve. Concierge() is already the tenant-wide reader.
        var settings = teams.Concierge();
        var stored = string.IsNullOrWhiteSpace(team) ? null : teams.ExistingName(team);

        // BY ID, because that is what the session key carries and an email can change under a
        // live session - a rename is a case to survive rather than an edge.
        //
        // NOT FALLING BACK TO THE ID when the account cannot be read, which it never should
        // be: the route has already refused anything but an authenticated User, and the mint
        // below would fail its owner foreign key anyway. An empty login lands on the
        // `user-<suffix>` fallback instead, and that choice is deliberate - a name is claimed
        // by its marker for good, so falling back to the id would let one unreadable lookup
        // pin this person to the id-named folder the migration exists to retire.
        var account = await accounts.FindByIdAsync(key.User, ct);

        var agent = await ConciergeAgentDefault.ResolveAsync(settings.Agent, catalog, probe, ct);

        // Resolved here, made into a terminal by the worker that runs it.
        var resolved = await launcher.ResolveAsync(
            stored ?? "",
            stored is null ? "this instance" : teams.LabelFor(stored),
            key.User,
            account?.Email ?? string.Empty,
            agent,
            teams.EnvFor(stored ?? ""),
            steeringCausation: null,
            publicUrl: browser?.PublicUrl,
            timeZone: browser?.TimeZone,
            ct: ct,
            steered: await steering.ReadAsync(key.User, ct));

        // THE PERSON'S CURRENT TEAM'S MCP SERVERS, AT THIS START, never re-read on a switch: Reload is a new start.
        var current = await accounts.CurrentTeamForAsync(key.User, ct) is { Length: > 0 } chosen ? teams.ExistingName(chosen) : null;
        await mcp.BeginConciergeAsync(key.User, current, resolved.StartingFolder, ct);

        var staged = terminals.Stage(resolved);
        if (staged.Env is { } environment) _floors.AddOrUpdate(environment, OutputFloor.Of(catalog.Definition(agent)));
        return staged;
    }

    /// <summary>The output floor of a session this launch staged; the platform default otherwise.</summary>
    public OutputFloor FloorOf(PtySpec spec) =>
        spec.Env is { } environment && _floors.TryGetValue(environment, out var floor) ? floor : OutputFloor.PlatformDefault;
}
