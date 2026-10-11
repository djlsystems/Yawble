using System.Text.Json;

namespace Harness.Contracts;

/// <summary>
/// ONE MEMBER'S LIVE MARKS, as rows rather than as sentences.
///
/// <para>
/// The rows, not extracted text, and that is deliberate: the payload field a mark's words live in
/// differs by type (`reason`, `question`, `launchError`) and deciding which to read is a RENDERING
/// question. `summarise.ts` answers it for the browser, `MessageText.cs` for agents, and both order
/// those fields in a shared chain precisely because the same ordering defect has been found in three
/// separate renderers that each wrote their own. A fourth private chain inside the message store
/// would be a fourth place to get it wrong - and the store would have to know about payload shapes
/// it otherwise never opens.
/// </para>
///
/// <para>
/// ALL THREE CAN BE SET AT ONCE AND NOTHING HERE RANKS THEM. A member can give up, then ask, then
/// have its run fail. The SPA's `containerMark()` and the workflow tile's ladder both order these
/// `failed › blocked › needs-decision`, in two places that are documented as having to agree; a
/// pre-ranked single answer here would be a third ordering, in a third place, disagreeing silently
/// with both.
/// </para>
/// </summary>
/// <param name="Blocked">
/// The member's live <c>container.blocked</c> - the agent's own decision to stop - or null. Live
/// means no <c>container.started</c> from this member since it, which is the only test that works:
/// the platform publishes `completed` for the run the agent gave up inside, seconds later, every
/// time.
/// </param>
/// <param name="NeedsDecision">
/// The member's live <c>container.needs-decision</c> - the agent asking a person - or null. The same
/// predicate as <paramref name="Blocked"/>, and both can be set: giving up and asking are different
/// acts and a member that did both did both.
/// </param>
/// <param name="Failed">
/// The member's last run outcome when it was <c>container.failed</c> - the PLATFORM reporting a run
/// that did not complete - or null. A different predicate: an outcome is an alternative to
/// `completed`, so the member's LAST of the two decides, and a run that failed and has since
/// succeeded is not failed.
/// </param>
public sealed record ContainerMarks(Message? Blocked, Message? NeedsDecision, Message? Failed)
{
    /// <summary>Nothing to say - the ordinary answer for a member that has only ever succeeded.</summary>
    public static readonly ContainerMarks None = new(null, null, null);

    /// <summary>Whether this member carries any mark at all.</summary>
    public bool Any => Blocked is not null || NeedsDecision is not null || Failed is not null;

    /// <summary>The words on the live <c>blocked</c> row, or null.</summary>
    public string? BlockedReason => TextOf(Blocked, PayloadFields.Reason, PayloadFields.Output);

    /// <summary>The words on the live <c>needs-decision</c> row, or null.</summary>
    public string? Question => TextOf(NeedsDecision, PayloadFields.Question, PayloadFields.Reason, PayloadFields.Output);

    /// <summary>The words on the live <c>failed</c> row, or null.</summary>
    public string? FailureReason => TextOf(Failed, PayloadFields.LaunchError, PayloadFields.Output);

    /// <summary>
    /// THE FIELD ORDERS ARE `MessageText`'S, AND THEY MUST STAY ITS.
    ///
    /// <para>
    /// `AGENTS.md` names this exact hazard - "the same ordering defect has been found in three
    /// separate renderers, each of which had written its own, and a fourth private chain is a
    /// fourth place to get it wrong" - so this reads through the SHARED
    /// <see cref="FailurePayloadText.FirstNonEmpty"/> helper with the same field lists
    /// `MessageText.Of` passes it, rather than reaching into the payload itself. `failed` reads
    /// `launchError` FIRST because a failed run carries `output: ""` and the reason lives there;
    /// getting that one backwards is what made every runner-level failure render as a blank row.
    /// </para>
    /// </summary>
    private static string? TextOf(Message? message, params string[] orderedFields)
    {
        if (message is null) return null;

        try
        {
            return FailurePayloadText.FirstNonEmpty(
                JsonDocument.Parse(message.Payload).RootElement, orderedFields);
        }
        catch (JsonException)
        {
            // THE LOG IS APPEND-ONLY, so a row written by an older build - or by hand - cannot be
            // repaired. A mark whose words cannot be read is still a mark: the caller renders the
            // fact without the sentence, which is strictly better than a container that refuses to
            // come back because one historical payload will not parse.
            return null;
        }
    }
}
