namespace Harness.Contracts;

/// <summary>
/// How loud one diagnostic row is. THREE VALUES AND NO MORE, because a severity scale nobody can
/// hold in their head is one every writer picks from at random - and this store is read by
/// filtering on exactly this column.
///
/// Stored as the lowercase name rather than the number: the rows outlive the enum, the same way
/// <see cref="TenantActions"/>' verbs outlive the code that wrote them, and a column of integers
/// is unreadable the day somebody renumbers.
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>Something that went wrong. An exception, a 500, a failed migration step, a spawn
    /// that did not happen.</summary>
    Error,

    /// <summary>Something that may be going wrong. A refusal that is ordinary once and a symptom
    /// repeated - the 409 from a busy guard is the case this exists for.</summary>
    Warning,

    /// <summary>A fact, not a fault. The startup and lifecycle lines: what this instance was
    /// configured as.</summary>
    Info,
}

/// <summary>
/// One diagnostic row.
///
/// <para>
/// FOUR COLUMNS ARE FIRST-CLASS RATHER THAN FIELDS INSIDE <paramref name="Detail"/> -
/// <paramref name="Kind"/>, <paramref name="Severity"/>, <paramref name="Route"/> and
/// <paramref name="ExceptionType"/> - because they are what a reader FILTERS on, and a filter that
/// has to parse JSON is one no screen will offer. Everything else that varies by kind goes in
/// <paramref name="Detail"/>, which is free-form and searched rather than filtered.
/// </para>
///
/// <para>
/// DENORMALISED, NO FOREIGN KEYS, for the reason <see cref="TenantEvent"/> has none: this log is
/// read when something is already gone. A row naming a team deleted an hour later has to still name
/// it.
/// </para>
///
/// <para>
/// NO CORRELATION AND NO CAUSATION, and that absence is the design rather than an omission. See
/// <see cref="IDiagnosticsLog"/>.
/// </para>
/// </summary>
/// <param name="Route">The route TEMPLATE (`/api/teams/{team}/members`), never the request's raw
/// URL. A template carries no query string and no path value, which is where a credential pasted
/// into a URL would be - and it is also the only spelling two requests to the same endpoint
/// share, so it is the only one worth grouping by.</param>
/// <param name="Status">The HTTP status this request answered, or null for a row that is not about
/// a request at all.</param>
/// <param name="Message">One sentence, REDACTED AT THE WRITE. An exception's message is the single
/// most likely place in this product for a credential to appear.</param>
/// <param name="Detail">Whatever else the kind carries, as JSON, redacted at the write.</param>
public sealed record DiagnosticEvent(
    long Seq,
    DateTimeOffset OccurredAt,
    DiagnosticSeverity Severity,
    string Kind,
    string? Source,
    string? Route,
    int? Status,
    string? ExceptionType,
    string? Message,
    string? Detail);

/// <summary>One page of the diagnostics log, and how many rows MATCH THE FILTER - not how many
/// rows there are. A grid paging a filtered read needs the filtered count.</summary>
public sealed record DiagnosticsPage(IReadOnlyList<DiagnosticEvent> Events, long Total);

/// <summary>
/// What a reader is asking for. Every property is optional and null means "do not narrow on this".
///
/// <para>
/// THE SHAPE THE ADMIN SCREEN NEEDS, stated here so the screen does not invent a second one.
/// Filter by <see cref="Kinds"/>, <see cref="Severities"/> and a time window; search free text.
/// </para>
/// </summary>
/// <param name="Search">Matched case-insensitively against <see cref="DiagnosticEvent.Message"/>,
/// <see cref="DiagnosticEvent.Detail"/>, <see cref="DiagnosticEvent.Route"/> and
/// <see cref="DiagnosticEvent.ExceptionType"/>. Deliberately NOT against <c>kind</c> or
/// <c>severity</c>: those have filters of their own, and a free-text box that also matched them
/// would make a typed filter and a typed search disagree about the same word.</param>
public sealed record DiagnosticsFilter(
    IReadOnlyCollection<string>? Kinds = null,
    IReadOnlyCollection<DiagnosticSeverity>? Severities = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Search = null)
{
    public static readonly DiagnosticsFilter None = new();
}

