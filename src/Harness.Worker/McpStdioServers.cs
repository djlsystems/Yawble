using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Harness.Contracts;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// THE MCP SERVERS THIS WORKER RUNS (MCP 3). An entry the platform runs (<c>npx -y pkg@1.2.3</c>, <c>uvx pkg==1.2.3</c>)
/// is started here on the first request for it, and kept: ONE PROCESS PER SERVER ON THIS WORKER, shared by every
/// caller, stopped after <see cref="DefaultIdle"/> without a request, when control says its entry or a connection it
/// reads changed (<see cref="McpStdioStop"/>), or when a request brings another <see cref="McpStdioLaunch.Fingerprint"/>.
/// A process that stops by itself is started again by the next request; a request it was answering when it stopped
/// fails with <see cref="Stopped"/>.
///
/// <para>
/// AS THE <c>mcp</c> USER, through the same <c>setpriv</c> switch agent children use (<see cref="AgentLaunchUser"/>),
/// in a session of its own (<c>setsid</c>), with every capability cleared. Its environment is the entry's variables,
/// a minimal <c>PATH</c> and a <c>HOME</c> under <see cref="Root"/>, and nothing of the worker's: no key, no other
/// secret. A shell started as <c>mcp</c> makes its home and its working folder (<c>work/&lt;worker&gt;/&lt;server&gt;</c>)
/// owner-only, enters the folder and execs the command, so npm's and uv's caches land in that home. The pipes are
/// this worker's: control reaches the process through this worker's connection, and no agent can open them.
/// </para>
///
/// <para>
/// The protocol is MCP over stdio, newline-delimited JSON-RPC: initialize (within the request's start bound, which
/// covers the package's install), the initialized notification, then the requests. A call's own bound counts from
/// the moment initialize was answered. A server's request to us (<c>ping</c>, or anything else) is answered at once.
/// </para>
/// </summary>
public sealed class McpStdioServers : IAsyncDisposable
{
    /// <summary>The user servers run as when it exists on this machine.</summary>
    public const string UserName = "mcp";

    /// <summary>How long a server may go without a request before it is stopped.</summary>
    public static readonly TimeSpan DefaultIdle = TimeSpan.FromMinutes(10);

    /// <summary>The PATH a server is given: the system folders where the image puts node, npx, uv and uvx.</summary>
    public const string DefaultPath = "/usr/local/bin:/usr/bin:/bin";

    /// <summary>The longest line read from a server: past it the answer is dropped whole, with a sentence.</summary>
    public const int MaximumLine = WorkerFrameCodec.MaxWriteBytes;

    /// <summary>The most of an answer that crosses to control, as the frame writes it, with room for the frame.</summary>
    public const int MaximumAnswer = WorkerFrameCodec.MaxWriteBytes - 256 * 1024;

    private const int MaximumPages = 20;

    private const string Script = "umask 077; mkdir -p -- \"$HOME\" \"$1\" && cd -- \"$1\" || exit 126; shift; exec \"$@\"";

    private readonly WorkerId _worker;
    private readonly AgentLaunchUser _user;
    private readonly string _path;
    private readonly TimeSpan _idle;
    private readonly TimeProvider _clock;
    private readonly ILogger? _log;
    private readonly ConcurrentDictionary<string, Session> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly ITimer _sweeper;

    /// <param name="user">Who servers run as: <see cref="ResolveUser"/> in the image.</param>
    /// <param name="root">The <c>mcp</c> user's folder: its home and every working folder are under it.</param>
    /// <param name="path">The PATH servers are given; a test puts a fake <c>npx</c> first.</param>
    public McpStdioServers(
        WorkerId worker, AgentLaunchUser user, string root, string? path = null, TimeSpan? idle = null, TimeProvider? clock = null, ILogger? log = null)
    {
        _worker = worker;
        _user = user;
        Root = root;
        _path = path ?? DefaultPath;
        _idle = idle ?? DefaultIdle;
        _clock = clock ?? TimeProvider.System;
        _log = log;

        var every = TimeSpan.FromTicks(Math.Clamp(_idle.Ticks / 4, TimeSpan.FromMilliseconds(50).Ticks, TimeSpan.FromSeconds(30).Ticks));
        _sweeper = _clock.CreateTimer(_ => _ = SweepAsync(), null, every, every);
    }

