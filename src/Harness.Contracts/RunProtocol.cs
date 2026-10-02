using System.Text.Json.Serialization;
using Harness.Host;

namespace Harness.Contracts;

// THE RUN PROTOCOL: everything control and a runtime worker say to each other about runs.
//
// Plain, immutable records with no delegates, tokens or Host types, so the same messages that
// cross the in-process transport today can travel over a connection later. Control resolves
// everything a run needs before it is started (StartRun); the worker launches, measures and
// reports, and holds nothing between runs.
//
// The two exceptions are a run's secrets on StartRun - its credential and its redaction set - which
// refuse to be written as JSON at all: they cross the in-process transport by reference, and a
// connection that carries them needs its own protected channel.

/// <summary>A runtime worker's name. Today there is one, in the Host's own process.</summary>
public sealed record WorkerId(string Value)
{
    public static WorkerId Local { get; } = new("local");

    public override string ToString() => Value;
}

/// <summary>
/// One run of one member. <see cref="Key"/> is the member's qualified name, the same string a
/// lease owner and a run's memory allowance are keyed by; <see cref="Nonce"/> tells two runs of one
/// member apart.
/// </summary>
public sealed record RunId(ContainerId Member, string Nonce)
{
    [JsonIgnore]
    public string Key => Member.ToString();

    public static RunId For(ContainerId member) => new(member, Guid.NewGuid().ToString("N"));
}

// ---------------------------------------------------------------------------------------------
// Control to worker.
// ---------------------------------------------------------------------------------------------

/// <summary>What control sends a worker. A send completes once the worker has applied it.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "message")]
[JsonDerivedType(typeof(StartRun), "startRun")]
[JsonDerivedType(typeof(CancelRun), "cancelRun")]
[JsonDerivedType(typeof(ChangeRunMemoryAllowance), "changeRunMemoryAllowance")]
[JsonDerivedType(typeof(HoldIdleClock), "holdIdleClock")]
[JsonDerivedType(typeof(TouchIdleClock), "touchIdleClock")]
[JsonDerivedType(typeof(SampleCapacity), "sampleCapacity")]
public abstract record ControlMessage;

/// <summary>A command about one run.</summary>
public abstract record RunCommand(RunId Run) : ControlMessage;

/// <summary>A command about whatever run a member has open. A member runs one at a time.</summary>
public abstract record MemberCommand(ContainerId Member) : ControlMessage;

/// <summary>
/// Start a run with everything control resolved for it. <see cref="Agent"/> through
/// <see cref="UnreachableRoot"/> are the invocation's own fields; <see cref="Launch"/> is null when
/// the catalog has no headless preset by that name, and the worker refuses it in today's words.
/// <see cref="Process"/> is set instead for a member that is a program rather than an agent (a
/// plugin): the worker starts it as given and streams what it prints.
///
/// <see cref="Credential"/> is the run's credential as control resolved it - the worker applies it
/// and never reads a store or a key ring - and <see cref="Redaction"/> the values the run's text is
/// redacted of before it leaves the worker: the credential's and every credential variable the
/// child's environment carries. Both hold secrets and refuse to be written as JSON; null on a run
/// that has none. <see cref="CredentialNames"/> names every variable that holds a credential - names
/// only, no values - so the worker redacts the values its own child's environment carries too.
/// </summary>
public sealed record StartRun(
    RunId Run,
    string Agent,
    string SystemPrompt,
    string Prompt,
    string Context,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    string? UnreachableRoot,
    RunLaunch? Launch,
    RunMemoryAllowance? Memory,
    string? TempRoot,
    RunLiveView? LiveView,
    RunProcess? Process = null,
    RunCredential? Credential = null,
    ValueRedactor? Redaction = null,
    IReadOnlyList<string>? CredentialNames = null) : RunCommand(Run);

/// <summary>
/// A member that is a program: its executable and arguments, the only variables it inherits from the
/// worker (<see cref="Inherited"/>, by name) and the ones control sets, the request it reads on
/// stdin, how long it may go without progress, and the folder its user is given read access to.
/// Each line it prints crosses as a <see cref="RunOutput"/>, in order, before the next is read.
/// </summary>
public sealed record RunProcess(
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> Inherited,
    IReadOnlyDictionary<string, string> Environment,
    string Stdin,
    int? TimeoutSeconds,
    string? SharedFolder);

