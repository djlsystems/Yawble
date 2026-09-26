using System.Diagnostics;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE SECOND CALL SITE OF <see cref="ITeamPublisher"/>, AND THE ONE A DYING RUN STILL REACHES.
///
/// <para>
/// The first call site is the `workflow-complete` route: the Host pushes the team's branches and
/// only then appends `workflow.completed`, so a card cannot read `done` without the work being on
/// origin. THAT IS NOT SUFFICIENT ON ITS OWN, because it has exactly one trigger - a member
/// declaring its turn finished. A run killed BETWEEN doing the work and finishing its turn never
/// declares anything, never reaches the route, and origin is never touched, leaving the work
/// committed in a worktree and nothing on origin. A spend limit, a watchdog kill, a Stop from the
/// board and a host restart all produce it.
/// </para>
///
/// <para>
/// SO THE PLATFORM PUBLISHES ON THE OTHER EVENT IT ALREADY OBSERVES: the terminal transition of a
/// container. This is handed to `ContainerHost` as `onRunEnding` and is awaited immediately above
/// the `agentContainer.completed`/`agentContainer.failed` append, by both writers of that row - the
/// container that saw its own run end, and the resume path that fails a run a restart cut short.
/// </para>
///
/// <para>
/// IT IS THE SAME PUBLISHER, DELIBERATELY. Every durability property is a property of
/// <see cref="TeamPublisher"/> and none of them is restated here: `main`/`master`/`HEAD` are never a
/// destination, a team with no repository writes nothing, a failed push is `repo.pushFailed` and
/// never the run's own outcome, and git's output is redacted AT THE WRITE. A second publisher for
/// the kill path would be a second place for each of those four to be forgotten.
/// </para>
/// </summary>
public static class TerminalPublish
{
    /// <summary>
    /// HOW LONG A DYING CONTAINER WAITS ON ORIGIN BEFORE ITS DEATH IS RECORDED ANYWAY.
    ///
    /// <para>
    /// THE THING BEING DELAYED IS A PROCESS THAT IS ALREADY BEING TORN DOWN, so the trade here is
    /// not the acceptance path's. There, a member is waiting on an HTTP response and the whole
    /// point is that the row must not be written first, so the publish is awaited inline with only
    /// `GitRunner`'s own 30s per-operation bound around it. Here, what waits is the terminal row -
    /// and everything that reads it: the card leaving `running`, the team tile, the kanban
    /// projection, the next instruction this member could be taking.
    /// </para>
    ///
    /// <para>
    /// TEN SECONDS, because that is comfortably longer than a healthy push of a handful of branches
    /// and far short of anything a person watching a stopped member would call hung. An origin that
    /// has not answered in ten seconds is not going to answer inside a teardown.
    /// </para>
    ///
    /// <para>
    /// <b>THE BOUND ABANDONS THE WAIT, IT DOES NOT CANCEL THE PUSH.</b> A cancelled push is a push
    /// that reports nothing - <see cref="ITeamPublisher"/> rethrows its CALLER's cancellation
    /// precisely because a cancelled request has nobody to report to - and "nothing moved on origin
    /// and no row says so" is the silence this class exists to prevent. So the publish runs on,
    /// still bounded by `GitRunner`'s 30s per operation, and writes its own `repo.pushed` or
    /// `repo.pushFailed` when it finishes.
    /// </para>
    ///
    /// <para>
    /// <b>TEN SECONDS OF NOTHING HAPPENING, NOT TEN SECONDS.</b> An elapsed-time bound is
    /// wrong on a busy machine: on a loaded host a healthy push of a dozen branches over real git,
    /// even to a LOCAL origin, can take well over ten seconds, and an elapsed bound would land the terminal row with origin holding nothing,
    /// which is the state
    /// `DurablePushOnTerminalTransitionTests.The_branch_is_on_origin_before_the_row_that_says_the_run_ended`
    /// forbids. Elapsed time cannot tell a slow publish from a hung one. Whether it is still
    /// STARTING AND FINISHING GIT OPERATIONS can, so that is what the window measures, and
    /// <see cref="Ceiling"/> is what keeps "wait while it is working" from meaning "wait forever".
    /// </para>
    /// </summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// THE OTHER BOUND, AND THE ABSOLUTE ONE. However busy git is, a dying container's terminal row
    /// arrives inside this.
    ///
    /// <para>
    /// <see cref="Budget"/> is a bound on IDLENESS - see <see cref="OnRunEndingAsync"/> - and
    /// the signal a host can honestly offer is git activity in the PROCESS rather than in this one
    /// publish. So a host busy with somebody else's git could keep this wait alive indefinitely,
    /// and a liveness promise with an indefinite in it is not a promise. This is where it stops.
    /// </para>
    ///
    /// <para>
    /// SIXTY SECONDS, which is far past what a healthy publish takes even on a loaded host, and
    /// short enough that nobody watching a stopped member calls it hung.
    /// Reaching it is the case where ordering was never going to hold.
    /// </para>
    /// </summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Publishes this member's team, if it has a repository, and waits for it while it is still
    /// working - <see cref="Budget"/> of NOTHING HAPPENING gives up, and <see cref="Ceiling"/>
    /// gives up regardless.
    ///
    /// NEVER THROWS except on <paramref name="ct"/>, which is the Host going down: the caller is
    /// about to write a terminal row and that row must not depend on git.
    /// </summary>
    /// <param name="teams">Resolves the member's team and its repository URLs. Read through the
    /// provider by the caller rather than captured - see the wiring in `Program.cs`.</param>
    /// <param name="publisher">The one publisher. See this type's own summary.</param>
    /// <param name="member">The container whose run is ending. Becomes the rows' Source, exactly as
    /// it does on the acceptance path, which is also what lets `MessageTeam.Of` recover the team.</param>
    /// <param name="causation">The seq of the message this run was handling, so the push rows land
    /// inside the workflow that was interrupted rather than rooting one of their own.</param>
    /// <param name="budget">
    /// Defaulted to <see cref="Budget"/> and passed by NOTHING in production - the wiring in
    /// `Program.cs` takes the default, so the documented number is the one that ships and there is
    /// no second place for it to be chosen. It is a parameter so that a spec can assert what the
    /// bound DOES - that a publish still running when it expires is abandoned rather than cancelled -
    /// without spending ten seconds of suite time per assertion to do it.
    /// </param>
    /// <param name="activity">
    /// A MONOTONIC COUNT OF GIT OPERATIONS STARTED AND FINISHED, read only for whether it CHANGED
    /// across a window. This is what makes <paramref name="budget"/> a bound on IDLENESS rather than
    /// on elapsed time - see <see cref="Budget"/> for the measurement behind that.
    ///
    /// <para>
    /// IT IS THE PROCESS'S GIT ACTIVITY, NOT THIS PUBLISH'S, and that is a deliberate choice
    /// rather than a shortcut taken for want of a better signal. A per-publish signal would have
    /// to come through <see cref="ITeamPublisher"/>, and the one thing that interface must not
    /// grow is a second way for a caller to influence what gets pushed. The coarser reading is
    /// also the one that answers the actual question: the case to tolerate is "this machine is
    /// busy", and git operations completing anywhere in this process is a direct measurement of
    /// a machine that is busy and alive rather than an origin that has stopped answering.
    /// <see cref="Ceiling"/> pays for the looseness.
    /// </para>
    ///
    /// <para>
    /// NULL IS THE HONEST DEFAULT. A caller with no signal cannot tell slow from hung, so it gives
    /// up at the first window.
    /// </para>
    /// </param>
    /// <param name="ceiling">
    /// Defaulted to <see cref="Ceiling"/> and, like <paramref name="budget"/>, passed by nothing in
    /// production. A parameter so a spec can assert the ceiling exists without spending a minute.
    /// </param>
    public static async Task OnRunEndingAsync(
        TeamRegistry teams,
        ITeamPublisher publisher,
        ContainerId member,
        long? causation,
        CancellationToken ct,
        TimeSpan? budget = null,
        Func<long>? activity = null,
        TimeSpan? ceiling = null)
    {
        // A TEAM WITH NO REPOSITORY IS NEVER REFUSED AND NEVER DELAYED, and here that is a
        // synchronous return with no await in it at all. Every measurement team ends every run
        // through this line, and a durability rule that costs them anything visible is a rule
        // somebody switches off. The publisher would answer the same way; this is about the clock.
        if (ct.IsCancellationRequested) return;
        if (teams.ExistingName(member.Team) is not { } stored) return;

        var repos = teams.ReposFor(stored);

        if (repos.Count == 0) return;

        var publish = publisher.PublishAsync(stored, repos, member, causation, ct);

        // The timers are cancelled the moment the publish wins, so a healthy host does not
        // accumulate one live ten-second timer per run that ever ended.
        using var settled = new CancellationTokenSource();

        var window = budget ?? Budget;
        var limit = ceiling ?? Ceiling;
        var since = Stopwatch.StartNew();

        // THE READING THE NEXT WINDOW IS COMPARED AGAINST. Null when the caller offered no signal,
        // and that is the arm every direct caller that passes none takes: with nothing to tell slow
        // from hung, the bound is the wall clock.
        var seen = activity?.Invoke();

        while (true)
        {
            if (await Task.WhenAny(publish, Task.Delay(window, settled.Token)).ConfigureAwait(false) == publish)
            {
                break;
            }

            // A WHOLE WINDOW PASSED WITH THE PUBLISH STILL RUNNING. Three reasons to stop waiting
            // and one to carry on: git is still working.
            var now = activity?.Invoke();

            if (now is null || now == seen || since.Elapsed >= limit)
            {
                // GIVING UP. The push keeps going and keeps its own promise to report; nothing here
                // waits for it, and the exception it may yet raise is observed so a slow origin
                // cannot become an unobserved task exception on a background thread.
                Forget(publish);
                settled.Cancel();
                return;
            }

            // IT MOVED, SO THE PUBLISH IS WORKING AND THE ORDERING IS STILL WORTH HAVING.
            seen = now;
        }

        settled.Cancel();

        try
        {
            await publish.ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The publisher does not throw for a push that failed - that is a row, not an
            // exception. This is for the ones nobody predicted, and the terminal row is worth more
            // than any of them.
        }
    }

    private static void Forget(Task task) =>
        _ = task.ContinueWith(
            static finished => _ = finished.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
