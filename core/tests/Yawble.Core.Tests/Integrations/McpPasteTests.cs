using Yawble.Core.Integrations;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>Parse a pasted MCP config into drafts (pure text, nothing run or read).</summary>
public class McpPasteTests
{
    [Fact]
    public void ClaudeDesktopBlock()
    {
        var d = McpPaste.Parse("""
            {"mcpServers": {
              "github": {"command":"npx","args":["-y","@modelcontextprotocol/server-github"],"env":{"GITHUB_TOKEN":"ghp_abc123","LOG":"info"}},
              "docs": {"url":"https://mcp.example.com/mcp","headers":{"Authorization":"Bearer tok_123"}}
            }}
            """);
        Assert.Equal(2, d.Count);
        var gh = d[0];
        Assert.Equal("github", gh.Name);
        Assert.Equal("stdio", gh.Transport);
        Assert.Equal("npx", gh.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-github"], gh.Args);
        var tok = Assert.Single(gh.Env, e => e.Name == "GITHUB_TOKEN");
        Assert.True(tok.Secret); Assert.False(tok.Needed); Assert.Equal("ghp_abc123", tok.Value);
        var log = Assert.Single(gh.Env, e => e.Name == "LOG");
        Assert.False(log.Secret); Assert.Equal("info", log.Value);
        var docs = d[1];
        Assert.Equal("http", docs.Transport);
        Assert.Equal("https://mcp.example.com/mcp", docs.Url);
        Assert.Equal("bearer", docs.Auth!.Type);
        var t = Assert.Single(docs.Env);
        Assert.Equal("token", t.Name); Assert.True(t.Secret); Assert.Equal("tok_123", t.Value);
    }

    [Fact]
    public void BareServerObject()
    {
        var d = Assert.Single(McpPaste.Parse("""{"command":"uvx","args":["mcp-server-time"]}"""));
        Assert.Null(d.Name);
        Assert.Equal("uvx", d.Command);
        var m = McpPaste.Parse("""{"time":{"command":"uvx","args":["mcp-server-time"]}}""");
        Assert.Equal("time", Assert.Single(m).Name);
        var u = Assert.Single(McpPaste.Parse("""{"url":"https://x.example/mcp"}"""));
        Assert.Equal("http", u.Transport);
    }

    [Fact]
    public void ClaudeMcpAddStdioWithEnv()
    {
        var d = Assert.Single(McpPaste.Parse("claude mcp add --scope user -e API_KEY=abc123 -e REGION=eu github -- npx -y @modelcontextprotocol/server-github"));
        Assert.Equal("github", d.Name);
        Assert.Equal("stdio", d.Transport);
        Assert.Equal("npx", d.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-github"], d.Args);
        Assert.True(Assert.Single(d.Env, e => e.Name == "API_KEY").Secret);
        Assert.False(Assert.Single(d.Env, e => e.Name == "REGION").Secret);
    }

    [Fact]
    public void ClaudeMcpAddHttpWithHeader()
    {
        var d = Assert.Single(McpPaste.Parse("claude mcp add --transport http -s project notion https://mcp.notion.com/mcp --header \"Authorization: Bearer secret_xyz\""));
        Assert.Equal("notion", d.Name);
        Assert.Equal("http", d.Transport);
        Assert.Equal("https://mcp.notion.com/mcp", d.Url);
        Assert.Equal("bearer", d.Auth!.Type);
        var t = Assert.Single(d.Env);
        Assert.Equal("token", t.Name); Assert.Equal("secret_xyz", t.Value); Assert.True(t.Secret);
    }

    [Fact]
    public void NpxLineNamesFromPackage()
    {
        var d = Assert.Single(McpPaste.Parse("npx -y @modelcontextprotocol/server-github"));
        Assert.Equal("github", d.Name);
        Assert.Equal("npx", d.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-github"], d.Args);
        Assert.Equal("time", Assert.Single(McpPaste.Parse("uvx mcp-server-time")).Name);
        Assert.Equal("fetch", Assert.Single(McpPaste.Parse("docker run -i --rm mcp/fetch")).Name);
    }

    [Fact]
    public void BareUrl()
    {
        var d = Assert.Single(McpPaste.Parse("  https://mcp.linear.app/sse  "));
        Assert.Equal("linear", d.Name);
        Assert.Equal("http", d.Transport);
        Assert.Equal("https://mcp.linear.app/sse", d.Url);
    }

    [Fact]
    public void PlaceholderIsNeeded()
    {
        var d = Assert.Single(McpPaste.Parse("""{"command":"npx","args":["x-mcp"],"env":{"API_KEY":"<your key>","EMPTY_TOKEN":"","PLAIN":"1"}}"""));
        Assert.True(Assert.Single(d.Env, e => e.Name == "API_KEY").Needed);
        Assert.True(Assert.Single(d.Env, e => e.Name == "EMPTY_TOKEN").Needed);
        var plain = Assert.Single(d.Env, e => e.Name == "PLAIN");
        Assert.False(plain.Needed); Assert.False(plain.Secret);
    }

    [Fact]
    public void MultilineContinuationsJoin()
    {
        var d = Assert.Single(McpPaste.Parse("claude mcp add github `\n  -e GITHUB_TOKEN=ghp_1 `\n  -- npx -y @modelcontextprotocol/server-github"));
        Assert.Equal("github", d.Name); Assert.Equal("npx", d.Command);
        var e = Assert.Single(d.Env); Assert.Equal("GITHUB_TOKEN", e.Name);
        var b = Assert.Single(McpPaste.Parse("claude mcp add github \\\n  -e GITHUB_TOKEN=ghp_1 \\\n  -- npx -y @modelcontextprotocol/server-github"));
        Assert.Equal("npx", b.Command);
    }

    [Fact]
    public void ManySpacesAreLinear()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(McpPaste.Parse("a" + new string(' ', 100_000) + "b"));
        Assert.Empty(McpPaste.Parse("a\\" + new string(' ', 50_000) + "\\" + new string(' ', 40_000) + "b"));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void HeaderDoesNotSwallowUrl()
    {
        var d = Assert.Single(McpPaste.Parse("claude mcp add x --header \"A: b\" https://mcp.x/mcp"));
        Assert.Equal("https://mcp.x/mcp", d.Url);
        Assert.Equal("header", d.Auth!.Type); Assert.Equal("A", d.Auth.Header);
    }

    [Fact]
    public void TrailingBackslashInQuotesIsAPath()
    {
        var d = Assert.Single(McpPaste.Parse("claude mcp add x -e \"P=C:\\dir\\\" -- npx y"));
        Assert.Equal("C:\\dir\\", Assert.Single(d.Env).Value);
        Assert.Equal("npx", d.Command);
        var e = Assert.Single(McpPaste.Parse("claude mcp add x -e \"Q=say `\"hi`\"\" -- npx y"));
        Assert.Equal("say \"hi\"", Assert.Single(e.Env).Value);
    }

    [Fact]
    public void QuotedWindowsPathCommandIsNotJson()
    {
        var d = Assert.Single(McpPaste.Parse("\"C:\\Program Files\\nodejs\\node.exe\" C:\\srv\\dist\\index.js"));
        Assert.Equal("C:\\Program Files\\nodejs\\node.exe", d.Command);
        Assert.Equal(["C:\\srv\\dist\\index.js"], d.Args);
    }

    [Fact]
    public void ClaudeMcpAddWithoutDashDash()
    {
        var d = Assert.Single(McpPaste.Parse("claude mcp add foo npx -y pkg --flag"));
        Assert.Equal("foo", d.Name);
        Assert.Equal("npx", d.Command);
        Assert.Equal(["-y", "pkg", "--flag"], d.Args);
    }

    [Theory]
    [InlineData("-t streamable-http")]
    [InlineData("-t sse")]
    [InlineData("--transport http")]
    public void RemoteTransports(string opt)
    {
        var d = Assert.Single(McpPaste.Parse($"claude mcp add {opt} x https://a.example/mcp"));
        Assert.Equal("http", d.Transport);
    }

    [Fact]
    public void ShortHeaderFlagAndBearerPlaceholder()
    {
        var d = Assert.Single(McpPaste.Parse("claude mcp add -t http x https://a.example/mcp -H \"Authorization: Bearer <your token>\""));
        Assert.Equal("bearer", d.Auth!.Type);
        var t = Assert.Single(d.Env);
        Assert.Equal("token", t.Name); Assert.True(t.Needed); Assert.Null(t.Value);
    }

    [Fact]
    public void CrlfAndScopedPackageWithVersion()
    {
        var d = Assert.Single(McpPaste.Parse("claude mcp add g \\\r\n  -- npx -y @modelcontextprotocol/server-github@1.2.3\r\n"));
        Assert.Equal("npx", d.Command);
        Assert.Equal("github", Assert.Single(McpPaste.Parse("npx -y @modelcontextprotocol/server-github@1.2.3")).Name);
    }

    [Fact]
    public void DeeplyNestedJsonGivesNothing()
    {
        Assert.Empty(McpPaste.Parse(new string('{', 5000) + new string('}', 5000)));
        Assert.Empty(McpPaste.Parse(string.Concat(Enumerable.Repeat("{\"a\":", 5000)) + "1" + new string('}', 5000)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"a\":1}")]
    [InlineData("claude mcp add")]
    [InlineData("\"unterminated 'quote")]
    public void GarbageGivesNothing(string text)
    {
        Assert.Empty(McpPaste.Parse(text));
        Assert.Empty(McpPaste.Parse(new string('x', 200_000)));
        Assert.Empty(McpPaste.Parse("https://x.example/" + new string('a', 200_000)));
    }
}