    /// <summary>This machine's servers: as <c>mcp</c> under <c>&lt;data root&gt;/mcp</c> when the worker can switch to it,
    /// else as the worker's own user under its temp folder (development, the test suite).</summary>
    public static McpStdioServers For(WorkerId worker, ILogger? log = null)
    {
        var user = ResolveUser();
        return new McpStdioServers(worker, user, RootFor(user), log: log);
    }

    /// <summary>Where <paramref name="user"/>'s servers keep their home and working folders on this machine.</summary>
    public static string RootFor(AgentLaunchUser user) => user.Switches
        ? Path.Combine(Environment.GetEnvironmentVariable("HARNESS_DATA_ROOT") is { Length: > 0 } data ? data : "/data", "mcp")
        : Path.Combine(Path.GetTempPath(), "harness-mcp-servers");

    /// <summary>The <c>mcp</c> user, decided as the agent user is - except that nothing is shared with its group.</summary>
    public static AgentLaunchUser ResolveUser(string passwd = "/etc/passwd") => AgentLaunchUser.Resolve(UserName, passwd, shares: false);

    /// <summary>The <c>mcp</c> user's folder on this worker.</summary>
    public string Root { get; }

    /// <summary>Who servers run as here.</summary>
    public AgentLaunchUser User => _user;

    /// <summary>What a call that a server's stop interrupted fails with.</summary>
    public static string Stopped(string server) => $"{server} stopped; the next call starts it again.";

    /// <summary>The working folder of <paramref name="server"/> on this worker.</summary>
    public string WorkingFolder(string server) => Path.Combine(Root, "work", Safe(_worker.Value), Safe(server));

    /// <summary>The process id of <paramref name="server"/>'s process here, while one runs.</summary>
    public int? Pid(string server) => _running.TryGetValue(server, out var session) && session.Alive ? session.Pid : null;

    // ---- requests ---------------------------------------------------------------------------------

    /// <summary>The answer to <paramref name="request"/>. Never throws but for <paramref name="ct"/>.</summary>
    public async Task<McpStdioAnswered> AnswerAsync(McpStdioRequest request, CancellationToken ct = default)
    {
        var launch = request.Launch;
        var secrets = launch.Environment.Values.Where(v => v.Length > 0).ToList();
        var startBound = TimeSpan.FromSeconds(Math.Max(1, request.StartSeconds));
        var callBound = TimeSpan.FromSeconds(Math.Max(1, request.CallSeconds));

        if (request.Method == McpStdioRequest.Check)
        {
            Session? check = null;
            try
            {
                check = await StartAsync(launch, startBound, ct);
                return Bounded(request.Request, await ListAsync(check, launch.Name, callBound, ct));
            }
            catch (StartFailed failed)
            {
                return Answer(request.Request, failure: ConnectionTester.Redact(failed.Message, secrets));
            }
            finally
            {
                if (check is not null) await check.StopAsync();
            }
        }

        Session session;
        try
        {
            session = await EnsureAsync(launch, startBound, ct);
        }
        catch (StartFailed failed)
        {
            return Answer(request.Request, failure: ConnectionTester.Redact(failed.Message, secrets));
        }

        session.Begin(_clock.GetUtcNow());
        try
        {
            return request.Method switch
            {
                McpStdioRequest.List => Bounded(request.Request, await ListAsync(session, launch.Name, callBound, ct)),
                McpStdioRequest.Call => Bounded(request.Request, await session.RequestAsync("tools/call", new JsonObject
                {
                    ["name"] = request.Tool ?? "",
                    ["arguments"] = request.Arguments?.DeepClone() ?? new JsonObject(),
                }, callBound, ct) switch
                {
                    { Stopped: true } => Reply.Failed(Stopped(launch.Name)),
                    var reply => reply,
                }),
                _ => Answer(request.Request, failure: $"A worker does not know the request `{request.Method}` for {launch.Name}."),
            };
        }
        finally
        {
            session.End(_clock.GetUtcNow());
        }
    }

