using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Harness.Host;

/// <summary>
/// A MEMBER'S OWN TEMPORARY FOLDER, handed to its child as <c>TMPDIR</c>:
/// <c>&lt;Host temp&gt;/member-&lt;random&gt;</c>, owner-only, recorded by a link
/// <c>&lt;workspace&gt;/.tmpdir</c> that points at it.
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
/// SHORT, AND A REAL FOLDER RATHER THAN A PATH INSIDE THE WORKSPACE. Tools put Unix sockets in
/// TMPDIR (the .NET runtime's named pipes are <c>TMPDIR/CoreFxPipe_&lt;name&gt;</c>, used by test
/// platforms, build servers and the compiler server) and a socket path holds 103 bytes. A
/// workspace path is as long as its team's and member's names, so <c>&lt;workspace&gt;/.tmp</c>
/// leaves too little room and the tool aborts before it starts. A short link to a workspace
/// folder is no answer either: the child's paths then resolve somewhere other than TMPDIR says,
/// and git and every tool that compares a resolved path with the one it was given disagree with
/// themselves. So the folder is short and real, and the link goes the other way.
/// </para>
///
/// <para>
/// THE LINK IN THE WORKSPACE IS WHICH FOLDER IS THIS MEMBER'S. The name is random, so a member
/// removed and made again under the same name starts with a new, empty folder, never its
/// predecessor's; a link that names anything but a folder of that shape in the Host's temp folder
/// is replaced. Both are made by the launch on first use: AS THE AGENT, through the same
/// <see cref="AgentLaunchUser.Prefix"/> every agent child gets, when the Host switches users, so
/// the agent owns them and keeps no capability; otherwise by the Host, whose user is the agent's.
/// On Windows, whose pipes are not files, the folder is <c>&lt;workspace&gt;/.tmp</c> itself.
/// </para>
/// </summary>
public static partial class MemberTemp
{
    /// <summary>The link in the member's workspace that names its folder.</summary>
    public const string LinkName = ".tmpdir";

    /// <summary>The folder's name inside the member's workspace, on Windows.</summary>
    public const string WindowsFolderName = ".tmp";

    /// <summary>The start of every member folder's name in the Host's temp folder.</summary>
    public const string FolderPrefix = "member-";

    /// <summary>The variable the child reads it from.</summary>
    public const string Variable = "TMPDIR";

    /// <summary>The link in <paramref name="workspace"/> that names its folder.</summary>
    public static string LinkFor(string workspace) => Path.Combine(Path.GetFullPath(workspace), LinkName);

    /// <summary>
    /// <paramref name="workspace"/>'s temporary folder, created when missing. Null when the
    /// workspace itself does not exist (the child then runs in the Host's directory, and inherits
    /// its <c>TMPDIR</c> as it always has) or the folder or its link could not be made.
    /// </summary>
    public static async Task<string?> EnsureAsync(string workspace, AgentLaunchUser? runAs, CancellationToken ct)
    {
        if (!Directory.Exists(workspace)) return null;

        if (OperatingSystem.IsWindows())
        {
            var folder = Path.Combine(Path.GetFullPath(workspace), WindowsFolderName);
            try
            {
                return Directory.CreateDirectory(folder).FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        var link = LinkFor(workspace);
        if (FolderNamedBy(link) is { } existing) return existing;

        var fresh = Path.Combine(Root, FolderPrefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..12]);

        // No -p: a folder already there under a fresh random name is not this member's. -T: what
        // sits at the link's name is replaced, never entered, so a directory there refuses the
        // launch instead of taking a link inside it.
        if (runAs is { Switches: true } agent)
        {
            if (SystemCommand.Find("mkdir") is not { } mkdir || SystemCommand.Find("ln") is not { } ln) return null;
            if (!await RunAsync([.. agent.Prefix, mkdir, "-m", "700", "--", fresh], ct)) return null;
            await RunAsync([.. agent.Prefix, ln, "-sfT", "--", fresh, link], ct);
        }
        else
        {
            try
            {
                Directory.CreateDirectory(fresh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                if (new FileInfo(link).LinkTarget is not null) File.Delete(link);
                File.CreateSymbolicLink(link, fresh);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return FolderNamedBy(link);
    }

    /// <summary>The Host's temp folder, where every member folder is made.</summary>
    private static string Root => Path.TrimEndingDirectorySeparator(Path.GetTempPath());

    /// <summary>
    /// The folder <paramref name="link"/> names, when it is a link to a real member folder in the
    /// Host's temp folder; null for anything else.
    /// </summary>
    private static string? FolderNamedBy(string link)
    {
        try
        {
            if (new FileInfo(link).LinkTarget is not { } target) return null;
            if (Path.GetDirectoryName(target) != Root || !FolderName().IsMatch(Path.GetFileName(target))) return null;

            var folder = new DirectoryInfo(target);
            return folder.Exists && folder.LinkTarget is null ? target : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex("^member-[0-9a-f]{12}$")]
    private static partial Regex FolderName();

    private static async Task<bool> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
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
            if (process is null) return false;

            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(output, error);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Not startable: the caller finds no folder and refuses the launch in its own words.
            return false;
        }
    }
}
