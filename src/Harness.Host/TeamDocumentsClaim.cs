namespace Harness.Host;

/// <summary>What claiming a documents folder for a new team did, so creation can say it rather than
/// guess. <paramref name="Retired"/> is the folder a PREDECESSOR of this team id left behind, moved
/// aside so the new team starts empty - null when there was no collision, which is the usual
/// case.</summary>
public sealed record DocumentsClaim(string Folder, string? Retired);

/// <summary>
/// The documents folder for this team id could not be claimed, so the team was not created. Either
/// it is not a folder the platform made, or a predecessor's could not be moved aside.
/// </summary>
public sealed class DocumentsClaimException(string message) : Exception(message);

/// <summary>
/// Gives a team being CREATED a documents folder of its own, and says what it displaced.
///
/// Team ids are REUSABLE - a team deleted and recreated under the same name has the same id - and
/// documents OUTLIVE their team, so one tenant root holding folders named for teams means a
/// recreated team meets its predecessor's documents. The same hazard applies to
/// <c>pending_deliveries</c> rows, which would be inherited by the next container of that name.
///
/// **RETIRED, NOT REFUSED.** A documents folder outliving its team is the DESIGNED, ORDINARY
/// outcome of every deletion, since a team is deleted as soon as its work is merged. Refusing here
/// would make "recreate the team and run it again" permanently impossible after a team's first
/// deletion, with an operator poking at the filesystem as the only cure. A successor must not
/// SILENTLY inherit - but creation must not stop.
///
/// So the predecessor's folder is RELABELLED to <see cref="TeamPaths.RetiredNameFor"/>, which contains
/// a dot and therefore can never be any team's folder again under any spelling - the documents are
/// re-keyed onto something that is NOT reusable, at the moment of collision rather than for every
/// team forever. Doing it only here is what keeps the
/// ordinary folder name readable: a person browsing the tenant documents root sees team identifiers,
/// not opaque keys, and a dead team's folder still says whose it was.
///
/// **NOTHING IS DELETED, MERGED OR INHERITED**, and the collision is REPORTED rather than
/// discovered.
///
/// A STATIC OVER <c>TeamPaths</c> rather than a method on <c>TeamDocuments</c>, because its one
/// caller is <c>TeamRegistry.CreateAsync</c> and <c>TeamRegistry</c> already holds the composer.
/// Widening that constructor for one operation is the shape <c>TeamDeletion</c>'s own summary
/// refuses, and it would reach all fourteen places that build a registry.
/// </summary>
public static class TeamDocumentsClaim
{
    /// <summary>
    /// FOUR STATES, and each is decided rather than defaulted:
    /// <list type="bullet">
    /// <item><b>No folder</b> - create it, with its marker. The ordinary case.</item>
    /// <item><b>A folder holding nothing but the marker</b> - ADOPT it. There is nothing to
    /// inherit, so retiring it would manufacture an empty retired folder after every bare repair -
    /// <c>TeamDocuments.EnsureFor</c> creates exactly this shape.</item>
    /// <item><b>Content AND a marker</b> - a predecessor's. Retire it and start the new team
    /// empty.</item>
    /// <item><b>Content and NO marker</b> - not the platform's, so nothing here touches it. Refused,
    /// naming the folder, exactly as <c>TeamDeletion</c> refuses a root carrying no marker: the
    /// tenant documents root is a folder a person can reach, and moving somebody's directory on the
    /// strength of a name is not a risk worth taking against their only copy.</item>
    /// </list>
    ///
    /// **A BLOCKED RETIRE FAILS CLOSED**, unlike the layout migration's blocked move, and the two
    /// are not in tension: a blocked MIGRATION leaves a team's documents where they have always been
    /// and serving from there costs nobody anything, while a blocked RETIRE would leave the new team
    /// pointed at its predecessor's documents - the exact exposure this exists to prevent. Creation
    /// is retryable and nothing has been written for this team yet when this runs.
    /// </summary>
    public static async Task<DocumentsClaim> ForAsync(
        TeamPaths paths, string team, CancellationToken ct = default)
    {
        var folder = Path.GetFullPath(paths.DocsFor(team));

        if (!Directory.Exists(folder) || !HoldsAnythingButTheMarker(folder))
        {
            TeamPaths.EnsureDocumentsFolder(folder, team);

            return new DocumentsClaim(folder, null);
        }

        if (!File.Exists(TeamPaths.MarkerIn(folder)))
        {
            throw new DocumentsClaimException(
                $"'{folder}' already holds files and carries no {TeamPaths.MarkerFileName} marker, "
                + "so it is not a folder this platform created. Move it aside, or choose another "
                + "team name.");
        }

        var retired = RetiredPathFor(folder);

        try
        {
            // A same-parent RENAME, so it is same-volume by construction and moves the whole tree or
            // nothing. A copy-then-delete over somebody's only copy of their documents is not a
            // trade this makes.
            Directory.Move(folder, retired);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DocumentsClaimException(
                $"'{folder}' holds the documents of an earlier team called '{team}' and could not "
                + $"be moved aside ({exception.Message}). The new team was NOT created, rather than "
                + "be handed them. Close anything holding that folder and try again.");
        }

        TeamPaths.EnsureDocumentsFolder(folder, team);

        // The retired folder keeps a marker naming the team it belonged to and the fact that it was
        // retired, so a person finding it on disk knows what it is - the same courtesy the team-root
        // marker pays. Best effort: the documents are already safely aside.
        try
        {
            await File.WriteAllTextAsync(
                TeamPaths.MarkerIn(retired), $"{team}\n{DateTimeOffset.UtcNow:O}\nretired\n", ct);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return new DocumentsClaim(folder, retired);
    }

    /// <summary>A retired name nothing already answers to. Two retirements inside one second is not
    /// a state to lose a folder over, so the collision is counted rather than assumed away.</summary>
    private static string RetiredPathFor(string folder)
    {
        var parent = Path.GetDirectoryName(folder)!;
        var stem = TeamPaths.RetiredNameFor(Path.GetFileName(folder), DateTimeOffset.UtcNow);
        var candidate = Path.Combine(parent, stem);

        for (var attempt = 2; Directory.Exists(candidate) || File.Exists(candidate); attempt++)
        {
            candidate = Path.Combine(parent, $"{stem}{attempt}");
        }

        return candidate;
    }

    /// <summary>Whether a folder holds anything a team would miss. The marker alone is the
    /// platform's own bookkeeping and is not content - an empty documents folder that had been
    /// repaired once would otherwise read as a predecessor's work.</summary>
    private static bool HoldsAnythingButTheMarker(string folder)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folder).Any(entry =>
                !string.Equals(
                    Path.GetFileName(entry),
                    TeamPaths.MarkerFileName,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // UNREADABLE READS AS OCCUPIED. A folder this cannot enumerate is one it cannot prove is
            // empty, and the failure that matters here is handing a successor a predecessor's
            // documents - so it fails in the direction that never does. The refusal above then names
            // it, because an unreadable folder's marker is unreadable too.
            return true;
        }
    }
}
