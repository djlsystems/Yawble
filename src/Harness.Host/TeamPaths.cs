using System.Collections.Concurrent;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// Where a team's files are. THE ONLY THING THAT COMPOSES A TEAM PATH, and the only reader of
/// `teams.root`.
///
/// That exclusivity is the whole point of the type rather than a nicety. Many sites need a team
/// path, and letting each build one from `dataRoot` gives each of them a lookup of its own: one
/// writer, and readers that simply never ask. A rule enforced by every caller remembering to
/// consult something is ABSENT wherever nobody remembered.
///
/// The layout is `&lt;root ?? dataRoot&gt;/teams/&lt;teamId&gt;/...`. **The `teams/` container is
/// load-bearing**: team folders are siblings only of each other, so `skills`, `backups` and `keys`
/// - all legal team identifiers under `ContainerId`'s ASCII allowlist - cannot collide with the
/// platform's own folders. Flattening it to `&lt;root&gt;/&lt;teamId&gt;` makes the collision
/// reachable and needs a reserved-name list to paper over, which is a denylist over directory names
/// and carries a denylist's defect.
///
/// SYNCHRONOUS, over an in-memory map filled at restore and creation, because
/// `FileTranscriptStore.PathFor` is synchronous and runs on every transcript write.
///
/// **This is a write-through cache and it is safe only because a root cannot change.** The trap
/// of a cache like this is the cache and the truth drifting apart in silence. An existing team's
/// root cannot be moved, and `ITeamStore` therefore has no setter. **If a root ever becomes
/// movable, this must gain a writer or go.**
/// </summary>
public sealed class TeamPaths(string dataRoot)
{
    // The value carries the ORIGINALLY REGISTERED spelling of the team id, not just its root. The
    // dictionary itself is case-insensitive so lookup succeeds under any casing, but a path built
    // from the caller's own casing would make "Alpha" and "alpha" resolve to two different folders
    // on disk even while the lookup found the same entry. Building from the registered spelling instead means every caller of
    // RootFor("alpha") lands in the one folder "Alpha" actually has, whichever case asked.
    private readonly ConcurrentDictionary<string, (string? Root, string TeamId)> _roots =
        new(StringComparer.OrdinalIgnoreCase);

    public string DataRoot { get; } = dataRoot;

    /// <summary>The on-disk skill-files folder a volume may still carry. Read once, by the
    /// start-up migration, which moves everything in it to <see cref="SkillBackups"/>; nothing
    /// writes here.</summary>
    public string TenantSkills { get; } = Path.Combine(dataRoot, "skills");

    /// <summary>The skill-drafts folder a volume may still carry. Read only by the migration.</summary>
    public string TenantSkillDrafts { get; } = Path.Combine(dataRoot, "skill-drafts");

    /// <summary>Where the migration moves on-disk skill files - kept, never deleted.</summary>
    public string SkillBackups { get; } = Path.Combine(dataRoot, "backups", "skills-before-builtins");

    /// <summary>
    /// ONE DOCUMENTS ROOT FOR THE WHOLE TENANT, a folder per team beneath it, outside every team
    /// root and outside what <c>TeamDeletion</c> removes: all documents live here
    /// WHATEVER <c>teams.root</c> says.
    ///
    /// **THE POINT IS THAT THERE IS NO SECOND ARM.** A documents root that followed `teams.root`
    /// would preserve a placed team's file locality and re-open the deletion problem for exactly
    /// the teams whose owners had been most deliberate about where their files go - and it would
    /// give this codebase two answers to "where are a team's documents", which is the shape that
    /// drifts. Deleting a team can never reach a document BY CONSTRUCTION rather than by care.
    ///
    /// **THE COST IS REAL AND IS NOT HIDDEN HERE.** Someone who placed a team on a particular
    /// volume to keep its work together does not get that for documents. If that matters it is a
    /// change of its own, not a quiet exception in this method.
    ///
    /// <see cref="ConciergeWorkspaceFor"/> is the precedent: a tenant-wide path a team does not
    /// own. `documents` is a sibling of `teams`, `skills` and `skill-drafts`, so the folders
    /// beneath it are siblings only of each other - the same argument the `teams/` container
    /// carries, and the reason a team identifier here cannot collide with a platform folder.
    /// </summary>
    public string TenantDocuments { get; } = TenantDocumentsIn(dataRoot);

    /// <summary>The tenant documents root under a data root, for a caller that has a data root and
    /// no instance.</summary>
    public static string TenantDocumentsIn(string dataRoot) => Path.Combine(dataRoot, "documents");

