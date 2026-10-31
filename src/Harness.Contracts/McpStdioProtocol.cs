using System.Text;
using System.Text.Json.Nodes;

namespace Harness.Contracts;

// ---------------------------------------------------------------------------------------------
// AN MCP SERVER THE PLATFORM RUNS, ON A WORKER (MCP 3). Control sends the worker the entry as it is
// started - the command, the pinned package, its fixed arguments and the environment its connections
// fill - and one request: list its tools, call one, or check it (start a process of its own, list,
// stop). The worker keeps one process per server, started as the `mcp` user on the first request,
// and answers under the same request id. The environment holds connection values: it crosses only
// sealed, as a terminal's environment does.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// An entry as a worker starts it. <see cref="Server"/> is the entry's id and <see cref="Name"/> its name;
/// <see cref="Environment"/> is every variable the entry names, filled from its connections, and nothing else.
/// <see cref="Fingerprint"/> changes whenever any of it does (a value included), so a worker running an
/// older start stops it and starts this one. Never printed with its values.
/// </summary>
public sealed record McpStdioLaunch(
    string Server,
    string Name,
    string Command,
    string Package,
    string Version,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Environment,
    string Fingerprint)
{
    /// <summary>The command line after <see cref="Command"/>: <c>npx -y pkg@1.2.3 …</c> or <c>uvx pkg==1.2.3 …</c>.</summary>
    public IReadOnlyList<string> CommandLine => Command == "uvx"
        ? [Command, $"{Package}=={Version}", .. Args]
        : [Command, "-y", $"{Package}@{Version}", .. Args];

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Server = {Server}, Name = {Name}, Command = {Command}, Package = {Package}, Version = {Version}, ");
        builder.Append($"Environment = [{string.Join(", ", Environment.Keys)}]");
        return true;
    }
}

/// <summary>
/// One request about a server the platform runs: <see cref="List"/> its tools (all pages), <see cref="Call"/>
/// <see cref="Tool"/> with <see cref="Arguments"/>, or <see cref="Check"/> it in a process of its own that is
/// stopped after. <see cref="CallSeconds"/> counts from the moment the process answered initialize;
/// <see cref="StartSeconds"/> is how long a start (the package's install among it) may take before that.
/// Answered by <see cref="McpStdioAnswered"/>.
/// </summary>
public sealed record McpStdioRequest(
    string Request,
    McpStdioLaunch Launch,
    string Method,
    string? Tool = null,
    JsonObject? Arguments = null,
    int CallSeconds = 120,
    int StartSeconds = McpStdioRequest.DefaultStartSeconds) : AgentCliCommand(Request)
{
    public const string List = "list";

    public const string Call = "call";

    public const string Check = "check";

    /// <summary>The first start installs the package: a minute is ordinary, five is the bound.</summary>
    public const int DefaultStartSeconds = 300;
}

/// <summary>Stop the process of <see cref="Server"/> on this worker, if one runs: its entry was edited or removed,
/// or a connection it reads changed. Answered by an empty <see cref="McpStdioAnswered"/>.</summary>
public sealed record McpStdioStop(string Request, string Server) : AgentCliCommand(Request);

/// <summary>
/// What a request came to: the server's <see cref="Result"/> (for a list, <c>{ tools: [...] }</c> across every
/// page), its JSON-RPC <see cref="Error"/> message, no answer within the call's bound (<see cref="NoAnswer"/>), or
/// a <see cref="Failure"/> sentence that already names the server. A stop answers all four empty.
/// </summary>
public sealed record McpStdioAnswered(string Request, JsonObject? Result, string? Error, bool NoAnswer, string? Failure) : WorkerEvent;
