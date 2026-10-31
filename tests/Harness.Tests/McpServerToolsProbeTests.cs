using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// TESTER PROBES FOR B007G (MCP 2), beyond what McpServerToolsTests asserts. Each test states the behaviour
/// the spec asks for; a failing one is a defect report, not a test to keep green by changing it.
/// </summary>
public sealed class McpServerToolsProbeTests
{
    private static CancellationToken Ct => McpServerToolsBed.Ct;

    [Fact]
    public void Names_of_50_characters_are_kept_and_51_are_cut_to_50_with_a_hash()
    {
        var fifty = McpToolNames.For("fake", new string('a', 45));
        Assert.Equal(50, fifty.Length);
        Assert.Equal("fake_" + new string('a', 45), fifty);

        var fiftyOne = McpToolNames.For("fake", new string('a', 46));
        Assert.Equal(50, fiftyOne.Length);
        Assert.Matches("_[0-9a-f]{8}$", fiftyOne);
        Assert.Equal(64, (McpToolNames.CliPrefix + fiftyOne).Length);
    }

    [Fact]
    public async Task An_output_path_that_is_a_link_to_a_file_outside_the_working_copy_is_refused()
    {
        await using var bed = await McpServerToolsBed.StartAsync();
        var outside = Path.Combine(Path.GetTempPath(), $"probe-outside-{Guid.NewGuid():N}.png");
        await File.WriteAllTextAsync(outside, "untouched", Ct);

        JsonObject? answer = null;
        try
        {
            await bed.RunAsync("Alpha", "Dev", async run =>
            {
                File.CreateSymbolicLink(Path.Combine(run.WorkingDirectory, "pic.png"), outside);
                using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
                answer = await mcp.CallAsync("fake_draw", new JsonObject { ["outputPath"] = "pic.png" });
            });

            Assert.Equal("untouched", await File.ReadAllTextAsync(outside, Ct));
            Assert.True(HarnessMcp.IsError(answer!), answer!.ToJsonString());
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task The_default_folder_as_a_link_to_a_folder_outside_the_working_copy_is_not_written_through()
    {
        await using var bed = await McpServerToolsBed.StartAsync();
        var outside = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"probe-outside-{Guid.NewGuid():N}")).FullName;

        JsonObject? answer = null;
        try
        {
            await bed.RunAsync("Alpha", "Dev", async run =>
            {
                Directory.CreateSymbolicLink(Path.Combine(run.WorkingDirectory, ".harness"), outside);
                using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
                answer = await mcp.CallAsync("fake_draw");
            });

            Assert.Empty(Directory.GetFiles(outside, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task A_clash_across_two_saved_entries_fails_the_second_save_naming_both()
    {
        await using var bed = await McpServerToolsBed.StartAsync();
        var connection = (await bed.PostAsync("/api/connections/credentials", new
        {
            kind = "apikey", preset = "other", name = "Fake key 2", fields = new[] { new { name = "token", value = McpServerToolsBed.Token, masked = true } },
        })).GetProperty("connection").GetProperty("id").GetString()!;

        // fake lists echo_credential (fake_echo_credential); fake_echo lists credential (fake_echo_credential).
        bed.Upstream.Extra.Add("credential");
        var saved = await bed.PostAsync("/api/mcp-servers", new { name = "fake_echo", kind = "url", url = McpServerToolsBed.Url, connection });

        var check = saved.GetProperty("check");
        Assert.Equal("failed", check.GetProperty("outcome").GetString());
        var sentence = check.GetProperty("sentence").GetString()!;
        Assert.Contains("`echo_credential` of fake", sentence, StringComparison.Ordinal);
        Assert.Contains("`credential` of fake_echo", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_tool_named_like_a_platform_tool_is_not_listed_twice_and_does_not_shadow()
    {
        await using var bed = await McpServerToolsBed.StartAsync();
        var connection = (await bed.PostAsync("/api/connections/credentials", new
        {
            kind = "apikey", preset = "other", name = "Fake key 3", fields = new[] { new { name = "token", value = McpServerToolsBed.Token, masked = true } },
        })).GetProperty("connection").GetProperty("id").GetString()!;

        // An entry named "skills" whose server offers "get" would be skills_get, the platform's own tool.
        bed.Upstream.Extra.Add("get");
        var saved = await bed.PostAsync("/api/mcp-servers", new { name = "skills", kind = "url", url = McpServerToolsBed.Url, connection });
        var outcome = saved.GetProperty("check").GetProperty("outcome").GetString();
        var id = saved.GetProperty("server").GetProperty("id").GetString()!;
        await bed.PostAsync("/api/teams/Alpha/mcp-servers", new { server = id });

        IReadOnlyList<JsonObject> listed = [];
        await bed.RunAsync("Alpha", "Dev", async run =>
        {
            using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
            listed = await mcp.ListAsync();
        });

        var count = listed.Count(t => t["name"]!.GetValue<string>() == "skills_get");
        Assert.True(count == 1, $"save outcome {outcome}; skills_get listed {count} times");
    }

    [Fact]
    public async Task A_binary_too_big_for_the_body_limit_still_says_what_was_dropped()
    {
        // 50 MB of image: 66.7 MB as base64, past the 64 MB body McpForwarding reads.
        var forwarding = new McpForwarding(new BigImage(50 * 1024 * 1024));
        var target = new McpServers.McpTarget("https://big.example.test/mcp", new Dictionary<string, string>(), [], null);

        var answer = await forwarding.CallAsync(target, "draw", null, Ct);

        Assert.True(answer.Result is not null, $"Failure: {answer.Failure}; Error: {answer.Error}; NoAnswer: {answer.NoAnswer}");
    }

    [Fact]
    public async Task The_default_call_timeout_is_120_seconds_in_practice()
    {
        var forwarding = new McpForwarding(new Silent());
        var target = new McpServers.McpTarget("https://silent.example.test/mcp", new Dictionary<string, string>(), [], null);

        var clock = Stopwatch.StartNew();
        var answer = await forwarding.CallAsync(target, "wait", null, Ct);
        clock.Stop();

        Assert.True(answer.NoAnswer);
        Assert.InRange(clock.Elapsed.TotalSeconds, 119.5, 125);
    }

    private sealed class BigImage(int bytes) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            if (body["id"] is null) return new HttpResponseMessage(HttpStatusCode.Accepted);

            var result = body["method"]!.GetValue<string>() == "initialize"
                ? new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject() }
                : new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "image", ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(new byte[bytes]),
                    }),
                };
            var answer = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = body["id"]!.DeepClone(), ["result"] = result };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Silent : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            if (body["id"] is null) return new HttpResponseMessage(HttpStatusCode.Accepted);
            if (body["method"]!.GetValue<string>() != "initialize") await Task.Delay(Timeout.InfiniteTimeSpan, ct);

            var answer = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = body["id"]!.DeepClone(),
                ["result"] = new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject() },
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
}