/// <summary>
/// EVERY KIND OF DIAGNOSTIC ROW THIS PLATFORM CAN WRITE, AS A CLOSED VOCABULARY.
///
/// <para>
/// Constants rather than free strings, for the reason <see cref="TenantActions"/> is: a log read by
/// filtering on a column is a log whose vocabulary has to be stable, and a stray
/// <c>http.server-error</c> beside <c>http.serverError</c> is two answers to one question with
/// nothing that would ever fail.
/// </para>
///
/// <para>
/// THE LIST IS SHORT ON PURPOSE AND WIDENING IT IS A DECISION. The failure this store must refuse
/// is becoming a second message log - a high-volume stream that costs money and buries the signal
/// it was added for, which is the <c>container.progress</c> mistake at instance scale. Two families
/// go in and one is explicitly out:
/// </para>
///
/// <list type="bullet">
///   <item><b>Anything that went wrong</b> - the <c>http.*</c>, <c>db.*</c>, <c>process.*</c> and
///     <c>transport.*</c> kinds below.</item>
///   <item><b>The startup and lifecycle facts</b> already printed and then lost - the
///     <c>startup.*</c> kinds. These answer "what was this instance even configured as" on a cold
///     start, and they are bounded by the number of BOOTS rather than by traffic.</item>
///   <item><b>OUT: per-request timings and contention measurements.</b> Deliberately excluded.
///     They are the highest-volume category by a wide margin, and a
///     per-request row is exactly the stream this store exists to avoid becoming. An item wanting
///     per-route timings must measure them another way or make its case from failures alone.</item>
/// </list>
/// </summary>
public static class DiagnosticKinds
{
    // ---- Anything that went wrong: HTTP -------------------------------------------------------

    /// <summary>A route threw and nothing caught it. Carries the route template, the status the
    /// request will answer, and the exception TYPE - never the stack, which is a megabyte of
    /// repetition across a storm of identical failures.</summary>
    public const string HttpUnhandledException = "http.unhandled-exception";

    /// <summary>A request answered 5xx without an exception reaching the pipeline - a handler that
    /// returned the status itself. Distinct from <see cref="HttpUnhandledException"/> because the
    /// two have opposite causes and the same symptom, and telling them apart is most of the
    /// diagnosis.</summary>
    public const string HttpServerError = "http.server-error";

    /// <summary>
    /// A refusal worth knowing about - today that is 409 and nothing else.
    ///
    /// ONE 409 FROM A BUSY GUARD IS ORDINARY; A REPEATED ONE IS A SYMPTOM, and the only way to see
    /// the difference is to have the rows. It is severity <see cref="DiagnosticSeverity.Warning"/>
    /// for that reason: the row is not itself a fault.
    ///
    /// 401, 403 and 404 are deliberately NOT here. They are the ordinary answer to a signed-out
    /// browser polling, and recording them would put this store's highest-volume rows next to its
    /// most useful ones.
    /// </summary>
    public const string HttpRefused = "http.refused";

    // ---- Anything that went wrong: the database -----------------------------------------------

    /// <summary>SQLite answered SQLITE_BUSY. The one database failure this product's shape makes
    /// likely: one file, several writers, a WAL.</summary>
    public const string DatabaseBusy = "db.busy";

    /// <summary>A connection waited out its `busy_timeout` and gave up. Distinct from
    /// <see cref="DatabaseBusy"/> because the timeout expiring means the contention lasted seconds
    /// rather than milliseconds, which is a different problem with the same error code.</summary>
    public const string DatabaseBusyTimeoutExpired = "db.busy-timeout-expired";

    /// <summary>A schema step's SQL failed. The row names the STEP, because that is what
    /// <c>MigrationFailedException</c> carries and what the operator has to fix.</summary>
    public const string DatabaseMigrationFailed = "db.migration-failed";

