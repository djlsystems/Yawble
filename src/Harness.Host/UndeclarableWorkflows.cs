using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Kanban;

namespace Harness.Host;

/// <summary>
/// A WORKFLOW ITS OWNER CANNOT DECLARE (part 2, E1).
///
/// <para>
/// A workflow is its owner's to declare: the member its root instruction addressed. A person can
/// tell a plugin member directly, which makes the plugin the owner - and a plugin holds no
/// credential and no Progress permit, so it cannot call <c>workflow-complete</c>, and the Manager is
/// refused because the workflow is not its own. Every such workflow stayed open for good.
/// </para>
///
/// <para>
/// <b>THE RULE.</b> When the owner of a workflow does not hold <see cref="Permits.Progress"/>, the
/// permit <c>workflow-complete</c> requires, the platform declares that workflow completed on the
/// owner's behalf at the first run end in it - any member's - that:
/// <list type="bullet">
/// <item>SUCCEEDED (a failed or stopped run leaves the workflow open, where a person sees it);</item>
/// <item>leaves nothing working it - nobody running, nothing pending, nothing appended and not yet
/// handed out (<see cref="WorkflowBusyState"/> and <see cref="IdleWorkflowOffer"/>'s undelivered
/// check, the same two questions the offer asks);</item>
/// <item>finds it open, not paused, and with no unfinished card.</item>
/// </list>
/// That is exactly the moment after which nothing will ever wake the workflow again, so it is either
/// declared now or open forever. The row is the owner's (its source), with
/// <see cref="DeclaredByPlatformField"/> saying who wrote it.
/// </para>
///
/// <para>
/// <b>KEYED ON THE CAPABILITY, NOT THE KIND</b>, as the offer is: every agent member holds Progress
/// and declares for itself, unchanged. It lives in the Host, never in the pump.
/// </para>
/// </summary>
public sealed class UndeclarableWorkflows(
    TeamRegistry teams,
    ContainerHost host,
    IMessageLog log,
    IPendingDeliveries pending,
    ICursors cursors,
    ISubscriptions subscriptions,
    IBacklogStore backlog,
    KanbanStore kanban,
    WorktreeRemoval worktrees,
    TeamPaths paths,
    ILogger<UndeclarableWorkflows>? diagnostics = null)
{
    /// <summary>On the <c>workflow.completed</c> payload the platform wrote for an owner that
    /// cannot declare. Absent on a member's own declaration.</summary>
    public const string DeclaredByPlatformField = "declaredByPlatform";

    /// <summary>Called as a run ends, beside <see cref="IdleWorkflowOffer"/>. Returns whether the
    /// workflow was declared.</summary>
    public async Task<bool> OnRunEndingAsync(ContainerId member, long? causation, bool succeeded, CancellationToken ct)
    {
        try
        {
            return await DecideAsync(member, causation, succeeded, ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            // A handler's failure is not this run's: the terminal row below must still be written.
            diagnostics?.LogWarning(error, "Could not decide whether to declare the workflow {Member} ended a run in.", member);
            return false;
        }
    }

    private async Task<bool> DecideAsync(ContainerId member, long? causation, bool succeeded, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || !succeeded) return false;

        if (causation is not { } waking) return false;

        if (teams.ExistingName(member.Team) is not { } stored) return false;

        if (await log.FindAsync(waking, ct) is not { } woken || woken.CorrelationId <= 0) return false;

        var correlation = woken.CorrelationId;

        // WHOSE IT IS, by `workflow-complete`'s own rule.
        var owner = await WorkflowOwner.OfAsync(log, correlation, ct)
            ?? new ContainerId(stored, TeamRegistry.DefaultManagerName);

        // AN OWNER THAT CAN DECLARE DECLARES FOR ITSELF. This is the whole of the scope - with one
        // addition: a workflow a TRIGGER rooted whose trigger chose not to be told of a run that
        // simply finishes (`onHandbackOrFailure`, `never`). Its run ending without a hand-back is
        // the end of it, as the trigger said; the idle offer is not spent on it (see
        // `IdleWorkflowOffer`), so it is declared here or left open on every fire.
        if (host.Find(owner) is not { } declarer) return false;

        var quietTrigger = await RootedByQuietTriggerAsync(log, correlation, ct);
        if (declarer.Permits.Contains(Permits.Progress) && !quietTrigger) return false;

        if (!(await log.OpenWorkflowsAmongAsync([correlation], ct)).Contains(correlation)) return false;

        var thread = await log.ReadCorrelationAsync(correlation, ct);

        if (WorkflowPause.IsPaused(thread)) return false;

        // NOTHING LEFT WORKING IT. `excluded: member` is the ending run itself.
        if ((await WorkflowBusyState.DescribeAsync(stored, correlation, host, pending, log, excluded: member, ct)).Count != 0)
        {
            return false;
        }

        if (await IdleWorkflowOffer.UndeliveredAsync(host, subscriptions, cursors, diagnostics, stored, thread, ct)) return false;

        // AN UNFINISHED CARD IS LEFT FOR A PERSON: the declaration moves every card to Done, and
        // only a member's own `dropped` may say why one is being left. The owner's and the ending
        // member's own cards are excluded, as `workflow-complete` excludes the declarer's.
        var looseEnds = await WorkflowLooseEnds.DescribeAsync(kanban, stored, correlation, owner.Name);
        if (!member.Equals(owner))
        {
            looseEnds = [.. looseEnds.Intersect(await WorkflowLooseEnds.DescribeAsync(kanban, stored, correlation, member.Name))];
        }

        if (looseEnds.Count != 0) return false;

        // The team's branches were put on origin by `TerminalPublish`, which runs before this in
        // the same run-ending hook - the ordering `workflow-complete` keeps by publishing first.
        var delivered = quietTrigger
            ? "A trigger started this workflow and chose not to wake the Manager when its run simply "
              + "finishes, so the platform declared it: a run in it completed and nothing is left working it."
            : $"{declarer.Snapshot().Name} cannot declare its own workflows, so the platform declared this one: "
              + "a run in it completed and nothing is left working it.";

        await WorkflowDeclaration.AppendAsync(
            stored, correlation, owner, waking,
            JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["delivered"] = delivered,
                ["dropped"] = null,
                ["looseEnds"] = null,
                [DeclaredByPlatformField] = true,
            }),
            log, backlog, host, kanban, worktrees, paths, teams.ReposFor(stored),
            (ILogger?)diagnostics ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, ct);

        diagnostics?.LogInformation(
            "Declared workflow {Correlation} completed for {Owner}, which cannot declare it.", correlation, owner);

        return true;
    }

    /// <summary>
    /// Whether <paramref name="correlation"/>'s root is a trigger's fire carrying a
    /// <see cref="WakeManagerPolicy"/> other than `always` - a workflow whose run finishing is, by
    /// the trigger's choice, nobody's business. Read off the root row, which the log never changes.
    /// </summary>
    public static async Task<bool> RootedByQuietTriggerAsync(IMessageLog log, long correlation, CancellationToken ct) =>
        await log.FindAsync(correlation, ct) is { } root
        && root.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal)
        && WakeManagerPolicy.OfInstruction(root.Source, root.Payload) is not null;
}
