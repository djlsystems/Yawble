namespace Harness.Contracts;

/// <summary>
/// WHAT KIND OF FAILURE a run was, as a value on the `agentContainer.failed` payload rather than as
/// a second message type.
///
/// <para>
/// A NEW FIELD AND NEVER A NEW TYPE. `MessageText` and the SPA's `summarise.ts` both read
/// `payload.output`, and every subscription in the system matches on TYPE alone, so a second
/// terminal type would have to be learnt by every subscriber and both renderers before a single
/// reader was better off. A field is invisible to everything that does not look for it, which is
/// exactly the property the append-only log needs: rows that carry no class render as plain
/// failures.
/// </para>
///
/// <para>
/// EACH CLASS DIFFERS IN ONE RESPECT: WHAT SHOULD HAPPEN NEXT. That is the only thing a taxonomy is
/// for, and it is why these are the boundaries rather than, say, one per provider or one per exit
/// code. <see cref="ResumesAutomatically"/> is the whole of the difference, and its answer is
/// deliberate: quota and rate, and nothing else, ever.
/// </para>
///
/// <para>
/// <see cref="Unknown"/> IS THE DEFAULT AND IS TREATED EXACTLY AS <see cref="AgentFault"/>. A
/// failure nobody could classify is not a guess. This product already refuses to invent a number it
/// does not have - `(unknown)` tokens rather than zeros, a null rather than a zero on a run the
/// host was restarted out from under - and the same rule now applies to a reason. A
/// misclassification that retries is a misclassification that SPENDS.
/// </para>
/// </summary>
public static class FailureClasses
{
    /// <summary>
    /// The provider says a budget is spent, usually with a reset time. NOTHING IS WRONG WITH THE
    /// RUN: the work was sound and the provider said come back later.
    ///
    /// Resumes at the stated reset - and only at a STATED one. A quota failure whose reset time
    /// could not be read carries no <c>retryAfter</c> and gets no automatic resume, for this file's
    /// standing reason: a horizon nobody stated is a horizon this platform would be inventing.
    /// </summary>
    public const string Quota = "quota";

    /// <summary>
    /// A 429 or `RESOURCE_EXHAUSTED` - too many requests rather than a spent budget. Backs off and
    /// retries on a SHORTER horizon, and unlike <see cref="Quota"/> the horizon may be the
    /// platform's own: "back off" names a behaviour whose delay is ours to choose, where "resumes at
    /// the stated reset" names a moment only the provider knows.
    /// </summary>
    public const string Rate = "rate";

    /// <summary>
    /// Network, DNS, socket.
    ///
    /// NEVER RESUMES AUTOMATICALLY, and that is DECIDED rather than defaulted, because a network failure can leave an agent half-finished and
    /// re-running spends again to reach a state nobody has looked at.
    /// </summary>
    public const string Transport = "transport";

    /// <summary>
    /// A non-zero exit with the agent's own diagnosis, or `AgentResult.DidNothing`. NEVER retried
    /// automatically: re-running spends again to fail the same way.
    /// </summary>
    public const string AgentFault = "agent-fault";

    /// <summary>
    /// The idle clock fired. Never automatic - the setting IS the decision, and a platform that
    /// resumed past it would be overruling the number an operator typed.
    /// </summary>
    public const string Timeout = "timeout";

    /// <summary>
    /// The run was cut off rather than failing: a Host restart, or a person pressing Stop.
    ///
    /// ONE SPELLING FOR A STATE THAT ALREADY EXISTED. `ContainerHost.ResumePendingAsync` has
    /// published exactly this fact since before there were classes; this names it rather than
    /// inventing a second word for it. Its unstarted/STARTED split is a DIFFERENT mechanism and is
    /// untouched by anything here.
    /// </summary>
    public const string Interrupted = "interrupted";

