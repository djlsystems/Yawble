using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Scripts.Engine;
using Yawble.Core.Scripts.Language;
using Yawble.Core.Scripts.Library;
using EngineRunStatus = Yawble.Core.Scripts.Engine.ScriptRunStatus;

namespace Yawble.Core.Scripts.Running;

/// <summary>
/// Runs script members (design section 6) and Test runs, through the script engine.
///
/// - What starts a member's run: a trigger aimed at it (<see cref="FireScript"/>, from the bus's trigger sweep, with the
///   structured payload as input), or a tell to it (<see cref="RunForBusAsync"/>, input { text }).
/// - One run at a time per member: a fire while a run is going is skipped and recorded as a skipped run.
/// - No workflow per fire: a trigger's run is a script_runs row and nothing on the bus; it does not wake the Manager.
/// - The member's timeout (default 10 minutes), the step budget (10,000) and the loop cap go to the engine.
/// - Every completed step is journaled (script_run_steps, its checkpoint on the run); after a restart a run left running
///   resumes from its last checkpoint (<see cref="ResumeInterrupted"/>).
/// - Saved variables are written only when a run succeeds.
/// - Three failed runs in a row open ONE decision card (<see cref="IScriptDecisions"/>); a success or the person's
///   answer starts the count again.
/// </summary>
public sealed class ScriptRunner : IScriptMemberHost, Launch.IScriptMemberRuns
{
    public const int StepBudget = 10_000;
    public const int LoopCap = 10_000;
    public const int FailuresForDecision = 3;

    private readonly ScriptRunStore _store;
    private readonly ScriptLibrary _library;
    private readonly IScriptActionsProvider _actions;
    private readonly IScriptRunEngine _engine;
    private readonly string _dataRoot;
    private readonly Func<long, IReadOnlyList<string>> _grantedFolders;
    private readonly Action<string>? _log;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<long, Task> _running = new();
    private readonly ConcurrentDictionary<long, Task> _background = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _claim = new();