/// <summary>
/// The preset's launch as control resolved it: the command, its tokens, its isolation and the
/// CLI's update-off, how long it may go without progress (null: no limit), and the variables the
/// child must not inherit from the worker - the ones that must be absent, and every provider key
/// that is not this command's own and was not handed in.
/// </summary>
public sealed record RunLaunch(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string>? SystemPromptArguments,
    string? InstructionsFile,
    string? UsageFormat,
    bool LanguageModel,
    IReadOnlyDictionary<string, string>? IsolationEnvironment,
    IReadOnlyDictionary<string, string>? UpdateEnvironment,
    int? TimeoutSeconds,
    IReadOnlyList<string> RemovedEnvironment);

/// <summary>
/// The preset's live view: the transcript path when it is known before launch, its format, and how
/// to find it after launch otherwise. Null on <see cref="StartRun"/> when the preset has none.
/// </summary>
public sealed record RunLiveView(string? Path, string Format, AgentLiveViewFind? Find);

/// <summary>
/// The run's memory figures from the settings, read when the start was built: the normal limit and
/// the ceiling a heavy-lease holder may be raised to. The worker applies its own mechanism to them.
/// </summary>
public sealed record RunMemoryAllowance(MemoryFigure Limit, MemoryFigure Ceiling);

/// <summary>One memory figure in megabytes (null: none), where it came from, and whether a person set it.</summary>
public sealed record MemoryFigure(long? Mb, string Source, bool Set);

/// <summary>
/// The run's caller gave up on it: a person's Stop or the Host stopping. The runner cannot tell
/// the two apart, and the member runtime supplies the words, so this carries no reason.
/// </summary>
public sealed record CancelRun(RunId Run) : RunCommand(Run);

/// <summary>
/// Who holds the <c>heavy</c> lease now, by owner key. The worker raises the memory allowance of
/// each of its runs on the list and lowers the rest. Sent on every lease move.
/// </summary>
public sealed record ChangeRunMemoryAllowance(IReadOnlyList<string> HeavyHolders) : ControlMessage;

/// <summary>Pause (<see cref="Held"/>) or resume a member's idle clock while it waits in a lease's queue.</summary>
public sealed record HoldIdleClock(ContainerId Member, bool Held) : MemberCommand(Member);

/// <summary>A member reported: push its run's idle clock out again.</summary>
public sealed record TouchIdleClock(ContainerId Member) : MemberCommand(Member);

/// <summary>Measure now: each open run's process group, then the worker's own cgroup.</summary>
public sealed record SampleCapacity : ControlMessage;

// ---------------------------------------------------------------------------------------------
// Worker to control.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// One event from one worker. <see cref="Seq"/> rises by one per event, and events are delivered
/// in <see cref="Seq"/> order: lost-run detection depends on it.
/// </summary>
public sealed record WorkerEnvelope(WorkerId Worker, long Seq, WorkerEvent Event);

/// <summary>What a worker says.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "event")]
[JsonDerivedType(typeof(WorkerReady), "workerReady")]
[JsonDerivedType(typeof(RunCredentialApplied), "runCredentialApplied")]
[JsonDerivedType(typeof(RunStarted), "runStarted")]
[JsonDerivedType(typeof(RunProgress), "runProgress")]
[JsonDerivedType(typeof(RunOutput), "runOutput")]
[JsonDerivedType(typeof(RunUsage), "runUsage")]
[JsonDerivedType(typeof(RunMeasured), "runMeasured")]
[JsonDerivedType(typeof(RunChildStoppedByMemoryLimit), "runChildStoppedByMemoryLimit")]
[JsonDerivedType(typeof(RunLiveViewChanged), "runLiveViewChanged")]
[JsonDerivedType(typeof(RunDiagnostic), "runDiagnostic")]
[JsonDerivedType(typeof(RunEnded), "runEnded")]
[JsonDerivedType(typeof(WorkerCapacitySampled), "workerCapacitySampled")]
public abstract record WorkerEvent;

/// <summary>An event about one run.</summary>
public abstract record RunEvent(RunId Run) : WorkerEvent;

/// <summary>The worker is connected and takes runs.</summary>
public sealed record WorkerReady(WorkerId Worker) : WorkerEvent;