    /// <summary>Stops <paramref name="server"/>'s process here, if one runs. True when one did.</summary>
    public async Task<bool> StopAsync(string server)
    {
        var gate = _gates.GetOrAdd(server, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (!_running.TryRemove(server, out var session)) return false;
            await session.StopAsync();
            _log?.LogInformation("Worker {Worker} stopped MCP server {Server}.", _worker, session.Name);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Stops every process with no request in <see cref="DefaultIdle"/> (or the idle given), and forgets
    /// every one that has stopped by itself. Answers how many it stopped.</summary>
    public async Task<int> SweepAsync()
    {
        var stopped = 0;
        var now = _clock.GetUtcNow();
        foreach (var (server, session) in _running.ToArray())
        {
            if (session.Alive && !session.IdleSince(now, _idle)) continue;

            var gate = _gates.GetOrAdd(server, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(0)) continue;
            try
            {
                if (!_running.TryGetValue(server, out var current) || current != session) continue;
                if (session.Alive && !session.IdleSince(_clock.GetUtcNow(), _idle)) continue;

                _running.TryRemove(server, out _);
                var wasAlive = session.Alive;
                await session.StopAsync();
                if (wasAlive)
                {
                    stopped++;
                    _log?.LogInformation("Worker {Worker} stopped MCP server {Server}: no call in {Minutes} minutes.", _worker, session.Name, _idle.TotalMinutes);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        return stopped;
    }

    public async ValueTask DisposeAsync()
    {
        await _sweeper.DisposeAsync();
        foreach (var server in _running.Keys.ToArray()) await StopAsync(server);
    }

    // ---- starting -----------------------------------------------------------------------------------

    private async Task<Session> EnsureAsync(McpStdioLaunch launch, TimeSpan startBound, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(launch.Server, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_running.TryGetValue(launch.Server, out var running))
            {
                if (running.Alive && running.Fingerprint == launch.Fingerprint) return running;

                // A PROCESS THAT CLOSED ITS OUTPUT may still be there, or have left children in its group: stopped either way.
                _running.TryRemove(launch.Server, out _);
                var wasAlive = running.Alive;
                await running.StopAsync();
                if (wasAlive) _log?.LogInformation("Worker {Worker} stopped MCP server {Server}: its entry or a connection it reads changed.", _worker, launch.Name);
            }

            var started = await StartAsync(launch, startBound, ct);
            _running[launch.Server] = started;
            return started;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Session> StartAsync(McpStdioLaunch launch, TimeSpan startBound, CancellationToken ct)
    {
        if (_user.Refuses) throw new StartFailed(_user.Refusal($"MCP server {launch.Name}"));
        if (!OperatingSystem.IsLinux() || SystemCommand.Find("sh") is not { } sh)
        {
            throw new StartFailed($"{launch.Name} was not started: a worker runs MCP servers only on Linux, with sh in a system directory.");
        }

        if (ChildProcess.StartInfo(sh, "/", _user) is not { } start)
        {
            throw new StartFailed($"{launch.Name} was not started: setsid is not in a system directory on worker {_worker}.");
        }

        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(Script);
        start.ArgumentList.Add("sh");
        start.ArgumentList.Add(WorkingFolder(launch.Server));
        foreach (var part in launch.CommandLine) start.ArgumentList.Add(part);

        // NOTHING OF THE WORKER'S: the entry's variables, then the two the platform sets, which an entry cannot replace.
        start.Environment.Clear();
        foreach (var (name, value) in launch.Environment) start.Environment[name] = value;
        start.Environment["PATH"] = _path;
        start.Environment["HOME"] = Path.Combine(Root, "home");

        var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new StartFailed($"{launch.Name} could not be started on worker {_worker}: {exception.Message}");
        }

        _log?.LogInformation("Worker {Worker} started MCP server {Server} ({Command} {Package}@{Version}) as {User}, pid {Pid}.",
            _worker, launch.Name, launch.Command, launch.Package, launch.Version, _user.Switches ? _user.Name : "the worker's own user", process.Id);

        var session = new Session(launch.Name, launch.Fingerprint, process, _clock.GetUtcNow());
        session.Read();

        // THE START'S BOUND covers the install: initialize is answered once the package is there and running.
        var initialized = await session.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = McpServerCheck.ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "Harness", ["version"] = "1" },
        }, startBound, ct);

        if (initialized.Result is null)
        {
            var why = initialized switch
            {
                { NoAnswer: true } => $"{launch.Name} did not start within {startBound.TotalSeconds:0} seconds; the next call tries again.",
                { Stopped: true } => $"{launch.Name} stopped before it answered initialize{session.ExitText()}; the next call tries again.",
                { Error: { } error } => $"{launch.Name} refused initialize: {error}",
                _ => $"{launch.Name} did not answer initialize as an MCP server.",
            };
            await session.StopAsync();
            throw new StartFailed(why + session.LastWords());
        }

        await session.NotifyAsync("notifications/initialized");
        return session;
    }

    private static async Task<Reply> ListAsync(Session session, string name, TimeSpan bound, CancellationToken ct)
    {
        var tools = new JsonArray();
        string? cursor = null;
        for (var page = 0; page < MaximumPages; page++)
        {
            var parameters = new JsonObject();
            if (cursor is not null) parameters["cursor"] = cursor;

            var listed = await session.RequestAsync("tools/list", parameters, bound, ct);
            if (listed.Stopped) return Reply.Failed(Stopped(name));
            if (listed.Result is not { } result) return listed;
            if (result["tools"] is not JsonArray array) return Reply.Failed($"{name} answered the tools list without any tools.");

            foreach (var tool in array.OfType<JsonObject>()) tools.Add(tool.DeepClone());

            cursor = result["nextCursor"] is JsonValue next && next.TryGetValue<string>(out var said) ? said : null;
            if (string.IsNullOrEmpty(cursor)) break;
        }

        return Reply.Answered(new JsonObject { ["tools"] = tools });
    }

    /// <summary>A reply as the answer that crosses, dropped whole with a sentence when it would not fit the frame.</summary>
    private static McpStdioAnswered Bounded(string request, Reply reply)
    {
        if (reply.Result is { } result && Encoding.UTF8.GetByteCount(result.ToJsonString(WorkerFrameCodec.Json)) > MaximumAnswer)
        {
            return Answer(request, result: TooBig());
        }

        return Answer(request, reply.Result, reply.Error, reply.NoAnswer, reply.Failure);
    }

    private static JsonObject TooBig() => new()
    {
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = $"The answer was more than {MaximumAnswer / (1024 * 1024)} MB and was dropped whole: at most 20 MB of binary results is written per call.",
        }),
        ["isError"] = true,
    };