    /// <param name="dataRoot">Where members' data folders (members/&lt;id&gt;), Test run folders (test/&lt;run&gt;) and dry-run folders (dry-run/&lt;run&gt;) are.</param>
    /// <param name="grantedFolders">A member's folders outside its data folder that its runs may write (the team's folder grants); none by default.</param>
    public ScriptRunner(ScriptRunStore store, ScriptLibrary library, IScriptActionsProvider actions, IScriptRunEngine engine, string dataRoot,
        Func<long, IReadOnlyList<string>>? grantedFolders = null, Action<string>? log = null, TimeProvider? clock = null)
    {
        _store = store;
        _library = library;
        _actions = actions;
        _engine = engine;
        _dataRoot = dataRoot;
        _grantedFolders = grantedFolders ?? (_ => []);
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    public IScriptDecisions? Decisions { get; set; }

    /// <summary>Who a run's tell steps give work to (the bus, in the app); null: a tell step fails (no team to tell).</summary>
    public IScriptTellers? Tellers { get; set; }

    /// <summary>What tells the runner a waiting run's wait ended, by kind (a tell's workflow: the bus, in the app).</summary>
    public IReadOnlyList<IScriptWaitSource> WaitSources { get; set; } = [];

    public bool IsScriptMember(long memberId) => _store.IsScriptMember(memberId);

    /// <summary>Whether the member has a run going now.</summary>
    public bool IsRunning(long memberId) => _running.ContainsKey(memberId);

    /// <summary>Waits for every run started in the background (tests; the app's stop).</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_background.Values.ToArray());

    public string MemberFolder(long memberId) => Path.Combine(_dataRoot, "members", memberId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // ------------------------------------------------------------------ the bus's ways in

    /// <summary>
    /// A trigger fired at a script member (called inside the bus's gate: it never waits for the run). The run starts in
    /// the background with <paramref name="payload"/> as input; while one is going the fire is skipped and noted.
    /// </summary>
    public TriggerFire FireScript(MemberRef member, string source, JsonObject payload)
    {
        var fire = Start(member.Id, source, payload);
        return fire.Started
            ? new TriggerFire("fired", null, null, fire.RunId)
            : new TriggerFire("skipped", null, fire.Reason, fire.RunId);
    }

    /// <summary>A start in the background (a trigger's fire, Run now).</summary>
    public ScriptFireOutcome Start(long memberId, string trigger, JsonNode? input)
    {
        if (!TryClaim(memberId, trigger, out var release, out var skipped)) return skipped!;
        long runId;
        try { runId = BeginMemberRun(memberId, trigger, input); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            release();
            Warn(memberId, null, trigger, e, "could not start a run");
            return new ScriptFireOutcome(false, null, e.Message);
        }
        var task = Task.Run(async () =>
        {
            try { await ExecuteMemberRunAsync(memberId, runId, null, _stopping.Token); }
            catch (Exception e) { Warn(memberId, runId, trigger, e, "the run stopped with an error"); }
            finally { release(); }
        });
        Track(runId, task);
        return new ScriptFireOutcome(true, runId, null);
    }

    /// <summary>
    /// A bus run of a script member (a tell to it, or the person's answer to its failure decision). An answer to the
    /// failure decision starts the failure count again and runs nothing; anything else runs the script with { text }.
    /// </summary>
    public async Task<RunResult> RunForBusAsync(RunContext run, CancellationToken ct)
    {
        var state = _store.State(run.Member.Id);
        if (state.FailureDecisionSeq is { } decision && run.Items.All(i => i.CausationSeq == decision))
        {
            _store.ClearFailures(run.Member.Id);
            return RunResult.Ok;
        }
        ScriptRunOutcome outcome;
        try { outcome = await RunMemberAsync(run.Member.Id, "tell", ScriptPayloads.Tell(run.Prompt), ct); }
        catch (ScriptRunRefused e) { return RunResult.Fail(e.Message); }
        return outcome.Run.Status switch
        {
            ScriptRunStatus.Succeeded or ScriptRunStatus.Waiting => RunResult.Ok,
            ScriptRunStatus.Skipped => RunResult.Fail(outcome.Run.Reason ?? "a run was going"),
            _ => RunResult.Fail($"script run {outcome.Run.Id} {outcome.Run.Status}" + (outcome.Run.FailedStepId is { } s ? $" at step {s}" : "")
                + (outcome.Run.Reason is { } r ? ": " + r : "")),
        };
    }

    /// <summary>Runs a member's script now and waits for it; a skipped run when one is going.</summary>
    public async Task<ScriptRunOutcome> RunMemberAsync(long memberId, string trigger, JsonNode? input, CancellationToken ct)
    {
        if (!TryClaim(memberId, trigger, out var release, out var skipped))
            return skipped!.RunId is { } skippedId ? Outcome(skippedId) : throw new ScriptRunRefused("not_found", skipped.Reason ?? $"member {memberId} is not a script member");
        try
        {
            var runId = BeginMemberRun(memberId, trigger, input);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
            var task = ExecuteMemberRunAsync(memberId, runId, null, linked.Token);
            Track(runId, task);
            await task;
            return Outcome(runId);
        }
        finally { release(); }
    }

    // ------------------------------------------------------------------ Test run

    /// <summary>
    /// A Test run: the version given (a draft too), on a sample input and settings, for a team; dry run unless told
    /// otherwise. It writes no saved variables and counts toward no member's failures.
    /// </summary>
    public async Task<ScriptRunOutcome> TestRunAsync(ScriptTestRun test, CancellationToken ct)
    {
        var team = _store.TeamName(test.TeamId) ?? throw new ScriptRunRefused("not_found", $"there is no team {test.TeamId}");
        var version = _library.Get(test.ScriptId, test.Version);
        var document = ParseDocument(version);
        var settings = ScriptSettings.WithDefaults(version.Document, test.Settings ?? []);
        var bindings = test.Bindings ?? [];
        var runId = _store.StartRun(test.TeamId, null, test.ScriptId, version.Version, "test", test.Input, settings, bindings, test.DryRun, test: true);
        var folder = Path.Combine(_dataRoot, "test", runId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var caller = new ScriptCaller(test.TeamId, team, null, null, runId, ScriptSettings.Effective(version.Document, bindings, _library.Integrations), test.DryRun);
        var actions = _actions.For(caller);
        var request = new ScriptRunRequest
        {
            Document = document,
            Input = test.Input?.DeepClone(),
            Settings = settings,
            SavedVariables = [],
            Actions = actions,
            Catalog = Yawble.Core.Scripts.Actions.ScriptActionKinds.CatalogFor(actions, _actions), // the team's: a dry run suppresses changing actions only with it
            Schemas = _library,
            DataFolder = folder,
            AllowedFolders = [],
            DryRun = test.DryRun,
            DryRunFolder = test.DryRun ? DryRunFolder(runId) : null,
            DryTells = true, // a Test run never sends a tell
            TellReplies = (JsonObject?)test.TellReplies?.DeepClone(),
            Team = team,
            Clock = _clock,
            Timeout = TimeSpan.FromMinutes(ScriptRunStore.DefaultTimeoutMinutes),
            StepBudget = StepBudget,
            LoopCap = LoopCap,
        };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
        var task = RunEngineAsync(runId, request, request.Timeout, linked.Token);
        Track(runId, task);
        await task;
        return Outcome(runId);
    }

    // ------------------------------------------------------------------ restart

    /// <summary>
    /// The app started: every member run the old core left running resumes from its last completed step (its checkpoint),
    /// in the background; Test runs and runs of members that are gone are recorded interrupted. The resumed runs.
    /// </summary>
    public IReadOnlyList<long> ResumeInterrupted()
    {
        var resumed = new List<long>();
        foreach (var run in _store.Unfinished().OrderBy(r => r.Id))
        {
            if (run.Test || run.MemberId is not { } memberId || _store.Member(memberId) is null)
            {
                _store.CloseOpenSteps(run.Id, ScriptRunStatus.Interrupted);
                _store.Finish(run.Id, ScriptRunStatus.Interrupted, null, null, "interrupted: the app stopped while this run was going");
                continue;
            }
            if (!TryClaim(memberId, run.Trigger, out var release, out _))
            {
                _store.Finish(run.Id, ScriptRunStatus.Interrupted, null, null, "interrupted: another run of the member was going");
                continue;
            }
            ScriptRunCheckpoint? checkpoint = null;
            if (run.Checkpoint is { } cp)
            {
                try { checkpoint = ScriptRunCheckpoint.FromJson(cp); }
                catch (Exception e) when (e is JsonException or FormatException) { _log?.Invoke($"script run {run.Id}: its checkpoint could not be read ({e.Message}); it starts again"); }
            }
            _store.CloseOpenSteps(run.Id, ScriptRunStatus.Interrupted);
            _store.Log(run.Id, checkpoint is null ? "resumed after a restart, from the start" : "resumed after a restart, from the last completed step");
            var task = Task.Run(async () =>
            {
                try { await ExecuteMemberRunAsync(memberId, run.Id, new Resume(run, checkpoint), _stopping.Token); }
                catch (Exception e) { Warn(memberId, run.Id, run.Trigger, e, "the resumed run stopped with an error"); }
                finally { release(); }
            });
            Track(run.Id, task);
            resumed.Add(run.Id);
        }
        return resumed;
    }

    // ------------------------------------------------------------------ waiting runs

    public static readonly TimeSpan WaitSweepEvery = TimeSpan.FromSeconds(15);
    private ITimer? _waitSweep;
    private int _sweeping;

    /// <summary>Checks waiting runs now and then every <paramref name="every"/> (default 15 seconds), until the runner stops.</summary>
    public void StartWaitSweep(TimeSpan? every = null)
    {
        var period = every ?? WaitSweepEvery;
        _waitSweep?.Dispose();
        _waitSweep = _clock.CreateTimer(_ => _ = SweepOnceAsync(), null, TimeSpan.Zero, period);
    }

    private async Task SweepOnceAsync()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return;
        try { await ResolveWaitsAsync(_stopping.Token); }
        catch (OperationCanceledException) { /* stopping */ }
        catch (Exception e) when (e is not OutOfMemoryException) { _log?.Invoke($"the waiting runs could not be checked: {e.Message}"); }
        finally { Volatile.Write(ref _sweeping, 0); }
    }

    /// <summary>
    /// Every waiting run whose wait ended (its source says how, or its timeout passed: timedOut) resumes from its checkpoint in the
    /// background, the step that waited completing with the outcome. A run whose member is gone, or a Test run, is recorded
    /// interrupted. Runs whose wait goes on are left as they are. The resumed runs.
    /// </summary>
    public async Task<IReadOnlyList<long>> ResolveWaitsAsync(CancellationToken ct = default)
    {
        var resumed = new List<long>();
        foreach (var run in _store.Waiting())
        {
            ct.ThrowIfCancellationRequested();
            if (run.Test || run.MemberId is not { } memberId || _store.Member(memberId) is null)
            {
                _store.CloseOpenSteps(run.Id, ScriptRunStatus.Interrupted);
                _store.Finish(run.Id, ScriptRunStatus.Interrupted, null, null, "interrupted: the member is no longer a script member");
                continue;
            }
            ScriptRunCheckpoint? checkpoint = null;
            try { checkpoint = run.Checkpoint is { } cp ? ScriptRunCheckpoint.FromJson(cp) : null; }
            catch (Exception e) when (e is JsonException or FormatException) { /* below */ }
            if (checkpoint?.Wait is not { } wait)
            {
                _store.CloseOpenSteps(run.Id, ScriptRunStatus.Failed);
                _store.Finish(run.Id, ScriptRunStatus.Failed, null, null, "the run's wait could not be read from its checkpoint");
                continue;
            }
            ScriptWaitResult? result = null;
            if (wait.Until is { } until && _clock.GetUtcNow() >= until) result = new ScriptWaitResult(ScriptTell.Outcomes.TimedOut);
            else if (WaitSources.FirstOrDefault(s => s.Kind == wait.Kind) is { } source)
            {
                try { result = await source.CheckAsync(wait, ct); }
                catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
                {
                    _log?.Invoke($"script run {run.Id}: its wait could not be checked: {e.Message}");
                }
            }
            if (result is null) continue;
            if (!TryClaim(memberId, run.Trigger, out var release, out _, resuming: run.Id, note: false)) continue;
            if (!_store.Unpark(run.Id))
            {
                release();
                continue;
            }
            _store.Log(run.Id, result.Outcome == ScriptTell.Outcomes.TimedOut
                ? "the wait timed out: the run goes on"
                : $"the wait ended ({result.Outcome}): the run goes on");
            var resume = new Resume(run, checkpoint, result);
            var task = Task.Run(async () =>
            {
                try { await ExecuteMemberRunAsync(memberId, run.Id, resume, _stopping.Token); }
                catch (Exception e) { Warn(memberId, run.Id, run.Trigger, e, "the run that waited stopped with an error"); }
                finally { release(); }
            }, CancellationToken.None);
            Track(run.Id, task);
            resumed.Add(run.Id);
        }
        return resumed;
    }

    /// <summary>
    /// Cancels a waiting run: it ends interrupted, and the workflow its tell started goes on (nothing is told to the team). False when
    /// the run is not waiting.
    /// </summary>
    public bool CancelWaiting(long runId)
    {
        var run = _store.Run(runId);
        if (run is null || run.Status != ScriptRunStatus.Waiting) return false;
        lock (_claim)
        {
            if (!_store.Unpark(runId)) return false;
            _store.CloseOpenSteps(runId, ScriptRunStatus.Interrupted);
            _store.Log(runId, "cancelled while it waited" + (run.WaitWorkflowId is { } w ? $"; workflow {w} goes on" : ""));
            _store.Finish(runId, ScriptRunStatus.Interrupted, null, null,
                "cancelled while it waited" + (run.WaitWorkflowId is { } w2 ? $" on workflow {w2}; the workflow goes on" : ""));
        }
        return true;
    }

    /// <summary>The app is stopping: runs going are cut off and left running in the store, so the next start resumes them.</summary>
    public async Task StopAsync()
    {
        _waitSweep?.Dispose();
        await _stopping.CancelAsync();
        try { await WhenIdleAsync(); } catch { /* each run logs its own end */ }
    }

    // ------------------------------------------------------------------ one member run

    private sealed record Resume(ScriptRunRow Run, ScriptRunCheckpoint? Checkpoint, ScriptWaitResult? WaitResult = null);

    /// <summary>Records the run's row (the version the member runs now) and returns its id. Throws ScriptRunRefused.</summary>
    private long BeginMemberRun(long memberId, string trigger, JsonNode? input)
    {
        var member = _store.Member(memberId) ?? throw new ScriptRunRefused("not_found", $"member {memberId} is not a script member");
        var version = member.Version ?? _library.RunnableVersion(member.ScriptId);
        if (version is null)
        {
            var key = _library.Get(member.ScriptId).Key;
            var id = _store.StartRun(member.TeamId, memberId, member.ScriptId, _library.Get(member.ScriptId).Version, trigger, input, member.Settings, member.Bindings,
                dryRun: false, test: false);
            _store.Finish(id, ScriptRunStatus.Failed, null, null, $"the script {key} has no version that passes its check (only drafts)");
            throw new ScriptRunRefused("not_runnable", $"the script {key} has no version that passes its check (only drafts)");
        }
        var settings = ScriptSettings.WithDefaults(_library.Get(member.ScriptId, version).Document, member.Settings);
        return _store.StartRun(member.TeamId, memberId, member.ScriptId, version.Value, trigger, input, settings, member.Bindings, dryRun: false, test: false);
    }

    private async Task ExecuteMemberRunAsync(long memberId, long runId, Resume? resume, CancellationToken ct)
    {
        var run = _store.Run(runId)!;
        var member = _store.Member(memberId);
        if (member is null)
        {
            _store.Finish(runId, ScriptRunStatus.Interrupted, null, null, "the member is no longer a script member");
            return;
        }
        ScriptVersionRow version;
        ScriptDocument document;
        try
        {
            version = _library.Get(run.ScriptId, run.Version);
            if (!version.Ok) throw new ScriptRunRefused("not_runnable", $"version {run.Version} of {version.Key} is a draft: it does not pass its check");
            document = ParseDocument(version);
        }
        catch (Exception e) when (e is ScriptRunRefused or ScriptLibraryException or FormatException)
        {
            _store.Finish(runId, ScriptRunStatus.Failed, null, null, e.Message);
            await AfterMemberRunAsync(member, succeeded: false, runId, e.Message);
            return;
        }
        var timeout = TimeSpan.FromMinutes(Math.Max(1, member.TimeoutMinutes));
        var dataFolder = MemberFolder(memberId);
        var caller = new ScriptCaller(member.TeamId, member.Team, memberId, member.Name, runId, ScriptSettings.Effective(version.Document, run.Bindings ?? member.Bindings, _library.Integrations), DryRun: false);
        var actions = _actions.For(caller);
        var request = new ScriptRunRequest
        {
            Document = document,
            Input = run.Input?.DeepClone(),
            Settings = (JsonObject)(run.Settings ?? member.Settings).DeepClone(),
            SavedVariables = (JsonObject)_store.State(memberId).Variables.DeepClone(),
            Actions = actions,
            Catalog = Yawble.Core.Scripts.Actions.ScriptActionKinds.CatalogFor(actions, _actions), // the team's: a dry run suppresses changing actions only with it
            Schemas = _library,
            DataFolder = dataFolder,
            AllowedFolders = _grantedFolders(memberId).Select(path => new ScriptFolderGrant(path, CanWrite: true)).ToList(),
            DryRun = false,
            DryRunFolder = null,
            Timeout = timeout,
            StepBudget = StepBudget,
            LoopCap = LoopCap,
            ResumeFrom = resume?.Checkpoint,
            WaitResult = resume?.WaitResult,
            Teller = Tellers?.For(caller, version.Key),
            Team = member.Team,
            MaxTells = Math.Clamp(member.MaxTells, 0, ScriptTell.MaxTellsLimit),
            Clock = _clock,
        };
        var result = await RunEngineAsync(runId, request, timeout, ct);
        if (result is null) return; // the app is stopping: left running, resumed at the next start
        if (result.Value.Waiting) return; // parked on its wait: no failure count, no saved variables; it goes on when the wait ends
        if (result.Value.Succeeded)
        {
            // Saved variables move only when a run succeeds (a failed poll never moves lastSeen).
            if (result.Value.SavedVariables is { } saved) _store.SetVariables(memberId, saved);
        }
        await AfterMemberRunAsync(member, result.Value.Succeeded, runId, result.Value.Reason);
    }

    /// <summary>The failure count, and at three in a row the one decision card.</summary>
    private async Task AfterMemberRunAsync(ScriptMemberRow member, bool succeeded, long runId, string? reason)
    {
        var failures = _store.RecordOutcome(member.MemberId, succeeded);
        if (succeeded || failures < FailuresForDecision || _store.State(member.MemberId).FailureDecisionSeq is not null || Decisions is null) return;
        var script = _library.Get(member.ScriptId).Key;
        try
        {
            var seq = await Decisions.OpenFailureDecisionAsync(member.MemberId,
                $"{member.Name}'s script {script} failed {failures} runs in a row: should it keep running?",
                $"The last run (script run {runId}) failed: {reason ?? "no reason given"}.\n\nThe member keeps running on its triggers; "
                + "this card is raised once, not for every failed run. Answer it to start the count again.",
                CancellationToken.None);
            if (seq is { } s) _store.SetFailureDecision(member.MemberId, s);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Warn(member.MemberId, runId, null, e, "could not open the failure decision");
        }
    }

    private readonly record struct EngineEnd(bool Succeeded, string? Reason, JsonObject? SavedVariables, bool Waiting = false);

    /// <summary>
    /// One pass through the engine with the journal; the run's row is finished here. Null when the app is stopping (the row
    /// stays running, to be resumed).
    /// </summary>
    private async Task<EngineEnd?> RunEngineAsync(long runId, ScriptRunRequest request, TimeSpan timeout, CancellationToken ct)
    {
        var journal = new StoreJournal(_store, runId);
        // The engine enforces the timeout; this is the backstop for an engine that does not return.
        using var backstop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        backstop.CancelAfter(timeout + TimeSpan.FromSeconds(30));
        ScriptRunResult result;
        try
        {
            result = await _engine.RunAsync(request, journal, backstop.Token);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = new ScriptRunResult(EngineRunStatus.Failed, journal.CurrentStep, $"timed out after {timeout.TotalMinutes:0} minutes", null, null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            result = new ScriptRunResult(EngineRunStatus.Failed, journal.CurrentStep, e.Message, null, null);
        }
        if (result.Status == EngineRunStatus.Waiting && result.Wait is { } wait)
        {
            // The checkpoint (with the wait) is written; the row says waiting, with what it waits on. No run slot is held from here.
            _store.Park(runId, wait.Kind, wait.Workflow, wait.Until);
            _store.Log(runId, (wait.Workflow is { } w ? $"waiting on workflow {w}" : $"waiting ({wait.Kind})")
                + (wait.Until is { } u ? $" until {u.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : ""));
            return new EngineEnd(false, null, null, Waiting: true);
        }
        var succeeded = result.Status == EngineRunStatus.Succeeded;
        var status = succeeded ? ScriptRunStatus.Succeeded : ScriptRunStatus.Failed;
        _store.CloseOpenSteps(runId, status);
        _store.Finish(runId, status, result.Outputs, succeeded ? null : result.FailedStepId, succeeded ? null : result.Reason);
        if (!succeeded) LogFailedRun(runId, result.FailedStepId, journal);
        return new EngineEnd(succeeded, result.Reason, succeeded ? result.SavedVariables : null);
    }

    /// <summary>
    /// the one warning line for a failed run, from its finished row (the reason as the store scrubbed it): the script
    /// run, the member (qualified), the script key@version, the trigger, the failed step's id and type, the integration and
    /// action it called, and the duration. Never the step's input or output (they may carry message content). Never throws.
    /// </summary>
    private void LogFailedRun(long runId, string? failedStepId, StoreJournal journal)
    {
        try
        {
            var run = _store.Run(runId);
            if (run is null) return;
            var qualified = run.Member is { } name ? $"{run.Team}/{name}" : null;
            var step = failedStepId ?? journal.CurrentStep;
            var called = step is null ? null : journal.Called(step);
            var integration = called?.Use is { } use && run.Bindings?[use] is JsonValue bound && bound.TryGetValue<string>(out var boundName) ? boundName : called?.Use;
            long? ms = DateTimeOffset.TryParse(run.StartedAt, out var started) && DateTimeOffset.TryParse(run.EndedAt, out var ended)
                ? (long)(ended - started).TotalMilliseconds : null;
            var text = $"script run {runId} failed" + (qualified is not null ? $": {qualified}" : run.Test ? ": a Test run" : "")
                + $", {run.Script}@{run.Version}, started by {run.Trigger}"
                + (step is not null ? $", at step {step}" + (called?.Type is { } t ? $" ({t})" : "") : "")
                + (run.Reason is { } r ? ": " + r : "");
            Logs.Diag.Warning("scripts", text,
                ("team", run.Team), ("member", qualified), ("scriptRun", runId), ("script", $"{run.Script}@{run.Version}"), ("trigger", run.Trigger),
                ("step", step), ("stepType", called?.Type), ("integration", integration), ("action", called?.Action),
                ("reason", run.Reason), ("ms", ms), ("status", run.Status));
        }
        catch (Exception e) when (e is not OutOfMemoryException) { /* the run is recorded; its log line is not the caller's problem */ }
    }

    /// <summary>A run that could not start or stopped with an error: member qualified name, trigger, script key and the exception type.</summary>
    private void Warn(long memberId, long? runId, string? trigger, Exception e, string what)
    {
        string? qualified = null, team = null, script = null;
        try
        {
            if (_store.Member(memberId) is { } m)
            {
                qualified = m.Qualified;
                team = m.Team;
                script = _library.Get(m.ScriptId).Key;
            }
        }
        catch (Exception) { /* the line is written with what is known */ }
        var reason = Connections.SecretScrubber.Scrub(e.Message);
        var text = $"script member {qualified ?? memberId.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + (runId is { } id ? $", script run {id}" : "") + $": {what} ({e.GetType().Name}): {reason}";
        Logs.Diag.Warning("scripts", text, ("team", team), ("member", qualified), ("scriptRun", runId), ("script", script), ("trigger", trigger),
            ("exception", e.GetType().Name), ("reason", reason));
        _log?.Invoke(text);
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>
    /// Claims the member's one run. A run going in memory, or a run of the member waiting on its tell (it counts as the member's run,
    /// though it holds no slot), refuses the claim; the refused start is noted as a skipped run unless <paramref name="note"/> is false.
    /// <paramref name="resuming"/>: the waiting run that is being resumed (its own wait does not refuse it).
    /// </summary>
    private bool TryClaim(long memberId, string trigger, out Action release, out ScriptFireOutcome? skipped, long? resuming = null, bool note = true)
    {
        lock (_claim)
        {
            var waiting = _running.ContainsKey(memberId) ? null : _store.WaitingRunOf(memberId);
            if (_running.ContainsKey(memberId) || (waiting is not null && waiting.Id != resuming))
            {
                release = () => { };
                var reason = waiting is null ? "a run of this member was going (one run at a time)"
                    : $"a run of this member (script run {waiting.Id}) was waiting" + (waiting.WaitWorkflowId is { } w ? $" on workflow {w}" : "") + " (one run at a time)";
                skipped = note ? Skip(memberId, trigger, reason) : new ScriptFireOutcome(false, null, reason);
                return false;
            }
            var gate = new TaskCompletionSource();
            _running[memberId] = gate.Task;
            release = () =>
            {
                _running.TryRemove(memberId, out _);
                gate.TrySetResult();
            };
            skipped = null;
            return true;
        }
    }

    /// <summary>A fire while a run was going: noted in the run list as a skipped run.</summary>
    private ScriptFireOutcome Skip(long memberId, string trigger, string reason)
    {
        var member = _store.Member(memberId);
        if (member is null) return new ScriptFireOutcome(false, null, reason);
        var version = member.Version ?? _library.RunnableVersion(member.ScriptId) ?? _library.Get(member.ScriptId).Version;
        var id = _store.StartRun(member.TeamId, memberId, member.ScriptId, version, trigger, null, member.Settings, member.Bindings, false, false,
            ScriptRunStatus.Skipped, reason);
        return new ScriptFireOutcome(false, id, reason);
    }

    private void Track(long runId, Task task)
    {
        _background[runId] = task;
        _ = task.ContinueWith(_ => _background.TryRemove(runId, out Task? _), TaskScheduler.Default);
    }

    private ScriptRunOutcome Outcome(long runId) => new(_store.Run(runId)!, _store.Steps(runId), _store.LogLines(runId));

    private string DryRunFolder(long runId) => Path.Combine(_dataRoot, "dry-run", runId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static ScriptDocument ParseDocument(ScriptVersionRow version)
    {
        try { return ScriptDocument.Parse(version.DocumentText); }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new FormatException($"version {version.Version} of {version.Key} cannot be read: {e.Message}", e); }
    }

    /// <summary>The engine's journal over the store: each step's row, its checkpoint on the run, and the run's log.</summary>
    private sealed class StoreJournal(ScriptRunStore store, long runId) : IScriptRunJournal
    {
        private readonly Dictionary<string, StepCall> _calls = new(StringComparer.Ordinal);

        public string? CurrentStep { get; private set; }

        /// <summary>The last start of a step: its type, and for an integration step the requirement and action (never its args).</summary>
        public StepCall? Called(string stepId) => _calls.GetValueOrDefault(stepId);

        public Task StepStartedAsync(string stepId, string type, JsonNode? input)
        {
            CurrentStep = stepId;
            _calls[stepId] = new StepCall(type, Text(input, "use"), Text(input, "action"));
            store.StepStarted(runId, stepId, type, input);
            return Task.CompletedTask;
        }

        public Task StepCompletedAsync(ScriptStepRecord rec, ScriptRunCheckpoint cp)
        {
            // A failed step has no output: its row keeps the reason instead ({ "error": ... }, scrubbed by the store).
            var output = rec.Output ?? (rec.Error is { } error ? new JsonObject { ["error"] = error } : null);
            store.StepCompleted(runId, rec.StepId, rec.Type, rec.Input, output, rec.Status, rec.StartedAt, rec.EndedAt);
            // Written before the next step starts: a restart resumes after this step.
            store.SetCheckpoint(runId, cp.ToJson());
            return Task.CompletedTask;
        }

        public Task LogAsync(string line)
        {
            store.Log(runId, line);
            return Task.CompletedTask;
        }

        private static string? Text(JsonNode? input, string key) =>
            input is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    private sealed record StepCall(string Type, string? Use, string? Action);
}
