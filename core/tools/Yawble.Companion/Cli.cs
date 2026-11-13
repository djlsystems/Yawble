using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Companion;

namespace Yawble.Companion;

/// <summary>
/// `yawble &lt;command&gt;`: one command, one connection. Text for a person by default; --json writes exactly one JSON document
/// to stdout ({ok, exitCode, text, ...} or {ok:false, error{code, message}}). Exit codes: <see cref="ExitCodes"/>.
/// A destructive command prints the plan the Yawble window shows and waits (--wait, default 120 s) for the person's decision.
/// </summary>
internal static class Cli
{
    public const int DefaultWaitSeconds = 120;
    public const int MaxWaitSecondsOption = 3600;
    public const string McpWaitVariable = "YAWBLE_MCP_WAIT";
    internal static readonly TimeSpan CallLimit = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan PairLimit = TimeSpan.FromMinutes(6);

    /// <summary>The build's version, the same as the core's (core/BuildVersion.targets).</summary>
    public static string Version { get; } =
        typeof(Cli).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "YawbleVersion")?.Value is { Length: > 0 } v
            ? v : "0.0.0+dev.unknown";

    private const string Help = """
        yawble: talk to the Yawble app running on this computer.

        usage: yawble [--client <name>] [--json] [--wait <seconds>] <command> ...

          pair --name <name> [--level read|act|destructive]   pair this computer (approve the code in the Yawble window)
          whoami                                   which client this is, and its level
          request <id> [--wait S]                  where an approval request stands
          forget                                   delete this computer's key (then revoke it in Settings > Companion)
          mcp                                      serve MCP over stdin/stdout (for Claude Code)
        """;

    /// <summary>How long `yawble mcp` waits for an approval (YAWBLE_MCP_WAIT seconds, default 60).</summary>
    public static TimeSpan McpWait(Func<string, string?> env)
    {
        var text = env(McpWaitVariable);
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s <= MaxWaitSecondsOption
            ? TimeSpan.FromSeconds(s)
            : McpStdio.DefaultWait;
    }

    public static async Task<int> RunAsync(string[] args, Func<string, string?> env, TextReader stdin, TextWriter stdout, TextWriter stderr,
        IControlConnector connector, CancellationToken ct)
    {
        // Options for yawble itself come before the command word. After it they are read only where they cannot be free
        // text: never inside a tell/answer/comment text, never after `--` (Commands decides where its text starts).
        var g = new GlobalOptions();
        var version = false;
        var help = false;
        var i = 0;
        for (; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--")
            {
                i++;
                break;
            }
            if (a is "--version" or "-V") version = true;
            else if (a is "--help" or "-h") help = true;
            else if (g.TryTake(args, ref i)) { }
            else if (a.Length > 1 && a[0] == '-') g.Error ??= $"'{a}' is not an option of yawble (yawble help lists them)";
            else break;
        }
        var rest = args[i..];

        string? tool = null;
        JsonObject? toolArgs = null;
        string? commandError = null;
        var command = rest.Length > 0 ? rest[0] : null;
        var commandArgs = new List<string>();
        if (command is "mcp" or "pair" or "whoami" or "request" or "forget" or "help")
        {
            // No free text in these: yawble's options may follow their words too (until `--`).
            for (var j = 0; j < rest.Length; j++)
            {
                if (j > 0 && rest[j] == "--")
                {
                    commandArgs.AddRange(rest[(j + 1)..]);
                    break;
                }
                if (j > 0 && g.TryTake(rest, ref j)) continue;
                commandArgs.Add(rest[j]);
            }
        }
        else if (command is not null && !Commands.TryMap(rest, g, out tool, out toolArgs, out commandError))
        {
            tool = null;
        }

        var output = new Output(g.Json, stdout, stderr);
        if (version)
        {
            if (g.Json) output.Set("version", Version);
            output.Line($"yawble {Version}");
            return output.Finish(ExitCodes.Ok);
        }
        if (g.Error is null && g.Client is not null && !KeyFiles.ValidName(g.Client))
            g.Error = $"'{g.Client}' is not a client name (lowercase letters, digits and '-', 1 to 32 characters)";
        if (g.Error is not null) return output.Fail(new UsageException(g.Error));
        if (help || commandArgs is ["help"])
        {
            output.Line(Help + "\n" + Commands.Usage);
            return output.Finish(ExitCodes.Ok);
        }
        if (command is null)
        {
            stderr.WriteLine(Help + "\n" + Commands.Usage);
            return output.Fail(new UsageException("no command given"));
        }
        if (commandError is not null) return output.Fail(new UsageException(commandError));
        var client = g.Client;
        var wait = g.Wait;

        string home;
        try
        {
            home = ControlDiscovery.Home(env);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return output.Fail(new HomeException($"YAWBLE_HOME is not a usable folder path ({e.Message})"));
        }
        var keys = new KeyFiles(home);

        try
        {
            if (tool is not null && toolArgs is not null)
                return await ToolAsync(tool, toolArgs, connector, keys, client, wait ?? DefaultWaitSeconds, output, ct).ConfigureAwait(false);
            switch (command)
            {
                case "mcp":
                    if (commandArgs.Count > 1) throw new UsageException("usage: yawble mcp");
                    return await new McpStdio(connector, keys, client, stdin, stdout, stderr, McpWait(env)).RunAsync(ct).ConfigureAwait(false);
                case "pair": return await PairAsync(commandArgs, connector, keys, output, ct).ConfigureAwait(false);
                case "whoami": return await WhoAmIAsync(commandArgs, connector, keys, client, output, ct).ConfigureAwait(false);
                case "request": return await RequestAsync(commandArgs, connector, keys, client, wait ?? 0, output, ct).ConfigureAwait(false);
                default: return Forget(commandArgs, keys, client, output);
            }
        }
        catch (ClientException e)
        {
            return output.Fail(e);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Interrupted; // Ctrl+C: quietly
        }
        catch (OperationCanceledException)
        {
            return output.Fail(new TimedOutException());
        }
        catch (Exception e)
        {
            // Nothing else is expected; still one clear line (or one JSON document), never a stack trace.
            return output.Fail(ExitCodes.Failed, "failed", $"yawble failed unexpectedly ({e.GetType().Name}): {e.Message}");
        }
    }

    /// <summary>The exit code after Ctrl+C (128 + SIGINT, as shells report it).</summary>
    public const int Interrupted = 130;

    // ------------------------------------------------------------------ tool commands

    private static async Task<int> ToolAsync(string tool, JsonObject toolArgs, IControlConnector connector, KeyFiles keys, string? client, int wait,
        Output output, CancellationToken ct)
    {
        await using var channel = await ControlSession.OpenAsync(connector, keys, client, ct).ConfigureAwait(false);
        JsonObject r;
        using (var bounded = Bounded(ct, CallLimit))
            r = await channel.CallAsync(CompanionProtocol.Call, new JsonObject { ["tool"] = tool, ["args"] = toolArgs }, null, bounded.Token).ConfigureAwait(false);
        var text = StreamChannel.Text(r["text"]) ?? "";
        var isError = r["isError"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        var requestId = StreamChannel.Text(r["requestId"]);
        output.Set("tool", tool);
        output.Set("isError", isError);
        if (requestId is null)
        {
            if (!isError)
            {
                output.Line(text);
                return output.Finish(ExitCodes.Ok);
            }
            var refused = text.StartsWith("refused", StringComparison.Ordinal);
            return output.Fail(refused ? ExitCodes.Refused : ExitCodes.Failed, refused ? "refused" : "failed", text);
        }
        output.Set("requestId", requestId);
        output.Line(text);
        if (wait > 0) output.Progress($"Waiting up to {wait} s for the person's decision in the Yawble window...");
        return await WaitForDecisionAsync(channel, requestId, wait, output, ct).ConfigureAwait(false);
    }

    /// <summary>Long-polls companion.request in steps of at most 60 s (the app's limit) for <paramref name="wait"/> seconds in all.</summary>
    private static async Task<int> WaitForDecisionAsync(IControlChannel channel, string id, int wait, Output output, CancellationToken ct)
    {
        JsonObject? status = null;
        var left = wait;
        while (left > 0)
        {
            var step = Math.Min(left, CompanionProtocol.MaxWaitSeconds);
            status = await RequestOnceAsync(channel, id, step, ct).ConfigureAwait(false);
            left -= step;
            if (StreamChannel.Text(status["state"]) is not ("pending" or "running")) break;
        }
        return Report(id, status, output);
    }

    private static async Task<JsonObject> RequestOnceAsync(IControlChannel channel, string id, int waitSeconds, CancellationToken ct)
    {
        var p = new JsonObject { ["id"] = id };
        if (waitSeconds > 0) p["waitSeconds"] = waitSeconds;
        using var bounded = Bounded(ct, TimeSpan.FromSeconds(waitSeconds) + CallLimit);
        try
        {
            return await channel.CallAsync(CompanionProtocol.Request, p, null, bounded.Token).ConfigureAwait(false);
        }
        catch (ControlErrorException e) when (e.Code == "not_found")
        {
            throw new RequestLostException(id);
        }
    }

    private static int Report(string id, JsonObject? status, Output output)
    {
        var state = status is null ? "pending" : StreamChannel.Text(status["state"]) ?? "pending";
        var result = status is null ? null : StreamChannel.Text(status["result"]);
        var nextId = status is null ? null : StreamChannel.Text(status["nextId"]);
        output.Set("state", state);
        if (result is not null) output.Set("result", result);
        if (nextId is not null) output.Set("nextId", nextId);
        switch (state)
        {
            case "done":
                output.Line(result ?? "Done.");
                return output.Finish(ExitCodes.Ok);
            case "failed":
                return output.Fail(ExitCodes.Failed, "failed", result ?? $"Request {id} was approved but did not succeed.");
            case "denied":
                return output.Fail(ExitCodes.Refused, "denied", $"The person denied request {id} in the Yawble window; nothing was done.");
            case "expired":
                return output.Fail(ExitCodes.Failed, "expired", $"Request {id} expired before the person decided; nothing was done.");
            case "changed":
                output.Line($"What request {id} would do changed before it was approved, so it did not run. The Yawble window shows a new request {nextId} " +
                            $"with the current list; check it with: yawble request {nextId} --wait {DefaultWaitSeconds}");
                return output.Finish(ExitCodes.Pending);
            case "running":
                output.Line($"Approved; still running. Check again with: yawble request {id} --wait {DefaultWaitSeconds}");
                return output.Finish(ExitCodes.Pending);
            default:
                output.Line($"Still waiting for the person's approval in the Yawble window. Check again with: yawble request {id} --wait {DefaultWaitSeconds}");
                return output.Finish(ExitCodes.Pending);
        }
    }

    private static async Task<int> RequestAsync(List<string> rest, IControlConnector connector, KeyFiles keys, string? client, int wait, Output output, CancellationToken ct)
    {
        if (rest.Count != 2) throw new UsageException("usage: yawble request <id> [--wait S]");
        await using var channel = await ControlSession.OpenAsync(connector, keys, client, ct).ConfigureAwait(false);
        var id = rest[1];
        output.Set("requestId", id);
        var first = await RequestOnceAsync(channel, id, 0, ct).ConfigureAwait(false);
        if (StreamChannel.Text(first["plan"]) is { Length: > 0 } plan) output.Line(plan);
        if (wait == 0 || StreamChannel.Text(first["state"]) is not ("pending" or "running")) return Report(id, first, output);
        return await WaitForDecisionAsync(channel, id, wait, output, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ pairing and the rest

    private static async Task<int> PairAsync(List<string> rest, IControlConnector connector, KeyFiles keys, Output output, CancellationToken ct)
    {
        string? name = null;
        var level = "read";
        for (var i = 1; i < rest.Count; i++)
        {
            switch (rest[i])
            {
                case "--name" when i + 1 < rest.Count: name = rest[++i]; break;
                case "--level" when i + 1 < rest.Count: level = rest[++i]; break;
                default: throw new UsageException("usage: yawble pair --name <name> [--level read|act|destructive]");
            }
        }
        if (name is null) throw new UsageException("usage: yawble pair --name <name> [--level read|act|destructive]");
        if (!KeyFiles.ValidName(name)) throw new UsageException($"'{name}' is not a client name (lowercase letters, digits and '-', 1 to 32 characters)");
        if (level is not ("read" or "act" or "destructive")) throw new UsageException("--level is read, act or destructive");

        // The key can be saved before the app is asked for one: a populated or unwritable YAWBLE_HOME fails here, not after approval.
        keys.Prepare();
        await using var channel = await connector.ConnectAsync(ct).ConfigureAwait(false);
        JsonObject reply;
        using (var bounded = Bounded(ct, PairLimit))
        {
            reply = await channel.CallAsync(CompanionProtocol.PairRequest, new JsonObject { ["name"] = name, ["level"] = level }, e =>
            {
                if (StreamChannel.Text(e["event"]) != CompanionProtocol.PairCode || e["data"] is not JsonObject data) return;
                var code = StreamChannel.Text(data["code"]) ?? "?";
                output.Set("code", code);
                output.Progress($"Pairing code: {code}");
                output.Progress($"In the Yawble window, check that it shows the same code for '{name}', then approve it there (within 5 minutes).");
            }, bounded.Token).ConfigureAwait(false);
        }
        var approved = reply["approved"] is JsonValue a && a.TryGetValue<bool>(out var yes) && yes;
        if (approved && StreamChannel.Text(reply["key"]) is { Length: > 0 } key)
        {
            reply.Remove("key");
            try
            {
                keys.Save(name, key); // at once: the app handed this key out once and keeps only its hash
            }
            catch (ClientException e)
            {
                return output.Fail(ExitCodes.Failed, "key_not_saved",
                    $"The app paired '{name}' but its key could not be saved ({e.Message}). Revoke '{name}' in the Yawble window (Settings > Companion) and pair again.");
            }
            var granted = StreamChannel.Text(reply["level"]) ?? level;
            output.Set("client", name);
            output.Set("level", granted);
            output.Set("keyFile", keys.PathOf(name));
            output.Line($"Paired as '{name}' with the {granted} level. Its key is saved in {keys.PathOf(name)} (only you can open it).");
            output.Line($"For Claude Code: claude mcp add --scope user yawble -- \"{Environment.ProcessPath ?? "yawble"}\" mcp");
            return output.Finish(ExitCodes.Ok);
        }
        var reason = StreamChannel.Text(reply["reason"]) ?? "not approved";
        return output.Fail(ExitCodes.Refused, "not_paired", $"Not paired: {reason}.");
    }

    private static async Task<int> WhoAmIAsync(List<string> rest, IControlConnector connector, KeyFiles keys, string? client, Output output, CancellationToken ct)
    {
        if (rest.Count != 1) throw new UsageException("usage: yawble whoami");
        await using var channel = await ControlSession.OpenAsync(connector, keys, client, ct).ConfigureAwait(false);
        JsonObject r;
        using (var bounded = Bounded(ct, CallLimit))
            r = await channel.CallAsync(CompanionProtocol.WhoAmI, null, null, bounded.Token).ConfigureAwait(false);
        var name = StreamChannel.Text(r["client"]) ?? "?";
        var level = StreamChannel.Text(r["level"]) ?? "?";
        output.Set("client", name);
        output.Set("level", level);
        output.Line($"{name} ({level}): {StreamChannel.Text(r["levelDescription"])}");
        return output.Finish(ExitCodes.Ok);
    }

    private static int Forget(List<string> rest, KeyFiles keys, string? client, Output output)
    {
        if (rest.Count != 1) throw new UsageException("usage: yawble [--client <name>] forget");
        var name = ControlSession.ResolveClient(keys, client);
        if (!keys.Delete(name)) throw new NotPairedException($"There is no key for '{name}' on this computer.");
        output.Set("client", name);
        output.Line($"Deleted this computer's key for '{name}'. The app still lists it: revoke it in the Yawble window " +
                    "(Settings > Companion > Revoke) so that key can never be used again.");
        return output.Finish(ExitCodes.Ok);
    }

    private static CancellationTokenSource Bounded(CancellationToken ct, TimeSpan limit)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);
        return cts;
    }

    /// <summary>Text for a person, or (--json) one document written at the end.</summary>
    private sealed class Output(bool json, TextWriter stdout, TextWriter stderr)
    {
        private readonly JsonObject _doc = [];
        private readonly StringBuilder _text = new();

        public void Line(string text)
        {
            if (!json)
            {
                stdout.WriteLine(text);
                stdout.Flush();
                return;
            }
            if (_text.Length > 0) _text.Append('\n');
            _text.Append(text);
        }

        /// <summary>Something the person must see while yawble waits (a pairing code): stdout as text, stderr with --json.</summary>
        public void Progress(string text)
        {
            var writer = json ? stderr : stdout;
            writer.WriteLine(text);
            writer.Flush();
        }

        public void Set(string name, JsonNode? value) => _doc[name] = value;

        public void Set(string name, string value) => _doc[name] = value;

        public void Set(string name, bool value) => _doc[name] = value;

        public int Fail(ClientException e) => Fail(e.ExitCode, e.Code, e.Message);

        public int Fail(int exitCode, string code, string message)
        {
            if (json) _doc["error"] = new JsonObject { ["code"] = code, ["message"] = message };
            else
            {
                stderr.WriteLine(message);
                stderr.Flush();
            }
            return Finish(exitCode);
        }

        public int Finish(int exitCode)
        {
            if (json)
            {
                _doc["ok"] = exitCode == ExitCodes.Ok;
                _doc["exitCode"] = exitCode;
                if (_text.Length > 0) _doc["text"] = _text.ToString();
                stdout.WriteLine(_doc.ToJsonString());
                stdout.Flush();
            }
            return exitCode;
        }
    }
}

