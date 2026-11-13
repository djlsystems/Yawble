using Yawble.Core.Bus;

namespace Yawble.Core.Teams;

/// <summary>
/// Wraps the launcher or a fake: before each run it makes sure the member's clone, workspace and temp
/// folder exist and sets <see cref="RunContext.Checkout"/>; after the run it runs the post-run git check and fetches
/// the member's branch into the team repository. The inner runner uses the clone as the sandbox's
/// cwd; <see cref="Policy.RunLayout.ClonePath"/> is the same path.
///
/// A run whose work could not be brought into the team repository is reported failed, with the reason, so the
/// Manager is told. A run cut short by the core stopping is fetched back at the next start-up.
/// </summary>
public sealed class TeamRepoRunner(IAgentRunner inner, TeamRepos repos, Action<string>? log = null) : IAgentRunner
{
    /// <summary>The key of the push report in the run's details (agent.completed / agent.failed payload "run").</summary>
    public const string PushDetail = "push";

    public async Task<RunResult> RunAsync(RunContext run, CancellationToken ct)
    {
        // A workflow's first run (its root instruction): bring the team's branches up to the base first, on the host. A
        // failed sync never stops the work; a conflict is carried as a note at the top of the instruction.
        if (run.Items.Count > 0 && run.Items[0].Seq == run.Items[0].CorrelationId)
        {
            try
            {
                run.SyncNote = (await repos.SyncBranchesAsync(run.Member.TeamId, run.Member.Team, run.Correlation.ToString(System.Globalization.CultureInfo.InvariantCulture), ct)).Note;
            }
            catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                log?.Invoke($"team {run.Member.Team}: the branch sync before workflow {run.Correlation} failed: {ex.Message}");
            }
        }
        try
        {
            run.Checkout = await repos.PrepareAsync(run.Member, ct);
        }
        catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return RunResult.Fail("could not prepare the member's clone: " + ex.Message);
        }

        RunResult result;
        try
        {
            result = await inner.RunAsync(run, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the core is stopping: the run is reported interrupted, and fetched back at start-up
        }

        string? problem;
        try
        {
            await repos.CheckAndScrubAsync(run.Member, CancellationToken.None);
            var fetched = await repos.FetchBackAsync(run.Member, CancellationToken.None);
            problem = fetched.Error;
        }
        catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
        }
        if (problem is null)
        {
            // The app publishes, so work cannot be lost to a run that ended early. A failed
            // push never fails the run; it is reported with it (the Manager and the person see it).
            var push = await repos.PublishAsync(run.Member, CancellationToken.None);
            if (push.Result is TeamPublishResult.NoRepository or TeamPublishResult.NothingToPublish) return result;
            var details = new Dictionary<string, object?>(result.Details ?? new Dictionary<string, object?>())
            {
                [PushDetail] = new Dictionary<string, object?>
                {
                    ["result"] = push.Result.ToString(),
                    ["branches"] = push.Branches,
                    ["published"] = push.Published,
                    ["reason"] = push.Reason,
                },
            };
            return result with { Details = details };
        }
        log?.Invoke($"{run.Member.Qualified}: run {run.RunId}: branch not fetched back: {problem}");
        var reason = $"the member's branch was not brought into the team repository: {problem}";
        return RunResult.Fail(result.Succeeded ? reason : $"{result.Reason}; {reason}");
    }
}
