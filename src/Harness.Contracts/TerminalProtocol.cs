using Harness.Host;

namespace Harness.Contracts;

// TERMINALS AND READS ON A WORKER: a person's Concierge terminal, a running member's live view, and a
// finished run's transcript. Control keeps the person's side - the browser's socket, the session, its
// replay, its credential and its lease - and asks a worker to start the terminal and to read the
// agent's files as the agent. What comes back is a stream (StreamChunk), ended by a sequenced event
// where the end must not be lost.

/// <summary>A message about a worker's terminals or the agent's files. A worker applies each without holding its queue.</summary>
public abstract record TerminalCommand : ControlMessage;

/// <summary>
/// Everything a person's terminal needs that control resolves: the CLI's arguments as the catalog
/// gives them (the MCP config tokens and the system-prompt file not yet applied), where it starts,
/// its environment and the inherited variables it clears, the system prompt a CLI reads from a file
/// (with the arguments that name that file), and where its MCP config points. The worker writes the
/// MCP config and the prompt file, wraps the arguments to run as the agent, and spawns it.
/// <see cref="Environment"/> holds the session's credential and an issued one, and crosses a
/// connection only sealed.
/// </summary>
public sealed record TerminalLaunch(
    IReadOnlyList<string> Argv,
    string StartingFolder,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> ClearEnvironment,
    string? SystemPrompt,
    IReadOnlyList<string>? SystemPromptArguments,
    TerminalMcp? Mcp);

/// <summary>The platform address a terminal's MCP config points at, and whose it is: the worker writes the config with the session's key variable.</summary>
public sealed record TerminalMcp(string BaseUrl, string Owner);

/// <summary>Start a terminal under <see cref="Session"/>, at <see cref="Cols"/> by <see cref="Rows"/>. Its output is the stream of that id.</summary>
public sealed record StartTerminal(string Session, TerminalLaunch Launch, int Cols, int Rows) : TerminalCommand;

/// <summary>The person's terminal is now <see cref="Cols"/> by <see cref="Rows"/>.</summary>
public sealed record ResizeTerminal(string Session, int Cols, int Rows) : TerminalCommand;

/// <summary>End the terminal: stop its CLI and remove what was written for it. <see cref="TerminalEnded"/> follows.</summary>
public sealed record StopTerminal(string Session) : TerminalCommand;

/// <summary>
/// The terminal is over, after its last output: its CLI exited, or it was stopped. Sent once per
/// terminal. <see cref="ExitCode"/> is the CLI's, or -1 when it was stopped before it said.
/// </summary>
public sealed record TerminalEnded(string Session, int ExitCode) : WorkerEvent;

/// <summary>
/// Follow <see cref="Run"/>'s transcript at <see cref="Path"/> as the agent, from its first line, each
/// line one chunk of <see cref="Stream"/>, until the run ends on this worker or control stops it.
/// </summary>
public sealed record FollowTranscript(string Stream, RunId Run, string Path, string Format) : TerminalCommand;

/// <summary>
/// Stop following a transcript: the watcher left, and its tail stops now. With <see cref="RunEnded"/>,
/// control saw the run end: what the agent wrote last is read, then the stream ends.
/// </summary>
public sealed record StopStream(string Stream, bool RunEnded = false) : TerminalCommand;

/// <summary>
/// Read the agent's file at <see cref="Path"/> whole, as the agent, and send its text as the stream
/// <c>read:</c><see cref="Request"/>. When <see cref="Redact"/>, the text is redacted first of
/// <see cref="Redaction"/> and of whatever this worker's own environment holds under
/// <see cref="CredentialNames"/>. <see cref="Redaction"/> holds secrets and crosses only sealed. With
/// <see cref="AsTranscript"/>, a worker that could not follow a transcript as the agent refuses the
/// read as the live view would. <see cref="AgentFileRead"/> answers under the same request.
/// </summary>
public sealed record ReadAgentFile(
    string Request,
    string Path,
    IReadOnlyList<string> CredentialNames,
    bool Redact,
    ValueRedactor? Redaction = null,
    bool AsTranscript = false) : TerminalCommand
{
    /// <summary>The stream a read's text comes on.</summary>
    public static string StreamOf(string request) => "read:" + request;
}

/// <summary>
/// What reading a file found: <see cref="Ok"/>, <see cref="Gone"/>, <see cref="Unreadable"/> or
/// <see cref="Refused"/> (this worker cannot read it as the agent at all), with <see cref="Detail"/>
/// saying why, and how many chunks the text was sent in, so control can tell a read that lost one.
/// </summary>
public sealed record AgentFileRead(string Request, string Result, string? Detail, long Chunks) : WorkerEvent
{
    public const string Ok = "ok";

    public const string Gone = "gone";

    public const string Unreadable = "unreadable";

    public const string Refused = "refused";
}