/// <summary>yawble's own options: --json, --client &lt;name&gt;, --wait &lt;seconds&gt;, each also as --opt=value.</summary>
internal sealed class GlobalOptions
{
    public bool Json;
    public string? Client;
    public int? Wait;

    /// <summary>The first problem found (a missing or bad value), reported as a usage error.</summary>
    public string? Error;

    /// <summary>Takes <c>args[i]</c> (and its value, moving <paramref name="i"/> past it) when it is one of these options.</summary>
    public bool TryTake(IReadOnlyList<string> args, ref int i)
    {
        var a = args[i];
        if (!a.StartsWith("--", StringComparison.Ordinal)) return false;
        var eq = a.IndexOf('=');
        var name = eq > 0 ? a[..eq] : a;
        switch (name)
        {
            case "--json":
                if (eq > 0) Error ??= "--json takes no value";
                Json = true;
                return true;
            case "--client":
                Client = Value(args, ref i, eq, name) ?? Client;
                return true;
            case "--wait":
                var text = Value(args, ref i, eq, name);
                if (text is null) return true;
                if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var w) && w <= Cli.MaxWaitSecondsOption)
                    Wait = w;
                else
                    Error ??= $"--wait needs a whole number of seconds from 0 to {Cli.MaxWaitSecondsOption}, not '{text}'";
                return true;
            default:
                return false;
        }
    }

    private string? Value(IReadOnlyList<string> args, ref int i, int eq, string name)
    {
        if (eq > 0) return args[i][(eq + 1)..];
        if (i + 1 < args.Count) return args[++i];
        Error ??= $"{name} needs a value";
        return null;
    }
}