    /// <summary>
    /// The agent's program was not on PATH when the run started, and still not after looking
    /// again for about 30 seconds. NOTHING RAN AND NOTHING WAS SPENT, which is what separates it
    /// from <see cref="AgentFault"/>: the shared install is replaced in place while a CLI updates,
    /// so the likeliest cause is an install or update in progress, and re-sending the instruction
    /// tries again. Not resumed by the platform: the Manager re-sends, and escalates only when it
    /// has already happened twice.
    /// </summary>
    public const string LaunchMissing = "launch-missing";

    /// <summary>
    /// The run went over its own memory limit (`runs.memoryLimitMb`, applied by
    /// `RunMemoryLimits`) and was stopped by it. NOT AN AGENT FAULT: the limit is the platform's,
    /// set so one runaway run cannot take the Host and every other team down with it. Never resumed
    /// automatically - the same work would hit the same limit - and the run's own error names the
    /// limit in force and the setting that raises it.
    /// </summary>
    public const string OutOfMemory = "out-of-memory";

    /// <summary>
    /// NOBODY COULD CLASSIFY IT - the default, and a real class rather than an absence. Treated
    /// exactly as <see cref="AgentFault"/>: never resumed.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>Every class, for a test or a renderer that wants to enumerate them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Quota, Rate, Transport, AgentFault, LaunchMissing, OutOfMemory, Timeout, Interrupted, Unknown,
    ];

    /// <summary>
    /// Whether a failure of this class may be resumed by the platform WITHOUT a person.
    ///
    /// THE RESUME POLICY, IN ONE EXPRESSION, and the only place it is written down in code. Quota and rate resume; transport, timeout, agent-fault and unknown
    /// never do. An unrecognised value - including a class written by a newer build and read back by
    /// an older one - answers false, which is the safe direction: the cost of not resuming is a
    /// person pressing Nudge, and the cost of resuming wrongly is money.
    /// </summary>
    public static bool ResumesAutomatically(string? failureClass) =>
        failureClass is Quota or Rate;

    /// <summary>
    /// Whether this is a class this build knows about. An unknown SPELLING is not the same thing as
    /// the <see cref="Unknown"/> CLASS - the log is append-only and a row from a newer build may
    /// name a class this one has never heard of - so renderers ask this before putting a sentence
    /// to a value.
    /// </summary>
    public static bool IsKnown(string? failureClass) =>
        failureClass is not null && All.Contains(failureClass, StringComparer.Ordinal);

    /// <summary>
    /// The class in words a person reads, with no trailing space and no subject - the caller
    /// supplies both. Null for a row that carries no class and for a spelling this build does not know, so a renderer falls through to exactly what it
    /// printed before.
    ///
    /// ONE STORE FOR THE C# SIDE, the move `PayloadFields` makes one level in. The SPA spells its
    /// own copy in `web/src/lib/summarise.ts` for the reason recorded there: there is no server in
    /// the web test run, so no test could compare them, and a renderer fetching `/api/events` to
    /// learn a sentence buys a runtime dependency for a wording problem.
    /// </summary>
    public static string? Sentence(string? failureClass) => failureClass switch
    {
        Quota => "The provider says a budget is spent, so nothing is wrong with this run.",
        Rate => "The provider refused for sending too much too fast.",
        Transport => "The network failed, so how far this run got is not known.",
        AgentFault => "The agent itself failed, so re-running it would spend again to fail the same way.",
        LaunchMissing => "The program was not found when the run started. It may be being installed "
            + "or updated, so nothing ran and nothing was spent; re-sending the instruction will try again.",
        OutOfMemory => "The run used more memory than its limit, the setting runs.memoryLimitMb, so it was "
            + "stopped. This is not an agent fault; the run's own error says what the limit was.",
        Timeout => "The idle clock fired.",
        Interrupted => "The run was cut off before it finished.",
        Unknown => "Nothing here could say why, so this is treated exactly as an agent fault and "
            + "will not be resumed automatically.",
        _ => null,
    };
}
