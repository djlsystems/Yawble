namespace Harness.Host;

/// <summary>One folder the picker may reach. Being listed IS read permission; the three flags gate
/// writes and all default to false. <see cref="AllowWatch"/> lets a folder trigger watch
/// inside it, also false by default: a watch is a standing wake, not a read.</summary>
public sealed record FileBrowserRoot(
    string Name,
    string Path,
    bool AllowCreate = false,
    bool AllowUpdate = false,
    bool AllowDelete = false,
    bool AllowWatch = false);

/// <summary>
/// Which host folders the file picker may reach.
///
/// THE DEFAULT IS THIS INSTANCE'S OWN FOLDER, read-only - not the whole filesystem, and not nothing.
/// Nothing would make the feature inert: the picker would open empty and a typed path would be
/// refused, so "Place team in" would do nothing at all until somebody edited a config file.
/// Everything is ruled out for the obvious reason.
///
/// THE INSTANCE ROOT IS COMPUTED, NEVER WRITTEN TO appsettings.json, for the same reason
/// `teams.root` is nullable with NULL meaning the instance root: the data root is a RUNTIME value
/// that comes from --DataRoot or HARNESS_DATA_ROOT. A resolved copy in a file names a folder a
/// differently-started instance does not use.
///
/// A CONFIGURED ROOT ADDS; IT NEVER REPLACES. Replacing would remove the instance folder the moment
/// anyone configures anything, and the only way back would be writing that runtime value into the
/// file.
/// </summary>
public sealed class FileBrowserOptions
{
    public const string Section = "FileBrowser";

    public IReadOnlyList<FileBrowserRoot> Roots { get; init; } = [];