    /// <summary>One team's documents folder under a data root. Composed HERE, like every other
    /// path, so no caller keeps a second spelling of it.</summary>
    public static string DocumentsFolderIn(string dataRoot, string folder) =>
        Path.Combine(TenantDocumentsIn(dataRoot), folder);

    /// <summary>Case-insensitive on the team, like every other team comparison in this codebase -
    /// `ContainerId`'s equality, `teams.id COLLATE NOCASE`, and `TeamRegistry`'s own dictionary.
    /// A second, ordinal answer here would make `/api/teams/alpha` and `/api/teams/Alpha` two
    /// different folders.</summary>
    public void Register(string teamId, string? root) => _roots[teamId] = (root, teamId);

    public void Forget(string teamId) => _roots.TryRemove(teamId, out _);

    /// <summary>
    /// THROWS for a team nobody registered. A fallback to the instance root would put a placed
    /// team's files somewhere it never asked for, and nothing would fail: the team works, the board
    /// renders, and its documents are in a folder nobody looks in.
    /// </summary>
    public string RootFor(string teamId) =>
        _roots.TryGetValue(teamId, out var entry)
            ? ContainerOf(entry.Root ?? DataRoot, entry.TeamId)
            : throw new KeyNotFoundException(
                $"No root is registered for team '{teamId}'. A team's root is registered when it is "
                + "created and when it is restored, so this is a wiring fault rather than a bad name.");

    /// <summary>
    /// The folder that CONTAINS this team's folder, or null for the instance root.
    ///
    /// The STORED value rather than the resolved path, and the difference is the whole reason this
    /// exists: a clone must be created under the same PARENT so its folder is a SIBLING of the
    /// source's. Handing it the source's own team folder would ask the platform to adopt a
    /// directory it did not create, which it refuses - and the `teams/` container is exactly what
    /// makes a sibling safe, because a team identifier is only ever beside other identifiers.
    ///
    /// Answers null for a team nobody has registered, where <see cref="RootFor"/> throws: a caller
    /// asking where a team is placed can act on "nowhere in particular"; a caller composing a path
    /// cannot act on a guess.
    /// </summary>
    public string? StoredRootFor(string teamId) =>
        _roots.TryGetValue(teamId, out var placed) ? placed.Root : null;

    /// <summary>The team folder under a root, for the one caller that has a root and no
    /// registration yet: creation, which must build the path before the row exists.</summary>
    public static string ContainerOf(string root, string teamId) =>
        Path.Combine(root, "teams", teamId);

    /// <summary>
    /// A team's documents: <see cref="TenantDocuments"/>/&lt;teamId&gt;, outside the one root
    /// <c>TeamDeletion</c> removes - a team is deleted as soon as its work is merged, which is
    /// exactly when its reports become the only record of how that work was checked.
    ///
    /// **IT ANSWERS FOR A TEAM NOBODY REGISTERED**, where <see cref="RootFor"/> throws, and the
    /// difference is the whole feature rather than a relaxation. RootFor throws because guessing a
    /// PLACEMENT puts a team's files somewhere it never asked for; there is no placement to guess
    /// here - every team's documents are under the one tenant root - so the only open question is
    /// SPELLING, and a registered team answers with the spelling it was registered under exactly as
    /// RootFor does. A folder whose team is gone is the ordinary state of this feature, not a
    /// wiring fault, so it is composed rather than refused.
    ///
    /// </summary>
    public string DocsFor(string teamId) =>
        DocumentsFolderIn(DataRoot, DocumentsFolderFor(teamId));

    /// <summary>
    /// The FOLDER NAME under <see cref="TenantDocuments"/> for a team, in its registered spelling
    /// when it has one.
    ///
    /// REFUSES anything that is not a name this platform composes. Every other path in this type is
    /// built from a REGISTERED team, which <c>ContainerId</c> has already vetted; this one is
    /// reachable with a folder name that arrived from outside - a route value naming a team that no
    /// longer exists - so the segment is checked here, at the one place that composes it, rather
    /// than left to <see cref="TeamDocuments.Resolve"/>'s containment check further down. A
    /// segment carrying a separator would escape the root BEFORE Resolve ever saw a relative path.
    /// </summary>
    private string DocumentsFolderFor(string teamId)
    {
        if (!IsDocumentsFolder(teamId))
        {
            throw new ArgumentException(
                $"'{teamId}' is not a documents folder this platform composes.", nameof(teamId));
        }

        return _roots.TryGetValue(teamId, out var entry) ? entry.TeamId : teamId;
    }

