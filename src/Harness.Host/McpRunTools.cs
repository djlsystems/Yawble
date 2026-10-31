using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Harness.Host;

/// <summary>
/// A TEAM'S MCP SERVERS ON THE HARNESS SERVER (MCP 2). At the start of a member's run its team's web address
/// servers are asked for their tools, and the run keeps that list until it ends: <c>/mcp</c> stays stateless,
/// and lists the platform's own tools plus these, each named <c>&lt;server&gt;_&lt;tool&gt;</c>
/// (<see cref="McpToolNames"/>), with the server's description and input schema as it gave them.
///
/// A CALL finds the server and the tool from the name, checks the caller's team holds that server at that
/// moment, and forwards it with the credential added (<see cref="McpForwarding"/>). What comes back is redacted
/// of every Connection value the call used, as plugin output is (<see cref="PluginMemberRunner"/>'s redaction);
/// image, audio and embedded-resource bytes are written into the caller's working copy and the result carries
/// <c>{path, mimeType, bytes}</c> in their place; text is cut at <see cref="MaximumText"/> characters. A refusal,
/// a server that does not answer and a server that no longer offers the tool are tool errors in a sentence:
/// the run goes on. The Host adds no run step. See <c>docs/mcp-servers.md</c>.
/// </summary>
public sealed class McpRunTools(
    McpServers servers, McpForwarding forwarding, TimeProvider clock, ILogger<McpRunTools> log,
    long maximumBinary = McpRunTools.DefaultMaximumBinary, int maximumText = McpRunTools.DefaultMaximumText)
{
    public const long DefaultMaximumBinary = 20L * 1024 * 1024;

    public const int DefaultMaximumText = 100_000;

    /// <summary>Where binary results go, under the working copy, one folder per server. The platform's own dot-folder,
    /// as <c>.harness-team</c> is: the product's name stays out of anything a path or a tool relies on.</summary>
    public static readonly string Folder = Path.Combine(".harness", "mcp");

    /// <summary>The argument that names where a binary result goes instead, relative to the working copy.</summary>
    public static readonly IReadOnlyList<string> OutputArguments = ["outputPath", "output_path"];

    public int MaximumText => maximumText;

    private readonly ConcurrentDictionary<string, RunTools> _runs = new(StringComparer.Ordinal);

    /// <summary>One tool of the run's list: its name here, its server, its name there, and how it is listed.</summary>
    public sealed record ListedTool(string Name, McpServerRecord Server, string Upstream, Tool Protocol);

    /// <summary>What one run was given: its team's tools as listed at its start, and its working copy.</summary>
    public sealed record RunTools(string Team, string WorkingCopy, IReadOnlyList<ListedTool> Tools);

    // ---- a run's list -----------------------------------------------------------------------------

    /// <summary>
    /// Reads <paramref name="member"/>'s team's web address servers' tools and keeps them for the run, until the
    /// answer is disposed. A server that cannot be listed is left out with a warning; two tools that would share a
    /// name are both left out. A team with none keeps nothing, and its list is the platform's alone.
    /// </summary>
    public async Task<IAsyncDisposable> BeginAsync(ContainerId member, string workingCopy, CancellationToken ct)
    {
        var assigned = await servers.Store.AssignedAsync(member.Team, ct);
        if (assigned.Count == 0) return Nothing.Instance;

        var entries = new List<McpServerRecord>();
        foreach (var given in assigned)
        {
            if (await servers.Store.GetAsync(given.ServerId, ct) is { Kind: McpServerRecord.Web } entry) entries.Add(entry);
        }

        var listed = await Task.WhenAll(entries.Select(entry => ListAsync(entry, ct)));
        var tools = listed.SelectMany(t => t).ToList();

        var clashing = McpToolNames.Clashes(tools.Select(t => (t.Server.Name, t.Upstream))).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in clashing)
        {
            log.LogWarning("Two tools of {Team}'s MCP servers would both be named {Name}; neither is listed for {Member}.", member.Team, name, member);
        }

        var run = new RunTools(member.Team, workingCopy, [.. tools.Where(t => !clashing.Contains(t.Name))]);
        var key = member.ToString();
        _runs[key] = run;
        return new Ending(this, key, run);
    }

    private async Task<IReadOnlyList<ListedTool>> ListAsync(McpServerRecord server, CancellationToken ct)
    {
        var (target, error) = await servers.TargetAsync(server, ct);
        if (target is null)
        {
            log.LogWarning("MCP server {Server} is not listed for this run: {Why}", server.Name, ConnectionTester.Redact(error!.Sentence, error.Secrets));
            return [];
        }

        var (tools, failure) = await forwarding.ListAsync(target, ct);
        if (tools is null)
        {
            log.LogWarning("MCP server {Server} is not listed for this run: {Why}", server.Name, ConnectionTester.Redact(failure!, target.Secrets));
            return [];
        }

        var listed = new List<ListedTool>();
        foreach (var tool in tools)
        {
            Tool? protocol;
            try
            {
                protocol = tool.Deserialize<Tool>(McpJsonUtilities.DefaultOptions);
            }
            catch (JsonException)
            {
                protocol = null;
            }

            if (protocol is null) continue;

            var upstream = protocol.Name;
            protocol.Name = McpToolNames.For(server.Name, upstream);
            listed.Add(new ListedTool(protocol.Name, server, upstream, protocol));
        }

        return listed;
    }

    /// <summary>The tools <paramref name="principal"/>'s run was given, beyond the platform's own: none outside a run.</summary>
    public IReadOnlyList<Tool> ListFor(string? principal) =>
        principal is not null && _runs.TryGetValue(principal, out var run) ? [.. run.Tools.Select(t => t.Protocol)] : [];

    /// <summary>The run <paramref name="principal"/> is in, for a test.</summary>
    public RunTools? RunOf(string principal) => _runs.GetValueOrDefault(principal);

    // ---- a call -----------------------------------------------------------------------------------

    public static string NotTheTeams(string server, string team) => $"{server} is not one of {team}'s MCP servers.";

    public static string DidNotAnswer(string server) => $"{server} did not answer.";

    public static string NoLongerOffers(string server, string tool) => $"{server} no longer offers {tool}.";

    /// <summary>
    /// A call of <paramref name="name"/> by <paramref name="principal"/>. An unknown name is the protocol's own
    /// unknown-tool error, as for any tool; everything else answers a result, an error result in a sentence.
    /// </summary>
    public async Task<CallToolResult> CallAsync(string? principal, string name, IDictionary<string, JsonElement>? arguments, CancellationToken ct)
    {
        if (principal is null || !ContainerId.TryParse(principal, out var member)) throw Unknown(name);

        var run = _runs.GetValueOrDefault(principal);

        // THE RUN'S OWN LIST FIRST; then what every web address entry last listed, so a member calling another
        // team's tool is told whose servers those are rather than that no such tool exists.
        var (server, upstream) = run?.Tools.FirstOrDefault(t => t.Name == name) is { } listed
            ? (listed.Server, listed.Upstream)
            : Find(await servers.Store.ListAsync(ct), name);
        if (server is null || upstream is null) throw Unknown(name);

        // HELD AT THIS MOMENT: an entry taken off the team, or removed from the instance, during the run is refused.
        var held = (await servers.Store.AssignedAsync(member.Team, ct)).Any(a => a.ServerId == server.Id);
        if (!held || await servers.Store.GetAsync(server.Id, ct) is not { Kind: McpServerRecord.Web } current)
        {
            return Error(NotTheTeams(server.Name, member.Team));
        }

        var args = arguments is null ? null : JsonSerializer.SerializeToNode(arguments, McpJsonUtilities.DefaultOptions) as JsonObject;

        var workingCopy = run?.WorkingCopy;
        string? output = null;
        if (OutputArguments.Select(a => args?[a]).OfType<JsonValue>().FirstOrDefault() is { } named && named.TryGetValue<string>(out var path))
        {
            if (Inside(workingCopy, path) is not { } inside)
            {
                return Error($"The output path `{path}` is outside your working copy: name a relative path inside it, or leave it out.");
            }

            output = inside;
        }

        var (target, error) = await servers.TargetAsync(current, ct);
        if (target is null) return Error(ConnectionTester.Redact(error!.Sentence, error.Secrets));

        var redactor = ValueRedactor.For(target.Secrets);
        var answer = await forwarding.CallAsync(target, upstream, args, ct);

        if (answer.NoAnswer) return Error(DidNotAnswer(current.Name));
        if (answer.Failure is { } failure) return Error(PluginMemberRunner.Redact($"{current.Name}: {failure}", redactor));

        // A REFUSAL OR AN ERROR RESULT MAY BE A TOOL THAT IS GONE: asked again, a list without it says so.
        if (answer.Error is not null || answer.Result?["isError"] is JsonValue flag && flag.TryGetValue<bool>(out var isError) && isError)
        {
            var (now, _) = await forwarding.ListAsync(target, ct);
            if (now is not null && !now.Any(t => t["name"] is JsonValue n && n.TryGetValue<string>(out var said) && said == upstream))
            {
                return Error(NoLongerOffers(current.Name, upstream));
            }

            if (answer.Error is { } refused) return Error(PluginMemberRunner.Redact($"{current.Name} refused the call: {refused}", redactor));
        }

        return Shape(answer.Result!, current.Name, workingCopy, output, redactor);
    }

    /// <summary>The entry and tool a name stands for, from what each web address entry last listed.</summary>
    private static (McpServerRecord? Server, string? Tool) Find(IReadOnlyList<McpServerRecord> all, string name)
    {
        foreach (var server in all.Where(s => s.Kind == McpServerRecord.Web))
        {
            foreach (var tool in server.Tools ?? [])
            {
                if (McpToolNames.For(server.Name, tool) == name) return (server, tool);
            }
        }

        return (null, null);
    }

    /// <summary><paramref name="path"/> made whole inside <paramref name="workingCopy"/>, or null when it is not inside.</summary>
    private static string? Inside(string? workingCopy, string path)
    {
        if (workingCopy is null || string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return null;

        var root = Path.GetFullPath(workingCopy).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var whole = Path.GetFullPath(Path.Combine(root, path));
        if (!whole.StartsWith(root, StringComparison.Ordinal) || whole.Length == root.Length) return null;

        // A FOLDER ON THE WAY THAT IS A LINK may lead out: each one that exists must resolve inside too.
        for (var folder = Path.GetDirectoryName(whole); folder is not null && folder.Length >= root.Length; folder = Path.GetDirectoryName(folder))
        {
            if (new DirectoryInfo(folder) is { Exists: true, LinkTarget: not null } link
                && !(link.ResolveLinkTarget(true)?.FullName + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return whole;
    }

    // ---- what comes back --------------------------------------------------------------------------

    /// <summary>The server's result as the member sees it: binary written out, every string redacted, text cut.</summary>
    private CallToolResult Shape(JsonObject result, string server, string? workingCopy, string? output, ValueRedactor redactor)
    {
        var content = result["content"] as JsonArray ?? [];
        var shaped = new JsonArray();
        var notes = new List<string>();
        var written = 0L;
        var stamp = clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var n = 0;

        foreach (var block in content.OfType<JsonObject>())
        {
            var (data, mime) = Binary(block);
            if (data is null)
            {
                shaped.Add(block.DeepClone());
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(data);
            }
            catch (FormatException)
            {
                notes.Add($"A {mime} result from {server} was not valid base64 and was dropped.");
                continue;
            }

            if (workingCopy is null)
            {
                notes.Add($"A {mime} result of {bytes.Length} bytes from {server} was dropped: this call is not in a run with a working copy.");
                continue;
            }

            if (written + bytes.Length > maximumBinary)
            {
                notes.Add($"A {mime} result of {bytes.Length} bytes from {server} was dropped: at most {maximumBinary / (1024 * 1024)} MB is written per call.");
                continue;
            }

            n++;
            var path = output is null ? Default(workingCopy, server, stamp, ref n, mime) : Numbered(output, n);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            written += bytes.Length;

            shaped.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = new JsonObject { ["path"] = path, ["mimeType"] = mime, ["bytes"] = bytes.Length }.ToJsonString(),
            });
        }

        foreach (var note in notes) shaped.Add(new JsonObject { ["type"] = "text", ["text"] = note });

        var answer = new JsonObject { ["content"] = shaped };
        if (result["isError"] is { } isError) answer["isError"] = isError.DeepClone();
        if (result["structuredContent"] is { } structured) answer["structuredContent"] = structured.DeepClone();
        if (result["_meta"] is { } meta) answer["_meta"] = meta.DeepClone();

        Redact(answer, redactor);
        Cut(answer);

        return answer.Deserialize<CallToolResult>(McpJsonUtilities.DefaultOptions)!;
    }

    /// <summary>A block's base64 bytes and MIME type, when it is an image, audio or an embedded resource's blob.</summary>
    private static (string? Data, string Mime) Binary(JsonObject block)
    {
        var type = (block["type"] as JsonValue)?.GetValue<string>();
        var holder = type switch
        {
            "image" or "audio" => block,
            "resource" => block["resource"] as JsonObject,
            _ => null,
        };

        var key = type == "resource" ? "blob" : "data";
        if (holder?[key] is not JsonValue value || !value.TryGetValue<string>(out var data)) return (null, "");

        var mime = holder["mimeType"] is JsonValue m && m.TryGetValue<string>(out var said) && said.Length > 0 ? said : "application/octet-stream";
        return (data, mime);
    }

    private static string Default(string workingCopy, string server, string stamp, ref int n, string mime)
    {
        var folder = Path.Combine(workingCopy, Folder, server);
        string path;
        while (File.Exists(path = Path.Combine(folder, $"{stamp}-{n}.{Extension(mime)}"))) n++;
        return path;
    }

    /// <summary>The named output path for the first binary block; <c>name-2.ext</c> and on for the rest.</summary>
    private static string Numbered(string output, int n) =>
        n == 1
            ? output
            : Path.Combine(Path.GetDirectoryName(output)!, $"{Path.GetFileNameWithoutExtension(output)}-{n}{Path.GetExtension(output)}");

    public static string Extension(string mime) => mime.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "image/png" => "png",
        "image/jpeg" or "image/jpg" => "jpg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        "image/svg+xml" => "svg",
        "image/bmp" => "bmp",
        "audio/mpeg" or "audio/mp3" => "mp3",
        "audio/wav" or "audio/x-wav" or "audio/wave" => "wav",
        "audio/ogg" => "ogg",
        "audio/webm" => "webm",
        "audio/flac" => "flac",
        "application/pdf" => "pdf",
        "application/json" => "json",
        "application/zip" => "zip",
        "text/plain" => "txt",
        "text/csv" => "csv",
        "text/html" => "html",
        _ => "bin",
    };

    /// <summary>Every string in the result, at any depth, redacted as plugin output is. Keys are kept.</summary>
    private static void Redact(JsonNode? node, ValueRedactor redactor)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    if (value is JsonValue v && v.TryGetValue<string>(out var text)) obj[key] = PluginMemberRunner.Redact(text, redactor);
                    else Redact(value, redactor);
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue v && v.TryGetValue<string>(out var text)) array[i] = PluginMemberRunner.Redact(text, redactor);
                    else Redact(array[i], redactor);
                }

                break;
        }
    }

    /// <summary>Text blocks cut at <see cref="MaximumText"/> characters in all, with a line saying the rest was cut.</summary>
    private void Cut(JsonObject answer)
    {
        var content = (JsonArray)answer["content"]!;
        var left = maximumText;
        var cut = 0L;
        var kept = new JsonArray();

        foreach (var block in content.ToList())
        {
            content.Remove(block);
            if (block is JsonObject { } text && text["type"] is JsonValue t && t.TryGetValue<string>(out var type) && type == "text"
                && text["text"] is JsonValue v && v.TryGetValue<string>(out var words))
            {
                if (words.Length > left)
                {
                    cut += words.Length - left;
                    if (left == 0) continue;
                    text["text"] = words[..left];
                    left = 0;
                }
                else
                {
                    left -= words.Length;
                }
            }

            kept.Add(block);
        }

        if (answer["structuredContent"] is { } structured && structured.ToJsonString().Length > maximumText)
        {
            answer.Remove("structuredContent");
            kept.Add(new JsonObject { ["type"] = "text", ["text"] = $"The structured result was over {maximumText:N0} characters and was cut." });
        }

        if (cut > 0)
        {
            kept.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = $"[The rest was cut: {cut:N0} more characters past the {maximumText:N0}-character limit.]",
            });
        }

        answer["content"] = kept;
    }

    private static CallToolResult Error(string sentence) => new() { IsError = true, Content = [new TextContentBlock { Text = sentence }] };

    private static McpProtocolException Unknown(string name) => new($"Unknown tool: '{name}'", McpErrorCode.InvalidParams);

    private sealed class Ending(McpRunTools owner, string key, RunTools run) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            owner._runs.TryRemove(new KeyValuePair<string, RunTools>(key, run));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Nothing : IAsyncDisposable
    {
        public static readonly Nothing Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
