using System.Diagnostics;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A4: a plugin member runs as the `agent` user, as an agent does, and
/// can read its installed directory, which is the Host's and <c>0750</c> to the agent's group - the
/// layout <c>docs/plugins.md</c> installs. A REAL SWITCH: where this process cannot start a child
/// as `agent` (no such user, no capability, no setpriv) it skips and says which. The release suite
/// runs as root in the product image, where it runs for real.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PluginLaunchUserTests : IDisposable
{
    private readonly string _dataRoot = Directory.CreateTempSubdirectory("harness-plugin-runas-").FullName;
    private readonly string _workspace = Directory.CreateTempSubdirectory("harness-plugin-runas-ws-").FullName;

    public PluginLaunchUserTests() =>
        // The workspace belongs to the agent in the image; here it only has to let the agent in.
        File.SetUnixFileMode(_workspace, (UnixFileMode)0b111_111_111);

    public void Dispose()
    {
        Directory.Delete(_dataRoot, recursive: true);
        Directory.Delete(_workspace, recursive: true);
    }

    private sealed class NoReports : IMemberReports
    {
        private static Task<MemberReportOutcome> Refused() => throw new InvalidOperationException("this probe reports nothing");

        public Task<MemberReportOutcome> ProgressAsync(ContainerId member, string status, CancellationToken ct = default) => Refused();
        public Task<MemberReportOutcome> BlockedAsync(ContainerId member, string reason, int? item = null, CancellationToken ct = default) => Refused();
        public Task<MemberReportOutcome> NeedsDecisionAsync(ContainerId member, string question, CancellationToken ct = default) => Refused();
        public Task<MemberReportOutcome> HandbackAsync(ContainerId member, string delivered, CancellationToken ct = default) => Refused();
        public Task<MemberReportOutcome> PublishAsync(ContainerId member, string type, string payload, CancellationToken ct = default) => Refused();
    }

    [Fact]
    public async Task A_plugin_runs_as_the_agent_user_and_reads_its_0750_directory()
    {
        var runAs = AgentLaunchUser.Resolve(AgentLaunchUser.DefaultName);
        if (!runAs.Switches) Assert.Skip($"This process cannot switch to the `{AgentLaunchUser.DefaultName}` user: {runAs.Reason}.");

        var version = PluginInstall.Write(_dataRoot, "probe", script:
            """
            cat >/dev/null
            dir=$(cd "$(dirname "$0")/.." && pwd)
            who="$(id -u) $(id -g) $(id -un)"
            mode=$(stat -c '%g %a' "$dir")
            ls "$dir" >/dev/null 2>&1 && listed=listed || listed=not-listed
            grep -q schemaVersion "$dir/plugin.json" && read=read || read=not-read
            echo "{\"t\":\"result\",\"ok\":true,\"output\":\"$who|$mode|$listed $read\"}"
            """);

        // INSTALLED AS docs/plugins.md SAYS: the Host's files, the agent's group, 0750 directories,
        // 0640 files, and the executable 0750.
        Run("chgrp", "-R", runAs.Gid.ToString(), Path.Combine(_dataRoot, "plugins"));
        foreach (var directory in Directory.EnumerateDirectories(Path.Combine(_dataRoot, "plugins"), "*", SearchOption.AllDirectories)
                     .Append(Path.Combine(_dataRoot, "plugins")))
        {
            File.SetUnixFileMode(directory, (UnixFileMode)0b111_101_000);
        }

        foreach (var file in Directory.EnumerateFiles(Path.Combine(_dataRoot, "plugins"), "*", SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(file, file.EndsWith(Path.Combine("bin", "run"), StringComparison.Ordinal)
                ? (UnixFileMode)0b111_101_000
                : (UnixFileMode)0b110_100_000);
        }

        var catalog = new PluginCatalog(PluginInstall.PluginsRoot(_dataRoot));
        catalog.Rescan();
        Assert.NotNull(catalog.For("probe"));

        var result = await new PluginMemberRunner(catalog, new NoReports(), new RunHeartbeat(), runAs).RunAsync(
            new MemberInvocation(
                new ContainerId("alpha", "probe"),
                MemberRef.ForPlugin("probe"),
                [new Message(1, MessageTypes.InstructionFor(new ContainerId("alpha", "probe")), """{"instruction":"hi"}""", "person", 1, null, 0, DateTimeOffset.UtcNow)],
                _workspace,
                new Dictionary<string, string>(),
                new MemberRunContext(0, ArtifactLimits.Default, [], null, null, "")),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Output + result.LaunchError);
        var parts = result.Output.Split('|');

        // AS `agent`, and not as the Host.
        Assert.Equal($"{runAs.Uid} {runAs.Gid} {AgentLaunchUser.DefaultName}", parts[0]);
        Assert.NotEqual(Run("id", "-u"), runAs.Uid.ToString());

        // Its directory stayed the agent group's and 0750, and the plugin read it.
        Assert.Equal($"{runAs.Gid} 750", parts[1]);
        Assert.Equal("listed read", parts[2]);
        Assert.Equal($"{runAs.Gid} 750", Run("stat", "-c", "%g %a", version));
    }

    private static string Run(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }
}