    /// <summary>
    /// The suffix that RETIRES a documents folder, and the whole answer to a reused team id.
    ///
    /// **IT CONTAINS A DOT, WHICH <c>ContainerId.IsLegalName</c> REFUSES.** That is the mechanism,
    /// not decoration: team ids are REUSABLE, so a folder still named for a team can be met by that
    /// team's successor - and a folder named with this suffix can never again be any team's, under
    /// any spelling, because no team can ever be called that. The name is keyed on something that
    /// is not reusable, WITHOUT a schema column and without making a
    /// dead team's documents anonymous: the identifier it belonged to is still the front of the
    /// name, so the Projects list can say whose they were.
    /// </summary>
    public const string RetiredSuffix = ".retired-";

    /// <summary>A retired name for a documents folder. UTC and sortable, so a team retired three
    /// times reads in order.</summary>
    public static string RetiredNameFor(string teamId, DateTimeOffset when) =>
        $"{teamId}{RetiredSuffix}{when.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}";

    /// <summary>
    /// Whether this is a folder name <see cref="TenantDocuments"/> may hold: a legal team
    /// identifier, or one retired by <see cref="RetiredNameFor"/>.
    ///
    /// AN ALLOWLIST, like <c>ContainerId.IsLegalName</c> it defers to, and for the same reason: the
    /// alternative is a denylist over `..`, separators and device names, which is only ever as
    /// current as the last sink somebody thought of.
    /// </summary>
    public static bool IsDocumentsFolder(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        var cut = name.IndexOf(RetiredSuffix, StringComparison.Ordinal);

        if (cut < 0) return ContainerId.IsLegalName(name);

        var stamp = name[(cut + RetiredSuffix.Length)..];

        return ContainerId.IsLegalName(name[..cut])
            && stamp.Length is > 0 and <= 24
            && stamp.All(char.IsAsciiLetterOrDigit);
    }

    /// <summary>The team a documents folder belongs to, which is the folder's own name until it is
    /// retired. What lets the Projects list name whose documents these were.</summary>
    public static string TeamOfDocumentsFolder(string name)
    {
        var cut = name.IndexOf(RetiredSuffix, StringComparison.Ordinal);

        return cut < 0 ? name : name[..cut];
    }

    /// <summary>Whether this folder has been retired - its team can never claim it again.</summary>
    public static bool IsRetiredDocumentsFolder(string name) =>
        name.Contains(RetiredSuffix, StringComparison.Ordinal);

    /// <summary>A team's on-disk skill folder, if a volume still carries one. Read only by the migration.</summary>
    public string SkillsFor(string teamId) => Path.Combine(RootFor(teamId), "skills");

    /// <summary>A team's on-disk skill-draft folder, if a volume still carries one. Read only by the migration.</summary>
    public string SkillDraftsFor(string teamId) => Path.Combine(RootFor(teamId), "skill-drafts");

    public string ReposFor(string teamId) => Path.Combine(RootFor(teamId), "repos");

    /// <summary>This member's tree for one card: <c>&lt;teamRoot&gt;/repos/&lt;Repo&gt;/wt_&lt;Member&gt;_&lt;key&gt;</c>.
    /// One per card, so switching tasks never touches another task's files. See
    /// <see cref="Worktrees"/> for the key.</summary>
    public string WorktreeFor(ContainerId id, string repoName, string key) =>
        Path.Combine(ReposFor(id.Team), repoName, Worktrees.DirectoryName(id.Name, key));

    /// <summary>Every per-card tree this member has in one repository, as absolute paths. Matches
    /// the member exactly, so <c>Dev</c> never lists <c>Dev2</c>'s or <c>Dev_2</c>'s trees.</summary>
    public IReadOnlyList<string> WorktreesFor(ContainerId id, string repoName) =>
        CardWorktreesIn(id.Team, repoName)
            .Where(tree => string.Equals(tree.Member, id.Name, StringComparison.OrdinalIgnoreCase))
            .Select(tree => tree.Path)
            .ToList();

    /// <summary>Every member's per-card tree in one repository, with the member and key its name
    /// carries. Anything else under the repository - <c>main</c>, a per-member <c>wt_&lt;Member&gt;</c> -
    /// is not listed.</summary>
    public IReadOnlyList<CardWorktree> CardWorktreesIn(string teamId, string repoName)
    {
        var repoRoot = Path.Combine(ReposFor(teamId), repoName);

        if (!Directory.Exists(repoRoot)) return [];

        return Directory.EnumerateDirectories(repoRoot, Worktrees.Prefix + "*")
            .Select(path => (Path: path, Name: Path.GetFileName(path)))
            .Where(tree => Worktrees.MemberOf(tree.Name) is not null)
            .Select(tree => new CardWorktree(
                tree.Path, Worktrees.MemberOf(tree.Name)!, Worktrees.KeyOf(tree.Name)!))
            .OrderBy(tree => tree.Path, StringComparer.Ordinal)
            .ToList();
    }