    private static McpStdioAnswered Answer(
        string request, JsonObject? result = null, string? error = null, bool noAnswer = false, string? failure = null) =>
        new(request, result, error, noAnswer, failure);

    private static string Safe(string name) => Regex.Replace(name, "[^A-Za-z0-9_-]", "_");

    private sealed class StartFailed(string sentence) : Exception(sentence);

    // ---- one process ---------------------------------------------------------------------------------

    /// <summary>What one request came to: a result, the server's error, no answer in time, the process stopped, or a sentence.</summary>
    private sealed record Reply(JsonObject? Result, string? Error, bool NoAnswer, bool Stopped, string? Failure)
    {
        public static Reply Answered(JsonObject result) => new(result, null, false, false, null);

        public static Reply Failed(string sentence) => new(null, null, false, false, sentence);
    }

    /// <summary>One server process and the JSON-RPC conversation over its pipes.</summary>
    private sealed class Session(string name, string fingerprint, Process process, DateTimeOffset started)
    {
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly StringBuilder _stderr = new();
        private long _next;
        private long _lastUsed = started.UtcTicks;
        private int _inFlight;

        public string Name => name;

        public string Fingerprint => fingerprint;

        public int Pid { get; } = process.Id;

        public bool Alive => !_exited.Task.IsCompleted;