    /// <summary>
    /// What `PRAGMA journal_mode` actually ANSWERED.
    ///
    /// The pragma is fired on every start and its result has always been discarded - so an instance
    /// that silently failed to enter WAL looked exactly like one that did. This is where that
    /// answer lands. Severity is <see cref="DiagnosticSeverity.Info"/> when it says `wal` and
    /// <see cref="DiagnosticSeverity.Error"/> when it says anything else, which is the whole value
    /// of recording it.
    /// </summary>
    public const string DatabaseJournalMode = "db.journal-mode";

    // ---- Anything that went wrong: processes and launches -------------------------------------

    /// <summary>The command a preset names is not an executable file on PATH, still, after the launch
    /// looked again for its window. The run is reported
    /// as a launch failure without spawning anything.</summary>
    public const string ProcessExecutableNotFound = "process.executable-not-found";

    /// <summary>A child process could not be started at all.</summary>
    public const string ProcessLaunchFailed = "process.launch-failed";

    /// <summary>A child was killed - a timeout, a stop, a cancelled run.</summary>
    public const string ProcessKilled = "process.killed";

    /// <summary>The post-exit drain hit its grace period with a child still holding the output
    /// pipe: a server, a watcher or a tunnel outliving the agent that started it. The run's output
    /// is whatever had arrived by then, which is the thing this row exists to say.</summary>
    public const string ProcessOutputHeldOpen = "process.output-held-open";

    // ---- Anything that went wrong: the transport ----------------------------------------------

    /// <summary>A hub connection dropped. THE FAILURE THE CONSOLE COULD NOT RECOVER FROM on the
    /// night this store was asked for, and the one nothing wrote down.</summary>
    public const string TransportDisconnected = "transport.disconnected";

    /// <summary>A reconnect was attempted. Separate from the drop because a storm is many of these
    /// against one of those, and that ratio is the diagnosis.</summary>
    public const string TransportReconnecting = "transport.reconnecting";

    // ---- Anything that went wrong: a worker ----------------------------------------------------

    /// <summary>A worker's connection to control dropped, or the worker was lost with it: which
    /// worker, and the runs it held.</summary>
    public const string WorkerDropped = "worker.dropped";

    /// <summary>A worker was refused at its connection: its sentence, which the worker logs too.</summary>
    public const string WorkerRefused = "worker.refused";

    /// <summary>A connected worker did not end a stopped run within the stop backstop.</summary>
    public const string WorkerRunUnanswered = "worker.run-unanswered";

    /// <summary>A reconnecting worker still held a run control had already ended, and was told to stop it.</summary>
    public const string WorkerStaleRunStopped = "worker.stale-run-stopped";

    // ---- The startup and lifecycle facts ------------------------------------------------------

    /// <summary>WHAT THIS INSTANCE IS: data root, the address members call back on, the durability
    /// setting, the database path. One row per boot, and it is the row somebody reads first when
    /// asked "what was that instance even configured as".</summary>
    public const string StartupInstance = "startup.instance";

    /// <summary>The pre-migration backup's path. Printed today and lost with the terminal - and it
    /// is wanted at exactly the moment a migration went wrong, which is the moment the terminal is
    /// least likely to still be there.</summary>
    public const string StartupBackupWritten = "startup.backup-written";

    /// <summary>A team that will take no new work until somebody resumes it.</summary>
    public const string StartupTeamPaused = "startup.team-paused";

    /// <summary>A member, or a team's dynamic-member setting, with no Prompt chosen - which refuses
    /// at the wake rather than guessing.</summary>
    public const string StartupNoPromptChosen = "startup.no-prompt-chosen";

    /// <summary>A team with no Agent allowlist for new members, which refuses hiring.</summary>
    public const string StartupNoAgentAllowlist = "startup.no-agent-allowlist";

    /// <summary>A Headless preset with no usage format, so every run on it costs `(unknown)`.
    /// </summary>
    public const string StartupPresetWithoutUsageFormat = "startup.preset-without-usage-format";

