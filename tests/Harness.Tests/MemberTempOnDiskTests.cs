using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A MEMBER'S TMPDIR IS ON THE DATA VOLUME. An engine may mount /tmp as tmpfs, where a member's
/// temporary files cost the container's memory; the Host's root for them is <c>&lt;dataRoot&gt;/tmp</c>,
/// and /tmp only when the data root's path is too long for a socket inside it.
/// </summary>
public sealed class MemberTempOnDiskTests : IDisposable
{
    // A data root as short as the image's /data, so the folder is chosen rather than the fallback.
    private readonly string _data = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath().StartsWith("/tmp", StringComparison.Ordinal) ? "/tmp" : Path.GetTempPath(),
            "y" + Guid.NewGuid().ToString("N")[..6])).FullName;

    public void Dispose()
    {
        MemberTempCleanup.Remove(_data);
        Directory.Delete(_data, recursive: true);
    }

    [Fact]
    public async Task A_members_TMPDIR_is_made_under_the_data_root_and_not_in_tmp()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "On Windows a member's folder is <workspace>/.tmp.");
        Assert.SkipWhen(Path.Combine(_data, "tmp").Length > MemberTemp.MaxRootLength,
            $"This machine's temp folder makes '{_data}' too long for a data root to hold member folders.");

        var root = MemberTemp.RootUnder(_data);
        Assert.Equal(Path.Combine(_data, "tmp"), root.Path);
        Assert.Contains("on the data volume", root.Reason);

        // Made open to every user with the sticky bit, as /tmp is: the agent makes its folder there.
        Assert.Equal((UnixFileMode)Convert.ToInt32("1777", 8), File.GetUnixFileMode(root.Path));

        var bin = Directory.CreateDirectory(Path.Combine(_data, "bin")).FullName;
        await TestExecutable.WriteAsync(Path.Combine(bin, "probe"), "#!/bin/sh\ncat >/dev/null\necho \"TMPDIR=$TMPDIR\"\n");
        var catalog = new AgentCatalog(
            [new AgentDefinition("probe", AgentMode.Headless, new AgentLaunch(Path.Combine(bin, "probe"), [], LanguageModel: false))]);
        var runner = new ProcessAgentRunner(catalog, new RunHeartbeat(), temp: root);

        var workspace = Directory.CreateDirectory(Path.Combine(_data, "teams", "alpha", "workspaces", "Worker")).FullName;

        // A member whose link still names a folder in /tmp, from before, is given a new one on disk.
        var old = Directory.CreateDirectory(Path.Combine("/tmp", MemberTemp.FolderPrefix + Guid.NewGuid().ToString("N")[..12])).FullName;
        File.CreateSymbolicLink(MemberTemp.LinkFor(workspace), old);
        try
        {
            var result = await runner.RunAsync(
                new AgentInvocation(new ContainerId("alpha", "Worker"), "You are a probe.", "print TMPDIR", workspace,
                    new Dictionary<string, string> { ["TMPDIR"] = "/tmp" }, Agent: "probe"),
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            var tmpdir = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith("TMPDIR=", StringComparison.Ordinal))["TMPDIR=".Length..];

            Assert.Equal(root.Path, Path.GetDirectoryName(tmpdir));
            Assert.True(Directory.Exists(tmpdir));
            Assert.Equal(tmpdir, new FileInfo(MemberTemp.LinkFor(workspace)).LinkTarget);
        }
        finally
        {
            Directory.Delete(old);
        }
    }

    [Fact]
    public void A_data_root_too_long_for_a_socket_path_keeps_member_folders_in_tmp_and_says_why()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "On Windows a member's folder is <workspace>/.tmp.");

        var deep = Path.Combine(_data, "a-data-root-with-a-long-name");
        var root = MemberTemp.RootUnder(deep);

        Assert.Equal(MemberTemp.Root, root.Path);
        Assert.Contains("too long for a socket path", root.Reason);
        Assert.False(Directory.Exists(Path.Combine(deep, "tmp")));
    }
}