        public void Begin(DateTimeOffset now)
        {
            Interlocked.Increment(ref _inFlight);
            Interlocked.Exchange(ref _lastUsed, now.UtcTicks);
        }

        public void End(DateTimeOffset now)
        {
            Interlocked.Exchange(ref _lastUsed, now.UtcTicks);
            Interlocked.Decrement(ref _inFlight);
        }

        /// <summary>No request in flight, and none for <paramref name="idle"/>.</summary>
        public bool IdleSince(DateTimeOffset now, TimeSpan idle) =>
            Volatile.Read(ref _inFlight) == 0 && now.UtcTicks - Interlocked.Read(ref _lastUsed) >= idle.Ticks;

        /// <summary>Starts reading both pipes; when stdout ends the process has stopped, and every request waiting fails.</summary>
        public void Read()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var reader = new StreamReader(process.StandardOutput.BaseStream, new UTF8Encoding(false));
                    var lines = new Lines(reader, MaximumLine);
                    while (await lines.NextAsync() is { } line)
                    {
                        if (line.TooLong is { } id)
                        {
                            if (_pending.TryRemove(id, out var waiting)) waiting.TrySetResult(new JsonObject { ["result"] = TooBig() });
                            continue;
                        }

                        await HandleAsync(line.Text!);
                    }
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
                {
                }
                finally
                {
                    try
                    {
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                    }
                    catch (TimeoutException)
                    {
                    }

                    _exited.TrySetResult();
                    foreach (var (id, waiting) in _pending.ToArray())
                    {
                        if (_pending.TryRemove(id, out _)) waiting.TrySetCanceled();
                    }
                }
            });

            _ = Task.Run(async () =>
            {
                var buffer = new char[4096];
                try
                {
                    while (await process.StandardError.ReadAsync(buffer.AsMemory()) is var n and > 0)
                    {
                        lock (_stderr)
                        {
                            _stderr.Append(buffer, 0, n);
                            if (_stderr.Length > 8192) _stderr.Remove(0, _stderr.Length - 4096);
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
                {
                }
            });
        }

        /// <summary>One request, answered within <paramref name="bound"/>.</summary>
        public async Task<Reply> RequestAsync(string method, JsonObject parameters, TimeSpan bound, CancellationToken ct)
        {
            if (!Alive) return new Reply(null, null, false, true, null);

            var id = Interlocked.Increment(ref _next);
            var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = answer;
            try
            {
                if (!await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }))
                {
                    return new Reply(null, null, false, true, null);
                }

                var done = await Task.WhenAny(answer.Task, Task.Delay(bound, ct));
                if (done != answer.Task) return new Reply(null, null, true, false, null);
                if (answer.Task.IsCanceled) return new Reply(null, null, false, true, null);

                var message = await answer.Task;
                if (message["error"] is JsonObject error)
                {
                    return new Reply(null, error["message"] is JsonValue said && said.TryGetValue<string>(out var text) && text.Length > 0 ? text : "no reason given", false, false, null);
                }

                return message["result"] is JsonObject result
                    ? Reply.Answered(result)
                    : Reply.Failed($"{name} answered without a result.");
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        public async Task NotifyAsync(string method) =>
            await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method });

        /// <summary>Closes its input, asks its whole process group to stop, and kills it after two seconds.</summary>
        public async Task StopAsync()
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }

            _ = kill(-Pid, SigTerm);
            try
            {
                await _exited.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
            }

            _ = kill(-Pid, SigKill);
            ChildProcess.Kill(process);
            try
            {
                await _exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
            }

            process.Dispose();
        }

        /// <summary>" (exit code N)" once the process has gone, for a sentence.</summary>
        public string ExitText()
        {
            try
            {
                return process.HasExited ? $" (exit code {process.ExitCode.ToString(CultureInfo.InvariantCulture)})" : "";
            }
            catch (InvalidOperationException)
            {
                return "";
            }
        }

        /// <summary>The end of what it wrote to stderr, as a sentence's last part; empty when it wrote nothing.</summary>
        public string LastWords()
        {
            string text;
            lock (_stderr) text = _stderr.ToString().Trim();
            if (text.Length == 0) return "";
            if (text.Length > 1000) text = "…" + text[^1000..];
            return $" Its last words: {text}";
        }

        private async Task HandleAsync(string line)
        {
            JsonObject? message;
            try
            {
                message = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                return;
            }

            if (message is null) return;

            var hasMethod = message["method"] is JsonValue;
            var idNode = message["id"];

            // A RESPONSE to one of ours.
            if (!hasMethod)
            {
                if (idNode is JsonValue value && value.TryGetValue<long>(out var id) && _pending.TryRemove(id, out var waiting))
                {
                    waiting.TrySetResult(message);
                }

                return;
            }

            // A REQUEST FROM THE SERVER: ping is answered, anything else is not offered. A notification is read and let go.
            if (idNode is null) return;

            var method = message["method"]!.GetValue<string>();
            await WriteAsync(method == "ping"
                ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode.DeepClone(), ["result"] = new JsonObject() }
                : new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = idNode.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"The client does not offer {method}." },
                });
        }

        private async Task<bool> WriteAsync(JsonObject message)
        {
            await _write.WaitAsync();
            try
            {
                await process.StandardInput.WriteAsync(message.ToJsonString() + "\n");
                await process.StandardInput.FlushAsync();
                return true;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                return false;
            }
            finally
            {
                _write.Release();
            }
        }
    }

    /// <summary>
    /// A server's output, line by line, with a bound: each line's <see cref="Line.Text"/>, or for a line past the bound
    /// the id of the response it was (<see cref="Line.TooLong"/>), read from its start or its end, where JSON-RPC
    /// writers put it.
    /// </summary>
    private sealed class Lines(StreamReader reader, int maximum)
    {
        private readonly char[] _buffer = new char[64 * 1024];
        private int _start;
        private int _end;

        public sealed record Line(string? Text, long? TooLong);

        public async Task<Line?> NextAsync()
        {
            var line = new StringBuilder();
            var head = "";
            var tail = new StringBuilder();
            var over = false;

            while (true)
            {
                if (_start == _end)
                {
                    _start = 0;
                    _end = await reader.ReadAsync(_buffer.AsMemory());
                    if (_end == 0)
                    {
                        if (line.Length == 0 && !over) return null;
                        break;
                    }
                }

                var newline = Array.IndexOf(_buffer, '\n', _start, _end - _start);
                var stop = newline < 0 ? _end : newline;
                var segment = _buffer.AsSpan(_start, stop - _start);
                _start = newline < 0 ? _end : newline + 1;

                if (!over)
                {
                    line.Append(segment);
                    if (line.Length > maximum)
                    {
                        over = true;
                        head = line.ToString(0, 4096);
                        line.Clear();
                    }
                }
                else
                {
                    tail.Append(segment);
                    if (tail.Length > 8192) tail.Remove(0, tail.Length - 4096);
                }

                if (newline >= 0) break;
            }

            if (!over) return new Line(line.ToString().TrimEnd('\r'), null);

            var id = Regex.Match(head, "^\\s*\\{\\s*\"jsonrpc\"\\s*:\\s*\"2\\.0\"\\s*,\\s*\"id\"\\s*:\\s*(\\d+)") is { Success: true } first
                ? first.Groups[1].Value
                : Regex.Match(tail.ToString().TrimEnd(), "\"id\"\\s*:\\s*(\\d+)\\s*\\}$") is { Success: true } last ? last.Groups[1].Value : null;

            return new Line(null, long.TryParse(id, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1);
        }
    }

    private const int SigTerm = 15;

    private const int SigKill = 9;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
