using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// MCP 2's contract on the harness server: a team with no MCP servers is listed the platform's own tools,
/// unchanged; a tool whose name would be 70 characters is listed within the limit and called as itself; and a
/// member of each CLI kind reaches a fake server's tool through the harness entry its own launch is given.
/// </summary>
public sealed partial class McpContractTests
{
    private static readonly string[] Kinds = ["claude-headless", "codex-headless", "grok-headless", "copilot-headless"];

    [Fact]
    public async Task A_team_with_no_mcp_servers_is_listed_the_platforms_own_tools_unchanged()
    {
        await using var bed = await McpServerToolsBed.StartAsync();

        IReadOnlyList<JsonObject> beta = [];
        IReadOnlyList<JsonObject> alpha = [];
        string betaKey = "";
        await bed.RunAsync("Beta", "Dev", async run =>
        {
            betaKey = McpServerToolsBed.Key(run);
            using var mcp = bed.Mcp(betaKey);
            beta = await mcp.ListAsync();
        });
        await bed.RunAsync("Alpha", "Dev", async run =>
        {
            using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
            alpha = await mcp.ListAsync();
        });

        // THE PLATFORM'S TOOLS, every one declared on PlatformMcpTools and nothing else.
        var declared = typeof(PlatformMcpTools).GetMethods()
            .Select(m => m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false)
                .OfType<ModelContextProtocol.Server.McpServerToolAttribute>().SingleOrDefault()?.Name)
            .OfType<string>().Order(StringComparer.Ordinal);
        Assert.Equal(declared, beta.Select(t => t["name"]!.GetValue<string>()).Order(StringComparer.Ordinal));

        // UNCHANGED: the same JSON, tool for tool, during the run, outside it, and for a team that has servers.
        await McpServerToolsTests.WaitIdleAsync(bed, new ContainerId("Beta", "Dev"));
        using (var outside = bed.Mcp(betaKey))
        {
            Assert.Equal(Json(beta), Json(await outside.ListAsync()));
        }

