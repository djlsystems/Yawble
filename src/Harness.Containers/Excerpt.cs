namespace Harness.Containers;

/// <summary>
/// Cutting agent output down to what may travel on the log.
///
/// The payload IS the ledger, and the ledger is what the next prompt is built from — so a bound here
/// is a bound on everything downstream. Without it one runaway agent puts an unbounded blob in the
/// log, in the ledger, and in the next invocation, and the log stops being cheap to read.
/// </summary>
public static class Excerpt
{
    /// <summary>
    /// The text to publish. A cut is always MARKED and names the full length: a trimmed answer must
    /// never be indistinguishable from a short one, and the marker says where the rest is.
    ///
    /// For FAILED runs, the last N characters are published: a process that fails says why at the
    /// END. The marker is placed at the TOP so the reason is visible.
    ///
    /// For SUCCESSFUL runs, the first N characters are published: the agent's summary and flow are
    /// at the beginning. The marker is placed at the BOTTOM.
    /// </summary>
    /// <param name="failed">
    /// Whether the run did not succeed, and therefore which END of the output is kept. REQUIRED and
    /// positional, never optional-with-a-default: a default of <c>false</c> means a new call site
    /// silently keeps the HEAD excerpt, which is the exact defect this parameter was added to fix -
    /// a failed run whose reason is in its last line, published without that line. "A value a
    /// caller can omit is the same defect wearing a parameter list" is a rule this codebase already
    /// earned on floor sequences and on team environments.
    /// </param>
    public static string Of(string output, int max, string transcript, bool failed)
    {
        if (max <= 0 || output.Length <= max) return output;

        if (failed)
        {
            // For failed runs: take the TAIL (last N chars) and put the marker at the TOP
            var start = output.Length - max;
            var tail = output.AsSpan(start);
            return $"… truncated: {output.Length} chars in total. Full transcript at {transcript}.\n{new string(tail)}";
        }
        else
        {
            // For successful runs: take the HEAD (first N chars) and put the marker at the BOTTOM
            return string.Concat(
                output.AsSpan(0, max),
                $"\n… truncated: {output.Length} chars in total. Full transcript at {transcript}.");
        }
    }
}
