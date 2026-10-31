using System.Diagnostics;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// THE MCP SERVERS A WORKER RUNS, WITH A REAL SWITCH. Skipped unless this process can really start a child as the
/// <c>mcp</c> user and as the <c>agent</c> user (both <see cref="AgentLaunchUser"/>s switch): run it in the worker image,
/// as the worker's user holding CAP_SETUID and CAP_SETGID, the way the container entrypoint starts the worker, on a
/// volume whose <c>mcp</c> folder the entrypoint prepared. Then the fake server runs as uid 10004, and from the agent's
/// side its environment, its home, its cache, its working folder and its pipes are all refused.
/// </summary>
public sealed class McpStdioRealSwitchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task As_mcp_the_server_runs_as_uid_10004_and_the_agent_reads_none_of_its_environment_home_cache_folder_or_pipes()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        var mcp = McpStdioServers.ResolveUser();
        Assert.SkipUnless(mcp.Switches, "This process cannot start a child as mcp: " + mcp.Reason);
        var agent = AgentLaunchUser.Resolve();
        Assert.SkipUnless(agent.Switches, "This process cannot start a child as agent: " + agent.Reason);

        // NEITHER IN THE OTHER'S GROUP: mcp's group has no member, and agent's group does not hold mcp.
        var groups = File.ReadAllLines("/etc/group").Select(l => l.Split(':')).Where(f => f.Length >= 4).ToDictionary(f => f[0], f => f[3]);
        Assert.Equal("", groups["mcp"]);
        Assert.DoesNotContain("mcp", groups["agent"].Split(','));

        using var fake = new FakeStdioServer(open: true);
        var root = McpStdioServers.RootFor(mcp);
        await using var servers = new McpStdioServers(WorkerId.Local, mcp, root, fake.ServerPath);
        var launch = FakeStdioServer.Launch();

        var answered = await servers.AnswerAsync(
            new McpStdioRequest(WorkerAsks.NewRequest(), launch, McpStdioRequest.Call, "whoami", null, 20, 60), Ct);
        Assert.True(answered.Result is not null, answered.Failure);
        var who = JsonDocument.Parse(answered.Result!["content"]![0]!["text"]!.GetValue<string>()).RootElement;

        Assert.Equal(10004, who.GetProperty("uid").GetInt32());
        Assert.Equal(mcp.Gid, who.GetProperty("gid").GetInt32());
        Assert.All(who.GetProperty("groups").EnumerateArray(), g => Assert.Equal(mcp.Gid, g.GetInt32()));
        var pid = who.GetProperty("pid").GetInt32();
        Assert.Equal("10004", Status(pid, "Uid:").Split('\t', StringSplitOptions.RemoveEmptyEntries)[0]);
        Assert.Equal("0000000000000000", Status(pid, "CapEff:"));
        Assert.Equal("0000000000000000", Status(pid, "CapAmb:"));
        Assert.Equal("700", Mode(root));

        // FROM THE AGENT'S SIDE, every read is refused by the system, not missing.
        var home = who.GetProperty("home").GetString()!;
        var folder = who.GetProperty("cwd").GetString()!;
        foreach (var command in new[]
                 {
                     new[] { "cat", $"/proc/{pid}/environ" },
                     ["ls", home],
                     ["ls", Path.Combine(home, ".npm")],
                     ["ls", Path.Combine(home, ".cache", "uv")],
                     ["ls", folder],
                     ["ls", root],
                     ["ls", $"/proc/{pid}/fd"],
                     ["cat", $"/proc/{pid}/fd/0"],
                 })
        {
            var (code, output) = await AsAgentAsync(agent, command);
            Assert.True(code != 0, $"`{string.Join(' ', command)}` as agent succeeded: {output}");
            Assert.Contains("ermission denied", output);
        }
    }

    private static async Task<(int Code, string Output)> AsAgentAsync(AgentLaunchUser agent, IReadOnlyList<string> command)
    {
        var start = new ProcessStartInfo(agent.Prefix[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var part in agent.Prefix.Skip(1)) start.ArgumentList.Add(part);
        start.ArgumentList.Add(SystemCommand.Find(command[0])!);
        foreach (var part in command.Skip(1)) start.ArgumentList.Add(part);

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(Ct) + await process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        return (process.ExitCode, output);
    }

    private static string Status(int pid, string field) =>
        File.ReadLines($"/proc/{pid}/status").First(l => l.StartsWith(field, StringComparison.Ordinal))[field.Length..].Trim();

    private static string Mode(string path) => Convert.ToString((int)File.GetUnixFileMode(path) & 0x1FF, 8);
}