        Assert.Equal(Json(beta), Json(alpha.Where(t => !t["name"]!.GetValue<string>().StartsWith("fake_", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task A_tool_name_of_70_characters_is_listed_within_the_limit_and_called_correctly()
    {
        await using var bed = await McpServerToolsBed.StartAsync();
        var whole = $"{McpServerToolsBed.Server}_{FakeToolServer.LongName}";
        Assert.Equal(70, whole.Length);

        IReadOnlyList<JsonObject> listed = [];
        JsonObject? called = null;
        var name = McpToolNames.For(McpServerToolsBed.Server, FakeToolServer.LongName);
        await bed.RunAsync("Alpha", "Dev", async run =>
        {
            using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
            listed = await mcp.ListAsync();
            called = await mcp.CallAsync(name);
        });

        Assert.Contains(listed, t => t["name"]!.GetValue<string>() == name);
        Assert.All(listed, t => Assert.True((McpToolNames.CliPrefix + t["name"]!.GetValue<string>()).Length <= McpToolNames.Limit, t["name"]!.ToString()));
        Assert.Matches("^[a-zA-Z0-9_-]+_[0-9a-f]{8}$", name);
        Assert.False(HarnessMcp.IsError(called!), called!.ToJsonString());
        Assert.Equal($"{FakeToolServer.LongName} ran", HarnessMcp.Text(called!));
        Assert.Equal([FakeToolServer.LongName], bed.Upstream.Called);
    }

    /// <summary>
    /// EACH CLI KIND, through its own launch's harness entry. The agent is the test's: no model is called. Each
    /// member's run reads its preset's arguments from the shipped catalog, writes the launch's MCP files as the
    /// runner does (<see cref="McpLaunchConfig"/>), and finds the harness entry the way that CLI does - claude's
    /// <c>--mcp-config</c> file and copilot's <c>--additional-mcp-config @file</c> (JSON, <c>${HARNESS_KEY}</c>
    /// in the header), codex's <c>-c mcp_servers.harness.*</c> flags (<c>env_http_headers</c> naming the
    /// variable) and grok's <c>config.toml</c> entry - then lists and calls the fake tool at that URL with
    /// that header.
    /// </summary>
    [Fact]
    public async Task A_member_of_each_cli_kind_lists_and_calls_a_fake_servers_tool_in_a_member_run()
    {
        await using var bed = await McpServerToolsBed.StartAsync(memberAgents: Kinds);
        var catalog = bed.Services.GetRequiredService<AgentCatalog>();

        foreach (var kind in Kinds)
        {
            var launch = catalog.Definition(kind)!.Launch;
            IReadOnlyList<JsonObject> listed = [];
            JsonObject? called = null;
            string? via = null;

            await bed.RunAsync("Alpha", McpServerToolsBed.MemberName(kind), async run =>
            {
                Assert.Equal(kind, run.Agent);
                var grok = Path.Combine(bed.DataRoot, $"grok-{Guid.NewGuid():N}", "config.toml");
                var config = McpLaunchConfig.TryWrite("http://localhost", McpServerToolsBed.Key(run), run.Container.ToString(), grokConfig: grok)!;
                try
                {
                    var args = launch.Arguments.Select(a => McpLaunchConfig.Apply(a, config)).ToList();
                    var (url, key, how) = HarnessEntry(launch.FileName, args, grok, run.Environment);
                    via = how;

                    using var mcp = bed.Mcp(key, new Uri(url).AbsolutePath);
                    listed = await mcp.ListAsync();
                    called = await mcp.CallAsync("fake_echo_credential", new JsonObject { ["message"] = kind });
                }
                finally
                {
                    config.Delete();
                }
            });

            Assert.True(listed.Any(t => t["name"]!.GetValue<string>() == "fake_echo_credential"), $"{kind} via {via}");
            Assert.True(listed.Any(t => t["name"]!.GetValue<string>() == "skills_get"), $"{kind} via {via}");
            Assert.False(HarnessMcp.IsError(called!), $"{kind}: {called!.ToJsonString()}");
            Assert.Contains($"you said {kind}", HarnessMcp.Text(called!), StringComparison.Ordinal);
        }

        Assert.Equal(Kinds.Length, bed.Upstream.Called.Count);
    }

    /// <summary>The harness entry's URL and key as <paramref name="cli"/> reads them from its launch, and where from.</summary>
    private static (string Url, string Key, string Via) HarnessEntry(
        string cli, IReadOnlyList<string> args, string grokConfig, IReadOnlyDictionary<string, string> environment)
    {
        string Expand(string value) => Regex.Replace(value, @"\$\{(\w+)\}", m => environment[m.Groups[1].Value]);

        switch (cli)
        {
            case "claude" or "copilot":
            {
                var flag = cli == "claude" ? "--mcp-config" : "--additional-mcp-config";
                var file = args[args.ToList().IndexOf(flag) + 1].TrimStart('@');
                var harness = JsonNode.Parse(File.ReadAllText(file))!["mcpServers"]!["harness"]!;
                return (harness["url"]!.GetValue<string>(), Expand(harness["headers"]!["X-Api-Key"]!.GetValue<string>()), $"{flag} {file}");
            }

            case "codex":
            {
                var overrides = args.Where((_, i) => i > 0 && args[i - 1] == "-c").ToList();
                var url = Regex.Match(overrides.Single(o => o.StartsWith("mcp_servers.harness.url=", StringComparison.Ordinal)), "\"(.*)\"").Groups[1].Value;
                var variable = Regex.Match(overrides.Single(o => o.StartsWith("mcp_servers.harness.env_http_headers=", StringComparison.Ordinal)),
                    "\"X-Api-Key\"=\"(\\w+)\"").Groups[1].Value;
                return (url, environment[variable], "-c mcp_servers.harness");
            }

            case "grok":
            {
                var toml = File.ReadAllText(grokConfig);
                var url = Regex.Match(toml, "url = \"(.*)\"").Groups[1].Value;
                var header = Regex.Match(toml, "\"X-Api-Key\" = \"([^\"]*)\"").Groups[1].Value;
                return (url, Expand(header), grokConfig);
            }

            default:
                throw new InvalidOperationException($"No harness entry is known for {cli}.");
        }
    }

    private static string Json(IEnumerable<JsonObject> tools) => new JsonArray([.. tools.Select(t => t.DeepClone())]).ToJsonString();
}
