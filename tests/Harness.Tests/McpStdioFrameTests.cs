using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// AN MCP SERVER THE PLATFORM RUNS, ON THE WORKER CONNECTION: the variables a request starts it with are connection
/// values, so they cross only sealed and never print; an answer may carry a 20 MB binary result, so that one event has
/// the room a write as the member has, and no other event does; and a request and its answer meet under one id.
/// </summary>
public sealed class McpStdioFrameTests
{
    private static readonly byte[] Key = WorkerFrameCodec.Key("the-worker-key", "control-nonce", "worker-nonce");

    private const string Secret = "frame-secret-0c0ffee";

    private static McpStdioRequest Request() => new(
        "r1", FakeStdioServer.Launch(new Dictionary<string, string> { ["API_TOKEN"] = Secret }), McpStdioRequest.Call, "echo",
        new JsonObject { ["said"] = "hi" });

    [Fact]
    public void The_environment_crosses_sealed_and_opens_on_the_other_side()
    {
        var codec = new WorkerFrameCodec(Key);

        var text = codec.Write(new CommandFrame(4, Request()));

        Assert.DoesNotContain(Secret, text);
        var back = Assert.IsType<McpStdioRequest>(Assert.IsType<CommandFrame>(codec.Read(text)).Message);
        Assert.Equal(Secret, back.Launch.Environment["API_TOKEN"]);
        Assert.Equal("hi", back.Arguments!["said"]!.GetValue<string>());
        Assert.Equal(["npx", "-y", "@fake/server@1.2.3", "--flag", "two words"], back.Launch.CommandLine);

        // Before a connection's key is made, no value may cross at all.
        Assert.Throws<InvalidOperationException>(() => new WorkerFrameCodec(null).Write(new CommandFrame(4, Request())));
    }

    [Fact]
    public void A_launch_never_prints_its_values()
    {
        var launch = Request().Launch;

        Assert.DoesNotContain(Secret, launch.ToString());
        Assert.DoesNotContain(Secret, Request().ToString());
        Assert.Contains("API_TOKEN", launch.ToString());
    }

    [Fact]
    public void An_answer_has_room_for_20_MB_of_binary_and_no_other_event_does()
    {
        var codec = new WorkerFrameCodec(Key);
        var data = Convert.ToBase64String(new byte[20 * 1024 * 1024]);
        var result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = data }) };

        var text = codec.Write(new EventFrame(new WorkerEnvelope(WorkerId.Local, 1, new McpStdioAnswered("r1", result, null, false, null))));

        Assert.True(Encoding.UTF8.GetByteCount(text) > WorkerFrameCodec.MaxBytes);
        var answered = Assert.IsType<McpStdioAnswered>(Assert.IsType<EventFrame>(codec.Read(text)).Envelope.Event);
        Assert.Equal(data, answered.Result!["content"]![0]!["data"]!.GetValue<string>());

        var over = new JsonObject { ["text"] = new string('x', WorkerFrameCodec.MaxWriteBytes) };
        Assert.Equal(WorkerFrameCodec.WriteTooLargeText, Assert.Throws<InvalidDataException>(
            () => codec.Write(new EventFrame(new WorkerEnvelope(WorkerId.Local, 2, new McpStdioAnswered("r2", over, null, false, null))))).Message);

        // Another event of the same size, naming an answer in its text, is still refused.
        var output = new EventFrame(new WorkerEnvelope(WorkerId.Local, 3, new RunOutput(
            new RunId(new ContainerId("t", "m"), "n"), "\"mcpStdioAnswered\"" + new string('x', WorkerFrameCodec.MaxBytes))));
        var forged = JsonSerializer.Serialize<WorkerFrame>(output, WorkerFrameCodec.Json);
        Assert.Equal(WorkerFrameCodec.TooLargeText, Assert.Throws<InvalidDataException>(() => codec.Read(forged)).Message);
    }

    [Fact]
    public async Task A_request_through_an_in_process_worker_is_answered_under_its_id()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        using var fake = new FakeStdioServer();
        await using var servers = fake.Servers();
        var asks = WorkerAsks.InProcess(null, stdio: servers);

        var asked = await asks.AskAsync<McpStdioAnswered>(Request() with { Request = WorkerAsks.NewRequest() }, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal("{\"said\": \"hi\"}", asked.Answer!.Result!["content"]![0]!["text"]!.GetValue<string>());

        var stopped = await asks.AskAsync<McpStdioAnswered>(new McpStdioStop(WorkerAsks.NewRequest(), "mcp-fake"), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.NotNull(stopped.Answer);
        Assert.Null(servers.Pid("mcp-fake"));
    }
}
