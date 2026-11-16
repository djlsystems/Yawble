namespace Yawble.Core.Ops;

/// <summary>
/// Walks a folder's files without following links. Agents' sandboxes can hold symbolic links and junctions (a test that
/// links a folder into itself, a tool's cache): following them could loop for ever, or reach files outside the folder.
/// A link itself is never entered; unreadable folders are skipped.
/// </summary>
public static class FolderWalk
{
    private static EnumerationOptions Options() => new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>The files under <paramref name="folder"/> matching <paramref name="pattern"/>, links not followed.</summary>
    public static IEnumerable<FileInfo> Files(string folder, string pattern = "*") =>
        new DirectoryInfo(folder).EnumerateFiles(pattern, Options());

    /// <summary>The total size of the files under <paramref name="folder"/>, links not followed.</summary>
    public static long Size(string folder, string pattern = "*") => Files(folder, pattern).Sum(f => f.Length);
}
