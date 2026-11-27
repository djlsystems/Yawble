namespace Yawble.Core.Ops;

/// <summary>
/// Whether a path inside a folder passes through a link. A symbolic link or junction placed in a shared folder (agents
/// write the team's documents) could point anywhere, so a path confined to a folder by its text alone is not confined:
/// every component that exists, from just below the folder down to the leaf, must be a plain file or folder.
/// </summary>
internal static class PathLinks
{
    /// <summary>
    /// The first component of <paramref name="relative"/> under <paramref name="root"/> (the root itself is not checked) that
    /// exists and is a link or junction, as a full path; null when there is none. Components that do not exist yet are fine.
    /// </summary>
    public static string? FirstLink(string root, string relative)
    {
        var at = root;
        foreach (var part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            at = Path.Combine(at, part);
            if (IsLink(at)) return at;
        }
        return null;
    }

    /// <summary>
    /// Tests only: paths to count as links where the file system refuses to make one (an agent's sandbox refuses junctions and
    /// symbolic links). It flows with the async context, so it reaches only the test that sets it.
    /// </summary>
    internal static readonly AsyncLocal<Func<string, bool>?> SeenAsLink = new();

    /// <summary>Whether the file or folder at <paramref name="path"/> is itself a link or junction (a dangling one too).</summary>
    public static bool IsLink(string path)
    {
        if (SeenAsLink.Value?.Invoke(path) == true) return true;
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Something is there that cannot be read: treat it as a link only when it says it is one.
            return LinkTarget(path) is not null;
        }
        return (attributes & FileAttributes.ReparsePoint) != 0 || LinkTarget(path) is not null;
    }

    /// <summary>Whether an entry a folder walk meets is a link or junction (from its attributes, no extra call).</summary>
    public static bool IsLink(ref System.IO.Enumeration.FileSystemEntry entry) =>
        (entry.Attributes & FileAttributes.ReparsePoint) != 0 || SeenAsLink.Value?.Invoke(entry.ToFullPath()) == true;

    private static string? LinkTarget(string path)
    {
        try { return new FileInfo(path).LinkTarget; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
}
