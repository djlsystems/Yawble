using System.Diagnostics;
using Harness.Host;

namespace Harness.Tests.Host;

/// <summary>
/// A SESSION TEAM CONNECTIONS HANDS A MEMBER IS READABLE BY THE MEMBER: the member runs as the agent and
/// reads the file by its group, so the handoff folder and file take the workspace's group (the agent's,
/// handed down by setgid as prepare-volume.sh makes every team folder), never the Host's own. Seen on a
/// live instance: the folder was set to 0750 without setgid, so the file was harness:harness and the
/// member's browser got "Permission denied".
/// </summary>
public sealed class SessionHandoffTests : IDisposable
{
    /// <summary>A group the Host is not in, standing for the agent's.</summary>
    private const string AgentGroup = "65534";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "harness-handoff-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void A_handed_over_session_takes_the_workspaces_group_so_the_member_can_read_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix groups and modes only.");
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspaces", "Dev")).FullName;
        Assert.SkipWhen(Run("chgrp", AgentGroup, workspace) != 0, $"This process cannot give a folder to group {AgentGroup} (it runs as root in the container).");
        File.SetUnixFileMode(workspace, File.GetUnixFileMode(workspace) | UnixFileMode.SetGroup);

        var file = PluginMemberRunner.WriteHandoff(workspace, "portal-abc.json", "{\"kind\":\"browser\"}", runAs: null);

        var folder = Path.GetDirectoryName(file)!;
        Assert.Equal(Path.Combine(workspace, PluginMemberRunner.HandoffFolder), folder);
        Assert.Equal(AgentGroup, Gid(folder));
        Assert.Equal(AgentGroup, Gid(file));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(file));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute,
            File.GetUnixFileMode(folder) & ~UnixFileMode.SetGroup);
        Assert.Equal("{\"kind\":\"browser\"}", File.ReadAllText(file));
    }

    [Fact]
    public void A_second_handoff_into_an_existing_folder_takes_the_workspaces_group_too()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix groups and modes only.");
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspaces", "Dev")).FullName;
        Assert.SkipWhen(Run("chgrp", AgentGroup, workspace) != 0, $"This process cannot give a folder to group {AgentGroup} (it runs as root in the container).");
        File.SetUnixFileMode(workspace, File.GetUnixFileMode(workspace) | UnixFileMode.SetGroup);

        PluginMemberRunner.WriteHandoff(workspace, "first.json", "{}", runAs: null);
        var second = PluginMemberRunner.WriteHandoff(workspace, "second.json", "{}", runAs: null);

        Assert.Equal(AgentGroup, Gid(second));
    }

    private static int Run(string tool, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }

    private static string Gid(string path)
    {
        var start = new ProcessStartInfo("stat") { RedirectStandardOutput = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("%g");
        start.ArgumentList.Add(path);
        using var process = Process.Start(start)!;
        var gid = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return gid;
    }
}
