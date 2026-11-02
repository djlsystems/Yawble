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

    /// <summary>
    /// Seen on a live instance: a member had made the folder itself (agent:agent 2770), so the Host, not
    /// its owner, was refused the mode change with EPERM and handed nothing over, though the folder let it
    /// write. The refusal is played here, since the suite runs as root, which no chmod refuses.
    /// </summary>
    [Fact]
    public void A_folder_the_host_may_not_change_the_mode_of_still_gets_the_session()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix modes only.");
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspaces", "Dev")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(workspace, PluginMemberRunner.HandoffFolder));
        var before = File.GetUnixFileMode(folder.FullName);

        var file = PluginMemberRunner.WriteHandoff(workspace, "portal-abc.json", "{\"kind\":\"browser\"}", runAs: null,
            static (_, _) => throw new UnauthorizedAccessException("Access to the path is denied."));

        Assert.Equal("{\"kind\":\"browser\"}", File.ReadAllText(file));
        Assert.Equal(before, File.GetUnixFileMode(folder.FullName));
    }

    /// <summary>
    /// CON 14: a handoff's expiry is in its NAME, so a plain storage state - nothing inside to read - is swept
    /// when its time is up, at a Host's start (whose last timers are gone) and at the next handoff alike.
    /// </summary>
    [Fact]
    public void An_expired_hand_off_is_swept_by_the_expiry_in_its_name_and_one_still_good_is_kept()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspaces", "Dev")).FullName;
        const string Plain = "{\"cookies\":[],\"origins\":[]}";
        var until = DateTimeOffset.UtcNow.AddMinutes(-1);
        var good = PluginMemberRunner.WriteHandoff(workspace, PluginMemberRunner.HandoffName("Portal", DateTimeOffset.UtcNow.AddMinutes(30)), Plain, runAs: null);
        var expired = PluginMemberRunner.WriteHandoff(workspace, PluginMemberRunner.HandoffName("Portal", until), Plain, runAs: null);
        Assert.True(File.Exists(expired));
        Assert.Equal(until.ToUnixTimeSeconds(), PluginMemberRunner.HandoffExpiry(expired)!.Value.ToUnixTimeSeconds());

        PluginMemberRunner.SweepHandoffsAtStart(workspace);

        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(good));
        Assert.Equal(Plain, File.ReadAllText(good));

        // And at the next handoff: the one written already expired goes.
        var third = PluginMemberRunner.WriteHandoff(workspace, PluginMemberRunner.HandoffName("Portal", DateTimeOffset.UtcNow.AddMinutes(-1)), Plain, runAs: null);
        Assert.True(File.Exists(third));
        PluginMemberRunner.WriteHandoff(workspace, PluginMemberRunner.HandoffName("Portal", DateTimeOffset.UtcNow.AddMinutes(30)), Plain, runAs: null);
        Assert.False(File.Exists(third));
        Assert.True(File.Exists(good));
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