    public string TranscriptsRootFor(string teamId) => Path.Combine(RootFor(teamId), "transcripts");

    public string WorkspaceFor(ContainerId id) =>
        Path.Combine(RootFor(id.Team), "workspaces", id.Name);

    public string TranscriptsFor(ContainerId id) =>
        Path.Combine(RootFor(id.Team), "transcripts", id.Name);

    /// <summary>Where every Concierge workspace lives - tenant-wide, outside every team root.</summary>
    public string ConciergeWorkspacesRoot =>
        // Spelled `interactive-agent` because this directory is on-disk data: renaming it would
        // strand every existing workspace.
        Path.Combine(DataRoot, "tenant-interactive-agent-workspaces");

    /// <summary>
    /// One Concierge workspace, by DIRECTORY SEGMENT.
    ///
    /// **THE SEGMENT IS NOT A USER ID.** It is a label composed from the person's
    /// login by <see cref="ConciergeWorkspaceName"/>, and which segment belongs to whom is decided
    /// by <see cref="ConciergeWorkspaces.Resolve"/> reading the ownership marker inside each
    /// folder. This method is pure composition and asks no questions.
    /// </summary>
    public string ConciergeWorkspaceFor(string segment) =>
        Path.Combine(ConciergeWorkspacesRoot, segment);

    /// <summary>
    /// WHOSE a Concierge workspace is, written inside it. The precedent is
    /// <see cref="MarkerFileName"/> and the shape is identical - id, then timestamp, written only
    /// when absent - because it answers the same kind of question about a directory a person can
    /// type the name of.
    ///
    /// **THIS FILE, NOT THE FOLDER NAME, IS THE IDENTIFIER.** The folder is named after a login,
    /// which can be relabelled, can collide with another login once folded, and is only ever a label.
    /// Resolution reads this to CONFIRM ownership; a marker naming a different user id is a hard
    /// refusal rather than a silent adoption, which is the one failure mode a readable name
    /// introduces and the only reason this file exists.
    ///
    /// PINNED BESIDE `CONTEXT.md`, which is written into the same workspace; the two names are
    /// fixed together, so change neither alone.
    /// </summary>
    public const string ConciergeMarkerFileName = ".harness-concierge";

    public static string ConciergeMarkerIn(string workspace) =>
        Path.Combine(workspace, ConciergeMarkerFileName);