    /// <summary>Something this host needs is not on the machine - `git`, `gh`. It serves anyway and
    /// the affected panel refuses; this is the durable half of the line it prints.</summary>
    public const string StartupPrerequisiteMissing = "startup.prerequisite-missing";

    /// <summary>Every kind above, for a screen that offers a filter and for the test that proves
    /// nothing writes a kind outside this list.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        HttpUnhandledException,
        HttpServerError,
        HttpRefused,
        DatabaseBusy,
        DatabaseBusyTimeoutExpired,
        DatabaseMigrationFailed,
        DatabaseJournalMode,
        ProcessExecutableNotFound,
        ProcessLaunchFailed,
        ProcessKilled,
        ProcessOutputHeldOpen,
        TransportDisconnected,
        TransportReconnecting,
        StartupInstance,
        StartupBackupWritten,
        StartupTeamPaused,
        StartupNoPromptChosen,
        StartupNoAgentAllowlist,
        StartupPresetWithoutUsageFormat,
        StartupPrerequisiteMissing,
    };
}

/// <summary>
/// WHICH PART OF THE PLATFORM WROTE THE ROW. A coarse grouping above <see cref="DiagnosticKinds"/>,
/// so a reader can ask "show me the database" without naming four kinds.
/// </summary>
public static class DiagnosticSources
{
    public const string Http = "http";
    public const string Database = "db";
    public const string Process = "process";
    public const string Transport = "transport";
    public const string Worker = "worker";
    public const string Startup = "startup";
}

/// <summary>
/// THE THIRD STORE, AND KEEPING IT DISTINCT FROM THE OTHER TWO IS THE WHOLE DESIGN.
///
/// <para>
/// <b><see cref="IMessageLog"/> is the CAUSAL STREAM.</b> Things wake Agent Containers; every row
/// carries a correlation and a cause; a container subscribes and work happens. Something belongs
/// there when another container has to ACT on it.
/// </para>
///
/// <para>
/// <b><see cref="ITenantLog"/> is ADMINISTRATIVE ACTS.</b> A person did something to the tenant -
/// created a team, deleted an account, dispatched an item. Nothing subscribes, nothing wakes, there
/// is no correlation, and it is denormalised so it can answer questions about things that are gone.
/// Something belongs there when the question is "who did this, and when".
/// </para>
///
/// <para>
/// <b>THIS IS NEITHER.</b> A 500, a SQLITE_BUSY, a spawn failure, a reconnect storm, the port this
/// instance came up on: none is work and none is an administrative act. Nobody did them and nobody
/// has to react to them. Something belongs HERE when the question is "what was this instance doing
/// when it went wrong", and the reader is a person looking at a screen after the fact.
/// </para>
///
/// <para>
/// <b>NOTHING SUBSCRIBES TO A DIAGNOSTIC EVENT AND NO CONTAINER IS WOKEN BY ONE.</b> That is the
/// failure mode this interface exists to refuse - it becoming a second message log. It is
/// structural rather than a rule anybody has to remember: a diagnostic row is not a
/// <see cref="Messages"/> type, it is not on `messages`, the delivery pump cannot see this table,
/// and there is no method here that could publish one. `DiagnosticsAreNotOnTheMessageLogTests`
/// drives a real pump over a real host and pins it.
/// </para>
///
/// <para>
/// <b>WRITING NEVER THROWS.</b> Inherited from <see cref="ITenantLog"/> and harder here, because
/// this is written from inside the failure paths themselves: a store that could break a request
/// while recording that the request broke is worse than no store. The guarantee is on the
/// IMPLEMENTATION rather than on a Host-side helper, because the write sites are spread across
/// modules - the migrator is in `Harness.Messaging` and has no Host helper to go through.
/// </para>
///
/// <para>
/// <b>NEVER RECORD A SECRET, AND REDACTION BELONGS AT THE WRITE.</b> Exception messages, request
/// bodies and environment dumps are exactly where credentials leak, and a credential has reached
/// this product's append-only log once already. Redacting at the READ would leave the plaintext on
/// disk, where a backup, a `VACUUM INTO` and anyone with the file would have it. See
/// <see cref="DiagnosticRedaction"/>; the store applies it, so no call site can forget.
/// </para>
///
/// <para>
/// <b>BOUNDED.</b> This is the highest-volume store this product has, and an unbounded one on a
/// small host is a full disk. See <see cref="DiagnosticRetention"/> for the bound and why it is
/// derived rather than a constant.
/// </para>
///
/// <para>
/// <b>IT MUST SURVIVE THE THING IT OBSERVES.</b> A plain table with no moving parts - no queue, no
/// background flusher, no timer - for the same reason the tenant log is one: whatever was written
/// stays written when the host dies. It cannot record the failure that stops the process writing to
/// a table at all; that is what capturing the host's own stdout to a file is for, and it ships
/// separately.
/// </para>
/// </summary>
public interface IDiagnosticsLog
{
    /// <summary>
    /// Records one diagnostic fact.
    ///
    /// <b>NEVER THROWS, INCLUDING ON CANCELLATION.</b> A failure to record must not fail the act
    /// being recorded, and a cancelled write must not surface as a cancellation in the request that
    /// was being recorded - which is the same failure wearing a different exception type.
    ///
    /// <paramref name="message"/> and <paramref name="detail"/> are REDACTED BY THE
    /// IMPLEMENTATION. A call site that redacted them itself would be the second place the rule
    /// lives, and the one that is forgotten.
    /// </summary>
    Task WriteAsync(
        DiagnosticSeverity severity,
        string kind,
        string? source = null,
        string? route = null,
        int? status = null,
        string? exceptionType = null,
        string? message = null,
        string? detail = null,
        CancellationToken ct = default);

