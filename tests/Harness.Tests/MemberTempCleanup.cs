using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A member a test launches gets its own folder in the temp folder, named by the <c>.tmpdir</c>
/// link in its workspace (see <see cref="MemberTemp"/>). Deleting the test's folder removes the
/// link and leaves the member folder behind, so a test that launches members calls
/// <see cref="Remove"/> on its folder before deleting it.
/// </summary>
public static class MemberTempCleanup
{
    /// <summary>Deletes every member folder a <c>.tmpdir</c> link under <paramref name="root"/>
    /// names. Only a <c>member-</c> folder directly in the temp folder is touched, whatever a link
    /// says.</summary>
    public static void Remove(string root)
    {
        if (!Directory.Exists(root)) return;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        };
        var temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());

        foreach (var link in Directory.EnumerateFileSystemEntries(root, MemberTemp.LinkName, options))
        {
            if (new FileInfo(link).LinkTarget is not { } target) continue;
            if (Path.GetDirectoryName(target) != temp) continue;
            if (!Path.GetFileName(target).StartsWith(MemberTemp.FolderPrefix, StringComparison.Ordinal)) continue;

            try { Directory.Delete(target, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }
}
