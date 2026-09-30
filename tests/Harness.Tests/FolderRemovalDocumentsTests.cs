using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A documents delete goes through <see cref="FolderRemoval.RemoveDocumentsAsync"/>: links are
/// removed as links and never followed, a path behind a link is left alone, the whole folder goes
/// marker last, what cannot go is named with why, and an agent's owner-only folder is removed as
/// the agent where the Host switches users.
/// </summary>
public sealed class FolderRemovalDocumentsTests : IDisposable
{
    private readonly string _parent = Directory.CreateTempSubdirectory("harness-docs-removal-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Folder => Path.Combine(_parent, "documents", "Alpha");

    public FolderRemovalDocumentsTests()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(TeamPaths.MarkerIn(Folder), "Alpha\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_parent, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Write(string relative, string root)
    {
        var file = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");
        return file;
    }

    [Fact]
    public async Task A_link_in_a_deleted_folder_is_removed_as_a_link_and_what_it_points_at_is_untouched()
    {
        var outside = Write("elsewhere/precious.md", _parent);
        Write("notes/a.md", Folder);
        File.CreateSymbolicLink(Path.Combine(Folder, "notes", "out"), Path.GetDirectoryName(outside)!);

        var report = await new FolderRemoval().RemoveDocumentsAsync(Folder, Path.Combine(Folder, "notes"), "Alpha", Ct);

        Assert.True(report.Complete, string.Join(", ", report.Remaining) + report.Refused);
        Assert.False(Directory.Exists(Path.Combine(Folder, "notes")));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task A_path_behind_a_link_is_left_alone()
    {
        var outside = Write("elsewhere/precious.md", _parent);
        File.CreateSymbolicLink(Path.Combine(Folder, "via"), Path.GetDirectoryName(outside)!);

        var report = await new FolderRemoval().RemoveDocumentsAsync(
            Folder, Path.Combine(Folder, "via", "precious.md"), "Alpha", Ct);

        Assert.NotNull(report.Refused);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task The_whole_folder_goes_marker_last_and_what_the_host_cannot_remove_is_named_with_why()
    {
        var stuck = Write("keep/stuck.md", Folder);
        Write("free.md", Folder);
        var refusing = new FolderRemoval.HostDeletes(
            path =>
            {
                if (path == stuck) throw new UnauthorizedAccessException(path);
                File.Delete(path);
            },
            path => Directory.Delete(path, recursive: false));

        var report = await new FolderRemoval(host: refusing).RemoveDocumentsAsync(Folder, Folder, "Alpha", Ct);

        Assert.Equal([stuck], report.Remaining);
        Assert.Equal("permission denied", report.Reasons![stuck]);
        Assert.True(File.Exists(TeamPaths.MarkerIn(Folder)));
        Assert.False(File.Exists(Path.Combine(Folder, "free.md")));

        var retried = await new FolderRemoval().RemoveDocumentsAsync(Folder, Folder, "Alpha", Ct);

        Assert.True(retried.Complete);
        Assert.False(Directory.Exists(Folder));
    }

    [Fact]
    public async Task An_agent_owned_owner_only_folder_in_the_documents_is_removed_as_the_agent()
    {
        var runAs = AgentLaunchUser.Resolve("nobody");
        if (!runAs.Switches) Assert.Skip($"This process cannot switch users: {runAs.Reason}.");

        foreach (var directory in new[] { _parent, Path.Combine(_parent, "documents"), Folder })
        {
            File.SetUnixFileMode(directory, (UnixFileMode)0b111_111_111);
        }

        var agentDir = Path.Combine(Folder, "made");
        var start = new System.Diagnostics.ProcessStartInfo(runAs.Prefix[0]);
        foreach (var argument in runAs.Prefix.Skip(1)) start.ArgumentList.Add(argument);
        foreach (var argument in new[] { "/bin/sh", "-c", $"umask 077 && mkdir -p '{agentDir}/deep' && echo x > '{agentDir}/deep/a.md' && chmod 700 '{agentDir}' '{agentDir}/deep'" })
        {
            start.ArgumentList.Add(argument);
        }
        using (var process = System.Diagnostics.Process.Start(start)!) process.WaitForExit();
        Assert.True(Directory.Exists(agentDir));

        var inside = agentDir + Path.DirectorySeparatorChar;
        var refusing = new FolderRemoval.HostDeletes(
            path =>
            {
                if (path.StartsWith(inside, StringComparison.Ordinal)) throw new UnauthorizedAccessException(path);
                File.Delete(path);
            },
            path =>
            {
                if (path.StartsWith(inside, StringComparison.Ordinal) || path == agentDir) throw new UnauthorizedAccessException(path);
                Directory.Delete(path, recursive: false);
            },
            path => path.StartsWith(agentDir, StringComparison.Ordinal) ? throw new UnauthorizedAccessException(path) : Directory.EnumerateFileSystemEntries(path));

        Assert.NotEmpty((await new FolderRemoval(host: refusing).RemoveDocumentsAsync(Folder, agentDir, "Alpha", Ct)).Remaining);

        var report = await new FolderRemoval(runAs, host: refusing).RemoveDocumentsAsync(Folder, agentDir, "Alpha", Ct);

        Assert.True(report.Complete, string.Join(", ", report.Remaining) + report.Refused);
        Assert.False(Directory.Exists(agentDir));
    }
}