    /// <summary>
    /// The user id a Concierge workspace claims, or NULL when it claims nobody - no marker, an
    /// empty one, or a file that cannot be read.
    ///
    /// NULL IS NEVER "PROBABLY MINE". An unmarked folder is something a person put here, and
    /// <see cref="ConciergeWorkspaces"/> does not adopt it on the strength of its name.
    ///
    /// SWALLOWING, like <see cref="EnsureDocumentsFolder"/> and for its reason: this is on the path
    /// that opens somebody's terminal, and an unreadable marker must read as "claims nobody" rather
    /// than as a WebSocket upgrade throwing.
    /// </summary>
    public static string? ConciergeOwnerOf(string workspace)
    {
        try
        {
            var marker = ConciergeMarkerIn(workspace);

            if (!File.Exists(marker)) return null;

            // The FIRST line, exactly as the team marker's first line is the team id. Everything
            // after it is the courtesy half - a timestamp today, whatever a later release adds -
            // and a reader that parsed the whole file would break on the first thing appended.
            foreach (var line in File.ReadLines(marker))
            {
                var claimed = line.Trim();

                if (claimed.Length > 0) return claimed;
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A new Concierge workspace and its marker. IDEMPOTENT - the marker is written only when
    /// absent - and best effort about the marker but not about the directory, which is a working
    /// directory a terminal is about to open in.
    ///
    /// **THE SWALLOW IS SAFE BECAUSE THE FOLDER IS EMPTY.** A marker that fails to write leaves a
    /// directory nothing claims, which the next resolution REFUSES rather than hands to the wrong
    /// person - harmless when this call has just created the folder, because the person loses a
    /// name and no data.
    /// </summary>
    public static void EnsureConciergeWorkspace(string workspace, string userId)
    {
        Directory.CreateDirectory(workspace);

        try
        {
            var marker = ConciergeMarkerIn(workspace);

            // NEVER OVERWRITTEN. A marker already here names the owner, and the caller has already
            // established that owner is this user - rewriting it would only discard the date the
            // workspace was claimed, which is the one thing in the file a person reads.
            if (!File.Exists(marker))
            {
                File.WriteAllText(marker, $"{userId}\n{DateTimeOffset.UtcNow:O}\n");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The workspace is there and the terminal can open in it, marked or not.
        }
    }

    /// <summary>
    /// Every folder a team owns. ONE list: creation and restoration build the same skeleton, and
    /// two copies of it is the shape that drifts.
    ///
    /// **`interactive-agents` IS NOT ONE OF THEM.** A Concierge is keyed on the
    /// PERSON - one per user, serving every team they reach - so its workspace lives at
    /// <c>&lt;dataRoot&gt;/tenant-interactive-agent-workspaces/&lt;user&gt;</c>, outside every team
    /// root, and <see cref="ConciergeLaunchFactory"/> is the only thing that resolves one. A folder
    /// for it under every team would be written to by nothing: a name asserting that a team owns
    /// something it does not.
    ///
    /// **`docs` IS NOT ONE OF THEM**, and that is not tidiness. A team does not own its
    /// documents - they are under
    /// <see cref="TenantDocuments"/> and survive the team - so a `docs` folder created inside every
    /// team root would be a name asserting ownership that is not there, exactly as
    /// `interactive-agents` would.
    ///
    /// **`skills` AND `skill-drafts` ARE NOT ONE OF THEM.** No skill is team-scoped: a
    /// custom skill lives in the database, and a built-in comes from the build.
    /// </summary>
    public static readonly IReadOnlyList<string> TeamFolders =
        ["repos", "workspaces", "transcripts"];

    /// <summary>Written at creation; REQUIRED by deletion. It is what makes
    /// "the platform only ever removes a directory it made" true rather than assumed - a team root
    /// is a path a person can type.</summary>
    public const string MarkerFileName = ".harness-team";

    public static string MarkerIn(string teamRoot) => Path.Combine(teamRoot, MarkerFileName);

    /// <summary>
    /// A documents folder and its marker. IDEMPOTENT - the marker is written only when absent - and
    /// wholly BEST EFFORT, returning whether the folder is now there.
    ///
    /// ONE CREATOR, beside <see cref="EnsureSkeletonAsync"/> and for the same reason: the repair on
    /// read and the claim at creation want the same folder-plus-marker, and two copies of a directory create and a file format is the shape that drifts.
    ///
    /// **THE MARKER IS WHAT MAKES THE CLAIM ANSWERABLE.** A documents folder outlives its team, so
    /// the next team of that identifier has to be able to tell "the platform made this for my
    /// predecessor" from "somebody put a folder here" - the same question <c>TeamDeletion</c> asks
    /// of a team root, answered the same way rather than a second way.
    ///
    /// SYNCHRONOUS AND SWALLOWING, unlike its sibling, because its callers are: the repair runs
    /// inside <c>AgentEnvironment</c> on the restoration path, where an unguarded throw becomes an
    /// instance that will not boot, taking every other team with it.
    /// </summary>
    public static bool EnsureDocumentsFolder(string folder, string teamId)
    {
        try
        {
            Directory.CreateDirectory(folder);

            var marker = MarkerIn(folder);

            // Contents are a courtesy, like host.lock's pid: enough for whoever finds one to know
            // what it belonged to - which here is the whole point, since the team may be gone.
            if (!File.Exists(marker))
            {
                File.WriteAllText(marker, $"{teamId}\n{DateTimeOffset.UtcNow:O}\n");
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Every folder a team owns, plus the marker. IDEMPOTENT - the marker is written only when
    /// absent - so it is safe on an existing team, which is what restoration needs.
    /// </summary>
    public static async Task EnsureSkeletonAsync(
        string teamRoot, string teamId, CancellationToken ct = default)
    {
        foreach (var folder in TeamFolders) Directory.CreateDirectory(Path.Combine(teamRoot, folder));

        var marker = MarkerIn(teamRoot);

        // Contents are a courtesy, like host.lock's pid: enough for whoever finds one to know what
        // it belonged to.
        if (!File.Exists(marker))
        {
            await File.WriteAllTextAsync(marker, $"{teamId}\n{DateTimeOffset.UtcNow:O}\n", ct);
        }
    }
}

/// <summary>One member's tree for one card, as found on disk.</summary>
public sealed record CardWorktree(string Path, string Member, string Key);
