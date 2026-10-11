using System.Text;
using Harness.Contracts;

namespace Harness.Containers;

/// <summary>
/// Renders a container's ledger into the prose its agent reads.
///
/// Reuses <see cref="MessageText"/>, which already turns a message into a sentence worth reacting
/// to. An agent must never be handed the envelope — that holds for what it is reminded of as much as
/// for the message that woke it.
///
/// ONE THREAD, NOT A MEMBER'S WHOLE MEMORY. A member holds several workflows at once and keeps them
/// apart: woken for B it reads B's history, not everything it has ever done. `MaxEntries` is
/// therefore per THREAD, because a shared window would let a chatty workflow push a quieter one out
/// of a member's memory entirely.
/// </summary>
public sealed class LedgerContextBuilder(ILedger ledger) : IContextBuilder
{
    /// <summary>
    /// How many entries to consider before the character budget does the real trimming. A bound on
    /// the query as well as the render, so a container with a year of history does not read a year
    /// of rows in order to throw nearly all of them away.
    /// </summary>
    private const int MaxEntries = 200;

    private const string Header = "Earlier in your work, oldest first:";

    public async Task<string> BuildAsync(
        ContainerId container,
        Message waking,
        ArtifactLimits limits,
        long sinceSeq,
        CancellationToken ct = default)
    {
        // THE WAKING MESSAGE'S OWN WORKFLOW, and it is read off `waking` rather than passed in.
        //
        // `IContextBuilder.BuildAsync` deliberately gained no correlation parameter: the message
        // already carries one, and a parameter beside it would be a second spelling of one fact -
        // two answers waiting to disagree, and a caller free to pass the wrong one.
        //
        // The prefix each thread produces is SHORTER and still monotonic, so prompt caching still
        // matches and every invocation is cheaper than today's mixed stream.
        var entries = await ledger.ReadAsync(
            container, sinceSeq, waking.CorrelationId, waking.Seq, MaxEntries, ct);

        // `started` carries nothing `completed` does not, and including it would double what the
        // agent reads for no added meaning.
        var rendered = entries
            .Where(e => !string.Equals(e.Type, MessageTypes.Started, StringComparison.Ordinal))
            .Select(Render)
            .ToArray();

        if (rendered.Length == 0) return string.Empty;

        // Filled from the NEWEST backwards, because the recent exchanges are the relevant ones, then
        // reversed so it reads in order.
        var kept = new List<string>();
        var used = Header.Length;

        for (var i = rendered.Length - 1; i >= 0; i--)
        {
            var cost = rendered[i].Length + 1;
            if (used + cost > limits.ContextChars) break;

            kept.Add(rendered[i]);
            used += cost;
        }

        if (kept.Count == 0) return string.Empty;

        kept.Reverse();

        var text = new StringBuilder(Header);

        // Never silent. A manager that has forgotten looks exactly like one that never knew, and
        // nothing downstream can tell the two apart afterwards.
        var dropped = rendered.Length - kept.Count;
        if (dropped > 0) text.Append($" ({dropped} earlier entries dropped to fit)");

        foreach (var line in kept) text.Append('\n').Append(line);

        return text.ToString();
    }

    private static string Render(Message entry) =>
        entry.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal)
            ? $"You were asked: {MessageText.Of(entry)}"
            : MessageText.Of(entry);
}
