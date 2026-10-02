namespace Harness.Host;

/// <summary>
/// WORK THAT FINISHES WHEN ITS CALLER GOES AWAY. A team create clones its repositories, and a large
/// clone outlasts the browser that asked for it: the request is aborted, and work run on the
/// request's token stopped at the abort and left a team with an empty clone and no dispatch. Work
/// run here takes the Host's lifetime instead. The caller still waits for it and gets its answer;
/// a caller that has gone stops waiting, and the work carries on.
///
/// <para>
/// <b>THE WORK MUST NOT TOUCH THE REQUEST.</b> Once the caller has gone its <c>HttpContext</c> is
/// recycled, so who acted and anything else read from the request is read BEFORE the work starts
/// and handed in. The request's execution context does not flow into the work, so nothing reached
/// through <c>IHttpContextAccessor</c> can be read late by accident.
/// </para>
///
/// <para>
/// <b>A FAILURE NOBODY IS WAITING FOR IS LOGGED.</b> While the caller waits, a failure is its
/// answer. Once it has gone there is nobody to answer, so the failure is logged instead of being
/// lost. What the work leaves is what the same failure leaves when the caller waits.
/// </para>
/// </summary>
public sealed class DetachedWork(IHostApplicationLifetime lifetime, ILogger<DetachedWork> logger)
{
    /// <summary>
    /// Runs <paramref name="work"/> on the Host's lifetime and waits for it while
    /// <paramref name="caller"/> lasts. <paramref name="what"/> names the work in the log.
    /// </summary>
    public async Task<T> RunAsync<T>(string what, Func<CancellationToken, Task<T>> work, CancellationToken caller)
    {
        Task<T> running;
        using (ExecutionContext.SuppressFlow())
        {
            running = Task.Run(() => work(lifetime.ApplicationStopping), CancellationToken.None);
        }

        _ = running.ContinueWith(
            finished => Report(what, finished, caller),
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return await running.WaitAsync(caller);
    }

    private void Report(string what, Task finished, CancellationToken caller)
    {
        // A caller still waiting has the failure as its answer.
        if (!caller.IsCancellationRequested) return;

        if (finished.IsCanceled)
        {
            logger.LogWarning("{What} was stopped by the Host shutting down after its caller had gone.", what);
            return;
        }

        logger.LogError(
            finished.Exception?.GetBaseException(),
            "{What} failed after its caller had gone, so nobody was told.", what);
    }
}
