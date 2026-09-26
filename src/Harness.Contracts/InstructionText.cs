namespace Harness.Contracts;

/// <summary>
/// The two halves of an instruction as a person reads it: a SUBJECT short enough to be a card's
/// title, and a BODY carrying everything else.
/// </summary>
/// <remarks>
/// Both are always a string and never null, so no reader needs a null check for a field that is
/// merely empty.
/// </remarks>
public readonly record struct InstructionParts(string Subject, string Body);

/// <summary>
/// THE FALLBACK FOR TEXT THAT ARRIVED WITHOUT A SUBJECT. It is not the intended path.
///
/// In the normal flow an AGENT writes the subject: a Manager dispatching work runs
/// <c>harness tell &lt;Member&gt; "&lt;instruction&gt;" --subject "&lt;line&gt;"</c> and says what the card
/// should be called, because it knows what the job IS and a mechanical cut does not. That subject
/// arrives here as the <c>subject</c> argument and wins the title outright - the cut below only
/// ever decides the BODY on that path, by dropping the line when the text says the subject again.
///
/// The split exists for the text that has no subject to honour, which is a real and permanent set:
/// every row that carries no subject (the log is append-only and REPLAYED), a person typing
/// free-form into the console, <c>TriggerSweep</c>'s scheduled wakes, and the repositories
/// instruction in <c>Teams.cs</c>. For those it produces something readable rather than a title
/// that stops in the middle of a word. IT IS A CONSOLATION PRIZE, and the next reader should not
/// take it for the design.
///
/// ONE place either way, so every reader agrees. It lives in Contracts because the tell route
/// writes the split, the Kanban projection reads it back, and a projection replays a log that
/// outlives both: a second copy of this rule in the projector would let a card's title stop
/// somewhere the stored subject did not, and the disagreement would look like the board lying
/// about the row.
///
/// TRUNCATION APPENDS NOTHING. The clamp this replaces put an ellipsis on a 100-character title
/// because there was nowhere else for the rest to go. There is a body now, so the subject is a CUT
/// and the body carries the remainder - and a reader who sees no mark has no reason to wonder
/// whether text was lost.
///
/// Pure: no I/O, no clock, no culture-sensitive comparison.
/// </summary>
public static class InstructionText
{
    /// <summary>
    /// The longest a subject may be. A card's title is one line at a glance; 80 is where a line
    /// stops being one.
    /// </summary>
    public const int MaxSubject = 80;

    /// <summary>
    /// How far back a word-boundary cut may reach before it is abandoned.
    ///
    /// A long unbroken token - a URL, a sha, a stack frame - can leave the only space near the
    /// start, and cutting there gives a five-character title over a body holding everything. Below
    /// this the cut is hard at <see cref="MaxSubject"/> instead: a subject that breaks a word is
    /// worse than nothing only when it is also too short to read.
    /// </summary>
    private const int MinWordBoundary = MaxSubject / 2;

    /// <summary>
    /// Split <paramref name="text"/>, with the subject its author wrote when there is one.
    ///
    /// AN EXPLICIT SUBJECT WINS AND IS THE EXPECTED CASE - the caller knew what the work was. What
    /// it does to the BODY depends on whether the text OPENS with that same line, and the two
    /// answers are different because the two shapes mean different things:
    ///
    /// - THE TEXT REPEATS THE SUBJECT. An agent passes <c>--subject "&lt;line&gt;"</c> and then writes
    ///   that line at the top of the instruction, because that is how anybody writes a titled
    ///   message. The line is dropped, exactly as the derived path drops it, or the card renders
    ///   its own title again immediately beneath itself - which is the one thing "the whole body
    ///   minus the title" is asking to avoid. THIS IS THE COMMON SHAPE, not a corner case.
    /// - THE TEXT DOES NOT. Then the subject really is a separate LABEL over an instruction that
    ///   begins somewhere else, and the whole text stays: removing a sentence because a title
    ///   happens to exist would delete prose nothing else holds.
    ///
    /// The comparison is on the first non-empty line, TRIMMED and case-insensitively - surrounding
    /// space and a different case are the same line to the person reading the card, and ordinal
    /// so no culture decides it. A subject the author spelled differently still wins the TITLE:
    /// they typed that spelling deliberately.
    ///
    /// Only text with no subject falls through to the cut, where the first line - or the first
    /// <see cref="MaxSubject"/> characters of it - becomes the subject and the rest becomes the
    /// body, which is therefore never a copy of the subject.
    ///
    /// Empty in, empty out. What an ABSENT subject renders as is a display decision and belongs to
    /// the caller: the projection writes "Untitled", and this function has no business knowing that
    /// word.
    /// </summary>
    public static InstructionParts Split(string? text, string? subject = null)
    {
        var body = Normalise(text);

        // Whitespace is nobody having typed one. A subject arrives from a CLI flag and from a JSON
        // field, and both spell "the user left it out" as a blank string at least as often as null.
        if (!string.IsNullOrWhiteSpace(subject))
        {
            var wanted = Normalise(subject);

            // `HeadOf(body).Body` AND NOT "the text after the first newline", because a repeated
            // line longer than the cap must keep its TAIL - the same arithmetic the derived path
            // does, from the same function, so the two paths cannot drift apart.
            return new InstructionParts(
                HeadOf(wanted).Subject,
                Opens(body, wanted) ? HeadOf(body).Body : body);
        }

        return body.Length == 0 ? new InstructionParts("", "") : HeadOf(body);
    }

