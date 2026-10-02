namespace Harness.Contracts;

// ---------------------------------------------------------------------------------------------
// AN AGENT CLI ASKED ON A WORKER: whether it is signed in, what it prints for a command, and what
// the agent owns removed as the agent. Each is a request control sends one worker and that worker
// answers with a sequenced event under the same Request; it is applied on its own task, so the
// worker's ordered command queue is never held by a CLI. A request is never kept for a dropped
// worker: its caller answers "not measured" at once.
// ---------------------------------------------------------------------------------------------

/// <summary>A request about an agent CLI, answered by an event that carries the same <see cref="Request"/>.</summary>
public abstract record AgentCliCommand(string Request) : ControlMessage;

/// <summary>
/// One command's sign-in probe, as <c>auth-probes.json</c> declares it, with the update-off every
/// launch of the command carries.
/// </summary>
public sealed record SignInProbeSpec(
    string Command,
    string? CredentialVariable,
    IReadOnlyList<string>? CredentialFiles,
    IReadOnlyList<string>? StatusArguments,
    IReadOnlyDictionary<string, string>? UpdateEnvironment,
    IReadOnlyList<string>? UpdateArguments);

/// <summary>
/// One command's answer. <see cref="Authenticated"/> null is not measured; <see cref="Detail"/> says
/// how it was decided, in the worker's words.
/// </summary>
public sealed record SignInProbeResult(string Command, bool Installed, bool? Authenticated, string Detail);

/// <summary>Ask each command whether it is signed in on this worker, as the agent its children run as.</summary>
public sealed record ProbeSignIn(string Request, IReadOnlyList<SignInProbeSpec> Commands) : AgentCliCommand(Request);

/// <summary>
/// One CLI run on a worker, as the agent: the program and its arguments, what is set on the worker's
/// environment (<see cref="Environment"/>, then the request's credential) and taken out of it after
/// (<see cref="RemovedEnvironment"/>: every provider key that is not this CLI's own), how long it may take, and whether it runs from a scratch folder
/// of its own (a listing, so no repository's configuration is read). <see cref="HomesIn"/> set: the
/// run gets a home of its own made in that folder (and its cache beside it), removed after, as an
/// issued member run's. <see cref="WithUpdatesOn"/>: a platform update, which must not carry the
/// CLI's update-off; every other run's arguments already carry it.
/// </summary>
public sealed record AgentCliRun(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> RemovedEnvironment,
    int TimeoutSeconds,
    bool Scratch = false,
    string? HomesIn = null,
    bool WithUpdatesOn = false);

/// <summary>
/// What one run printed, each stream cut at <see cref="AgentCliRuns.MaxOutputBytes"/> with
/// <see cref="Truncated"/> saying so. <see cref="Installed"/> false: the program is not on the
/// worker's PATH and nothing ran. <see cref="Error"/>: it could not be run, in a sentence.
/// </summary>
public sealed record AgentCliRunResult(
    bool Installed, int? ExitCode, bool TimedOut, string Stdout, string Stderr, bool Truncated, string? Error);

/// <summary>
/// Run each command in order on this worker. <see cref="Credential"/> is a member run's credential,
/// applied to every one of them as the run applies it; it holds a secret and is sealed on a connection.
/// </summary>
public sealed record RunAgentCommands(string Request, IReadOnlyList<AgentCliRun> Commands, RunCredential? Credential = null)
    : AgentCliCommand(Request);

/// <summary>
/// Remove each of <see cref="Paths"/> as the agent (<c>rm -rf --one-file-system</c>). Each must lie
/// inside <see cref="Boundary"/> with no symbolic link on the way; one that does not is refused,
/// never removed.
/// </summary>
public sealed record RemoveAsAgent(string Request, string Boundary, IReadOnlyList<string> Paths) : AgentCliCommand(Request);

/// <summary>The answer to <see cref="ProbeSignIn"/>, one result per command asked, in order.</summary>
public sealed record SignInProbed(string Request, IReadOnlyList<SignInProbeResult> Results) : WorkerEvent;

/// <summary>The answer to <see cref="RunAgentCommands"/>, one result per command, in order.</summary>
public sealed record AgentCommandsRan(string Request, IReadOnlyList<AgentCliRunResult> Results) : WorkerEvent;

/// <summary>The answer to <see cref="RemoveAsAgent"/>: done, or <see cref="Error"/> saying what was refused or failed.</summary>
public sealed record RemovedAsAgent(string Request, string? Error) : WorkerEvent;

/// <summary>Bounds every agent CLI request shares.</summary>
public static class AgentCliRuns
{
    /// <summary>The most of each of a run's streams that crosses back.</summary>
    public const int MaxOutputBytes = 1024 * 1024;
}