    public static IReadOnlyList<FileBrowserRoot> Effective(
        FileBrowserOptions? configured, string dataRoot)
    {
        var resolvedDataRoot = Resolve(dataRoot);

        var effective = new List<FileBrowserRoot>
        {
            // Named for the instance so a person sees where they are rather than meeting a special
            // case. No write flags: placing a team here is POST /api/teams doing what it already
            // does, not a picker write.
            new(NameFor(resolvedDataRoot), resolvedDataRoot),
        };

        foreach (var root in configured?.Roots ?? [])
        {
            // A blank path would resolve to the process's current directory - the SOURCE TREE for
            // anyone running the Host from a clone. Dropped rather than repaired.
            if (string.IsNullOrWhiteSpace(root.Path)) continue;

            // GUARDED, not trusted: an embedded NUL or a path past MAX_PATH throws out of
            // Path.GetFullPath, and this is startup configuration a hand-edited appsettings.json
            // can hand in any string at all. An unhandled throw here would take the whole instance
            // down over one bad root - the opposite of the warn-and-serve posture every other
            // startup guard in this file follows. Dropped the same way a blank path is; the
            // caller that builds the final FileBrowserPolicy is what reports it, by diffing this
            // method's RAW input against the policy's surviving roots.
            string resolved;

            try { resolved = Resolve(root.Path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            effective.Add(root with { Path = resolved });
        }

        return effective;
    }

    /// <summary>
    /// Names every root that did not survive to <paramref name="policy"/>, at WHICHEVER of the two
    /// stages dropped it: a configured root <see cref="Effective"/> itself drops (a blank path, or
    /// one <c>Path.GetFullPath</c> could not resolve) never reaches <paramref name="effective"/> at
    /// all, so it is found by diffing the RAW <paramref name="configured"/> list against
    /// <paramref name="effective"/>; a root that survives <see cref="Effective"/> - a configured one
    /// OR the IMPLIED INSTANCE ROOT <see cref="Effective"/> always adds - but is then dropped inside
    /// <see cref="FileBrowserPolicy"/>'s own constructor (a device path, or one that trims to
    /// nothing once normalised) is found by diffing <paramref name="effective"/> against
    /// <paramref name="policy"/>.Roots. Both diffs matter: covering only the first would miss
    /// `--DataRoot=\\?\C:\foo` producing a picker with ZERO roots and nothing saying why, since the
    /// instance root is never part of the raw configured list a caller could hand in.
    ///
    /// A THIRD PASS WARNS ABOUT A ROOT THAT SURVIVED BOTH AND IS NOT THERE. Neither stage touches the
    /// filesystem, so an unplugged drive or a downed share reaches the picker intact and answers
    /// 404 on the first click, which reads exactly like a typo. That one is WARNED AND KEPT rather
    /// than dropped: see the pass itself for why, in one line - a share that comes back must not
    /// need a restart.
    ///
    /// Neither <see cref="Effective"/> nor <see cref="FileBrowserPolicy"/> can report any of this
    /// themselves - both are pure types with no logger - so the caller that already has the raw
    /// configuration, the effective list, and the finished policy is where this lives. Takes a
    /// <see cref="TextWriter"/> rather than writing to <see cref="Console"/> directly, the same seam
    /// <c>AgentCatalogFile.LoadCustom</c> uses, precisely so a test can capture it without swapping
    /// a process-global that every other test sharing this run is also writing to.
    ///
    /// Matched by NAME throughout: <c>Path</c> is the field normalisation rewrites at both stages,
    /// and nothing here renames a surviving root, so a name reappearing downstream is exactly "this
    /// one made it" - matching on `Path` instead would report every surviving root as dropped the
    /// moment it needed normalising at all.
    /// </summary>
    public static void ReportDrops(
        FileBrowserOptions? configured,
        IReadOnlyList<FileBrowserRoot> effective,
        FileBrowserPolicy policy,
        TextWriter writer)
    {
        // STAGE ONE: a configured root Effective itself never let through.
        foreach (var configuredRoot in configured?.Roots ?? [])
        {
            if (effective.Any(root => root.Name == configuredRoot.Name)) continue;

            writer.WriteLine(
                $"FileBrowser root '{configuredRoot.Name}' ({configuredRoot.Path}) was dropped: "
                + "blank, or a path that could not be resolved. It will not appear in the picker.");
        }

        // STAGE TWO: anything that reached Effective's output - a configured root OR the implied
        // instance root - but did not survive FileBrowserPolicy's own construction.
        foreach (var survivor in effective)
        {
            if (policy.Roots.Any(root => root.Name == survivor.Name)) continue;

            writer.WriteLine(
                $"FileBrowser root '{survivor.Name}' ({survivor.Path}) was dropped: a device path, "
                + "or empty once its separators are trimmed. It will not appear in the picker.");
        }

        // STAGE THREE: A ROOT THAT SURVIVED BOTH AND IS NOT THERE. Neither Effective nor
        // FileBrowserPolicy's constructor touches the filesystem - both are pure string work - so a
        // root on a drive that was unplugged, or a share that is down, passes every check above,
        // appears in the picker, and answers 404 on the first click. From the outside that is
        // indistinguishable from a typo in appsettings.json, which is the confusion this line ends.
        //
        // WARNED, NEVER DROPPED, and that direction is this codebase's warn-and-serve rule rather
        // than a soft option: a network share that comes back an hour later must not need a Host
        // restart to reappear, and `Directory.Exists` answers FALSE for a permission failure just
        // as readily as for absence - so dropping on it would silently remove a perfectly good root
        // the Host merely could not stat at the instant it started. The routes behind the picker
        // already tell those two apart properly, at the moment of use: 404 for absent, 423 for
        // present-but-unreadable. This is a startup hint, not a gate.
        foreach (var root in policy.Roots)
        {
            if (Directory.Exists(root.Path)) continue;

            writer.WriteLine(
                $"FileBrowser root '{root.Name}' ({root.Path}) is not reachable right now: it does "
                + "not exist, or this Host cannot read it. It is still offered in the picker - a "
                + "share that comes back needs no restart - but it will refuse until it returns.");
        }
    }

    /// <summary>
    /// The label comes from the resolved folder, the one fact about it that cannot be wrong. `/`
    /// has no leaf, so it names itself rather than rendering as <c>Harness ()</c>.
    /// </summary>
    private static string NameFor(string resolved) =>
        $"Harness ({(Path.GetFileName(resolved) is { Length: > 0 } leaf ? leaf : resolved)})";

    // Path.GetFullPath alone does not strip a trailing separator - "D:\Projects\" resolves to
    // itself, not "D:\Projects" - so a hand-edited trailing-slash path would never match a
    // resolved candidate elsewhere and the root would silently permit nothing.
    // Path.TrimEndingDirectorySeparator leaves a bare drive root ("C:\") alone.
    private static string Resolve(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
