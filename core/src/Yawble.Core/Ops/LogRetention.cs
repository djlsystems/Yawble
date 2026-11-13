namespace Yawble.Core.Ops;

/// <summary>
/// Deletes core and run log files (*.log) older than a number of days in the folders it is given: on start and then
/// daily. It never touches the database, so chats and team messages stay.
/// </summary>
public sealed class LogRetention(IEnumerable<string> folders, TimeProvider? clock = null, int days = 14, Action<string>? log = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Deletes what is too old; the number of files deleted. A file in use is left for the next sweep.</summary>
    public int Sweep()
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(days);
        var deleted = 0;
        foreach (var folder in folders.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*.log", SearchOption.AllDirectories).ToList(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;
                    File.Delete(file);
                    deleted++;
                }
                catch (IOException) { /* open for writing right now: next time */ }
                catch (UnauthorizedAccessException) { }
            }
        }
        if (deleted > 0) log?.Invoke($"logs: deleted {deleted} file(s) older than {days} days");
        return deleted;
    }

    /// <summary>A sweep now, then one a day until stopped.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { Sweep(); }
                catch (Exception e) when (e is not OperationCanceledException) { log?.Invoke($"logs: sweep failed: {e.GetType().Name}"); }
                await Task.Delay(TimeSpan.FromDays(1), _clock, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }
}
