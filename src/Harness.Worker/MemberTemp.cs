using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Harness.Host;

/// <summary>
/// A MEMBER'S OWN TEMPORARY FOLDER, handed to its child as <c>TMPDIR</c>:
/// <c>/tmp/member-&lt;random&gt;</c>, owner-only, recorded by a link
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
/// predecessor's; a link that names anything but a folder of that shape in /tmp
/// is replaced. Both are made by the launch on first use: AS THE AGENT, through the same
/// <see cref="AgentLaunchUser.Prefix"/> every agent child gets, when the Host switches users, so
/// the agent owns them and keeps no capability; otherwise by the Host, whose user is the agent's.
/// On Windows, whose pipes are not files, the folder is <c>&lt;workspace&gt;/.tmp</c> itself.
/// </para>
///
/// <para>
/// ON THE DATA VOLUME, NOT IN /tmp. An engine may mount <c>/tmp</c> as tmpfs, where every file a
/// member's builds and tests leave there is held in the container's MEMORY and counts against its
/// limit. So the Host passes <see cref="RootUnder"/>'s folder, <c>&lt;dataRoot&gt;/tmp</c>, which is on disk
/// whatever the engine and the image; <c>/tmp</c> is only the fallback for a data root whose path is too
/// long to leave room for a socket, and the Host log's "Member temporary folders" line says which.
/// </para>
/// </summary>
public static partial class MemberTemp
{
    /// <summary>The link in the member's workspace that names its folder.</summary>
    public const string LinkName = ".tmpdir";

    /// <summary>The folder's name inside the member's workspace, on Windows.</summary>
    public const string WindowsFolderName = ".tmp";

    /// <summary>The start of every member folder's name in <see cref="Root"/>.</summary>
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
    /// <param name="root">Where the folder is made: the Host's <see cref="RootUnder"/>; <see cref="Root"/> when null.</param>
    public static async Task<string?> EnsureAsync(
        string workspace, AgentLaunchUser? runAs, CancellationToken ct, string? root = null)
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

        root ??= Root;
        var link = LinkFor(workspace);
        if (FolderNamedBy(link, root) is { } existing) return existing;

        var fresh = Path.Combine(root, FolderPrefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..12]);

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

        return FolderNamedBy(link, root);
    }

    /// <summary>
    /// The longest root a member folder may be made in. A tool's socket in TMPDIR is
    /// <c>root/member-&lt;12&gt;/CoreFxPipe_&lt;name&gt;</c>, a socket path holds 103 bytes, and the
    /// longest pipe name met in practice is the compiler server's 43 characters.
    /// </summary>
    public const int MaxRootLength = 28;

    /// <summary>
    /// Where the Host makes member folders: <c>&lt;dataRoot&gt;/tmp</c>, on disk, made when missing.
    /// The entrypoint's volume pass creates it as the agent's; a Host that makes it itself (a
    /// development data root) leaves it open to every user with the sticky bit, as <c>/tmp</c> is,
    /// because the agent makes its folder there. Falls back to <see cref="Root"/>, saying why, when
    /// the path is longer than <see cref="MaxRootLength"/> or the folder cannot be made.
    /// </summary>
    public static MemberTempRoot RootUnder(string dataRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            return new MemberTempRoot(Root, "on Windows each member's folder is <workspace>/.tmp");
        }

        var candidate = Path.Combine(Path.GetFullPath(dataRoot), "tmp");
        if (candidate.Length > MaxRootLength)
        {
            return new MemberTempRoot(Root,
                $"{candidate} is longer than {MaxRootLength} characters, too long for a socket path in it, so they stay in {Root}");
        }

        try
        {
            if (!Directory.Exists(candidate))
            {
                Directory.CreateDirectory(candidate);
                File.SetUnixFileMode(candidate, (UnixFileMode)Convert.ToInt32("1777", 8));
            }

            return new MemberTempRoot(candidate, $"{candidate} is on the data volume, so temporary files cost disk, not memory");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MemberTempRoot(Root, $"{candidate} could not be made ({ex.Message}), so they stay in {Root}");
        }
    }

    /// <summary>
    /// Where member folders are made when the caller names no root: <c>/tmp</c>, never the Host's own temp folder. When the
    /// Host switches users the AGENT makes the folder, so the place must be one every user can
    /// write; <c>/tmp</c> is <c>1777</c> in the image (the entrypoint sees to it) and short enough
    /// for socket paths. The Host's <c>TMPDIR</c> may name a folder the agent cannot write - the
    /// release suite's scratch folder does - and a member there would never start.
    /// </summary>
    public static string Root => OperatingSystem.IsWindows()
        ? Path.TrimEndingDirectorySeparator(Path.GetTempPath())
        : "/tmp";

    /// <summary>
    /// The folder <paramref name="link"/> names, when it is a link to a real member folder in
    /// <paramref name="root"/>; null for anything else, including a folder in another root (a
    /// member whose folder was in /tmp gets a new one in the data root).
    /// </summary>
    private static string? FolderNamedBy(string link, string root)
    {
        try
        {
            if (new FileInfo(link).LinkTarget is not { } target) return null;
            if (Path.GetDirectoryName(target) != root || !FolderName().IsMatch(Path.GetFileName(target))) return null;

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

/// <summary>Where member temporary folders are made, and why there; the Host logs both at start.</summary>
public sealed record MemberTempRoot(string Path, string Reason);
