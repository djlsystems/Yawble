using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// What one run says while it runs, to control: that its process started, its live view, its
/// progress, a child its memory limit stopped, and a diagnostics row. The worker turns each into a
/// run protocol event; a sentence or row is written by control, never here.
/// </summary>
public interface IRunSink
{
    /// <summary>The run's process exists. Said from inside the process-start callback, so it is synchronous.</summary>
    void Started(int processId);

    /// <summary>The run's live view, as it begins. Synchronous for the same reason as <see cref="Started"/>.</summary>
    void LiveViewBegun(string? transcript, string? format, string reason, bool finding);

    /// <summary>A transcript looked for after launch was found, or was not, with why.</summary>
    Task LiveViewFoundAsync(string? transcript, string? format, string reason);

    Task ProgressAsync(string sentence);

    /// <summary>The run's credential is applied and its child's environment final; the child starts next.</summary>
    Task CredentialAppliedAsync(ValueRedactor redaction);

    Task ChildStoppedAsync(string sentence, RunMemoryLimit limit);

    Task DiagnosticAsync(
        DiagnosticSeverity severity, string kind, string source, string? message = null, string? detail = null,
        string? exceptionType = null);

    /// <summary>The redacted end of what the run's child wrote on stderr.</summary>
    void Stderr(string tail);

    /// <summary>One line a program run printed. The next is not read until control has handled it.</summary>
    Task OutputLineAsync(string line);
}

/// <summary>
/// One run's live view on the worker: its transcript once it is known, or why there is none. Control
/// keeps its own copy for the live route from what this tells the run's <see cref="IRunSink"/>.
/// </summary>
public sealed class RunLive : IDisposable
{
    private readonly IRunSink _sink;
    private readonly TaskCompletionSource<string?> _located = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private RunLive(IRunSink sink, string? transcript, string? format, string reason, bool finding)
    {
        _sink = sink;
        Transcript = transcript;
        Format = format;
        Reason = reason;
        Finding = finding;
    }

    /// <summary>
    /// A run's live view, begun when its child starts. A view whose transcript is looked for after
    /// launch passes <paramref name="finding"/> true and a null <paramref name="transcript"/>.
    /// </summary>
    public static RunLive Begin(IRunSink sink, string? transcript, string? format, string reason, bool finding = false)
    {
        var run = new RunLive(sink, transcript, format, reason, finding);
        sink.LiveViewBegun(transcript, format, reason, finding);
        if (!finding) run._located.TrySetResult(transcript);
        return run;
    }

    public string? Transcript { get; private set; }

    public string? Format { get; }

    public string Reason { get; private set; }

    public bool Finding { get; private set; }

    /// <summary>The transcript once it is known, or null when there is none.</summary>
    public Task<string?> Located => _located.Task;

    /// <summary>
    /// The search is over. Control hears it before anything waiting on <see cref="Located"/> goes
    /// on, so the run's end can never reach control ahead of it.
    /// </summary>
    public async Task FoundAsync(string? transcript, string reason = "")
    {
        Transcript = transcript;
        if (transcript is null) Reason = reason;
        Finding = false;

        try
        {
            await _sink.LiveViewFoundAsync(transcript, Format, Reason);
        }
        finally
        {
            _located.TrySetResult(transcript);
        }
    }

    public void Dispose()
    {
        if (Finding && Transcript is null)
        {
            Reason = "The run ended before its agent's session transcript was found.";
            Finding = false;
        }

        _located.TrySetResult(Transcript);
    }
}
