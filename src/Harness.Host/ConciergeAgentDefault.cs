namespace Harness.Host;

/// <summary>What the auth probe says about the agent the Concierge will run.</summary>
/// <param name="SignedIn">True only when the probe measured a sign-in. A probe that could not
/// measure (<see cref="AgentAuthReport.Authenticated"/> null) is false here, and
/// <paramref name="Detail"/> says why.</param>
public sealed record ConciergeAuth(bool Installed, bool SignedIn, string? Detail);

/// <summary>
/// What the Concierge launcher will actually use, after defaults: <c>GET /api/concierge</c>'s
/// <c>effective</c> field, so the panel can say what is about to run before it opens a socket.
/// </summary>
/// <param name="Agent">Null when no agent can start: nobody chose one and no interactive preset is
/// installed.</param>
/// <param name="AgentSource"><c>chosen</c> or <c>default</c>.</param>
public sealed record ConciergeEffective(
    string? Agent,
    string AgentSource,
    ConciergeAuth Auth)
{
    public const string Chosen = "chosen";
    public const string Default = "default";
}

/// <summary>
/// Which agent and prompt the Concierge runs when nobody has chosen one. A fresh instance must open
/// its first terminal without a settings visit, so the choice falls to the catalog: the first
/// interactive preset that is authenticated, else the first that is installed. A chosen agent
/// always wins. The prompt falls to the seeded Concierge prompt. The result is what the terminal
/// shows in its header, so a person can see what was picked and change it.
/// </summary>
/// <remarks>
/// THE ONE RESOLUTION. The launcher and <c>GET /api/concierge</c> both call
/// <see cref="EffectiveAsync"/>; a second copy of these rules is how the panel would come to name an
/// agent the terminal does not start.
/// </remarks>
public static class ConciergeAgentDefault
{
    /// <summary>The agent the launcher starts. Throws when none can start.</summary>
    public static async Task<string> ResolveAsync(
        string? chosen, AgentCatalog catalog, AgentAuthProbe probe, CancellationToken ct) =>
        (await EffectiveAsync(chosen, catalog, probe, ct)).Agent
        ?? throw new InvalidOperationException(
            "No interactive agent is installed, so the Concierge cannot open. Install one, or "
            + "choose one in Tenant Settings.");

    public static async Task<ConciergeEffective> EffectiveAsync(
        string? chosenAgent, AgentCatalog catalog, AgentAuthProbe probe, CancellationToken ct)
    {
        var reports = await probe.ReportsAsync(ct);

        var agentChosen = !string.IsNullOrWhiteSpace(chosenAgent);
        var agent = agentChosen ? chosenAgent!.Trim() : Pick(catalog, reports);

        var report = agent is null
            ? null
            : reports.FirstOrDefault(r => string.Equals(r.Agent, agent, StringComparison.Ordinal));

        var auth = report is null
            ? new ConciergeAuth(
                false, false,
                agent is null
                    ? "No interactive agent is installed."
                    : $"'{agent}' is not an agent this tenant has.")
            : new ConciergeAuth(report.Installed, report.Authenticated == true, report.Detail);

        return new ConciergeEffective(
            agent,
            agentChosen ? ConciergeEffective.Chosen : ConciergeEffective.Default,
            auth);
    }

    private static string? Pick(AgentCatalog catalog, IReadOnlyList<AgentAuthReport> reports)
    {
        var interactive = catalog.Definitions
            .Where(d => d.Mode == AgentMode.Interactive && !d.Hidden && d.Launch.LanguageModel)
            .ToList();

        string? First(Func<AgentAuthReport, bool> wanted) =>
            interactive.FirstOrDefault(d => reports.Any(r => r.Agent == d.Name && wanted(r)))?.Name;

        // NOT "the first that exists" when nothing is installed: that launched a command that is not
        // on PATH and left a person a closed socket. Null lets the panel say so first.
        return First(r => r.Authenticated == true) ?? First(r => r.Installed);
    }
}
