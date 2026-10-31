using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// MCP 2 - MEMBERS USE THEIR TEAM'S MCP SERVERS THROUGH HARNESS, on the real Host against a fake web address
/// server (<see cref="McpServerToolsBed"/>). One test per Done-when line, and the limits around them.
/// </summary>
public sealed class McpServerToolsTests
{
    private static CancellationToken Ct => McpServerToolsBed.Ct;

    private static string Named(string tool) => McpToolNames.For(McpServerToolsBed.Server, tool);

    [Fact]
    public async Task A_teams_member_lists_its_servers_tools_and_another_teams_does_not_and_a_call_from_it_is_refused_naming_it()
    {
        await using var bed = await McpServerToolsBed.StartAsync();

        IReadOnlyList<JsonObject> alpha = [];
        JsonObject? called = null;
        await bed.RunAsync("Alpha", "Dev", async run =>
        {
            using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
            alpha = await mcp.ListAsync();
            called = await mcp.CallAsync("fake_echo_credential", new JsonObject { ["message"] = "hello" });
        });

        // THE TEAM'S TOOLS, named <server>_<tool>, with the server's description and schema as it gave them.
        var echo = alpha.Single(t => t["name"]!.GetValue<string>() == "fake_echo_credential");
        Assert.Equal(FakeToolServer.Description, echo["description"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(FakeToolServer.EchoSchema(), echo["inputSchema"]), echo["inputSchema"]!.ToJsonString());
        Assert.Contains(alpha, t => t["name"]!.GetValue<string>() == "fake_draw");
        Assert.Contains(alpha, t => t["name"]!.GetValue<string>() == "skills_get");
        Assert.False(HarnessMcp.IsError(called!), called!.ToJsonString());
        Assert.Contains("you said hello", HarnessMcp.Text(called!), StringComparison.Ordinal);

        IReadOnlyList<JsonObject> beta = [];
        JsonObject? refused = null;
        await bed.RunAsync("Beta", "Dev", async run =>
        {
            using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
            beta = await mcp.ListAsync();
            refused = await mcp.CallAsync("fake_echo_credential", new JsonObject { ["message"] = "hello" });
        });

        Assert.DoesNotContain(beta, t => t["name"]!.GetValue<string>().StartsWith("fake_", StringComparison.Ordinal));
        Assert.True(HarnessMcp.IsError(refused!), refused!.ToJsonString());
        Assert.Equal("fake is not one of Beta's MCP servers.", HarnessMcp.Text(refused!));
        Assert.Equal(["echo_credential"], bed.Upstream.Called);
    }

    [Fact]
    public async Task An_entry_taken_off_the_team_during_the_run_is_refused_at_the_call()
    {
        await using var bed = await McpServerToolsBed.StartAsync();

        JsonObject? refused = null;
        await bed.RunAsync("Alpha", "Dev", async run =>
        {
            using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
            Assert.Contains(await mcp.ListAsync(), t => t["name"]!.GetValue<string>() == "fake_draw");

            var removed = await bed.Person.DeleteAsync($"/api/teams/Alpha/mcp-servers/{bed.ServerId}", Ct);
            Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync(Ct));

            refused = await mcp.CallAsync("fake_draw");
        });

        Assert.Equal("fake is not one of Alpha's MCP servers.", HarnessMcp.Text(refused!));
        Assert.Empty(bed.Upstream.Called);
    }