    /// <summary>
    /// Whether <paramref name="text"/>'s first line is <paramref name="subject"/> said again.
    ///
    /// `text` has been through <see cref="Normalise"/>, so it opens with a line that holds
    /// something whenever it holds anything at all - there are no blank lines in front of it to
    /// skip past.
    ///
    /// A MULTI-LINE SUBJECT NEVER MATCHES, and that is the safe direction: nobody types one, and
    /// answering "no" leaves the body whole rather than cutting a line on a guess.
    /// </summary>
    private static bool Opens(string text, string subject)
    {
        if (text.Length == 0 || subject.Length == 0 || subject.Contains('\n'))
        {
            return false;
        }

        var newline = text.IndexOf('\n');
        var firstLine = newline < 0 ? text : text[..newline];

        return firstLine.Trim().Equals(subject, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The first line, capped, and everything after it.
    /// </summary>
    private static InstructionParts HeadOf(string text)
    {
        if (text.Length == 0)
        {
            return new InstructionParts("", "");
        }

        var newline = text.IndexOf('\n');
        var firstLine = (newline < 0 ? text : text[..newline]).TrimEnd();
        var rest = newline < 0 ? "" : text[(newline + 1)..];

        if (firstLine.Length <= MaxSubject)
        {
            return new InstructionParts(firstLine, TrimLeadingBlankLines(rest));
        }

        // The first line does not fit. What did not fit is still the instruction, so it goes to the
        // FRONT of the body rather than over the side.
        var cut = CutPoint(firstLine);
        var tail = firstLine[cut..].TrimStart();

        // `remainder` AND NEVER `rest`, on every branch below. Composing from the raw text would keep
        // blank lines after an over-long first line where the other two paths drop them - three
        // paths and two answers, which is a rule nobody can state.
        var remainder = TrimLeadingBlankLines(rest);

        var body = tail.Length == 0
            ? remainder
            : remainder.Length == 0 ? tail : tail + "\n" + remainder;

        return new InstructionParts(firstLine[..cut].TrimEnd(), body);
    }

    /// <summary>
    /// Where to cut a first line that is longer than <see cref="MaxSubject"/>.
    ///
    /// Scanned BACKWARDS from one past the cap, so a line whose 81st character is a space cuts to
    /// exactly the cap on a whole word rather than losing one to an earlier space.
    /// </summary>
    private static int CutPoint(string firstLine)
    {
        for (var i = MaxSubject; i >= MinWordBoundary; i--)
        {
            if (char.IsWhiteSpace(firstLine[i]))
            {
                return i;
            }
        }

        return MaxSubject;
    }

    /// <summary>
    /// Line endings folded to <c>\n</c> and the ends trimmed.
    ///
    /// A payload authored on Windows carries <c>\r\n</c>, and a carriage return that survives into
    /// a body renders as a blank line between every line in a `pre-wrap` block - so it is removed
    /// once, here, rather than in each renderer.
    /// </summary>
    private static string Normalise(string? text) =>
        (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

    /// <summary>
    /// Drops leading lines that hold nothing but whitespace, keeping the indentation of the first
    /// line that holds something.
    ///
    /// The blank line separating a title from its body has done its job once the title is lifted
    /// out. Indentation has NOT: an instruction's list or command example is shaped on purpose, and
    /// a blanket TrimStart would flatten it.
    /// </summary>
    private static string TrimLeadingBlankLines(string text)
    {
        var at = 0;

        while (at < text.Length)
        {
            var end = text.IndexOf('\n', at);
            var line = end < 0 ? text[at..] : text[at..end];

            if (line.Trim().Length != 0)
            {
                break;
            }

            if (end < 0)
            {
                return "";
            }

            at = end + 1;
        }

        return text[at..].TrimEnd();
    }
}
