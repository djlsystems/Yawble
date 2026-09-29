using Harness.Containers;
using Harness.Contracts;
using Harness.Host.Solutions;
using Harness.Kanban;

namespace Harness.Host;

/// <summary>
/// WHAT DECLARING A WORKFLOW COMPLETED DOES, once every check has passed: the settled cards' keys
/// read, the <c>workflow.completed</c> row appended, its backlog item moved, and the settled trees
/// removed. One sequence, shared by the <c>workflow-complete</c> route (a member declaring) and
/// <see cref="UndeclarableWorkflows"/> (the platform declaring for a member that cannot), so the two
/// declarations cannot come to mean different things.
/// </summary>
public static class WorkflowDeclaration
{
    /// <returns>The appended <c>workflow.completed</c> row.</returns>
    public static async Task<Message> AppendAsync(
        string team, long correlation, ContainerId declarer, long? causation, string payload,
        IMessageLog log, IBacklogStore backlog, ContainerHost host, KanbanStore kanban,
        WorktreeRemoval worktrees, TeamPaths paths, IReadOnlyList<string> repos, ILogger logger,
        CancellationToken ct, SolutionNotice? solutions = null)
    {
        // Which cards' trees this declaration settles, read BEFORE the row below moves every
        // card to Done - a loose end the declaration `dropped` is still open work, and keeps its tree.
        var settledKeys = await WorktreeRemoval.SettledKeysAsync(kanban, team, correlation, declarer.Name);

        var declaration = await log.AppendAsync(
            new NewMessage(MessageTypes.WorkflowCompleted, payload, declarer.ToString(), causation),
            ct);

        // A COMPLETED WORKFLOW MOVES ITS BACKLOG ITEM TO `declared`, ONCE - a Manager's CLAIM, not a
        // landing. The rule, and why it is a method rather than four lines here, are on
        // BacklogExecutionRecord.OnWorkflowCompletedAsync.
        //
        // THE SNAPSHOTS ARE READ HERE AND NOW, after the caller's busy check, so the guard inside
        // sees the team as it is at the instant of the declaration.
        await BacklogExecutionRecord.OnWorkflowCompletedAsync(
            declaration.CorrelationId, backlog, log, host.Snapshots(), ct);

        // A PACKAGE THIS WORKFLOW WROTE IS CHECKED, and the board and the backlog item get its notice.
        // Only a member's own declaration passes this: the platform declares for a plugin member's or
        // a quiet trigger's run, which writes no package. A notice is not part of the declaration, so
        // a failure to write one is logged rather than returned.
        if (solutions is not null)
        {
            try
            {
                await solutions.PostAsync(team, declaration.CorrelationId, declarer, declaration, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidOperationException or OperationCanceledException)
            {
                logger.LogWarning(ex, "Checking the solution packages of workflow {Correlation} on {Team} failed.", correlation, team);
            }
        }

        // THE SETTLED CARDS' TREES GO, for every member and every repository - after the caller's
        // publish put their branches on origin, and never forced: a tree that refuses stays on
        // disk and is named on the team feed. Nothing here can undo the declaration, so a failure is
        // logged rather than returned.
        try
        {
            await worktrees.RemoveKeysAsync(paths, team, repos, settledKeys, causation, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            logger.LogWarning(
                ex, "Removing the settled worktrees of workflow {Correlation} on {Team} failed.", correlation, team);
        }

        return declaration;
    }
}
