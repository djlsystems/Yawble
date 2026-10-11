namespace Harness.Host.Solutions;

/// <summary>What <see cref="SolutionService.Check"/> answers: a refused folder (400, with the
/// sentence), or the check itself - which may itself hold refusals.</summary>
public sealed record SolutionFolderCheck(string? Error, SolutionCheck? Check)
{
    public bool FolderRefused => Error is not null;
}

/// <summary>
/// THE ONE DOOR A FOLDER IS CHECKED THROUGH inside the running Host: the route, and later the install
/// wizard and the board's notice. It decides which folders may be read at all - absolute, inside the
/// data root, reached through no link that leaves it - and hands the rest to
/// <see cref="SolutionChecker"/>. Writes nothing.
///
/// <para>
/// A FOLDER FROM A LINK is held to less: only the instance's documents (<paramref name="linkRoots"/>
/// answers that folder and every team's folder). A link is a convenience someone else hands the
/// person, so it may point only where the platform's own teams deliver; the review is the safeguard.
/// </para>
/// </summary>
public sealed class SolutionService(SolutionChecker checker, string dataRoot, Func<IEnumerable<string>>? linkRoots = null)
{
    public SolutionChecker Checker => checker;

    /// <summary>What a link to a folder outside the documents and team folders is told.</summary>
    public const string LinkOutsideRoots =
        "A link may open only a package inside this instance's documents or a team's folder, and {0} is neither. "
        + "Choose the folder yourself in Admin → Plugins → Install from a folder if you mean it.";

    public SolutionFolderCheck Check(string? folder, bool fromLink = false)
    {
        if (fromLink && LinkRefusal(folder) is { } linkRefusal) return new(linkRefusal, null);

        return Check(folder);
    }

    /// <summary>Why a link may not open <paramref name="folder"/>, or null when it may.</summary>
    public string? LinkRefusal(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder.Trim())) return null;

        var resolved = PluginCatalog.Resolved(Path.GetFullPath(folder.Trim()));
        var roots = (linkRoots?.Invoke() ?? [Path.Combine(dataRoot, "documents")])
            .Select(root => PluginCatalog.Resolved(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))));

        return roots.Any(root => IsUnder(resolved, root))
            ? null
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, LinkOutsideRoots, folder.Trim());
    }

    public SolutionFolderCheck Check(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder.Trim()))
        {
            return new("Give the absolute path of a solution package: the folder holding solution.json.", null);
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        var asked = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim()));

        if (!IsUnder(asked, root))
        {
            return new($"{asked} is outside the data root ({root}); only a folder inside the instance can be checked.", null);
        }

        var resolved = PluginCatalog.Resolved(asked);

        if (!IsUnder(resolved, PluginCatalog.Resolved(root)))
        {
            return new($"{asked} goes through a link that leaves the data root (it leads to {resolved}).", null);
        }

        return new(null, checker.Check(resolved));
    }

    private static bool IsUnder(string path, string directory) =>
        path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
