using System.Diagnostics;

namespace Harness.Host;

/// <summary>
/// A MEMBER'S OWN TEMPORARY FOLDER: <c>&lt;workspace&gt;/.tmp</c>, handed to its child as
/// <c>TMPDIR</c>.
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
/// </summary>
public static class MemberTemp
{
    /// <summary>The folder's name inside the member's workspace.</summary>
    public const string FolderName = ".tmp";

    /// <summary>The variable the child reads it from.</summary>
    public const string Variable = "TMPDIR";

    /// <summary>Where <paramref name="workspace"/>'s temporary folder is.</summary>
    public static string PathFor(string workspace) => Path.Combine(Path.GetFullPath(workspace), FolderName);

    /// <summary>
    /// The folder for <paramref name="workspace"/>, created when missing; null when the workspace
    /// itself does not exist (the child then runs in the Host's directory, and inherits its
    /// <c>TMPDIR</c> as it always has) or the folder could not be made.
    /// </summary>
    public static async Task<string?> EnsureAsync(string workspace, AgentLaunchUser? runAs, CancellationToken ct)
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