    /// <summary>
    /// One page, newest first, narrowed by <paramref name="filter"/>, with the count of MATCHING
    /// rows beside it.
    ///
    /// A SEQ CURSOR, not an offset: rows with seq below <paramref name="before"/>, or from
    /// the newest when it is null. The next page's cursor is the last row's seq. An offset shifts
    /// every older page by one for each row appended while somebody scrolled - a duplicate at the
    /// seam - and an infinite list has no "page 3 of 47" for the offset to buy. The total stays,
    /// as a caption, and it is the filtered count WITHOUT the cursor: it answers "how many match",
    /// not "how many are left".
    ///
    /// <paramref name="take"/> is clamped to 1..<see cref="MaxTake"/>.
    /// </summary>
    Task<DiagnosticsPage> ReadAsync(
        DiagnosticsFilter? filter = null,
        long? before = null,
        int take = 50,
        CancellationToken ct = default);

    /// <summary>The most rows one <see cref="ReadAsync"/> answers.</summary>
    public const int MaxTake = 200;

    /// <summary>
    /// Whether this store has EVER held a row - regardless of the filter.
    ///
    /// EMPTY BECAUSE NOTHING WAS CAPTURED AND EMPTY BECAUSE NOTHING WENT WRONG ARE DIFFERENT
    /// ANSWERS, and a screen that renders them the same way is telling its reader that an instance
    /// with a broken diagnostics store is a healthy one. This product already has the rule
    /// elsewhere: `(unknown)` is a real state.
    ///
    /// Never throws, for the same reason the write does not: a diagnostics store that cannot be
    /// asked whether it is empty must answer "unknown", not take the screen down. Null IS that
    /// answer.
    /// </summary>
    Task<bool?> HasAnyAsync(CancellationToken ct = default);

    /// <summary>
    /// Applies the retention bound, removing whatever is over it.
    ///
    /// Called at startup and, throttled, from the write path - never on a timer, because a timer is
    /// a moving part and this store is deliberately a plain table without any. Never throws; a trim
    /// that fails leaves a bigger table, which is recoverable, where a trim that threw would take
    /// down the write it was attached to.
    /// </summary>
    Task TrimAsync(CancellationToken ct = default);
}
