namespace Harness.Contracts;

/// <summary>
/// THE TWO WORDS FOR "THIS BRANCH IS ALREADY ON MAIN", IN ONE PLACE FOR EVERY ASSEMBLY THAT SAYS
/// THEM.
///
/// <see cref="RepoStatus.TeamMergedToMainBy"/> is the contract - its <c>Description</c> is the spec
/// for all three values, including the null that means NOT MEASURED. THREE surfaces emit or read
/// these words: the status field, the <c>permittedBy</c> value on the delete-remote-branch audit
/// row, and the CLI's <c>merged-to-main yes (by content)</c> suffix. The first two live in
/// <c>RepoEndpoints.cs</c>; the third lives in another assembly, which is why the constants had to
/// leave the Host to cover it.
///
/// THE FAILURE THIS PREVENTS IS A VOCABULARY CHANGE, NOT A RENAME. If the word `content` is ever
/// changed, the host tests assert the emitted literal and redden, and get updated. A CLI comparing
/// its OWN literal would stay green - its fixture hand-writes the payload - and simply stop printing
/// the suffix in production. A person then reads `merged-to-main yes` about a branch whose shas were
/// rewritten, loses the one fact that says origin/team/... is not what is on main, and tidies it by
/// hand wrongly. Nothing anywhere would have failed.
///
/// THESE ARE WIRE VALUES. Changing one changes what the SPA parses and what every future audit row
/// says; the SPA spells its own copy in `web/src/api/types.ts`, which no C# test can compare against
/// a constant. Change the value here only as a deliberate contract change, on both sides.
///
/// NOT PROPERTIES ON <see cref="RepoStatus"/>, deliberately: that record is scraped property-by-
/// property against `types.ts` for wire parity, and a constant is not a field of the payload.
/// </summary>
public static class MergedToMainBy
{
    /// <summary>The team ref is reachable from origin/main - ordinary sha ancestry.</summary>
    public const string Ancestry = "ancestry";

    /// <summary>
    /// Ancestry says no, but every commit on the branch is patch-equivalent to one already upstream
    /// - what a branch whose commits were replayed onto a new base looks like. Measured from
    /// <see cref="RepoStatus.TeamCommitsNotOnMain"/>, never re-derived.
    ///
    /// SOME AUDIT ROWS SAY `patch-equivalence` FOR THIS SAME VERDICT, and they keep saying it: the
    /// log is append-only and what it recorded at the time is what it recorded.
    /// </summary>
    public const string Content = "content";
}
