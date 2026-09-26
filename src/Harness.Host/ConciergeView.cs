using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// <c>GET /api/concierge</c>: the stored setting, unchanged, and <see cref="Effective"/> - what the
/// launcher will run after defaults, so the panel can say so before it opens a socket. There is no
/// prompt here: the Concierge always runs the built-in Concierge prompt.
/// </summary>
public sealed record ConciergeView(
    string? Agent,
    ConciergeEffective Effective)
{
    public static async Task<ConciergeView> ReadAsync(
        TenantConciergeSettings settings, AgentCatalog catalog, AgentAuthProbe probe,
        CancellationToken ct) =>
        new(
            settings.Agent,
            await ConciergeAgentDefault.EffectiveAsync(settings.Agent, catalog, probe, ct));
}