/// <summary>
/// The run's credential is applied and its child's environment is final, just before the child is
/// started: from here control keeps the run's <see cref="StartRun.Redaction"/>, together with
/// <see cref="Redaction"/> - the set the worker read from the child's environment it built - as the
/// member's, for its reports and its transcript. A run refused before this point leaves the member's
/// set as it was. <see cref="Redaction"/> holds secrets and refuses to be written as JSON.
/// </summary>
public sealed record RunCredentialApplied(RunId Run, ValueRedactor? Redaction = null) : RunEvent(Run);

/// <summary>The run's process exists.</summary>

public sealed record RunStarted(RunId Run, int ProcessId, DateTimeOffset At) : RunEvent(Run);

/// <summary>A sentence the run's launch files on the member's card: a hold, a wait, a memory watch.</summary>
public sealed record RunProgress(RunId Run, string Sentence) : RunEvent(Run);

/// <summary>What the run printed, as the member's output. Appended in order.</summary>
public sealed record RunOutput(RunId Run, string Text) : RunEvent(Run);

/// <summary>The tokens the run reported spending.</summary>
public sealed record RunUsage(RunId Run, UsageFigures Usage) : RunEvent(Run);

/// <summary>
/// <see cref="InvocationUsage"/>, field for field, in a shape that travels: <see cref="Total"/> is
/// set only for a brand that reports one combined figure.
/// </summary>
public sealed record UsageFigures(
    int? TokensIn, int? TokensOut, int? Total, string Source, int? CachedIn, int? Reasoning, int? CacheCreation)
{
    public static UsageFigures From(InvocationUsage usage) =>
        new(usage.TokensIn, usage.TokensOut, usage.Total, usage.Source, usage.CachedIn, usage.Reasoning, usage.CacheCreation);

    public InvocationUsage ToUsage() =>
        Source == UsageSource.NoModel && TokensIn is null && TokensOut is null && Total is null
            ? InvocationUsage.NoModel
            : Total is { } total
                ? InvocationUsage.Combined(total, Source)
                : new InvocationUsage(TokensIn ?? 0, TokensOut ?? 0, Source, CachedIn, Reasoning, CacheCreation);
}

/// <summary>
/// One process group of a run as <c>/proc</c> read it: processes, resident memory, CPU ticks. A
/// group the worker measures that no open run of its owns carries an empty nonce.
/// </summary>
public sealed record RunMeasured(RunId Run, int Group, int Processes, long ResidentBytes, long CpuTicks, DateTimeOffset At)
    : RunEvent(Run);

/// <summary>A process of the run was stopped by its memory limit while the agent carried on.</summary>
public sealed record RunChildStoppedByMemoryLimit(RunId Run, string Sentence, MemoryFigure Limit) : RunEvent(Run);

/// <summary>
/// The run's live view: its transcript (null while it is looked for or when there is none), its
/// format, why there is none, and whether it is still being looked for.
/// </summary>
public sealed record RunLiveViewChanged(RunId Run, string? Transcript, string? Format, string Reason, bool Finding)
    : RunEvent(Run);

/// <summary>A diagnostics row about the run. Control writes it; the worker opens no store.</summary>
public sealed record RunDiagnostic(
    RunId Run,
    DiagnosticSeverity Severity,
    string Kind,
    string Source,
    string? Message,
    string? Detail,
    string? ExceptionType) : RunEvent(Run);

/// <summary>
/// The run is over. Everything an <see cref="AgentResult"/> says except its output
/// (<see cref="RunOutput"/>), its usage (<see cref="RunUsage"/>) and whether it reached the
/// platform, which control measures. <see cref="StderrTail"/> is the redacted end of what it wrote
/// on stderr. <see cref="Fault"/> is set when the launch threw instead of answering.
/// </summary>
public sealed record RunEnded(
    RunId Run,
    int ExitCode,
    int? ProcessId,
    string? LaunchError,
    string? FailureClass,
    DateTimeOffset? RetryAfter,
    string? StderrTail,
    AgentTranscript? Transcript,
    RunFault? Fault = null,
    RunProcessOutcome? Process = null) : RunEvent(Run);

/// <summary>
/// How a <see cref="RunProcess"/> came out, for control to say in its own words: refused before it
/// started (<see cref="Refused"/>: <c>setsid</c> was not in a system directory), failed to start
/// (<see cref="StartError"/>), stopped (<see cref="Killed"/>, by its idle clock when
/// <see cref="Expired"/>), still holding its output after the drain, and everything it wrote on
/// stderr, unredacted: control holds the secrets to redact.
/// </summary>
public sealed record RunProcessOutcome(
    string? Refused, string? StartError, bool Killed, bool Expired, bool HeldOpen, string Stderr);

