using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Contracts;
using Harness.Pty;

namespace Harness.Host;

/// <summary>
/// One thing an agent CLI says it would load: an MCP server, an account connector or app, a
/// plugin, a skill or a hook.
/// </summary>
/// <param name="Kind">One of the <see cref="ListedKinds"/>.</param>
/// <param name="Name">The name the CLI's own listing gives it (`claude.ai Gmail`, `github-mcp-server`).</param>
/// <param name="Source">Where the CLI says it comes from, when it says (`user`, `builtin`, a file).</param>
/// <param name="Off">Null when it would load. Otherwise why it does not: the listing marks it
/// disabled, or a launch argument the listing command cannot take switches it off.</param>
public sealed record ListedItem(string Kind, string Name, string? Source = null, string? Off = null);

public static class ListedKinds
{
    /// <summary>An MCP server from configuration: the home's, a plugin's, or one the CLI builds in.</summary>
    public const string Server = "server";

    /// <summary>A server or app that comes with the signed-in ACCOUNT: claude.ai connectors, ChatGPT apps.</summary>
    public const string Connector = "connector";

    public const string Plugin = "plugin";

    /// <summary>Instructions, never tools. Listed, never counted as foreign.</summary>
    public const string Skill = "skill";

    /// <summary>A command the CLI runs on an event. It offers the model no tool; listed, never foreign.</summary>
    public const string Hook = "hook";

    /// <summary>The kinds that put a tool in front of the model.</summary>
    public static bool OffersTools(string kind) => kind is Server or Connector or Plugin;
}

/// <summary>
/// What one CLI said it would load, for one launch shape (a member's, isolated; or the Concierge's).
/// <see cref="Listed"/> false is NOT MEASURED, never clean: the CLI is missing, has no listing,
/// or its listing failed - <see cref="Detail"/> says which.
/// </summary>
public sealed record CliListing(
    string Command,
    bool Listed,
    IReadOnlyList<ListedItem> Items,
    IReadOnlyList<string> Ran,
    string? Detail = null)
{
    public static CliListing NotMeasured(string command, string detail, IReadOnlyList<string>? ran = null) =>
        new(command, false, [], ran ?? [], detail);
}

/// <summary>What running one listing command came back with. <see cref="ExitCode"/> null: it did not finish.</summary>
public sealed record ListingRun(int? ExitCode, string Stdout, string? Failure = null)
{
    public bool Succeeded => ExitCode == 0 && Failure is null;
}

/// <summary>
/// THE SEAM between the listers and a real CLI. Tests hand the listers the CLIs' real, recorded
/// output through this, so no test runs a CLI or reaches an account.
/// </summary>
public interface IListingRunner
{
    /// <summary>Whether <paramref name="command"/> resolves, the way a launch resolves it.</summary>
    bool Installed(string command);

    /// <param name="credential">An issued credential, applied as a member run applies it
    /// (<see cref="AgentEnvironment.ApplyIssued"/>); null for the shared home.</param>
    Task<ListingRun> RunAsync(
        string command, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
        CancellationToken ct, RunCredential? credential = null);

    /// <summary>A home of one listing's own, as an issued member run gets (<see cref="RunHome"/>);
    /// null when it could not be made.</summary>
    Task<string?> MakeHomeAsync(CancellationToken ct);

    /// <summary>Removes a home <see cref="MakeHomeAsync"/> made, and everything the CLI wrote in it.</summary>
    Task RemoveHomeAsync(string home);
}

