using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WHETHER THE CURRENT DISPATCH'S START WAS RECORDED, as the Backlog says it. It matters because
/// only a dispatch with a recorded start ever has <c>landed</c> kept after its team is gone
/// (<see cref="BacklogLandedState"/>); without one, a person deleting the team loses the proof.
/// </summary>
/// <param name="Recorded">
/// True when every repository's start was recorded; false when one was not (<see cref="Detail"/>
/// says why); null when nothing was tried - no repository, contributor mode only, or dispatched
/// before starts were recorded.
/// </param>
/// <param name="Detail">The sentence a person reads when <see cref="Recorded"/> is false.</param>
/// <param name="Recordable">
/// Whether a person may still press Record where it started now: the team is here, and at least one
/// unrecorded start's retry has not stopped because the team committed.
/// </param>
public sealed record BacklogStart(bool? Recorded, string? Detail, bool Recordable)
{
    public static readonly BacklogStart None = new(null, null, false);

    /// <summary>The sentence, word for word; <c>{0}</c> is why.</summary>
    public const string NotRecordedSentence =
        "Where this dispatch started was not recorded ({0}); its landed state is read live and is not kept "
        + "after its team is gone.";

    /// <summary>
    /// From the dispatch's unrecorded starts and whether it has any recorded one. Every unrecorded
    /// repository is named when there are several.
    /// </summary>
    public static BacklogStart For(
        BacklogDispatch? dispatch,
        IReadOnlyList<BacklogDispatchMissedStart> unrecorded,
        bool hasStarts,
        TeamRegistry teams)
    {
        if (dispatch is null) return None;

        if (unrecorded.Count == 0) return hasStarts ? new BacklogStart(true, null, false) : None;

        var why = unrecorded.Count == 1
            ? unrecorded[0].Reason
            : string.Join("; ", unrecorded.Select(m => $"{m.Repo}: {m.Reason}"));

        return new BacklogStart(
            false,
            string.Format(System.Globalization.CultureInfo.InvariantCulture, NotRecordedSentence, why),
            teams.ExistingName(dispatch.TeamId) is not null && unrecorded.Any(m => m.StoppedAt is null));
    }
}
