using System.Runtime.InteropServices;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A FAKE STDIO MCP SERVER for the servers a worker runs: a Python script behind a fake <c>npx</c> and <c>uvx</c> in a
/// folder of its own, put first on the PATH the worker gives the server. Its tools say who and where it runs
/// (<c>whoami</c>: uid, pid, working folder, HOME, its whole environment unless <c>env</c> is false, and its command line), echo, stop the
/// process without answering (<c>crash</c>), sleep, ask the client for a ping first, and write to stderr then exit.
/// <c>FAKE_START_DELAY</c> holds initialize back that many seconds, as a package's install does.
/// </summary>
public sealed class FakeStdioServer : IDisposable
{
    public const string Script = """
        import hashlib, json, os, sys, time

        def send(message):
            sys.stdout.write(json.dumps(message) + "\n")
            sys.stdout.flush()

        def text(value):
            return {"content": [{"type": "text", "text": value}]}

        TOOLS = [
            {"name": "whoami", "description": "Who and where this runs.", "inputSchema": {"type": "object"}},
            {"name": "echo", "description": "Echoes its arguments.", "inputSchema": {"type": "object"}},
            {"name": "crash", "description": "Stops without answering.", "inputSchema": {"type": "object"}},
            {"name": "sleep", "description": "Sleeps `seconds`.", "inputSchema": {"type": "object"}},
            {"name": "pingme", "description": "Pings the client first.", "inputSchema": {"type": "object"}},
        ]

        if os.environ.get("FAKE_STDERR_THEN_EXIT"):
            sys.stderr.write("failed to start with " + os.environ.get("FAKE_STDERR_THEN_EXIT") + "\n")
            sys.stderr.flush()
            sys.exit(3)

        delay = float(os.environ.get("FAKE_START_DELAY", "0"))
        for line in sys.stdin:
            message = json.loads(line)
            method = message.get("method")
            ident = message.get("id")
            if ident is None:
                continue
            if method == "initialize":
                time.sleep(delay)
                send({"jsonrpc": "2.0", "id": ident, "result": {"protocolVersion": "2025-06-18", "capabilities": {"tools": {}}, "serverInfo": {"name": "fake", "version": "1"}}})
            elif method == "tools/list":
                cursor = (message.get("params") or {}).get("cursor")
                if cursor is None:
                    send({"jsonrpc": "2.0", "id": ident, "result": {"tools": TOOLS[:3], "nextCursor": "page-2"}})
                else:
                    send({"jsonrpc": "2.0", "id": ident, "result": {"tools": TOOLS[3:]}})
            elif method == "tools/call":
                params = message.get("params") or {}
                name = params.get("name")
                args = params.get("arguments") or {}
                if name == "whoami":
                    send({"jsonrpc": "2.0", "id": ident, "result": text(json.dumps({
                        "uid": os.getuid(), "gid": os.getgid(), "groups": os.getgroups(), "pid": os.getpid(), "cwd": os.getcwd(),
                        "home": os.environ.get("HOME"), "env": dict(os.environ) if args.get("env", True) else None, "argv": sys.argv[1:],
                        "token_sha": hashlib.sha256(os.environ.get("API_TOKEN", "").encode()).hexdigest()}))})
                elif name == "echo":
                    send({"jsonrpc": "2.0", "id": ident, "result": text(json.dumps(args))})
                elif name == "crash":
                    os._exit(7)
                elif name == "sleep":
                    time.sleep(float(args.get("seconds", 1)))
                    send({"jsonrpc": "2.0", "id": ident, "result": text("slept")})
                elif name == "pingme":
                    send({"jsonrpc": "2.0", "id": "from-server", "method": "ping"})
                    answer = json.loads(sys.stdin.readline())
                    send({"jsonrpc": "2.0", "id": ident, "result": text("pinged " + json.dumps(answer))})
                else:
                    send({"jsonrpc": "2.0", "id": ident, "error": {"code": -32602, "message": "Unknown tool: " + str(name)}})
            elif method == "ping":
                send({"jsonrpc": "2.0", "id": ident, "result": {}})
            else:
                send({"jsonrpc": "2.0", "id": ident, "error": {"code": -32601, "message": "no " + str(method)}})
        """;

    /// <param name="open">Readable and runnable by every user, in /tmp: for a server that really runs as <c>mcp</c>.</param>
    public FakeStdioServer(bool open = false)
    {
        var everyone = (UnixFileMode)Convert.ToInt32("755", 8);
        Folder = open ? Path.Combine("/tmp", $"fake-stdio-mcp-{Guid.NewGuid():N}") : Directory.CreateTempSubdirectory("fake-stdio-mcp-").FullName;
        Bin = Path.Combine(Folder, "bin");
        Directory.CreateDirectory(Bin);
        var script = Path.Combine(Folder, "fake_mcp.py");
        File.WriteAllText(script, Script);
        if (open)
        {
            foreach (var path in new[] { Folder, Bin, script }) File.SetUnixFileMode(path, everyone);
        }

        // BOTH COMMANDS RUN THE FAKE, with the arguments the worker gave them, so a test sees the command line.
        foreach (var command in new[] { "npx", "uvx" })
        {
            var path = Path.Combine(Bin, command);
            File.WriteAllText(path, $"#!/bin/sh\nexec python3 -u '{script}' \"$@\"\n");
            File.SetUnixFileMode(path, open ? everyone : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Root = Path.Combine(Folder, "mcp");
    }

    /// <summary>This fake's own folder; the servers' <c>mcp</c> folder is <see cref="Root"/> inside it.</summary>
    public string Folder { get; }

    public string Bin { get; }

    public string Root { get; }

    /// <summary>The PATH a worker gives the fake: its own bin first, then the system's for python3.</summary>
    public string ServerPath => $"{Bin}:{McpStdioServers.DefaultPath}";

    /// <summary>Servers on a worker of this test's, as the suite's own user unless <paramref name="user"/> says otherwise.</summary>
    public McpStdioServers Servers(AgentLaunchUser? user = null, TimeSpan? idle = null, TimeProvider? clock = null) =>
        new(WorkerId.Local, user ?? AgentLaunchUser.Same(McpStdioServers.UserName, "the test runs servers as itself"), Root, ServerPath, idle, clock);

    /// <summary>An entry as control would send it.</summary>
    public static McpStdioLaunch Launch(
        IReadOnlyDictionary<string, string>? environment = null, string server = "mcp-fake", string name = "fake", string command = "npx",
        string fingerprint = "one", IReadOnlyList<string>? args = null) =>
        new(server, name, command, command == "npx" ? "@fake/server" : "fake-server", "1.2.3", args ?? ["--flag", "two words"],
            environment ?? new Dictionary<string, string> { ["API_TOKEN"] = "stdio-secret-9f8e7d" }, fingerprint);

    public static bool Running(int pid) => Directory.Exists($"/proc/{pid}") && !File.ReadAllText($"/proc/{pid}/stat").Contains(") Z ", StringComparison.Ordinal);

    public void Dispose()
    {
        try { Directory.Delete(Folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [DllImport("libc")]
    public static extern int kill(int pid, int signal);
}
