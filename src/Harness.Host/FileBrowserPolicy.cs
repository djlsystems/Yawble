namespace Harness.Host;

/// <summary>
/// Whether a host path may be reached, and what may be done there.
///
/// THE ENTIRE SECURITY BOUNDARY OF /api/fs. Any signed-in person reaches these routes, so the gate
/// cannot be WHO - WHERE is all there is.
///
/// RESOLVED, NEVER STRING-SEARCHED. `Path.GetFullPath` turns `D:\Projects\..\Windows` into
/// `D:\Windows`; scanning the input for ".." is the check that looks right and is not. Same shape as
/// TeamDocuments.Resolve, including the trailing separator - without it `D:\Projects2` reads as
/// inside `D:\Projects`.
///
/// LIMITS OF THIS CHECK, all deliberate rather than overlooked:
/// - It is STRING-LEVEL after normalisation. `Path.GetFullPath` is pure string normalisation and
///   never touches the filesystem, so it does not resolve a symlink or a junction - one planted
///   inside a configured root is a way out this check does not see. Resolving links here would be
///   an I/O call on every candidate with its own failure modes; that decision belongs to the
///   routes that call this, not to this pure type.
/// - The comparison folds case (`OrdinalIgnoreCase`), which is correct on Windows and
///   over-permissive on a case-sensitive filesystem - on POSIX a root of `/data` would also admit
///   `/DATA/secret`, a different directory outside the root.
/// - A device-path share is refused wholesale, including the legitimate long-path spelling of a
///   UNC path (`\\?\UNC\server\share`). That is a deliberate, accepted cost rather than an
///   oversight: it bypasses `..` resolution exactly like `\\?\D:\...` does, and there is no way to
///   tell "the safe long-path form of a real share" apart from the dangerous one without doing the
///   I/O this type refuses to do. A share that needs the long-path form cannot be configured here.
///
/// NEVER TRUSTS ITS OWN INPUT. The constructor normalises every root exactly as
/// `FileBrowserOptions.Effective` does, drops a root that TRIMS TO NOTHING once its separators are
/// stripped - which catches both a blank path and a bare `\\` or `//` that survives normalisation
/// unchanged, since a trimmed `\\` is also "" - and refuses a device-path root (`\\?\...`,
/// `\\.\...`) outright, checked on the NORMALISED form so a root written as `//?/D:/Projects` does
/// not slip through under a spelling the raw check misses and then get canonicalised into a live
/// one. This type is told elsewhere that it is "the entire security boundary", and a boundary that
/// depends on a caller having already sanitised its input is not one - `Effective` is one caller
/// today, and it is not the only one this type will ever have.
/// `RootFor` mirrors that at the candidate: `Path.IsPathFullyQualified`, not `IsPathRooted`,
/// refuses anything that would resolve against either the process current directory OR a drive
/// own per-drive current directory (`IsPathRooted("C:foo")` is true; `IsPathFullyQualified("C:foo")`
/// is not, and `GetFullPath("C:foo")` still resolves it using drive C tracked directory) - and a
/// device-path candidate is refused outright, because `Path.GetFullPath` passes a `\\?\`/`\\.\`
/// path through WITHOUT resolving `..` in it.
/// </summary>
public sealed class FileBrowserPolicy
{
    public IReadOnlyList<FileBrowserRoot> Roots { get; }

    public FileBrowserPolicy(IReadOnlyList<FileBrowserRoot> roots)
    {
        var sanitised = new List<FileBrowserRoot>();

        foreach (var root in roots)
        {
            // A root with no path - empty, whitespace, or null despite the non-nullable type,
            // since a caller can hand one in regardless - must never reach normalisation. Checked
            // before GetFullPath to avoid its own quirks with an all-whitespace string.
            if (string.IsNullOrWhiteSpace(root.Path)) continue;

            string normalised;
            try { normalised = Normalize(root.Path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            // Checked on the NORMALISED path, not the raw one: a root written as
            // `//?/D:/Projects` fails an ordinal `\\?\` test on its raw form, and GetFullPath
            // turns it into `\\?\D:\Projects` regardless - the canonical, live spelling reaches
            // Roots unless this runs after normalisation too. A root in that form bypasses
            // GetFullPath's own ".." resolution for every candidate compared against it, so it is
            // refused outright rather than compared.
            if (IsDevicePath(normalised)) continue;

            // A root that TRIMS TO NOTHING once directory separators are stripped from both ends
            // - "" itself, or a bare "\\"/"//" that survives normalisation unchanged - admits
            // every path under an empty prefix: the comparison's own TrimEnd on such a root
            // leaves "", and every rooted path starts with a separator. One condition covers both
            // the blank case and a bare separator run.
            if (normalised.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length == 0)
            {
                continue;
            }

            sanitised.Add(root with { Path = normalised });
        }

        Roots = sanitised;
    }

    /// <summary>The root containing this path, or null when nothing does.</summary>
    public FileBrowserRoot? RootFor(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        // A relative OR drive-relative candidate cannot escape a root, but it maps a bare name to
        // a path nobody typed. `Path.IsPathRooted("C:foo")` is TRUE while
        // `Path.IsPathFullyQualified("C:foo")` is FALSE - GetFullPath resolves that spelling using
        // drive C own per-drive current directory, exactly the same hazard as an ordinary
        // relative path and one `IsPathRooted` alone does not catch. TeamDocuments.Resolve makes
        // the mirror-image decision explicitly, by refusing a QUALIFIED path; this refuses the
        // opposite.
        if (!Path.IsPathFullyQualified(path)) return null;

        // Checked on the RAW input, before GetFullPath: a \\?\ or \\.\ candidate is fail-closed
        // against a normal root already, since GetFullPath does not resolve ".." inside one, but
        // it is refused explicitly rather than relying on that being true by accident.
        if (IsDevicePath(path)) return null;

        string full;

        // A malformed path is OUTSIDE, not an exception out of a route handler.
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (IsDevicePath(full)) return null;

        // FIRST containing root wins, so overlapping roots are answerable rather than ambiguous -
        // and it is LIST ORDER that decides, not specificity. There is no "outer" or "nested" here:
        // a shorter, earlier-listed root wins over a longer, later-listed one that also contains the
        // path, even though the second is the more specific match. That is not a hypothetical -
        // FileBrowserOptions.Effective always puts the instance root FIRST, so any path inside the
        // data root answers with the instance root's (read-only) flags whatever a differently-flagged
        // root configured underneath it might have offered. Fail-closed, and correct for that reason;
        // there is no "outer wins" hierarchy, because this method does not look at one. TrimEnd rather than the
        // already-normalised path alone: Path.GetFullPath does not strip the trailing separator off a
        // bare drive root ("C:\"), so re-trimming here is what keeps a drive root's own comparison
        // correct.
        return Roots.FirstOrDefault(root =>
            full.Equals(root.Path, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(
                root.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when a root contains it. Convenience over RootFor for readability at call sites.</summary>
    public bool Allows(string path) => RootFor(path) is not null;

    private static bool IsDevicePath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal);

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
