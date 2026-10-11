namespace Harness.Pty;

/// <summary>
/// The one shared bound on a PTY dimension (cols or rows) before it reaches an OS call --
/// ResizePseudoConsole (Windows SHORT) or TIOCSWINSZ (Linux unsigned short) -- the same standard
/// this codebase holds everywhere else a request value reaches a syscall. An oversized value is
/// clamped down rather than rejected: a client that asked for 4000 almost certainly meant "very
/// wide" and gets the closest sane answer instead of a hard failure over a terminal size.
///
/// Shared by both places a dimension can reach that syscall: ApiHost's /pty and
/// /registration/pty connect routes (via <see cref="ClampOrDefault"/>) and PtyWebSocket's resize
/// control frame (via <see cref="Clamp"/>). Both must clamp: otherwise a resize frame sent after
/// connect could ask for an unbounded size the connect path would have refused.
/// </summary>
public static class PtyDimensionLimits
{
    public const int Max = 1000;

    /// <summary>
    /// Resolves a caller-supplied dimension for the CONNECT path: absent or non-positive (an
    /// omitted param, "cols=0", "cols=-5", a client that predates this parameter) falls back to
    /// the caller's own default rather than an unmeasured guess.
    /// </summary>
    public static int ClampOrDefault(int? value, int fallback) =>
        value switch
        {
            null or <= 0 => fallback,
            > Max => Max,
            _ => value.Value,
        };

    /// <summary>
    /// Clamps an already-known-positive dimension for a later RESIZE control frame, which carries
    /// no fallback of its own -- PtyWebSocket.Apply only reaches this once Cols/Rows have already
    /// matched its own <c>&gt; 0</c> pattern.
    /// </summary>
    public static int Clamp(int value) => Math.Min(value, Max);
}