    [Fact]
    public async Task An_image_is_written_under_the_working_copy_with_matching_bytes_and_no_base64_reaches_the_member_or_the_log()
    {
        await using var bed = await McpServerToolsBed.StartAsync();

        JsonObject? drawn = null;
        string workingCopy = "";
        var rowsBefore = 0;
        var rowsAfter = 0;
        await bed.RunAsync("Alpha", "Dev", async run =>
        {
            workingCopy = run.WorkingDirectory;
            using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
            rowsBefore = (await bed.LogTextAsync()).Count;
            drawn = await mcp.CallAsync("fake_draw");
            rowsAfter = (await bed.LogTextAsync()).Count;
        });

        Assert.False(HarnessMcp.IsError(drawn!), drawn!.ToJsonString());
        var content = (JsonArray)drawn!["result"]!["content"]!;
        var file = JsonNode.Parse(content[0]!["text"]!.GetValue<string>())!;
        Assert.Equal("a picture", content[1]!["text"]!.GetValue<string>());

        var path = file["path"]!.GetValue<string>();
        var folder = Path.Combine(workingCopy, ".harness", "mcp", "fake") + Path.DirectorySeparatorChar;
        Assert.StartsWith(folder, path, StringComparison.Ordinal);
        Assert.Matches(@"^\d{8}-\d{6}-1\.png$", Path.GetFileName(path));
        Assert.Equal("image/png", file["mimeType"]!.GetValue<string>());
        Assert.Equal(FakeToolServer.Png.Length, file["bytes"]!.GetValue<int>());
        Assert.Equal(FakeToolServer.Png, await File.ReadAllBytesAsync(path, Ct));

        // NO BASE64 IN WHAT THE MEMBER SEES, AND NO RUN STEP: the call adds no row to the log.
        var base64 = Convert.ToBase64String(FakeToolServer.Png);
        Assert.DoesNotContain(base64[..40], drawn.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(await bed.LogTextAsync(), row => row.Contains(base64[..40], StringComparison.Ordinal));
        Assert.Equal(rowsBefore, rowsAfter);
    }

    [Fact]
    public async Task Audio_and_an_embedded_resource_are_written_each_with_its_extension()
    {
        await using var bed = await McpServerToolsBed.StartAsync();

        JsonObject? recorded = null;
        await bed.RunAsync("Alpha", "Dev", async run =>
        {
            using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
            recorded = await mcp.CallAsync("fake_record");
        });

        var files = ((JsonArray)recorded!["result"]!["content"]!).Select(b => JsonNode.Parse(b!["text"]!.GetValue<string>())!).ToList();
        Assert.Equal(["audio/wav", "application/pdf"], files.Select(f => f["mimeType"]!.GetValue<string>()));
        Assert.EndsWith("-1.wav", files[0]["path"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.EndsWith("-2.pdf", files[1]["path"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(FakeToolServer.Audio, await File.ReadAllBytesAsync(files[0]["path"]!.GetValue<string>(), Ct));
        Assert.Equal(FakeToolServer.Pdf, await File.ReadAllBytesAsync(files[1]["path"]!.GetValue<string>(), Ct));
    }

    [Fact]
    public async Task A_relative_output_path_inside_the_working_copy_is_used_and_one_outside_is_refused()
    {
        await using var bed = await McpServerToolsBed.StartAsync();

        JsonObject? inside = null;
        var outside = new List<JsonObject>();
        string workingCopy = "";
        await bed.RunAsync("Alpha", "Dev", async run =>
        {
            workingCopy = run.WorkingDirectory;
            using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
            inside = await mcp.CallAsync("fake_draw", new JsonObject { ["outputPath"] = "art/picture.png" });
            foreach (var path in new[] { "../escaped.png", "/tmp/absolute.png", "art/../../escaped.png" })
            {
                outside.Add(await mcp.CallAsync("fake_draw", new JsonObject { ["outputPath"] = path }));
            }
        });

        var file = JsonNode.Parse(((JsonArray)inside!["result"]!["content"]!)[0]!["text"]!.GetValue<string>())!;
        Assert.Equal(Path.Combine(workingCopy, "art", "picture.png"), file["path"]!.GetValue<string>());
        Assert.Equal(FakeToolServer.Png, await File.ReadAllBytesAsync(Path.Combine(workingCopy, "art", "picture.png"), Ct));

        Assert.All(outside, refused =>
        {
            Assert.True(HarnessMcp.IsError(refused));
            Assert.Contains("is outside your working copy", HarnessMcp.Text(refused), StringComparison.Ordinal);
        });
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(workingCopy)!, "escaped.png")));
        Assert.Single(bed.Upstream.Called);
    }

    [Fact]
    public async Task Bytes_past_the_limit_are_dropped_and_the_result_says_what_was_dropped()
    {
        Assert.Equal(20L * 1024 * 1024, McpRunTools.DefaultMaximumBinary);
        await using var bed = await McpServerToolsBed.StartAsync(maximumBinary: 1000);

        JsonObject? recorded = null;
        await bed.RunAsync("Alpha", "Dev", async run =>
        {
            using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
            recorded = await mcp.CallAsync("fake_record");
        });

        // 900 bytes of audio fit under 1000; the 313-byte PDF after it does not.
        var text = HarnessMcp.Text(recorded!);
        Assert.Contains("\"mimeType\":\"audio/wav\"", text, StringComparison.Ordinal);
        Assert.Contains($"A application/pdf result of {FakeToolServer.Pdf.Length} bytes from fake was dropped", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_credential_the_server_echoes_back_is_redacted_from_the_result()
    {
        await using var bed = await McpServerToolsBed.StartAsync();

        JsonObject? echoed = null;
        await bed.RunAsync("Alpha", "Dev", async run =>
        {
            using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
            echoed = await mcp.CallAsync("fake_echo_credential", new JsonObject { ["message"] = "hi" });
        });

        var whole = echoed!.ToJsonString();
        Assert.Contains("[redacted]", HarnessMcp.Text(echoed), StringComparison.Ordinal);
        foreach (var secret in new[] { McpServerToolsBed.Token, McpServerToolsBed.Token.ToUpperInvariant(), McpServerToolsBed.QueryKey })
        {
            Assert.DoesNotContain(secret, whole, StringComparison.Ordinal);
        }

        Assert.Equal("[redacted]", echoed["result"]!["structuredContent"]!["token"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_server_that_does_not_answer_fails_that_call_and_the_next_call_succeeds()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), McpForwarding.DefaultCallTimeout);
        Assert.Equal(TimeSpan.FromSeconds(120), new McpForwarding().CallTimeout);
        await using var bed = await McpServerToolsBed.StartAsync(callTimeout: TimeSpan.FromMilliseconds(300));

        JsonObject? silent = null;
        JsonObject? next = null;
        await bed.RunAsync("Alpha", "Dev", async run =>
        {
            using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
            silent = await mcp.CallAsync("fake_wait");
            next = await mcp.CallAsync("fake_echo_credential", new JsonObject { ["message"] = "still here" });
        });

        Assert.True(HarnessMcp.IsError(silent!));
        Assert.Equal("fake did not answer.", HarnessMcp.Text(silent!));
        Assert.False(HarnessMcp.IsError(next!), next!.ToJsonString());
        Assert.Contains("you said still here", HarnessMcp.Text(next!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tool_removed_upstream_after_the_list_was_read_answers_that_the_server_no_longer_offers_it()
    {
        await using var bed = await McpServerToolsBed.StartAsync();

        IReadOnlyList<JsonObject> listed = [];
        JsonObject? gone = null;
        await bed.RunAsync("Alpha", "Dev", async run =>
        {
            using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
            bed.Upstream.Removed = true;

            // KEPT FOR THE RUN: the list read at the start still holds it.
            listed = await mcp.ListAsync();
            gone = await mcp.CallAsync("fake_gone");
        });

        Assert.Contains(listed, t => t["name"]!.GetValue<string>() == "fake_gone");
        Assert.True(HarnessMcp.IsError(gone!));
        Assert.Equal("fake no longer offers gone.", HarnessMcp.Text(gone!));
    }

    [Fact]
    public async Task Text_past_the_limit_is_cut_with_a_line_saying_the_rest_was_cut()
    {
        await using var bed = await McpServerToolsBed.StartAsync();

        JsonObject? cut = null;
        JsonObject? whole = null;
        await bed.RunAsync("Alpha", "Dev", async run =>
        {
            using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
            cut = await mcp.CallAsync("fake_long_text");
            bed.Upstream.LongText = 100_000;
            whole = await mcp.CallAsync("fake_long_text");
        });

        var blocks = (JsonArray)cut!["result"]!["content"]!;
        Assert.Equal(100_000, blocks[0]!["text"]!.GetValue<string>().Length);
        Assert.Equal("[The rest was cut: 50,000 more characters past the 100,000-character limit.]", blocks[1]!["text"]!.GetValue<string>());

        Assert.Equal(100_000, Assert.Single((JsonArray)whole!["result"]!["content"]!)!["text"]!.GetValue<string>().Length);
    }

    [Fact]
    public async Task An_unknown_name_is_the_protocols_unknown_tool_error()
    {
        await using var bed = await McpServerToolsBed.StartAsync();

        JsonObject? unknown = null;
        await bed.RunAsync("Alpha", "Dev", async run =>
        {
            using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
            unknown = await mcp.CallAsync("fake_no_such_tool");
        });

        Assert.NotNull(unknown!["error"]);
        Assert.Contains("fake_no_such_tool", unknown.ToJsonString(), StringComparison.Ordinal);
    }

    // ---- names ---------------------------------------------------------------------------------

    [Fact]
    public void A_name_keeps_only_letters_digits_underscore_and_dash_and_a_name_cut_to_fit_ends_in_a_hash_of_the_whole()
    {
        Assert.Equal("fake_search_issues", McpToolNames.For("fake", "search_issues"));
        Assert.Equal("fake_get-pageandmore", McpToolNames.For("fake", "get-page.and more!"));
        Assert.Equal(McpToolNames.Longest, 64 - McpToolNames.CliPrefix.Length);

        var a = McpToolNames.For("fake", FakeToolServer.LongName);
        var b = McpToolNames.For("fake", FakeToolServer.LongName[..^1] + "2");
        Assert.Equal(70, ("fake_" + FakeToolServer.LongName).Length);
        Assert.Equal(McpToolNames.Longest, a.Length);
        Assert.True((McpToolNames.CliPrefix + a).Length <= 64);
        Assert.NotEqual(a, b);
        Assert.Matches("^fake_summarise_every_open_pull_request_in_[0-9a-f]{8}$", a);
        Assert.Equal(a[..41], b[..41]);
    }

    [Fact]
    public async Task Two_tools_that_would_get_the_same_name_refuse_the_save_naming_both()
    {
        await using var bed = await McpServerToolsBed.StartAsync();
        bed.Upstream.Extra.AddRange(["get.page", "getpage"]);

        var tested = await bed.PostAsync($"/api/mcp-servers/{bed.ServerId}/test", new { });

        var check = tested.GetProperty("check");
        Assert.Equal("failed", check.GetProperty("outcome").GetString());
        Assert.Equal(
            "Tool `get.page` of fake and tool `getpage` of fake would both be named `fake_getpage` for members. "
            + "Rename the entry or ask the server's owner to rename one; nothing listed was kept.",
            check.GetProperty("sentence").GetString());
        Assert.DoesNotContain("getpage", tested.GetProperty("server").GetProperty("tools").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public void A_clash_across_two_entries_names_both()
    {
        var now = DateTimeOffset.UtcNow;
        McpServerRecord Entry(string id, string name, params string[] tools) => new(
            id, name, McpServerRecord.Web, "https://x.test/mcp", null, null, null, null, null, null, null, [], [], McpServerRecord.Ok, null, tools, now, now, now);

        var other = Entry("2", "a_b", "c");
        Assert.Equal(
            "Tool `b_c` of a and tool `c` of a_b would both be named `a_b_c` for members. Rename the entry or ask the server's owner to rename one; nothing listed was kept.",
            McpServers.Clash(Entry("1", "a"), ["b_c"], [other]));
        Assert.Null(McpServers.Clash(Entry("1", "a"), ["b_d"], [other]));
    }

    [Fact]
    public async Task Outside_a_run_a_member_is_listed_the_platforms_tools_alone()
    {
        await using var bed = await McpServerToolsBed.StartAsync();

        string key = "";
        await bed.RunAsync("Alpha", "Dev", run =>
        {
            key = McpServerToolsBed.Key(run);
            return Task.CompletedTask;
        });

        await WaitIdleAsync(bed, new ContainerId("Alpha", "Dev"));
        using var mcp = bed.Mcp(key);
        Assert.DoesNotContain(await mcp.ListAsync(), t => t["name"]!.GetValue<string>().StartsWith("fake_", StringComparison.Ordinal));
        Assert.Null(bed.Services.GetRequiredService<McpRunTools>().RunOf("Alpha/Dev"));
    }

    internal static async Task WaitIdleAsync(McpServerToolsBed bed, ContainerId member)
    {
        var host = bed.Services.GetRequiredService<Harness.Containers.ContainerHost>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && (host.Find(member)?.State != ContainerState.Idle || bed.Services.GetRequiredService<McpRunTools>().RunOf(member.ToString()) is not null))
        {
            await Task.Delay(50, Ct);
        }
    }
}
