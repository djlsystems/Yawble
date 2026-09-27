using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE ONLY WRITER OF `subscriptions`.
///
/// <para>
/// An event trigger has to make the delivery pump deliver a type, which means a container's
/// subscriptions must be able to change after the container exists. So the set is derived,
/// on demand, from two things that can each change independently: a container is woken by its BASE
/// set - <c>team_members.subscribes</c>, the row it was created with, never modified in place -
/// UNIONED with the event type of every ENABLED event trigger it holds, and its own addressed
/// instruction type, so it stays reachable by <c>tell</c> without a trigger of its own. The base set
/// is never modified, so deleting a trigger can never eat a Manager's <c>container.completed</c>.
/// </para>
///
/// <para>
/// RECOMPUTED WHOLE, NEVER INCREMENTED. Refcounting two contributors into one set is how the two
/// drift, and `subscriptions` is a materialised projection - written from several places it becomes a
/// cache, and a cache keyed on an Agent Container has already produced three drift bugs in this
/// codebase, each changing nothing until restart. A whole-set recompute with one writer cannot drift:
/// it is either called or it is not.
/// </para>
///
/// <para>
/// <c>EffectiveSubscriptionsTests.Only_EffectiveSubscriptions_calls_SetAsync</c> is what keeps that
/// true after someone adds a second trigger path in six weeks. Deleting it re-opens the class.
/// </para>
/// </summary>
public sealed class EffectiveSubscriptions(
    ITeamStore teams, ITriggerStore triggers, ISubscriptions subscriptions, ContainerHost containers)
{
    /// <summary>
    /// Recomputes and persists <paramref name="container"/>'s whole effective subscription set.
    /// Callers: <see cref="TeamRegistry.AddContainerAsync"/>, <see cref="TeamRegistry.RestoreAsync"/>,
    /// and the trigger POST/PATCH/DELETE handlers - every place that changes either contributor.
    /// </summary>
    public async Task<IReadOnlyCollection<string>> RecomputeAsync(
        ContainerId container, CancellationToken ct = default)
    {
        var member = (await teams.MembersAsync(ct)).SingleOrDefault(
            m => string.Equals(m.Team, container.Team, StringComparison.OrdinalIgnoreCase)
                && string.Equals(m.Name, container.Name, StringComparison.OrdinalIgnoreCase));

        var effective = await EffectiveTypesAsync(container, member?.Subscribes ?? [], ct);

        await subscriptions.SetAsync(container, effective, ct);

        // THE LIVE SNAPSHOT AGREES WITH THE DATABASE. Without this a trigger is created, the row
        // lands here, and the pump starts using the new set immediately - but the card a person
        // already has open goes on showing the subscriptions this container was CREATED with, which
        // is the state every reader of that card trusts. See ContainerHost.Resubscribe and
        // MemberRuntime.Resubscribe for the set comparison that keeps this from publishing on
        // every recompute, including one that changed nothing.
        containers.Resubscribe(container, effective);

        return effective;
    }

    /// <summary>
    /// The same union <see cref="RecomputeAsync"/> writes - <paramref name="baseSubscribes"/> plus
    /// the container's own addressed-instruction type plus every ENABLED event trigger's type - but
    /// READ-ONLY: nothing is persisted and no snapshot is touched.
    ///
    /// Exists for the firehose guards that only need to ASK "what would this container be woken by",
    /// never to write it: <c>TeamRegistry.UpdateContainerAsync</c>'s repoint check and
    /// <c>AgentEndpoints.ReferenceRefusalFor</c>'s ninth reference check. Both read only
    /// `team_members.subscribes` before triggers could contribute a subscription of their own, which
    /// left a language model repointable onto a firehose a trigger - rather than the base set - had
    /// supplied. Sharing this with <see cref="RecomputeAsync"/> is what keeps the two derivations
    /// from drifting; a second copy of the union is how they would.
    /// </summary>
    public async Task<IReadOnlyCollection<string>> EffectiveTypesAsync(
        ContainerId container, IEnumerable<string> baseSubscribes, CancellationToken ct = default)
    {
        var effective = new HashSet<string>(baseSubscribes, StringComparer.Ordinal)
        {
            // STRUCTURAL, NOT AN OPT-IN. A container that could not be told anything directly would
            // be unreachable, and making that an opt-in someone can forget is a worse default than
            // making it structural.
            MessageTypes.InstructionFor(container),
        };

        foreach (var trigger in await triggers.ListForContainerAsync(container.Team, container.Name, ct))
        {
            if (!trigger.Enabled) continue;
            if (trigger.EventType is not { Length: > 0 } type) continue;

            effective.Add(type);
        }

        return effective;
    }

    /// <summary>
    /// Clears every subscription for a container being DELETED.
    ///
    /// Not a derivation - by the time this is called the row it would derive from is either about
    /// to be removed or already gone, and "empty" is the only correct answer either way. Kept on
    /// this class, beside <see cref="RecomputeAsync"/>, for the reason the whole class exists: this
    /// file is the only one allowed to call <c>ISubscriptions.SetAsync</c>, so a deletion's "forget
    /// everything" has to be one more shape of that write rather than a second door to it.
    /// </summary>
    public Task ClearAsync(ContainerId container, CancellationToken ct = default) =>
        subscriptions.SetAsync(container, [], ct);
}