/// <summary>
/// A launch that threw rather than answered: cancelled, or an exception with its type and message.
/// Control throws it again on its side, so the caller sees what it saw before.
/// </summary>
public sealed record RunFault(bool Canceled, string Type, string Message);

/// <summary>The worker's own capacity sample, and the runs it has open at that moment.</summary>
public sealed record WorkerCapacitySampled(DateTimeOffset At, CapacityFigures Figures, IReadOnlyList<RunId> OpenRuns)
    : WorkerEvent;

/// <summary>What the worker's cgroup said, field for field the worker's own reading.</summary>
public sealed record CapacityFigures(
    string? Version,
    double? CpuLimit,
    bool CpuUnlimited,
    long? CpuUsageUsec,
    long? CpuThrottledPeriods,
    long? CpuThrottledUsec,
    long? MemoryLimitBytes,
    bool MemoryUnlimited,
    long? MemoryCurrentBytes,
    long? MemoryAnonBytes,
    long? MemoryFileBytes,
    long? MemoryShmemBytes,
    PressureReading? CpuPressure,
    PressureReading? MemoryPressure,
    long? PidsCurrent,
    long? PidsLimit,
    bool PidsUnlimited,
    IReadOnlyList<string> NotMeasured);

/// <summary>One pressure file: its <c>some</c> line and its <c>full</c> line where it has one.</summary>
public sealed record PressureReading(PressureLines Some, PressureLines? Full);

/// <summary>One pressure line: stall shares over 10, 60 and 300 seconds, and the total stall in µs.</summary>
public sealed record PressureLines(double Avg10, double Avg60, double Avg300, long TotalUsec);

// ---------------------------------------------------------------------------------------------
// The transport.
// ---------------------------------------------------------------------------------------------

/// <summary>Control's handle on one worker.</summary>
public interface IRunWorker
{
    WorkerId Id { get; }

    /// <summary>Completes when the worker has applied <paramref name="message"/>.</summary>
    Task SendAsync(ControlMessage message, CancellationToken ct = default);

    /// <summary>Completes when the connection to the worker is gone.</summary>
    Task Closed { get; }
}

/// <summary>
/// A worker reached over a connection that can drop and come back. While <see cref="Dropped"/>, its
/// runs are neither lost nor placeable: the connection's grace decides. When the worker does not come
/// back, <see cref="IRunWorker.Closed"/> completes with <see cref="Lost"/> saying why, and every run
/// still open on it fails <c>worker-lost</c>. A worker in the Host's own process is not one of these:
/// its connection only closes with the Host.
/// </summary>
public interface IRunWorkerConnection
{
    /// <summary>The connection is down and may still come back within its grace.</summary>
    bool Dropped { get; }

    /// <summary>Once <see cref="IRunWorker.Closed"/> has completed: why the worker is lost, as a sentence; null when it was not.</summary>
    string? Lost { get; }
}

/// <summary>A worker's handle on control: where its events go, in <see cref="WorkerEnvelope.Seq"/> order.</summary>
public interface IRunEvents
{
    Task PublishAsync(WorkerEnvelope envelope, CancellationToken ct = default);
}

/// <summary>A control part that reaches runs through one worker, and which one.</summary>
public interface IRunWorkerClient
{
    IRunWorker Worker { get; }
}

/// <summary>
/// An <see cref="AgentResult"/> as the events that carry it across, and back. Whether the run
/// reached the platform is not carried: control measures it.
/// </summary>
public static class RunResults
{
    public static (RunOutput Output, RunUsage? Usage, RunEnded Ended) Events(
        RunId run, AgentResult result, string? stderrTail = null) =>
        (new RunOutput(run, result.Output),
            result.Usage is { } usage ? new RunUsage(run, UsageFigures.From(usage)) : null,
            new RunEnded(
                run,
                result.ExitCode,
                result.ProcessId,
                result.LaunchError,
                result.FailureClass,
                result.RetryAfter,
                stderrTail,
                result.AgentTranscript));

    public static AgentResult Result(string output, UsageFigures? usage, RunEnded ended) =>
        new(
            ended.ExitCode,
            output,
            ended.LaunchError,
            usage?.ToUsage(),
            ended.ProcessId,
            FailureClass: ended.FailureClass,
            RetryAfter: ended.RetryAfter,
            AgentTranscript: ended.Transcript);
}
