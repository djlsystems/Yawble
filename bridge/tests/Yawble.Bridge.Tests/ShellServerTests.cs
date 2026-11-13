using System.Text.Json;
using Yawble.Bridge;
using Xunit;

namespace Yawble.Bridge.Tests;

public class ShellServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement[]> Run(params string[] lines)
    {
        var output = new StringWriter { NewLine = "\n" };
        await new ShellServer(Directory.GetCurrentDirectory()).RunAsync(new StringReader(string.Join("\n", lines) + "\n"), output, TextWriter.Null, Ct);
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();
    }

    [Fact]
    public async Task ItInitializes_ListsOneShellTool_AndIgnoresNotifications()
    {
        var replies = await Run(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        Assert.Equal(2, replies.Length);
        Assert.Equal("2025-06-18", replies[0].GetProperty("result").GetProperty("protocolVersion").GetString());
        var tools = replies[1].GetProperty("result").GetProperty("tools");
        Assert.Equal(ShellServer.ToolName, Assert.Single(tools.EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task RunShell_ReturnsTheOutputAndTheExitCode()
    {
        var replies = await Run(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"run_shell","arguments":{"command":"echo yawble-shell-ok; exit 3"}}}""");
        var result = Assert.Single(replies).GetProperty("result");
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("yawble-shell-ok", text);
        Assert.Contains("exit code 3", text);
        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task RunShell_StopsACommandAtItsTimeout()
    {
        var command = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 30" : "sleep 30";
        var started = DateTime.UtcNow;
        var replies = await Run(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"run_shell","arguments":{"command":""" + "\"" + command + "\""
            + ""","timeout_sec":2}}}""");
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
        Assert.Contains("timed out", Assert.Single(replies).GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task AnUnknownMethodIsAJsonRpcError()
    {
        var replies = await Run("""{"jsonrpc":"2.0","id":5,"method":"resources/list"}""");
        Assert.Equal(-32601, Assert.Single(replies).GetProperty("error").GetProperty("code").GetInt32());
    }
}
