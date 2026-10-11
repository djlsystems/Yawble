using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// PICKS WHAT RUNS A MEMBER, PER INVOCATION, from <see cref="MemberInvocation.Implementation"/>:
/// <c>plugin:&lt;id&gt;</c> goes to the plugin runner, anything else to the agent runner.
///
/// Per invocation and never per member, for the reason the Agent catalog is resolved per
/// invocation: a member repointed in its settings runs the new implementation on its next wake,
/// and nothing has to be rebuilt. This is the only place the choice is made, and it is outside
/// the pump: <c>ContainerHost</c> and <c>MemberRuntime</c> hold one <see cref="IMemberRunner"/> and
/// never learn which kind it served.
/// </summary>
public sealed class MemberRunnerRouter(AgentMemberRunner agents, IMemberRunner? plugins = null) : IMemberRunner
{
    public AgentMemberRunner Agents => agents;

    public IMemberRunner? Plugins => plugins;

    public Task<MemberResult> RunAsync(MemberInvocation invocation, CancellationToken ct = default)
    {
        if (!MemberRef.IsPlugin(invocation.Implementation, out var id)) return agents.RunAsync(invocation, ct);

        return plugins is not null
            ? plugins.RunAsync(invocation, ct)
            : Task.FromResult(MemberResult.NotRun(
                $"'{MemberRef.ForPlugin(id)}' is a plugin, and this Host runs no plugin members, so this "
                + "run did not start."));
    }
}
