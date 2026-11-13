namespace Yawble.Core.Bus;

/// <summary>
/// A message on the log, as stored. Seq, CorrelationId and Depth are assigned
/// by the log from CausationSeq; a publisher cannot supply them, so it cannot forge a causation chain or reset
/// the depth that bounds a runaway.
/// </summary>
public sealed record Message(
    long Seq,
    string Type,
    string Payload,
    string Source,
    long CorrelationId,
    long? CausationSeq,
    int Depth,
    DateTimeOffset OccurredAt);

/// <summary>A message on the way in. Null causation roots a new workflow (only the person does that).</summary>
/// <summary>How a message came in and who is behind it, and who started its chain (0015_provenance.sql).</summary>
public sealed record MessageProvenance(string? Via, string? User, long? OriginSeq, string? OriginVia, string? OriginUser);

public sealed record NewMessage(string Type, string Payload, string Source, long? CausationSeq = null);

/// <summary>The type strings of the bus. Addressing is by type: agent.instruction.&lt;team&gt;/&lt;member&gt;.</summary>
public static class MessageTypes
{
    public const string InstructionPrefix = "agent.instruction.";
    public const string Completed = "agent.completed";
    public const string Failed = "agent.failed";
    public const string Handback = "agent.handback";
    public const string Progress = "agent.progress";
    public const string Blocked = "agent.blocked";
    public const string NeedsDecision = "agent.needsDecision";
    public const string WorkflowCompleted = "workflow.completed";

    /// <summary>A person closed the workflow: it ends without being declared complete.</summary>
    public const string WorkflowClosed = "workflow.closed";

    public static string Instruction(MemberRef member) => InstructionPrefix + member.Qualified;

    /// <summary>
    /// What a Manager is woken by besides its own instructions.
    /// These are global types; the pump scopes them to the publisher's team and never wakes a member on its own row.
    /// </summary>
    public static readonly IReadOnlyList<string> ManagerWatches = [Completed, Failed, Handback];

    /// <summary>The person's source string. Anything else that publishes is a member, "&lt;team&gt;/&lt;name&gt;".</summary>
    public const string PersonSource = "person";
    public const string HostSource = "host";
}

/// <summary>Why a run ended without completing; carried in an agent.failed payload.</summary>
public static class FailureClasses
{
    public const string Failed = "failed";
    public const string Interrupted = "interrupted";

    // From the launcher: the card shows these.
    public const string Timeout = "timeout";
    public const string Idle = "idle";
    public const string Stopped = "stopped";
    public const string Credential = "credential";
    public const string StartFailed = "start_failed";

    /// <summary>The model API's usage or rate limit stopped the run; the failed row's run.retryAfter says when to come back.</summary>
    public const string Rate = "rate";
}

public enum MemberRole { Manager, Member }

/// <summary>A member's identity: the team and name its subscriber string and instruction type are built from.</summary>
public sealed record MemberRef(long Id, long TeamId, string Team, string Name, MemberRole Role)
{
    public string Qualified => $"{Team}/{Name}";
}

/// <summary>A tool call or append the bus refuses, with a stable code and a sentence for the agent.</summary>
public sealed class BusRefusal(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
