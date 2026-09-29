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
/// </summary>
public sealed class SolutionService(SolutionChecker checker, string dataRoot)
{
    public SolutionChecker Checker => checker;

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
