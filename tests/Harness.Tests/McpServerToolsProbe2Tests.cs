using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// TESTER PROBES FOR MCP 2, ROUND 2: links anywhere on a binary write's path, the temp name, the folder-swap race,
/// the platform's tool names in both clash checks, and the sentence for an answer past the body limit.
/// </summary>
public sealed class McpServerToolsProbe2Tests
{
    private static CancellationToken Ct => McpServerToolsBed.Ct;

    [Theory]
    [InlineData(".harness")]
    [InlineData(".harness/mcp")]
    [InlineData(".harness/mcp/fake")]
    public async Task A_link_at_any_level_of_the_default_folder_is_not_written_through(string linked)
    {
        await using var bed = await McpServerToolsBed.StartAsync();
        var outside = Directory.CreateTempSubdirectory("probe2-outside-").FullName;
        JsonObject? answer = null;
        try
        {
            await bed.RunAsync("Alpha", "Dev", async run =>
            {
                var at = Path.Combine(run.WorkingDirectory, linked);
                Directory.CreateDirectory(Path.GetDirectoryName(at)!);
                Directory.CreateSymbolicLink(at, outside);
                using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
                answer = await mcp.CallAsync("fake_draw");
            });

            Assert.Empty(Directory.GetFileSystemEntries(outside, "*", SearchOption.AllDirectories));
            Assert.True(HarnessMcp.IsError(answer!), answer!.ToJsonString());
            Assert.Contains("link", answer!.ToJsonString());
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Theory]
    [InlineData("sub/deeper/pic.png", "sub")]
    [InlineData("sub/deeper/pic.png", "sub/deeper")]
    public async Task A_link_on_an_output_paths_folders_is_refused(string outputPath, string linked)
    {
        await using var bed = await McpServerToolsBed.StartAsync();
        var outside = Directory.CreateTempSubdirectory("probe2-outside-").FullName;
        JsonObject? answer = null;
        try
        {
            await bed.RunAsync("Alpha", "Dev", async run =>
            {
                var at = Path.Combine(run.WorkingDirectory, linked);
                Directory.CreateDirectory(Path.GetDirectoryName(at)!);
                Directory.CreateSymbolicLink(at, outside);
                using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
                answer = await mcp.CallAsync("fake_draw", new JsonObject { ["outputPath"] = outputPath });
            });

            Assert.Empty(Directory.GetFileSystemEntries(outside, "*", SearchOption.AllDirectories));
            Assert.True(HarnessMcp.IsError(answer!), answer!.ToJsonString());
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task A_dangling_link_at_the_output_path_creates_nothing_outside()
    {
        await using var bed = await McpServerToolsBed.StartAsync();
        var outside = Directory.CreateTempSubdirectory("probe2-outside-").FullName;
        var target = Path.Combine(outside, "made.png");
        JsonObject? answer = null;
        string? linkStill = null;
        try
        {
            await bed.RunAsync("Alpha", "Dev", async run =>
            {
                var link = Path.Combine(run.WorkingDirectory, "pic.png");
                File.CreateSymbolicLink(link, target);
                using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
                answer = await mcp.CallAsync("fake_draw", new JsonObject { ["outputPath"] = "pic.png" });
                linkStill = new FileInfo(link).LinkTarget;
            });

            Assert.False(File.Exists(target));
            Assert.Equal(target, linkStill);
            Assert.True(HarnessMcp.IsError(answer!), answer!.ToJsonString());
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Theory]
    [InlineData("../escape.png")]
    [InlineData("/tmp/probe2-absolute-escape.png")]
    public async Task An_output_path_outside_the_working_copy_is_refused(string outputPath)
    {
        await using var bed = await McpServerToolsBed.StartAsync();
        JsonObject? answer = null;
        string? escaped = null;
        await bed.RunAsync("Alpha", "Dev", async run =>
        {
            escaped = Path.GetFullPath(Path.Combine(run.WorkingDirectory, outputPath));
            using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
            answer = await mcp.CallAsync("fake_draw", new JsonObject { ["outputPath"] = outputPath });
        });

        Assert.False(File.Exists(escaped), escaped);
        Assert.True(HarnessMcp.IsError(answer!), answer!.ToJsonString());
    }

    [Fact]
    public void An_exclusive_create_does_not_follow_a_link_already_at_the_temp_name()
    {
        // The temp name carries a fresh GUID, so it cannot be predicted; even if it were, CreateNew (O_CREAT|O_EXCL)
        // refuses a name that is already a link, dangling or not, and creates nothing at the link's target.
        var root = Directory.CreateTempSubdirectory("probe2-temp-").FullName;
        var outside = Directory.CreateTempSubdirectory("probe2-outside-").FullName;
        try
        {
            var temp = Path.Combine(root, ".x.part");
            File.CreateSymbolicLink(temp, Path.Combine(outside, "made"));
            Assert.Throws<IOException>(() => new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose());
            Assert.Empty(Directory.GetFileSystemEntries(outside));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task A_folder_swapped_for_a_link_while_writing_never_carries_bytes_outside()
    {
        // The residual Soren names: a member swaps an already-checked folder for a link before the temp file is made
        // or renamed. Run Write in a loop while another thread flips `d` between a real folder and a link outside.
        var root = Directory.CreateTempSubdirectory("probe2-race-").FullName;
        var outside = Directory.CreateTempSubdirectory("probe2-outside-").FullName;
        var real = Path.Combine(root, "d");
        var parked = Path.Combine(root, "parked");
        var link = Path.Combine(root, "lnk");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(link, outside);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var attempts = 0;
        long swaps = 0, swapFailures = 0;
        try
        {
            var flipper = Task.Factory.StartNew(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    // renameat2(RENAME_EXCHANGE): `d` and `lnk` trade places atomically, so `d` is always present.
                    if (renameat2(-100, real, -100, link, 2) != 0) Interlocked.Increment(ref swapFailures);
                    else Interlocked.Increment(ref swaps);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            while (!stop.IsCancellationRequested)
            {
                attempts++;
                try { McpRunTools.Write(root, Path.Combine(real, "pic.png"), [1, 2, 3]); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                if (Directory.GetFileSystemEntries(outside).Length > 0) break;
            }

            stop.Cancel();
            await flipper;

            var escaped = Directory.GetFileSystemEntries(outside);
            Assert.True(escaped.Length == 0, $"after {attempts} writes and {swaps} swaps ({swapFailures} failed), outside holds: {string.Join(", ", escaped.Select(Path.GetFileName))}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int renameat2(int olddirfd, string oldpath, int newdirfd, string newpath, uint flags);

    [Fact]
    public void The_platform_names_are_every_tool_the_harness_server_lists()
    {
        string[] expected =
        [
            "skills_get", "skills_search", "tell", "progress", "blocked", "handback", "needs_decision", "wake_me",
            "workflow_complete", "workflow_show", "team_list", "team_current", "team_create", "team_archive", "wip",
            "lease", "status", "hiring", "member", "kanban", "backlog", "repo", "preview", "site", "outcome",
        ];
        Assert.Equal(expected.Order(), McpToolNames.Platform.Order());
    }

    [Fact]
    public async Task A_platform_name_clash_fails_the_save_naming_both_and_the_listed_tool_is_the_platforms()
    {
        await using var bed = await McpServerToolsBed.StartAsync();
        var connection = (await bed.PostAsync("/api/connections/credentials", new
        {
            kind = "apikey", preset = "other", name = "Fake key 4", fields = new[] { new { name = "token", value = McpServerToolsBed.Token, masked = true } },
        })).GetProperty("connection").GetProperty("id").GetString()!;

        bed.Upstream.Extra.Add("get");
        var saved = await bed.PostAsync("/api/mcp-servers", new { name = "skills", kind = "url", url = McpServerToolsBed.Url, connection });
        var check = saved.GetProperty("check");
        var id = saved.GetProperty("server").GetProperty("id").GetString()!;
        await bed.PostAsync("/api/teams/Alpha/mcp-servers", new { server = id });

        IReadOnlyList<JsonObject> listed = [];
        JsonObject? called = null;
        await bed.RunAsync("Alpha", "Dev", async run =>
        {
            using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
            listed = await mcp.ListAsync();
            called = await mcp.CallAsync("skills_get", new JsonObject { ["name"] = "member" });
        });

        Assert.NotEqual("ok", check.GetProperty("outcome").GetString());
        var said = check.ToString();
        Assert.Contains("`get` of skills", said);
        Assert.Contains("platform's own tool `skills_get`", said);
        var skillsGet = Assert.Single(listed, t => t["name"]!.GetValue<string>() == "skills_get");
        Assert.DoesNotContain("fake", skillsGet["description"]!.GetValue<string>());
        Assert.DoesNotContain("fake get", called!.ToJsonString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_answer_past_64_MB_is_an_error_result_saying_what_was_dropped(bool eventStream)
    {
        var forwarding = new McpForwarding(new Big(50 * 1024 * 1024, eventStream));
        var target = new McpServers.McpTarget("https://big.example.test/mcp", new Dictionary<string, string>(), [], null);

        var answer = await forwarding.CallAsync(target, "draw", null, Ct);

        Assert.True(answer.Result is not null, $"Failure: {answer.Failure}; Error: {answer.Error}; NoAnswer: {answer.NoAnswer}");
        var text = answer.Result!.ToJsonString();
        Assert.True(answer.Result!["isError"]!.GetValue<bool>());
        Assert.Contains("more than 64 MB and was dropped whole", text);
        Assert.DoesNotContain("did not answer as an MCP server", text);
    }

    private sealed class Big(int bytes, bool eventStream) : HttpMessageHandler
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
            var answer = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = body["id"]!.DeepClone(), ["result"] = result }.ToJsonString();
            return eventStream && body["method"]!.GetValue<string>() != "initialize"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"event: message\ndata: {answer}\n\n", Encoding.UTF8, "text/event-stream") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }
}
