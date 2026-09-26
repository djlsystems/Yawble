using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// A person's Concierge workspace, and WHETHER IT WAS ALREADY THEIRS before we asked.
///
/// THE SECOND FIELD IS WHY THIS TYPE EXISTS, and why <see cref="ConciergeWorkspaces.Resolve"/>
/// does not simply return a path. "Is this a relaunch?" cannot be answered by anyone standing
/// outside the resolver, because resolving CREATES the directory: a caller that asks afterwards is
/// told yes every time, and a brand-new Concierge is asked to continue a conversation that has
/// never happened. Answering it here binds the question to the one act that knows - the same call
/// that either found the folder or made it - so there is no window between the two and no second
/// way to compose the path and get a different answer.
/// </summary>
/// <param name="Path">The directory. It exists by the time this record does.</param>
/// <param name="Existed">
/// True when this person's workspace was ALREADY THERE and is being handed back - so the next
/// launch into it is a RELAUNCH, which the launch does not treat differently.
///
/// True for a workspace found by its marker, INCLUDING one whose login has since been relabelled: the
/// folder still carries a month of context and the agent still keeps history against that path, and
/// a relabelled login reading as a first launch is the precise defect a name-composed probe would have.
///
/// False when this call has just created an empty directory. There is nothing behind that door.
/// </param>
public sealed record ConciergeWorkspace(string Path, bool Existed);

/// <summary>
/// WHICH DIRECTORY IS THIS PERSON'S CONCIERGE WORKSPACE - the one place that answers it.
///
/// The resolution order is the whole design, and the order is what makes a login rename safe:
///
/// <list type="number">
/// <item><b>The marker decides.</b> Every folder under the workspaces root is asked whose it is;
/// the one claiming this user id IS this user's workspace, whatever it happens to be called. A
/// person who renames their login keeps the folder they have had for a month, under its old
/// name, with its contents where they were.</item>
/// <item><b>Only then is a new name composed</b> from the login, taking the first of
/// <see cref="ConciergeWorkspaceName.CandidatesFor"/> that no OTHER user's marker claims.</item>
/// </list>
///
/// **IT ALSO ANSWERS "WAS IT ALREADY THERE", AND THAT IS NOT A CONVENIENCE.** Each of the two
/// steps above knows the answer as a by-product of taking its own branch, and NOTHING OUTSIDE CAN
/// WORK IT OUT AFTERWARDS - step 2 creates the directory, so a caller that resolves and then asks
/// is asking about its own footprint. <see cref="ConciergeWorkspace.Existed"/> is the bit, returned
/// from the act that settles it; the launch passes a preset's resume arguments on the strength of it.
///
/// **A FOLDER IS NEVER ADOPTED ON THE STRENGTH OF ITS NAME.** A marker naming somebody else is a
/// hard refusal and so is NO marker at all - the second is the quieter of the two and matters
/// more, because an unmarked folder at exactly the right name is what a person's own `mkdir` or a
/// half-finished restore look like. Refusing costs that person a suffix on their folder name; adopting costs them somebody else's files.
///
/// **THE DIRECTORY IS NOT RELABELLED WHEN A LOGIN IS.** Step 1 keeps serving the old name, which is
/// stale as a label and is the deliberate trade: renaming a live working directory moves it out
/// from under a running agent and discards that agent's own per-project history.
/// The old name is not "left behind to be claimed" either - it is still marked, so the next person
/// to take the freed login is refused it at step 2 and gets the suffixed name.
/// </summary>
public static class ConciergeWorkspaces
{
    /// <summary>
    /// The workspace this person already has, or null. Does not create one: creating here would
    /// make the next Concierge launch look like a relaunch of a conversation that never happened.
    /// </summary>
    public static string? TryExisting(TeamPaths paths, string userId)
    {
        var root = paths.ConciergeWorkspacesRoot;
        if (!Directory.Exists(root)) return null;

        return Claimed(root, userId);
    }

    /// <summary>
    /// This person's workspace, created and marked if it did not exist. The directory exists when
    /// this returns, and the result says whether it did BEFORE - see
    /// <see cref="ConciergeWorkspace.Existed"/> for why that bit is returned from here and cannot
    /// be asked anywhere else.
    /// </summary>
    /// <exception cref="InvalidOperationException">Every candidate name is held by somebody else.
    /// Deliberately loud: the alternative is inventing a name outside the deterministic list, and
    /// a name that cannot be recomputed is the counter this design refuses.</exception>
    public static ConciergeWorkspace Resolve(TeamPaths paths, string userId, string login)
    {
        var root = paths.ConciergeWorkspacesRoot;

        Directory.CreateDirectory(root);

        // 1. THE MARKER DECIDES. Before the name is even composed, because a relabelled login must
        //    not compose its way into a second folder.
        //
        //    ALREADY THEIRS, so a relaunch - and this is the branch a relabelled login takes. The
        //    folder is under its old name and nothing about that makes it new.
        if (Claimed(root, userId) is { } mine)
        {
            return new ConciergeWorkspace(mine, Existed: true);
        }

        var candidates = ConciergeWorkspaceName.CandidatesFor(login, userId);

        // 2. COMPOSE. First candidate nobody else holds.
        foreach (var candidate in candidates)
        {
            var folder = paths.ConciergeWorkspaceFor(candidate);

            if (!Directory.Exists(folder))
            {
                TeamPaths.EnsureConciergeWorkspace(folder, userId);

                // THE ONLY FIRST LAUNCH THERE IS. This line made the directory a statement ago,
                // so there is nothing in it to resume and nothing that could have been.
                return new ConciergeWorkspace(folder, Existed: false);
            }

            // Step 1 scanned for this already; re-reading costs one stat and covers the folder
            // that appeared between the scan and here. It is OURS and it is not new.
            if (TeamPaths.ConciergeOwnerOf(folder) == userId)
            {
                return new ConciergeWorkspace(folder, Existed: true);
            }

            // Somebody else's, or nobody's. Either way not ours - see this type's own summary for
            // why the unmarked case is refused rather than taken.
        }

        throw new InvalidOperationException(
            $"No Concierge workspace name is available for '{login}': "
            + $"{string.Join(", ", candidates)} are all held by another account or by a folder "
            + $"with no ownership marker. Move or remove the folder under {root} that is not a "
            + "Concierge workspace.");
    }

    /// <summary>
    /// The folder under <paramref name="root"/> whose marker names this user, or null.
    ///
    /// A SCAN rather than a lookup, because the folder's name is not the key - that is the whole
    /// point of the marker, and it is what lets a relabelled login find its own workspace. It reads
    /// one small file per top-level directory, once per console open, on a directory with one
    /// entry per person who has ever opened a Concierge.
    /// </summary>
    private static string? Claimed(string root, string userId)
    {
        IEnumerable<string> folders;

        try
        {
            folders = Directory.EnumerateDirectories(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var folder in folders)
        {
            if (TeamPaths.ConciergeOwnerOf(folder) == userId) return folder;
        }

        return null;
    }
}
