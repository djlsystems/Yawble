using System.Text.Json;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// The config is never keyed by member name alone. Every team has a member named Manager, and every
/// host in the container shares /tmp, so a path like /tmp/harness-mcp/&lt;member name&gt; would let
/// a test run overwrite the live Concierge's file with a fake key and another host's port.
/// </summary>
public sealed class McpLaunchConfigTests
{
    [Fact]
    public void Two_launches_for_one_name_never_share_a_file()
    {
        var first = McpLaunchConfig.TryWrite("http://127.0.0.1:8080", "key-for-launch1", "Manager")!;
        var second = McpLaunchConfig.TryWrite("http://127.0.0.1:5391", "key-for-launch2", "Manager")!;

        try
        {
            Assert.NotEqual(first.JsonPath, second.JsonPath);
            Assert.Equal("${HARNESS_KEY}", KeyIn(first.JsonPath));
            Assert.Equal("http://127.0.0.1:8080/mcp", first.Url);
        }
        finally
        {
            first.Delete();
            second.Delete();
        }
    }

    [Fact]
    public void No_file_holds_the_key_and_the_directory_is_the_owners_alone()
    {
        var config = McpLaunchConfig.TryWrite("http://127.0.0.1:8080", "secret-platform-key", "Dev1")!;

        try
        {
            Assert.DoesNotContain("secret-platform-key", File.ReadAllText(config.JsonPath));
            Assert.DoesNotContain("secret-platform-key", File.ReadAllText(config.TomlPath));
            Assert.Equal("${HARNESS_KEY}", KeyIn(config.JsonPath));

            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(Path.GetDirectoryName(config.JsonPath)!));
            }
        }
        finally
        {
            config.Delete();
        }
    }

    [Fact]
    public void Delete_removes_the_credential_file()
    {
        var config = McpLaunchConfig.TryWrite("http://127.0.0.1:8080", "secret", "Dev1")!;

        config.Delete();

        Assert.False(File.Exists(config.JsonPath));
        Assert.False(File.Exists(config.TomlPath));
    }

    /// <summary>What grok leaves behind after it has rewritten the file itself: the inline
    /// headers split into a sub-table, among the person's own settings.</summary>
    private const string GrokRewritten =
        """
        [cli]
        installer = "internal"

        [mcp_servers.harness]
        url = "http://127.0.0.1:5391/mcp"
        enabled = true

        [mcp_servers.harness.headers]
        X-Api-Key = "${HARNESS_KEY}"

        [marketplace]
        default_skills_installs_purged = true

        [[marketplace.sources]]
        name = "xAI Official"

        [ui]
        yolo = false # a comment
        """;

    [Fact]
    public void The_grok_entry_follows_the_host_that_launched_it()
    {
        var path = GrokConfigIn(GrokRewritten);

        McpLaunchConfig.EnsureGrokConfig("http://127.0.0.1:8080/mcp", path);

        var text = File.ReadAllText(path);
        Assert.Single(Occurrences(text, "[mcp_servers.harness]"));
        Assert.Contains("url = \"http://127.0.0.1:8080/mcp\"", text);
        Assert.DoesNotContain("5391", text);
        Assert.DoesNotContain("[mcp_servers.harness.headers]", text);
        Assert.Contains("headers = { \"X-Api-Key\" = \"${HARNESS_KEY}\" }", text);

        // The person's own settings survive, in their order.
        var order = new[] { "[cli]", "[marketplace]", "[[marketplace.sources]]", "[ui]", "yolo = false # a comment" }
            .Select(line => text.IndexOf(line, StringComparison.Ordinal))
            .ToArray();
        Assert.All(order, index => Assert.True(index >= 0));
        Assert.Equal(order.Order(), order);
    }

    [Fact]
    public void Writing_the_same_entry_twice_changes_nothing()
    {
        var path = GrokConfigIn(GrokRewritten);

        McpLaunchConfig.EnsureGrokConfig("http://127.0.0.1:8080/mcp", path);
        var first = File.ReadAllText(path);
        var written = File.GetLastWriteTimeUtc(path);

        McpLaunchConfig.EnsureGrokConfig("http://127.0.0.1:8080/mcp", path);

        Assert.Equal(first, File.ReadAllText(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.Equal(first, McpLaunchConfig.WithHarnessEntry(first, "http://127.0.0.1:8080/mcp"));
    }

    /// <summary>
    /// Grok rewrites the file after every launch into its own shape, so an entry that already
    /// points at this host is left alone rather than put back into ours on every launch.
    /// </summary>
    [Fact]
    public void An_entry_grok_rewrote_that_already_points_here_is_not_rewritten()
    {
        var path = GrokConfigIn(GrokRewritten);
        var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);

        McpLaunchConfig.EnsureGrokConfig("http://127.0.0.1:5391/mcp", path);

        Assert.Equal(GrokRewritten, File.ReadAllText(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void A_grok_config_with_no_harness_entry_gets_one_and_keeps_the_rest()
    {
        const string Theirs = "[cli]\ninstaller = \"internal\"\n";
        var path = GrokConfigIn(Theirs);

        McpLaunchConfig.EnsureGrokConfig("http://127.0.0.1:8080/mcp", path);

        var text = File.ReadAllText(path);
        Assert.StartsWith(Theirs, text);
        Assert.Single(Occurrences(text, "[mcp_servers.harness]"));
        Assert.Contains("url = \"http://127.0.0.1:8080/mcp\"", text);
    }

    [Fact]
    public void A_missing_grok_config_is_created_with_the_entry()
    {
        var path = Path.Combine(Path.GetTempPath(), "grok-" + Guid.NewGuid().ToString("N"), "config.toml");

        McpLaunchConfig.EnsureGrokConfig("http://127.0.0.1:8080/mcp", path);

        var text = File.ReadAllText(path);
        Assert.StartsWith("[mcp_servers.harness]\n", text);
        Assert.Contains("url = \"http://127.0.0.1:8080/mcp\"", text);
    }

    /// <summary>
    /// The Host runs as `harness` and grok as `agent`, one group between them. A .grok the
    /// Host creates, and every config.toml it writes, must let that group rewrite it, and a
    /// directory must pass its group on.
    /// </summary>
    [Fact]
    public void A_grok_home_the_host_creates_and_the_file_it_writes_are_group_writable()
    {
        if (OperatingSystem.IsWindows()) return;

        var home = Directory.CreateTempSubdirectory("grok-home-").FullName;
        var path = Path.Combine(home, ".grok", "config.toml");

        McpLaunchConfig.EnsureGrokConfig("http://127.0.0.1:8080/mcp", path);

        var directory = File.GetUnixFileMode(Path.GetDirectoryName(path)!);
        Assert.True(directory.HasFlag(UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.SetGroup), directory.ToString());
        Assert.True(File.GetUnixFileMode(path).HasFlag(UnixFileMode.GroupRead | UnixFileMode.GroupWrite));
    }

    [Fact]
    public void A_rewrite_of_an_owner_only_grok_config_leaves_it_group_writable()
    {
        if (OperatingSystem.IsWindows()) return;

        var path = GrokConfigIn("[cli]\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        McpLaunchConfig.EnsureGrokConfig("http://127.0.0.1:8080/mcp", path);

        Assert.True(File.GetUnixFileMode(path).HasFlag(UnixFileMode.GroupRead | UnixFileMode.GroupWrite));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    /// <summary>
    /// $HOME/.grok can belong to `agent` with no group write.
    /// The Host is then refused, and that must cost grok its MCP entry, not the member its run.
    /// </summary>
    [Fact]
    public void A_grok_config_that_cannot_be_written_is_reported_and_the_launch_goes_on()
    {
        // A regular file where the .grok directory should be: refused even for root.
        var home = Directory.CreateTempSubdirectory("grok-home-").FullName;
        File.WriteAllText(Path.Combine(home, ".grok"), "not a directory");
        var warnings = new List<string>();

        var config = McpLaunchConfig.TryWrite(
            "http://127.0.0.1:8080", "key", "Manager", warnings.Add, Path.Combine(home, ".grok", "config.toml"));

        try
        {
            Assert.NotNull(config);
            Assert.True(File.Exists(config.JsonPath));
            Assert.True(File.Exists(config.TomlPath));
            var warning = Assert.Single(warnings);
            Assert.Contains(Path.Combine(home, ".grok", "config.toml"), warning);
        }
        finally
        {
            config?.Delete();
        }
    }

    [Fact]
    public void A_test_run_never_writes_the_real_grok_home()
    {
        Assert.Equal(Path.Combine(GrokHomeIsolation.Root, "config.toml"), McpLaunchConfig.GrokConfigPath());
    }

    private static string GrokConfigIn(string text)
    {
        var directory = Path.Combine(Path.GetTempPath(), "grok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.toml");
        File.WriteAllText(path, text);
        return path;
    }

    private static IEnumerable<int> Occurrences(string text, string value)
    {
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            yield return index;
        }
    }

    private static string KeyIn(string jsonPath) =>
        JsonDocument.Parse(File.ReadAllText(jsonPath)).RootElement
            .GetProperty("mcpServers").GetProperty("harness").GetProperty("headers")
            .GetProperty("X-Api-Key").GetString()!;
}
