using System.Text.Json;
using System.Text.RegularExpressions;

namespace Harness.Host;

/// <summary>
/// The MCP client config written for one launch, outside the git workspace. It holds no
/// credential: every shape names the <c>HARNESS_KEY</c> variable instead. Both shapes are written
/// every time; a preset uses whichever token its arguments name (<c>{mcpConfig}</c> for Claude-shaped JSON, <c>{mcpConfigToml}</c> for Codex TOML).
/// The built-in codex presets name neither: they pass <c>{mcpUrl}</c> in two <c>-c</c> flags
/// (see <c>AgentCatalogFile</c>), and the TOML stays for a custom preset that wants a file.
/// </summary>
public sealed record McpLaunchConfig(string Url, string JsonPath, string TomlPath, string? GrokConfig = null)
{
    /// <summary>Writes this launch's config, or null when there is no platform to call or no
    /// key for the child to call it with. <paramref name="apiKey"/> gates the write and is never
    /// written: the child holds it as <c>HARNESS_KEY</c>. A grok config.toml that cannot be
    /// written is reported to <paramref name="warn"/> (standard error when null) and the launch
    /// goes on: only grok reads that file. <paramref name="grokConfig"/> overrides
    /// <see cref="GrokConfigPath"/>, for a test. <paramref name="homeOfItsOwn"/> is a launch that
    /// runs under a HOME of its own (<see cref="RunHome"/>): the shared home's grok config is not
    /// touched, and a fresh one holding only the <c>harness</c> entry is written to this launch's
    /// directory as <see cref="GrokConfig"/>, for the run to copy into its home.</summary>
    public static McpLaunchConfig? TryWrite(
        string? baseUrl, string? apiKey, string owner, Action<string>? warn = null, string? grokConfig = null,
        bool homeOfItsOwn = false)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey)) return null;

        var url = baseUrl.TrimEnd('/') + "/mcp";
        // ONE DIRECTORY PER LAUNCH. Keyed by the owner's name alone, every team's Manager would
        // share one file, and so would every host in the container, because they share /tmp: a
        // test run could overwrite the live Concierge's config with a fake key and another host's
        // port. The name stays in front so a person listing the folder can tell whose it is.
        var directory = Path.Combine(
            Path.GetTempPath(), "harness-mcp", $"{Sanitize(owner)}-{Guid.NewGuid():N}");
        // Owner-only: the file names no secret, but it says which host and which member,
        // and nobody else in the container has a reason to list it.
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var jsonPath = Path.Combine(directory, "mcp.json");
        var tomlPath = Path.Combine(directory, "mcp.toml");

        var json = JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["harness"] = new
                {
                    type = "http",
                    url,
                    headers = new Dictionary<string, string>
                    {
                        // THE VARIABLE, NOT THE KEY - as the TOML and grok's entry already do.
                        // Claude Code expands `${VAR}` in --mcp-config headers from the child's
                        // own environment (as of 2.1.280: the server receives the value; with the
                        // variable unset it receives the literal text). Both
                        // launch paths set HARNESS_KEY in the child before writing this file.
                        ["X-Api-Key"] = "${HARNESS_KEY}",
                    },
                },
            },
        });

        // The header value is the environment variable NAME, not the secret.
        var toml = "[mcp_servers.harness]\n"
            + "url = \"" + url + "\"\n"
            + "env_http_headers = { \"X-Api-Key\" = \"HARNESS_KEY\" }\n";

        File.WriteAllText(jsonPath, json);
        File.WriteAllText(tomlPath, toml);

        if (homeOfItsOwn)
        {
            var own = Path.Combine(directory, "grok-config.toml");
            File.WriteAllText(own, WithHarnessEntry(string.Empty, url));
            return new McpLaunchConfig(url, jsonPath, tomlPath, own);
        }

        // NOT FATAL. With agents running as a separate user, $HOME/.grok belongs to `agent` and
        // the Host writes there only through group permission; a volume from before the switch
        // can hold a .grok the Host may not write. That costs grok its MCP entry, not the launch.
        grokConfig ??= GrokConfigPath();
        try
        {
            if (grokConfig is not null) EnsureGrokConfig(url, grokConfig);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            (warn ?? Console.Error.WriteLine)(
                $"Grok config {grokConfig} was not updated for {url}; grok launches without the harness MCP entry: {error.Message}");
        }

        return new McpLaunchConfig(url, jsonPath, tomlPath);
    }

    /// <summary>Removes this launch's directory and the credential in it. Call once the process
    /// that read it has exited; never throws.</summary>
    public void Delete()
    {
        try
        {
            var directory = Path.GetDirectoryName(JsonPath);
            // RECURSIVE DELETE REVIEWED: agents cannot write here. The Host made this launch's directory
            // owner-only and AgentLaunchUser.Share gives the agent's group read and traverse, never
            // write, so everything in it is the Host's own.
            if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Grok reads MCP servers from <c>config.toml</c> in its home - <c>$GROK_HOME</c>, else
    /// <c>~/.grok</c> - not from <c>--mcp-config</c>. The header names the environment variable.
    /// The Concierge sets <c>HARNESS_KEY</c> on each launch, and that key rotates, so the secret is
    /// not written here.
    /// </summary>
    public static void EnsureGrokConfig(string mcpUrl)
    {
        if (GrokConfigPath() is { } configPath) EnsureGrokConfig(mcpUrl, configPath);
    }

    /// <summary>
    /// Makes <paramref name="configPath"/> hold exactly one <c>harness</c> MCP entry, pointing at
    /// <paramref name="mcpUrl"/>, and leaves every other line as it was.
    ///
    /// **REPLACED, NOT APPENDED ONCE.** The file is shared by every host in the container; written
    /// only when no <c>[mcp_servers.harness]</c> table was there, whichever host launched grok first
    /// would own the entry for good, and the live host's grok would call a test host's port long
    /// after that host was gone. The entry follows the host that launched grok. Grok rewrites the file itself and splits the inline <c>headers</c> into an
    /// <c>[mcp_servers.harness.headers]</c> table, so the old entry is every
    /// <c>[mcp_servers.harness]</c> table AND every sub-table under it.
    ///
    /// **WRITTEN ONLY WHEN THE ENTRY IS MISSING OR ITS URL DIFFERS.** Grok puts the file back into
    /// its own shape after every launch, so comparing text with what this would write rewrote the
    /// file on every launch. An entry already pointing at <paramref name="mcpUrl"/> is left alone,
    /// in whatever shape grok gave it. A write goes to a sibling file and is moved over the
    /// original, so a grok starting at the same moment reads the old file or the new one, never
    /// half of either.
    ///
    /// **SHARED WITH THE AGENT'S GROUP.** The Host and the grok it launches can be different users
    /// (see <see cref="AgentLaunchUser"/>) who share a group, and grok rewrites this file itself.
    /// A directory created here gets group rwx and setgid, and the file written gets group
    /// read/write, so under the image's setgid agent-home both carry the agent's group and each
    /// user can replace what the other wrote. A .grok the Host may not write throws, and
    /// <see cref="TryWrite"/> reports it.
    /// </summary>
    public static void EnsureGrokConfig(string mcpUrl, string configPath)
    {
        var directory = Path.GetDirectoryName(configPath)!;
        if (!Directory.Exists(directory)) CreateSharedDirectory(directory);

        var existing = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
        if (HarnessUrls(existing) is [var current] && current == mcpUrl) return;

        var updated = WithHarnessEntry(existing, mcpUrl);

        if (string.Equals(existing, updated, StringComparison.Ordinal)) return;

        var temporary = configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, updated);
            AddGroupReadWrite(temporary);
            File.Move(temporary, configPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Creates <paramref name="directory"/> (and any missing parent) with group rwx
    /// and setgid. The group itself is the kernel's to give: under a setgid parent - the
    /// image's agent-home - it is the parent's group, so no chown is needed.</summary>
    private static void CreateSharedDirectory(string directory)
    {
        var parent = Path.GetDirectoryName(directory);
        if (parent is not null && !Directory.Exists(parent)) CreateSharedDirectory(parent);

        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows()) return;

        // Set after creation, because the umask applies to the mode handed to mkdir.
        File.SetUnixFileMode(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
            | UnixFileMode.SetGroup);
    }

    private static void AddGroupReadWrite(string file)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(file,
            File.GetUnixFileMode(file) | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
    }

    /// <summary><c>$GROK_HOME/config.toml</c>, else <c>$HOME/.grok/config.toml</c>, else null -
    /// the same lookup grok itself makes.</summary>
    public static string? GrokConfigPath()
    {
        var grokHome = Environment.GetEnvironmentVariable("GROK_HOME");
        if (!string.IsNullOrWhiteSpace(grokHome)) return Path.Combine(grokHome, "config.toml");

        var home = Environment.GetEnvironmentVariable("HOME");
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".grok", "config.toml");
    }

    /// <summary>
    /// <paramref name="toml"/> with every <c>[mcp_servers.harness]</c> table and sub-table taken
    /// out and one fresh entry for <paramref name="mcpUrl"/> put at the end. Line-based rather
    /// than a TOML parser on purpose: a parse-and-reserialise would reorder and restyle the
    /// person's own settings, and this only ever has to find table headers.
    /// </summary>
    public static string WithHarnessEntry(string toml, string mcpUrl)
    {
        var kept = new List<string>();
        var inHarness = false;

        foreach (var line in toml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            // A header is a whole line, `[name]` or `[[name]]`, with an optional comment after it.
            // A `[` that opens an array element inside a multi-line value is not one.
            if (TableHeader.Match(line) is { Success: true } header)
            {
                var name = header.Groups["name"].Value.Replace(" ", "", StringComparison.Ordinal);
                inHarness = name == GrokTable || name.StartsWith(GrokTable + ".", StringComparison.Ordinal);
            }

            if (!inHarness) kept.Add(line);
        }

        var rest = string.Join("\n", kept).TrimEnd();

        var block = "[" + GrokTable + "]\n"
            + "url = \"" + mcpUrl + "\"\n"
            + "enabled = true\n"
            + "headers = { \"X-Api-Key\" = \"${HARNESS_KEY}\" }\n";

        return rest.Length == 0 ? block : rest + "\n\n" + block;
    }

    /// <summary>Every <c>url</c> set directly in an <c>[mcp_servers.harness]</c> table - one for
    /// a well-formed file, none when the entry is missing.</summary>
    private static List<string> HarnessUrls(string toml)
    {
        var urls = new List<string>();
        var inEntry = false;

        foreach (var line in toml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (TableHeader.Match(line) is { Success: true } header)
            {
                inEntry = header.Groups["name"].Value.Replace(" ", "", StringComparison.Ordinal) == GrokTable;
            }
            else if (inEntry && UrlLine.Match(line) is { Success: true } url)
            {
                urls.Add(url.Groups["url"].Value);
            }
        }

        return urls;
    }

    private const string GrokTable = "mcp_servers.harness";

    private static readonly Regex UrlLine = new(
        @"^\s*url\s*=\s*(?<q>[""'])(?<url>[^""']*)\k<q>\s*(#.*)?$", RegexOptions.CultureInvariant);

    private static readonly Regex TableHeader = new(
        @"^\s*\[\[?\s*(?<name>[A-Za-z0-9_\-\. ]+?)\s*\]\]?\s*(#.*)?$", RegexOptions.CultureInvariant);

    public static string Apply(string argument, McpLaunchConfig? config) =>
        config is null
            ? argument
            : argument
                .Replace("{mcpConfig}", config.JsonPath, StringComparison.Ordinal)
                .Replace("{mcpConfigToml}", config.TomlPath, StringComparison.Ordinal)
                .Replace("{mcpUrl}", config.Url, StringComparison.Ordinal);

    private static string Sanitize(string owner)
    {
        var chars = owner.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray();
        var text = new string(chars).Trim('-');
        return text.Length == 0 ? "member" : text;
    }
}
