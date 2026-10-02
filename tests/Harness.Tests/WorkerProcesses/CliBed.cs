using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A FAKE AGENT CLI ON A WORKER, for the bed's processes: a <c>claude</c> in <c>&lt;work&gt;/clibin</c>
/// that records every call - its arguments and its parent chain - under <c>&lt;out&gt;/cli</c> and
/// answers <c>auth status</c> (signed in), <c>mcp list</c> (one server, <c>bed-tool</c>),
/// <c>plugin list --json</c> (none) and <c>--version</c> (<c>9.9.9-bed</c>). It is on a worker's PATH
/// only, before the system's folders and nothing else, so no other CLI on this machine answers there;
/// the worker's HOME holds no login and its environment no provider key, so the probe asks the CLI.
/// </summary>
internal static class CliBed
{
    public const string Version = "9.9.9-bed";

    public const string Tool = "bed-tool";

    /// <summary>The system's own folders: a worker that may run the fake CLI and nothing else of this machine's.</summary>
    private const string SystemPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

    public static string Bin(this ProcessBed bed) => Path.Combine(bed.Work, "clibin");

    public static string Calls(this ProcessBed bed) => Path.Combine(bed.Out, "cli");

    /// <summary>Writes the fake <c>claude</c>; returns its folder.</summary>
    public static string WriteFakeClaude(this ProcessBed bed)
    {
        var bin = Directory.CreateDirectory(bed.Bin()).FullName;
        var calls = Directory.CreateDirectory(bed.Calls()).FullName;
        var claude = Path.Combine(bin, "claude");
        File.WriteAllText(claude, $$$"""
            #!/bin/sh
            out='{{{calls}}}/'"$$-$(date +%s%N)"
            p=$$; : > "$out.chain"
            while [ -n "$p" ] && [ "$p" -gt 1 ]; do echo "$p" >> "$out.chain"; p=$(sed 's/.*) //' /proc/$p/stat 2>/dev/null | cut -d' ' -f2); done
            echo "$*" > "$out.args"
            case " $* " in
              *" auth status "*) echo "signed in on the bed"; exit 0;;
              *" mcp list"*) echo "Checking MCP server health..."; echo ""; echo "{{{Tool}}}: npx {{{Tool}}} - Connected"; exit 0;;
              *" plugin list "*) echo "[]"; exit 0;;
              *" --version "*) echo "{{{Version}}} (Claude Code)"; exit 0;;
            esac
            cat >/dev/null
            printf '%s' '{"result":"done","usage":{"input_tokens":1,"output_tokens":1}}'
            """);
        File.SetUnixFileMode(claude, (UnixFileMode)Convert.ToInt32("755", 8));
        return bin;
    }

    /// <summary>A worker that finds the fake CLI first on its PATH, with an empty HOME and no Anthropic key.</summary>
    public static HostProcess StartCliWorker(this ProcessBed bed, string id)
    {
        var home = Directory.CreateDirectory(Path.Combine(bed.Work, "home-" + id)).FullName;
        return bed.StartWorker(id, more: new Dictionary<string, string>
        {
            ["PATH"] = bed.Bin() + ":" + SystemPath,
            ["HOME"] = home,
            ["ANTHROPIC_API_KEY"] = "",
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, ".claude"),
        });
    }

    /// <summary>Every call the fake CLI recorded: its arguments and its parent chain.</summary>
    public static IReadOnlyList<(string Arguments, IReadOnlyList<int> Chain)> CliCalls(this ProcessBed bed) =>
        Directory.Exists(bed.Calls())
            ? [.. Directory.GetFiles(bed.Calls(), "*.args").Order().Select(file => (
                File.ReadAllText(file).Trim(),
                (IReadOnlyList<int>)[.. ReadChain(Path.ChangeExtension(file, ".chain"))]))]
            : [];

    private static IEnumerable<int> ReadChain(string file) =>
        File.Exists(file) ? File.ReadAllLines(file).Where(l => l.Length > 0).Select(int.Parse) : [];

    /// <summary>Saves the bed's custom presets again (the fake one, with what <paramref name="change"/> adds), so the pre-flight and the launch check run again.</summary>
    public static async Task SaveCatalogAsync(this ProcessBed bed, Func<AgentDefinition, AgentDefinition>? change = null)
    {
        var custom = AgentCatalogFile.LoadCustom(bed.Root, TextWriter.Null).Select(d => change?.Invoke(d) ?? d).ToList();
        var body = JsonSerializer.Serialize(new { agents = custom }, AgentCatalogFile.JsonOptions);
        var saved = await bed.Person.PutAsync("/api/agents", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.True(saved.IsSuccessStatusCode, $"{(int)saved.StatusCode} {await saved.Content.ReadAsStringAsync()}");
    }

    /// <summary>One preset's report from <c>GET /api/agents/auth</c>.</summary>
    public static async Task<JsonElement?> AuthOfAsync(this ProcessBed bed, string preset) =>
        (await bed.GetAsync("/api/agents/auth")).EnumerateArray().FirstOrDefault(r => r.GetProperty("agent").GetString() == preset) is { ValueKind: JsonValueKind.Object } found
            ? found
            : null;

    /// <summary>One preset's pre-flight from <c>GET /api/agents/tools</c>.</summary>
    public static async Task<JsonElement?> ToolsOfAsync(this ProcessBed bed, string preset) =>
        (await bed.GetAsync("/api/agents/tools")).GetProperty("presets").EnumerateArray()
            .FirstOrDefault(r => r.GetProperty("preset").GetString() == preset) is { ValueKind: JsonValueKind.Object } found
            ? found
            : null;

    public static async Task<JsonElement> UpdateStateAsync(this ProcessBed bed, string preset) =>
        await bed.Person.GetFromJsonAsync<JsonElement>($"/api/agents/{preset}/update");
}
