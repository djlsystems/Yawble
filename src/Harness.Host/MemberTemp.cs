using System.Diagnostics;

namespace Harness.Host;

/// <summary>
/// A MEMBER'S OWN TEMPORARY FOLDER: <c>&lt;workspace&gt;/.tmp</c>, handed to its child as
/// <c>TMPDIR</c> through a short link to it.
///
/// <para>
/// Without it every member shares <c>/tmp</c> with every other member and with the Host, whose
/// own <c>/tmp/harness-mcp</c> is not the agent's: a test a member runs that writes there fails
/// with "Permission denied", and one member's leftovers are another's inputs. The Host's own
/// temporary files (prompt files, the per-launch MCP directory) stay in the Host's temp folder;
/// only what the child writes moves.
/// </para>
///
/// <para>
/// Created by the launch on first use, owner-only (700). When the Host switches users it is made
/// AS THE AGENT, with <c>mkdir</c> through the same <see cref="AgentLaunchUser.Prefix"/> every
/// agent child gets, so the agent owns it and keeps no capability; otherwise the Host's user is
/// the agent's and makes it itself. Removed with the workspace by <c>FolderRemoval</c>, whose
/// agent pass handles an owner-only directory the Host cannot empty.
/// </para>
///
/// <para>
/// THE CHILD IS HANDED A SHORT LINK, NOT THE FOLDER'S OWN PATH. Tools put Unix sockets in TMPDIR
/// (the .NET runtime's named pipes are <c>TMPDIR/CoreFxPipe_&lt;name&gt;</c>, used by test
/// platforms, build servers and the compiler server) and a socket path holds 103 bytes. A
/// workspace path is as long as its team's and member's names, so <c>&lt;workspace&gt;/.tmp</c>
/// alone leaves too little room and the tool aborts before it starts. The child gets
/// <c>&lt;Host temp&gt;/member-&lt;hash of the workspace&gt;</c>, a link made the same way as the
/// folder and pointing at it: short whatever the workspace is called, one per workspace, and what
/// is written through it stays in the workspace. On Windows, whose pipes are not files, the child
/// gets the folder itself.
/// </para>
/// </summary>
public static class MemberTemp
{
    /// <summary>The folder's name inside the member's workspace.</summary>
    public const string FolderName = ".tmp";

    /// <summary>The variable the child reads it from.</summary>
    public const string Variable = "TMPDIR";

    /// <summary>The start of the link's name in the Host's temp folder.</summary>
    public const string LinkPrefix = "member-";

    /// <summary>Where <paramref name="workspace"/>'s temporary folder is.</summary>
    public static string PathFor(string workspace) => Path.Combine(Path.GetFullPath(workspace), FolderName);

    /// <summary>The short link to <paramref name="workspace"/>'s folder that its child is handed.</summary>
    public static string LinkFor(string workspace)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(workspace)));
        return Path.Combine(Path.GetTempPath(), LinkPrefix + Convert.ToHexStringLower(hash)[..12]);
    }

    /// <summary>Removes <paramref name="workspace"/>'s link, if it is one; the folder is untouched.</summary>
    public static void Forget(string workspace)
    {
        var link = LinkFor(workspace);
        try
        {
            if (new FileInfo(link).LinkTarget is not null) File.Delete(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not ours to remove, or already gone: a dangling link names nothing.
        }
    }

    /// <summary>
    /// What <paramref name="workspace"/>'s child is handed as <c>TMPDIR</c>: the short link to its
    /// folder (the folder itself on Windows), both created when missing. Null when the workspace
    /// itself does not exist (the child then runs in the Host's directory, and inherits its
    /// <c>TMPDIR</c> as it always has) or either could not be made.
    /// </summary>
    public static async Task<string?> EnsureAsync(string workspace, AgentLaunchUser? runAs, CancellationToken ct)
    {
        if (await EnsureFolderAsync(workspace, runAs, ct) is not { } folder) return null;
        if (OperatingSystem.IsWindows()) return folder;

        var link = LinkFor(workspace);
        if (PointsAt(link, folder)) return link;

        // -T: whatever sits at the link's name is replaced, never entered, so a directory there
        // refuses the launch instead of taking a link inside it.
        if (runAs is { Switches: true } agent)
        {
            if (SystemCommand.Find("ln") is not { } ln) return null;
            await RunAsync([.. agent.Prefix, ln, "-sfT", "--", folder, link], ct);
        }
        else
        {
            try
            {
                if (new FileInfo(link).LinkTarget is not null) File.Delete(link);
                File.CreateSymbolicLink(link, folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return PointsAt(link, folder) ? link : null;
    }

    private static bool PointsAt(string link, string folder)
    {
        try
        {
            return new FileInfo(link).LinkTarget == folder && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<string?> EnsureFolderAsync(string workspace, AgentLaunchUser? runAs, CancellationToken ct)
    {
        if (!Directory.Exists(workspace)) return null;

        var path = PathFor(workspace);
        if (Directory.Exists(path)) return path;

        if (runAs is { Switches: true } agent)
        {
            if (SystemCommand.Find("mkdir") is not { } mkdir) return null;
            await RunAsync([.. agent.Prefix, mkdir, "-p", "-m", "700", "--", path], ct);
        }
        else
        {
            try
            {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
                else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return Directory.Exists(path) ? path : null;
    }

    private static async Task RunAsync(IReadOnlyList<string> command, CancellationToken ct)
    {
        var start = new ProcessStartInfo(command[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start);
            if (process is null) return;

            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(output, error);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Not startable: the caller finds no folder and refuses the launch in its own words.
        }
    }
}
