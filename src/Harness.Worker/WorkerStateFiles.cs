using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// THE TWO FILES A WORKER KEEPS FOR WHAT RUNS IT, in its state folder (in the container, not on the
/// volume). <c>connected</c> exists exactly while control has this worker - written at control's
/// welcome, touched at every keep-alive it answers, deleted when the connection drops or the worker
/// stops - so the image's healthcheck, which asks for it fresh within a minute, reads "connected".
/// <c>drain</c> is the operator's: while it exists this worker takes no new run.
/// </summary>
public sealed class WorkerStateFiles(string folder, ILogger? log = null)
{
    public const string ConnectedName = "connected";

    public const string DrainName = "drain";

    public string Connected { get; } = Path.Combine(folder, ConnectedName);

    public string Drain { get; } = Path.Combine(folder, DrainName);

    /// <summary>Whether the operator asked this worker to drain.</summary>
    public bool Draining => File.Exists(Drain);

    /// <summary>The connection is up (true: the file is written or its time renewed) or down (false: it is gone).</summary>
    public void Alive(bool alive)
    {
        try
        {
            if (!alive)
            {
                File.Delete(Connected);
                return;
            }

            if (File.Exists(Connected)) File.SetLastWriteTimeUtc(Connected, DateTime.UtcNow);
            else File.WriteAllText(Connected, DateTimeOffset.UtcNow.ToString("O") + "\n");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.LogWarning("Could not keep the health file {File}: {Message}", Connected, exception.Message);
        }
    }
}