/// <summary>
/// Runs a listing command the way a member's CLI is started: AS THE AGENT USER (the launch prefix),
/// with the Host's environment plus the launch's overlay, from an empty scratch folder so no
/// repository's own configuration is read. Never runs an agent-installed program as the Host: when
/// the agent user exists and cannot be reached, it does not run at all.
/// </summary>
/// <param name="homes">Where a listing's own home is made: the member temporary folders' root,
/// which the agent can write; <see cref="MemberTemp.Root"/> when null.</param>
public sealed class CliListingRunner(AgentLaunchUser? runAs = null, string? homes = null) : IListingRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    public bool Installed(string command) => PathSearch.Find(command) is not null;

    // A parent several launches share, as the launch check's: never swept.
    public Task<string?> MakeHomeAsync(CancellationToken ct) =>
        runAs is { Refuses: true }
            ? Task.FromResult<string?>(null)
            : RunHome.CreateAsync(homes ?? MemberTemp.Root, runAs, null, ct, memberFolder: false);

    public Task RemoveHomeAsync(string home) => RunHome.RemoveAsync(home, runAs);

    public async Task<ListingRun> RunAsync(
        string command, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
        CancellationToken ct, RunCredential? credential = null)
    {
        if (PathSearch.Find(command) is not { } resolved) return new ListingRun(null, "", $"'{command}' is not on PATH.");

        if (runAs is { Refuses: true }) return new ListingRun(null, "", runAs.Refusal($"'{command}'"));

        var scratch = Directory.CreateTempSubdirectory("harness-tool-listing-").FullName;
        runAs?.Share(scratch);

        try
        {
            var prefix = runAs?.Prefix ?? [];
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = prefix.Count > 0 ? prefix[0] : resolved,
                    WorkingDirectory = scratch,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                },
            };

            if (prefix.Count > 0)
            {
                foreach (var part in prefix.Skip(1)) process.StartInfo.ArgumentList.Add(part);
                process.StartInfo.ArgumentList.Add(resolved);
            }

            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            foreach (var (name, value) in environment) process.StartInfo.Environment[name] = value;

            // An issued credential as a member run gets it, then every provider key but this CLI's
            // own taken out, as at a member's spawn.
            var handedIn = AgentEnvironment.ApplyIssued(process.StartInfo.Environment, credential, environment);
            AgentEnvironment.ScopeProviderKeys(process.StartInfo.Environment, command, handedIn);

            if (!process.Start()) return new ListingRun(null, "", "It could not be started.");
            process.StandardInput.Close();

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            try
            {
                await process.WaitForExitAsync(ct).WaitAsync(Timeout, ct);
            }
            catch (TimeoutException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new ListingRun(null, "", $"It did not finish in {Timeout.TotalSeconds:0} seconds.");
            }

            var output = await stdout;
            await stderr;

            return new ListingRun(process.ExitCode, output);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ListingRun(null, "", exception.Message);
        }
        finally
        {
            // RECURSIVE DELETE REVIEWED: agents cannot write here. The Host made the scratch folder
            // owner-only and AgentLaunchUser.Share gives the agent's group read and traverse, never
            // write; the CLI runs in it but cannot create anything in it.
            try { Directory.Delete(scratch, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>
/// WHAT EACH INSTALLED CLI WOULD LOAD, from THE CLI'S OWN LISTING, never from reading its files:
/// the listing is the CLI's answer, and a second reader of its configuration is a second answer
/// waiting to disagree with the first. No listing makes a model call.
///
/// MEASURED on the CLIs in the image (claude 2.1.285, codex-cli 0.157.0, grok 1.0.44, copilot
/// 1.0.89), with the shared home left exactly as it was:
/// <list type="bullet">
/// <item>claude: `mcp list` names every server with the account's connectors as `claude.ai &lt;name&gt;`,
/// and honours ENABLE_CLAUDEAI_MCP_SERVERS=false. It takes NO launch flag - `--strict-mcp-config`
/// and `--setting-sources` placed before `mcp` are silently ignored - so what those two switch off
/// is marked here from the launch's own arguments. Its health check STARTS each configured stdio
/// server, as the Concierge's own launch does. `plugin list --json` lists plugins.</item>
/// <item>codex: `mcp list --json`, `features list` (apps, plugins, hooks) and `debug prompt-input`
/// (the skill listing the model is shown) all take the launch's `--disable`/`--enable`/`-c`;
/// `--ignore-user-config` is `exec`'s alone and is marked here.</item>
/// <item>grok: `inspect --json` lists servers, plugins, skills and hooks with their sources, and
/// reads the environment the launch sets.</item>
/// <item>copilot: `mcp list --json`, `plugin list --json` and `skill list --json`, each taking
/// `--disable-builtin-mcps` before the subcommand (the built-in server then reads `enabled: false`).</item>
/// </list>
/// A CLI not in this list has no listing: its presets are NOT MEASURED.
/// </summary>
public static class AgentToolListers
{
    /// <summary>The executables with a listing, by file name.</summary>
    public static IReadOnlyList<string> Listed { get; } = ["claude", "codex", "grok", "copilot"];

    /// <summary>
    /// What <paramref name="command"/> would load, run with <paramref name="environment"/> and, where
    /// its listing takes them, the launch's <paramref name="isolationArguments"/>; with
    /// <paramref name="credential"/> applied as a member run applies it, when one is issued.
    /// </summary>
    public static async Task<CliListing> ListAsync(
        string command, IReadOnlyList<string> isolationArguments, IReadOnlyDictionary<string, string> environment,
        IListingRunner runner, CancellationToken ct, RunCredential? credential = null)
    {
        var cli = Path.GetFileName(command);

        if (!Listed.Contains(cli, StringComparer.Ordinal))
            return CliListing.NotMeasured(command, $"'{cli}' has no listing of what it loads.");

        if (!runner.Installed(command)) return CliListing.NotMeasured(command, $"'{command}' is not on PATH.");

        var ran = new List<string>();
        var items = new List<ListedItem>();

        string? failed = null;

        // One listing command: run, then parsed IN FULL here, so output the parser cannot read is
        // this listing failing rather than an exception out of the probe.
        async Task Take(Func<string, IEnumerable<ListedItem>> parse, params string[] arguments)
        {
            var shown = string.Join(' ', [cli, .. arguments.Where(a => a.Length > 0)]);
            ran.Add(shown);
            if (failed is not null) return;

            var run = await runner.RunAsync(command, arguments, environment, ct, credential);
            if (!run.Succeeded)
            {
                failed = $"`{shown}` did not answer: {run.Failure ?? $"it exited {run.ExitCode}."}";
                return;
            }

            try
            {
                items.AddRange(parse(run.Stdout).ToList());
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                failed = $"`{shown}` answered in a shape this build does not read: {exception.Message}";
            }
        }

        switch (cli)
        {
            case "claude":
                await Take(o => ClaudeServers(o, isolationArguments), "mcp", "list");
                await Take(o => ClaudePlugins(o, isolationArguments), "plugin", "list", "--json");
                break;

            case "codex":
            {
                var flags = CodexFlags(isolationArguments);
                await Take(o => CodexServers(o, isolationArguments), [.. flags, "mcp", "list", "--json"]);
                await Take(CodexFeatures, [.. flags, "features", "list"]);
                await Take(CodexSkills, ["debug", "prompt-input", .. flags, ""]);
                break;
            }

            case "grok":
                await Take(GrokInspect, "inspect", "--json");
                break;

            case "copilot":
            {
                var flags = CopilotFlags(isolationArguments);
                await Take(CopilotServers, [.. flags, "mcp", "list", "--json"]);
                await Take(o => NamedArray(o, ListedKinds.Plugin), [.. flags, "plugin", "list", "--json"]);
                await Take(o => NamedArray(o, ListedKinds.Skill), [.. flags, "skill", "list", "--json"]);
                break;
            }
        }

        // ALL OR NOTHING. A listing that answered for servers and failed for plugins is not a
        // listing of what loads: a partial one would read as clean where it did not look.
        return failed is null
            ? new CliListing(command, true, items, ran)
            : CliListing.NotMeasured(command, failed, ran);
    }

    // `claude.ai Gmail: https://gmailmcp.googleapis.com/mcp/v1 - ✔ Connected`
    private static readonly Regex ClaudeServerLine = new(@"^(?<name>[^:]+?): (?<target>.*) - (?<status>.+)$");

    public static IEnumerable<ListedItem> ClaudeServers(string stdout, IReadOnlyList<string> launch)
    {
        var strict = launch.Contains("--strict-mcp-config", StringComparer.Ordinal) ? "--strict-mcp-config" : null;

        foreach (var line in Lines(stdout))
        {
            if (ClaudeServerLine.Match(line) is not { Success: true } match) continue;

            var name = match.Groups["name"].Value;
            var connector = name.StartsWith("claude.ai ", StringComparison.Ordinal);

            yield return new ListedItem(
                connector ? ListedKinds.Connector : ListedKinds.Server,
                name,
                connector ? "claude.ai account" : null,
                strict);
        }
    }

    public static IEnumerable<ListedItem> ClaudePlugins(string stdout, IReadOnlyList<string> launch)
    {
        // `--setting-sources` without `user` drops the user layer, and with it the plugins it enables.
        var index = launch.ToList().IndexOf("--setting-sources");
        var off = index >= 0 && index + 1 < launch.Count
                  && !launch[index + 1].Split(',').Contains("user", StringComparer.Ordinal)
            ? "--setting-sources " + launch[index + 1]
            : null;

        foreach (var item in NamedArray(stdout, ListedKinds.Plugin))
            yield return item with { Off = item.Off ?? off };
    }

    /// <summary>The launch's arguments codex's listing subcommands also take.</summary>
    public static IReadOnlyList<string> CodexFlags(IReadOnlyList<string> launch)
    {
        var flags = new List<string>();
        for (var i = 0; i < launch.Count - 1; i++)
        {
            if (launch[i] is "--disable" or "--enable" or "-c" or "--config")
            {
                flags.Add(launch[i]);
                flags.Add(launch[++i]);
            }
        }

        return flags;
    }

    public static IEnumerable<ListedItem> CodexServers(string stdout, IReadOnlyList<string> launch)
    {
        var ignored = launch.Contains("--ignore-user-config", StringComparer.Ordinal) ? "--ignore-user-config" : null;

        foreach (var item in NamedArray(stdout, ListedKinds.Server))
            yield return item with { Source = "~/.codex/config.toml", Off = item.Off ?? ignored };
    }

    /// <summary>The account and home surfaces codex switches with a feature flag, by that flag.</summary>
    private static readonly (string Feature, string Kind, string Name)[] CodexSurfaces =
    [
        ("apps", ListedKinds.Connector, "ChatGPT apps (codex_apps)"),
        ("plugins", ListedKinds.Plugin, "plugins"),
        ("remote_plugin", ListedKinds.Plugin, "remote plugins"),
        ("hooks", ListedKinds.Hook, "hooks"),
    ];

    public static IEnumerable<ListedItem> CodexFeatures(string stdout)
    {
        var state = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var line in Lines(stdout))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && bool.TryParse(parts[^1], out var on)) state[parts[0]] = on;
        }

        foreach (var (feature, kind, name) in CodexSurfaces)
        {
            if (!state.TryGetValue(feature, out var on)) continue;

            yield return new ListedItem(kind, name, "feature " + feature, on ? null : "disabled");
        }
    }

    // `- imagegen: Generate or edit raster images ...` under `### Available skills`.
    private static readonly Regex CodexSkillLine = new(@"^- (?<name>[^:\s]+): ");

    public static IEnumerable<ListedItem> CodexSkills(string stdout)
    {
        foreach (var text in PromptTexts(stdout))
        {
            if (!text.Contains("<skills_instructions>", StringComparison.Ordinal)) continue;

            var listing = false;
            foreach (var line in Lines(text))
            {
                if (line.StartsWith("### ", StringComparison.Ordinal)) listing = line == "### Available skills";
                else if (listing && CodexSkillLine.Match(line) is { Success: true } match)
                    yield return new ListedItem(ListedKinds.Skill, match.Groups["name"].Value);
            }
        }
    }

    private static IEnumerable<string> PromptTexts(string stdout)
    {
        using var document = JsonDocument.Parse(stdout);
        foreach (var message in document.RootElement.EnumerateArray())
        {
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;

            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) && text.GetString() is { } value) yield return value;
            }
        }
    }

    public static IEnumerable<ListedItem> GrokInspect(string stdout)
    {
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;

        foreach (var (property, kind) in new[]
                 {
                     ("mcpServers", ListedKinds.Server), ("plugins", ListedKinds.Plugin),
                     ("skills", ListedKinds.Skill), ("hooks", ListedKinds.Hook),
                 })
        {
            if (!root.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array) continue;

            foreach (var entry in list.EnumerateArray())
            {
                var name = Text(entry, "name") ?? Text(entry, "event") ?? Text(entry, "command") ?? entry.ToString();
                string? source = null;
                if (entry.TryGetProperty("source", out var from) && from.ValueKind == JsonValueKind.Object)
                    source = Text(from, "path") ?? Text(from, "type");

                var off = entry.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False
                    ? "disabled"
                    : null;

                yield return new ListedItem(kind, name, source, off);
            }
        }
    }

    /// <summary>The launch's arguments copilot's listing subcommands also take.</summary>
    public static IReadOnlyList<string> CopilotFlags(IReadOnlyList<string> launch) =>
        [.. launch.Where(a => a is "--disable-builtin-mcps")];

    public static IEnumerable<ListedItem> CopilotServers(string stdout)
    {
        using var document = JsonDocument.Parse(stdout);
        if (!document.RootElement.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var server in servers.EnumerateObject())
        {
            var off = server.Value.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False
                ? "disabled"
                : null;

            yield return new ListedItem(ListedKinds.Server, server.Name, Text(server.Value, "source"), off);
        }
    }

    /// <summary>A JSON array of objects with a `name` (and maybe `enabled`, `source`).</summary>
    public static IEnumerable<ListedItem> NamedArray(string stdout, string kind)
    {
        using var document = JsonDocument.Parse(stdout);
        if (document.RootElement.ValueKind != JsonValueKind.Array) yield break;

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if ((Text(entry, "name") ?? Text(entry, "id")) is not { } name) continue;

            var off = entry.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False
                ? "disabled"
                : null;

            yield return new ListedItem(kind, name, Text(entry, "source") ?? Text(entry, "scope"), off);
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IEnumerable<string> Lines(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
}
