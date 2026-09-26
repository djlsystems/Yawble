namespace Harness.Pty;

/// <summary>
/// Spawns a child attached to a pseudo-terminal. The implementation lives in Harness.Pty, which
/// carries the Porta.Pty package; this project stays interfaces plus two records so that anything
/// wanting the contract - including Harness.Core, which must stay package-free - can reference it
/// without taking a package dependency.
/// </summary>
public interface IPtyEngine
{
    Task<IPtySession> SpawnAsync(PtySpec spec, CancellationToken ct);
}

/// <summary>
/// A live pseudo-terminal. Output is raw bytes, never decoded here: a UTF-8 sequence can straddle
/// a read boundary, and xterm.js is what reassembles it.
/// </summary>
public interface IPtySession : IAsyncDisposable
{
    event Action<byte[]> Output;
    event Action<int> Exited;
    void Write(ReadOnlySpan<byte> bytes);
    void Resize(int cols, int rows);
}

/// <summary>
/// The size a child is spawned at when the caller has not measured one. Named constants rather than
/// literals in the record header because the connect path needs the same fallback: a client that
/// omits its geometry must land on the value the spec would have used, not a second guess.
/// </summary>
public static class PtySpecDefaults
{
    public const int Cols = 120;
    public const int Rows = 30;
}

public sealed record PtySpec(
    string CommandLine,
    string StartingFolder,
    int Cols = PtySpecDefaults.Cols,
    int Rows = PtySpecDefaults.Rows,
    IReadOnlyDictionary<string, string>? Env = null,

    /// <summary>
    /// Pre-tokenized argv. When present it is used VERBATIM and <see cref="CommandLine"/> is
    /// ignored for launching.
    ///
    /// This exists so an expanded template is never re-split. TemplateExpander substitutes strictly
    /// inside an already-tokenized element precisely so a quote or a space inside a system prompt
    /// cannot become an argument boundary; joining that back into a string here would undo it.
    /// </summary>
    IReadOnlyList<string>? Argv = null,

    /// <summary>
    /// Files written for this spawn, deleted when the SESSION retires rather than after the spawn.
    /// A headless run lasts seconds and cleans up in a finally; a PTY lasts hours, so deleting on
    /// spawn would yank the file before the CLI read it and never deleting leaks one per session.
    /// </summary>
    IReadOnlyList<string>? TempFiles = null,

    /// <summary>Inherited variables to clear before applying this child's explicit environment.</summary>
    IReadOnlyList<string>? ClearEnvironment = null);
