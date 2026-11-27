using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Workflows.Language;

namespace Yawble.Core.Workflows.Engine;

/// <summary>
/// Runs a workflow: binds the input, settings and saved variables (the implicit start), then runs steps from
/// <c>start</c> until a body ends at the top level or an <c>end</c> step. Every completed step goes to the journal with a
/// checkpoint before the next one starts, so a run that was cut off resumes from its last checkpoint. A failed step,
/// the timeout, the step budget or the loop cap fails the run with the step and the reason; saved variables come back
/// only from a run that succeeded. A caller's cancellation (the app closing) throws, leaving the run to resume.
/// </summary>
public static class WorkflowEngine
{
    public const string StartStepId = "start";
    private static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    /// <summary>Runs <paramref name="request"/>. Holds no state between calls: concurrent runs of one document are independent.</summary>
    public static async Task<WorkflowRunResult> RunAsync(WorkflowRunRequest request, IWorkflowRunJournal journal, CancellationToken ct)
    {
        var clock = request.Clock ?? TimeProvider.System;
        // The run timeout counts only the time the run went (a resumed run's ActiveMs), not the time it waited or the app was closed.
        var active = TimeSpan.FromMilliseconds(Math.Max(0, request.ResumeFrom?.ActiveMs ?? 0));
        var left = request.Timeout - active;
        using var timeout = new CancellationTokenSource(left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var run = new Run(clock, request, journal, linked.Token, clock.GetUtcNow() - active);
        try
        {
            return await run.ExecuteAsync();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return await run.FailAsync(run.CurrentStep, $"the run timed out after {WorkflowValues.FormatDuration(request.Timeout)}");
        }
    }

    private sealed class Run : IWorkflowScope
    {
        private readonly TimeProvider _clock;
        private readonly WorkflowRunRequest _req;
        private readonly IWorkflowRunJournal _journal;
        private readonly CancellationToken _ct;
        private readonly DateTimeOffset _began;
        private readonly WorkflowDocument _doc;
        private readonly WorkflowFiles _files;
        private readonly List<string> _log = [];
        private readonly Dictionary<string, WorkflowType> _varTypes = new(StringComparer.Ordinal);

        // The state a checkpoint holds.
        private string? _next;
        private List<WorkflowLoopFrame> _frames = [];
        private JsonObject _vars = [];
        private JsonObject _settings = [];
        private JsonNode? _input;
        private JsonObject _steps = [];
        private JsonObject? _outputs;
        private int _stepsRun;
        private bool _ended;
        private int _tells;
        private WorkflowWait? _wait;
        private WorkflowRunCheckpoint? _last;

        /// <summary>Set by a step that parks the run (a tell that waits); the loop saves the wait and returns.</summary>
        private WorkflowWait? _parking;

        public string? CurrentStep { get; private set; }

        public Run(TimeProvider clock, WorkflowRunRequest req, IWorkflowRunJournal journal, CancellationToken ct, DateTimeOffset began)
        {
            _clock = clock;
            _req = req;
            _journal = journal;
            _ct = ct;
            _began = began;
            _doc = req.Document;
            _files = new WorkflowFiles(req.DataFolder, req.AllowedFolders,
                req.DryRun ? req.DryRunFolder ?? Path.Combine(req.DataFolder, "dry-run") : null);
        }

        private WorkflowSchema? Find(string name) => BuiltInSchemas.Find(name) ?? _req.Schemas?.Find(name);

        private DateTimeOffset Now => _clock.GetUtcNow();

        public bool TryGetRoot(string name, out JsonNode? value)
        {
            switch (name)
            {
                case "input": value = _input; return true;
                case "settings": value = _settings; return true;
                case "vars": value = _vars; return true;
                case "steps": value = _steps; return true;
            }
            for (var i = _frames.Count - 1; i >= 0; i--)
                if (!_frames[i].IsWhile && _frames[i].As == name)
                {
                    var f = _frames[i];
                    value = f.Index < f.Items.Count ? f.Items[f.Index] : null;
                    return true;
                }
            value = null;
            return false;
        }

        public async Task<WorkflowRunResult> ExecuteAsync()
        {
            foreach (var v in _doc.Variables)
                _varTypes[v.Name] = WorkflowType.Parse(v.Type, Find, out _) ?? WorkflowType.Any;

            if (_req.ResumeFrom is { } cp) Restore(cp);
            else if (await StartAsync() is { } failed) return failed;

            if (_wait is { } parked && await EndWaitAsync(parked) is { } afterWait) return afterWait;

            while (!_ended && !(_next is null && _frames.Count == 0))
            {
                _ct.ThrowIfCancellationRequested();
                var stepId = _next!;
                CurrentStep = stepId;
                if (Now - _began > _req.Timeout)
                    return await FailAsync(stepId, $"the run timed out after {WorkflowValues.FormatDuration(_req.Timeout)}");
                var step = _doc.Step(stepId);
                if (step is null) return await FailAsync(stepId, $"step '{stepId}' does not exist");
                if (_stepsRun >= _req.StepBudget)
                    return await FailAsync(stepId, $"the step budget of {_req.StepBudget.ToString("N0", CultureInfo.InvariantCulture)} steps is used up");

                var started = Now;
                JsonNode? input = null;
                try
                {
                    input = Input(step);
                    await _journal.StepStartedAsync(step.Id, step.Type, input?.DeepClone());
                    var (output, next) = await ExecuteStepAsync(step, input);
                    if (_parking is { } wait)
                    {
                        // The step parks the run: saved at this step, with what it waits on; it completes when the wait ends.
                        _parking = null;
                        _wait = wait;
                        _last = Snapshot();
                        await _journal.StepWaitingAsync(new WorkflowStepRecord(step.Id, step.Type, input, output, "waiting", started, Now), _last);
                        CurrentStep = null;
                        return Parked(wait);
                    }
                    _stepsRun++;
                    _next = next;
                    Advance();
                    var record = new WorkflowStepRecord(step.Id, step.Type, input, output, "succeeded", started, Now);
                    await Checkpoint(record);
                }
                catch (Exception e) when (e is WorkflowValueException or WorkflowExpressionException or WorkflowStepException
                                              or IOException or UnauthorizedAccessException or WorkflowActionException)
                {
                    return await FailAsync(step.Id, e.Message, step.Type, input, started);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // An integration's own failure (the call refused, the server down): the step fails with its message.
                    return await FailAsync(step.Id, $"{step.Type} failed: {e.Message}", step.Type, input, started);
                }
            }

            CurrentStep = null;
            var saved = new JsonObject();
            foreach (var v in _doc.Variables.Where(v => v.IsSaved))
                saved[v.Name] = _vars[v.Name]?.DeepClone();
            return new WorkflowRunResult(WorkflowRunStatus.Succeeded, null, null, (JsonObject?)_outputs?.DeepClone() ?? [], saved)
            {
                Log = _log.ToList(),
                StepsRun = _stepsRun,
            };
        }

        public async Task<WorkflowRunResult> FailAsync(string? stepId, string reason, string? type = null, JsonNode? input = null, DateTimeOffset? started = null)
        {
            var now = Now;
            if (stepId is not null && _last is not null)
            {
                var record = new WorkflowStepRecord(stepId, type ?? _doc.Step(stepId)?.Type ?? "", input, null, "failed", started ?? now, now, reason);
                await _journal.StepCompletedAsync(record, _last);
            }
            var line = stepId is null ? $"run failed: {reason}" : $"run failed at step '{stepId}': {reason}";
            await LogAsync(line);
            return new WorkflowRunResult(WorkflowRunStatus.Failed, stepId, reason, null, null) { Log = _log.ToList(), StepsRun = _stepsRun };
        }

        private WorkflowRunResult Parked(WorkflowWait wait) =>
            new(WorkflowRunStatus.Waiting, null, null, null, null) { Wait = wait, Log = _log.ToList(), StepsRun = _stepsRun };

        /// <summary>
        /// A run resumed while parked: with the wait's result (or its timeout passed) the waiting step completes and the run goes on
        /// (null); otherwise it stays parked. A result the step refuses (a reply that does not match) fails the run.
        /// </summary>
        private async Task<WorkflowRunResult?> EndWaitAsync(WorkflowWait parked)
        {
            var result = _req.WaitResult
                         ?? (parked.Until is { } until && Now >= until ? new WorkflowWaitResult(WorkflowTell.Outcomes.TimedOut) : null);
            if (result is null) return Parked(parked);
            CurrentStep = parked.StepId;
            var step = _doc.Step(parked.StepId);
            try
            {
                if (step is null) throw new WorkflowStepException($"step '{parked.StepId}' does not exist");
                var output = step.Type switch
                {
                    WorkflowTell.StepType when parked.Kind == WorkflowWait.DeliverableKind => EndTell(step, parked, result),
                    _ => throw new WorkflowStepException($"a {step.Type} step does not wait on {parked.Kind}"),
                };
                _wait = null;
                _stepsRun++;
                _next = step.Text("next");
                Advance();
                await Checkpoint(new WorkflowStepRecord(step.Id, step.Type, parked.Input?.DeepClone(), output, "succeeded", parked.Since, Now));
                return null;
            }
            catch (Exception e) when (e is WorkflowStepException or WorkflowValueException)
            {
                _wait = null;
                return await FailAsync(parked.StepId, e.Message, step?.Type, parked.Input?.DeepClone(), parked.Since);
            }
        }

        // ---- start, checkpoints, loops --------------------------------------------------------------------------------

        private async Task<WorkflowRunResult?> StartAsync()
        {
            var started = Now;
            await _journal.StepStartedAsync(StartStepId, StartStepId, _req.Input?.DeepClone());
            try
            {
                var inputType = WorkflowChecker.InputType(_doc, Find);
                _input = WorkflowValues.Convert(WorkflowValues.FromNode(_req.Input), inputType, Find);

                foreach (var s in _doc.Settings)
                {
                    var type = WorkflowType.Parse(s.Type, Find, out var error) ?? throw new WorkflowStepException($"setting '{s.Name}': {error}");
                    var given = _req.Settings?[s.Name] ?? s.Default;
                    if (given is null) throw new WorkflowStepException($"setting '{s.Name}' is not set");
                    try { _settings[s.Name] = WorkflowValues.Convert(WorkflowValues.FromNode(given), type, Find); }
                    catch (WorkflowValueException e) { throw new WorkflowStepException($"setting '{s.Name}': {e.Message}"); }
                }

                foreach (var v in _doc.Variables)
                {
                    var type = _varTypes[v.Name];
                    var value = v.IsSaved && _req.SavedVariables is { } saved && saved.ContainsKey(v.Name) ? saved[v.Name] : v.Initial;
                    try { _vars[v.Name] = WorkflowValues.Convert(WorkflowValues.FromNode(value), type, Find); }
                    catch (WorkflowValueException e) { throw new WorkflowStepException($"variable '{v.Name}': {e.Message}"); }
                }
            }
            catch (Exception e) when (e is WorkflowStepException or WorkflowValueException)
            {
                var now = Now;
                _last = Snapshot();
                await _journal.StepCompletedAsync(new WorkflowStepRecord(StartStepId, StartStepId, _req.Input?.DeepClone(), null, "failed", started, now, e.Message), _last);
                await LogAsync($"run failed at the start: {e.Message}");
                return new WorkflowRunResult(WorkflowRunStatus.Failed, StartStepId, e.Message, null, null) { Log = _log.ToList() };
            }

            _next = _doc.Start;
            var output = new JsonObject { ["settings"] = _settings.DeepClone(), ["vars"] = _vars.DeepClone() };
            await Checkpoint(new WorkflowStepRecord(StartStepId, StartStepId, _req.Input?.DeepClone(), output, "succeeded", started, Now));
            return null;
        }

        private async Task Checkpoint(WorkflowStepRecord record)
        {
            _last = Snapshot();
            await _journal.StepCompletedAsync(record, _last);
        }

        private WorkflowRunCheckpoint Snapshot() => new()
        {
            Next = _next,
            Frames = _frames.Select(f => f.Clone()).ToList(),
            Vars = (JsonObject)_vars.DeepClone(),
            Settings = (JsonObject)_settings.DeepClone(),
            Input = _input?.DeepClone(),
            Steps = (JsonObject)_steps.DeepClone(),
            Outputs = (JsonObject?)_outputs?.DeepClone(),
            StepsRun = _stepsRun,
            Ended = _ended,
            Tells = _tells,
            ActiveMs = (long)Math.Max(0, (Now - _began).TotalMilliseconds),
            Wait = _wait,
        };

        private void Restore(WorkflowRunCheckpoint cp)
        {
            // Through JSON, so a checkpoint read back from storage and one handed over in memory behave the same.
            cp = WorkflowRunCheckpoint.FromJson(cp.ToJson());
            _next = cp.Next;
            _frames = cp.Frames;
            _vars = cp.Vars;
            _settings = cp.Settings;
            _input = cp.Input;
            _steps = cp.Steps;
            _outputs = cp.Outputs;
            _stepsRun = cp.StepsRun;
            _ended = cp.Ended;
            _tells = cp.Tells;
            _wait = cp.Wait;
            _last = Snapshot();
            // A variable the workflow declares that the checkpoint lacks (it cannot, unless the workflow changed): its initial value.
            foreach (var v in _doc.Variables.Where(v => !_vars.ContainsKey(v.Name)))
                _vars[v.Name] = WorkflowValues.Convert(WorkflowValues.FromNode(v.Initial), _varTypes[v.Name], Find);
        }

        /// <summary>
        /// After a step: a null successor ends the innermost body, which moves its loop to the next item or past it. A while's pass
        /// ends by going back to the while step, which checks its condition again (a step of its own, saved like any other).
        /// </summary>
        private void Advance()
        {
            while (_next is null && _frames.Count > 0)
            {
                var frame = _frames[^1];
                if (frame.IsWhile)
                {
                    _next = frame.StepId;
                    return;
                }
                var loop = _doc.Step(frame.StepId)!;
                frame.Index++;
                if (frame.Index < frame.Items.Count)
                {
                    _next = loop.Text("body");
                    return;
                }
                _frames.RemoveAt(_frames.Count - 1);
                _next = loop.Text("then");
            }
        }

        private async Task LogAsync(string line)
        {
            _log.Add(line);
            await _journal.LogAsync(line);
        }

        // ---- steps ----------------------------------------------------------------------------------------------------

        private WorkflowEvaluator Evaluator() => new(this, _clock);

        /// <summary>The step's input as the run view shows it: its evaluated values.</summary>
        private JsonNode? Input(WorkflowStep step)
        {
            var ev = Evaluator();
            JsonObject Pick(params string[] names)
            {
                var o = new JsonObject();
                foreach (var n in names.Where(step.Has)) o[n] = ev.EvaluateValue(step[n]);
                return o;
            }
            return step.Type switch
            {
                "set" => Pick("value"),
                "switch" => Pick("on"),
                "forEach" => Pick("items"),
                "integration" => new JsonObject { ["use"] = step.Text("use"), ["action"] = step.Text("action"), ["args"] = ev.EvaluateValue(step["args"]) ?? new JsonObject() },
                WorkflowStepTypes.FileWriteAllText or WorkflowStepTypes.FileAppendLine => Pick("path", "text"),
                WorkflowStepTypes.FileReadAllText or WorkflowStepTypes.FileReadLines or WorkflowStepTypes.FileExists or WorkflowStepTypes.FileDelete => Pick("path"),
                WorkflowStepTypes.FileMove or WorkflowStepTypes.FileCopy => Pick("path", "to", "overwrite"),
                WorkflowStepTypes.FileList => Pick("path", "pattern", "recursive"),
                "log" => Pick("text"),
                WorkflowTell.StepType => Pick("member", "team", "text", "wait", "timeout", "reply"),
                WorkflowCode.StepType => new JsonObject { ["inputs"] = ev.EvaluateValue(step["inputs"]) ?? new JsonObject() },
                "end" => step["outputs"] is { } o ? new JsonObject { ["outputs"] = ev.EvaluateValue(o) } : null,
                _ => null,
            };
        }

        private async Task<(JsonNode? Output, string? Next)> ExecuteStepAsync(WorkflowStep step, JsonNode? input)
        {
            var next = step.Text("next");
            switch (step.Type)
            {
                case "set":
                {
                    var name = step.Text("var") ?? throw new WorkflowStepException("set has no var");
                    var value = SetVar(name, input?["value"]);
                    return (value?.DeepClone(), next);
                }
                case "if":
                {
                    var result = WorkflowCondition.Parse(step["when"]).Evaluate(Evaluator());
                    return (new JsonObject { ["result"] = result }, step.Text(result ? "then" : "else"));
                }
                case "switch":
                {
                    var key = WorkflowValues.Format(WorkflowValues.FromNode(input?["on"]));
                    if (step["cases"] is JsonObject cases && cases.TryGetPropertyValue(key, out var target))
                        return (new JsonObject { ["case"] = key }, target is JsonValue t && t.GetValueKind() == JsonValueKind.String ? t.GetValue<string>() : null);
                    return (new JsonObject { ["case"] = "default" }, step.Text("default"));
                }
                case "forEach":
                {
                    var items = WorkflowValues.ToList(WorkflowValues.FromNode(input?["items"]));
                    if (items.Count > _req.LoopCap)
                        throw new WorkflowStepException($"forEach has {items.Count} items, over the loop cap of {_req.LoopCap}");
                    var name = step.Text("as") ?? throw new WorkflowStepException("forEach has no as");
                    var body = step.Text("body") ?? throw new WorkflowStepException("forEach has no body");
                    var output = new JsonObject { ["count"] = items.Count };
                    if (items.Count == 0) return (output, step.Text("then"));
                    _frames.Add(new WorkflowLoopFrame { StepId = step.Id, As = name, Items = (JsonArray)items.DeepClone(), Index = 0 });
                    return (output, body);
                }
                case WorkflowLoop.StepType:
                    return While(step);
                case WorkflowCode.StepType:
                    return (await CodeAsync(step, input as JsonObject ?? []), next);
                case "integration":
                    return (await IntegrationAsync(step, (JsonObject)input!), next);
                case WorkflowStepTypes.FileWriteAllText or WorkflowStepTypes.FileAppendLine:
                {
                    var path = Text(input, "path");
                    var text = Text(input, "text");
                    var (target, real) = _files.ForWrite(path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (step.Type == WorkflowStepTypes.FileWriteAllText) await File.WriteAllTextAsync(target, text, _ct);
                    else await File.AppendAllTextAsync(target, text + "\n", _ct);
                    if (_files.DryRun) await LogAsync($"would {(step.Type == WorkflowStepTypes.FileWriteAllText ? "write" : "append a line to")} {real} (dry run: {target})");
                    return (new JsonObject { ["path"] = real }, next);
                }
                case WorkflowStepTypes.FileReadAllText:
                {
                    var file = _files.ForRead(Text(input, "path"));
                    if (!File.Exists(file)) throw new WorkflowStepException($"'{Text(input, "path")}' does not exist");
                    var text = await File.ReadAllTextAsync(file, _ct);
                    return (Result(step, JsonValue.Create(text)), next);
                }
                case WorkflowStepTypes.FileReadLines:
                {
                    var file = _files.ForRead(Text(input, "path"));
                    if (!File.Exists(file)) throw new WorkflowStepException($"'{Text(input, "path")}' does not exist");
                    var lines = await File.ReadAllLinesAsync(file, _ct);
                    return (Result(step, new JsonArray(lines.Select(l => (JsonNode?)l).ToArray())), next);
                }
                case WorkflowStepTypes.FileExists:
                    return (Result(step, JsonValue.Create(File.Exists(_files.ForRead(Text(input, "path"))))), next);
                case WorkflowStepTypes.FileDelete:
                {
                    var path = Text(input, "path");
                    if (_files.DryRun)
                    {
                        var (real, _, _) = _files.Locate(path);
                        _files.DryDelete(path);
                        await LogAsync($"would delete {real}");
                        return (new JsonObject { ["deleted"] = false, ["path"] = real }, next);
                    }
                    _files.CheckDeletable(path);
                    var (full, _, _) = _files.Locate(path);
                    var existed = File.Exists(full);
                    if (existed) File.Delete(full);
                    return (new JsonObject { ["deleted"] = existed, ["path"] = full }, next);
                }
                case WorkflowStepTypes.FileMove or WorkflowStepTypes.FileCopy:
                    return (Result(step, JsonValue.Create(await MoveOrCopyAsync(step.Type == WorkflowStepTypes.FileMove, input))), next);
                case WorkflowStepTypes.FileList:
                {
                    var pattern = input?["pattern"] is null ? "*" : Text(input, "pattern");
                    var patterns = pattern.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    var files = _files.List(Text(input, "path"), patterns.Length == 0 ? ["*"] : patterns, Bool(input, "recursive"));
                    var list = new JsonArray(files.Select(f => (JsonNode?)new JsonObject
                    {
                        ["path"] = f.Path,
                        ["name"] = Path.GetFileName(f.Path),
                        ["size"] = f.Info.Length,
                        ["modifiedAt"] = WorkflowValues.FormatInstant(f.Info.LastWriteTimeUtc),
                    }).ToArray());
                    return (Result(step, list), next);
                }
                case "log":
                    await LogAsync(Text(input, "text"));
                    return (null, next);
                case WorkflowTell.StepType:
                    return (await TellAsync(step, input as JsonObject ?? []), next);
                case "end":
                {
                    if (input?["outputs"] is JsonObject outputs)
                    {
                        _outputs ??= [];
                        foreach (var (name, value) in outputs)
                        {
                            var declared = _doc.Outputs.FirstOrDefault(o => o.Name == name);
                            var type = declared is null ? WorkflowType.Any : WorkflowType.Parse(declared.Type, Find, out _) ?? WorkflowType.Any;
                            _outputs[name] = WorkflowValues.Convert(WorkflowValues.FromNode(value), type, Find);
                        }
                    }
                    _ended = true;
                    _frames.Clear();
                    return (_outputs?.DeepClone(), null);
                }
            }
            throw new WorkflowStepException($"'{step.Type}' is not a step type");
        }

        private static string Text(JsonNode? input, string name)
        {
            var value = WorkflowValues.FromNode(input?[name]);
            if (value is JsonArray or JsonObject) throw new WorkflowStepException($"'{name}' is {WorkflowValues.Describe(value)}, not text");
            return WorkflowValues.Format(value);
        }

        /// <summary>
        /// A file.move or file.copy: the bytes as they are (a file copy, never text), the target from <see cref="WorkflowFiles.ForTarget"/>.
        /// A move across volumes is a copy, then the source deleted. A dry run puts the bytes in the target's dry-run copy (a move also
        /// marks its source deleted for the rest of the dry run) and changes no real file. Gives the final real path.
        /// </summary>
        private async Task<string> MoveOrCopyAsync(bool move, JsonNode? input)
        {
            var verb = move ? "move" : "copy";
            var (read, source) = _files.ForSource(Text(input, "path"));
            var overwrite = Bool(input, "overwrite");
            var target = _files.ForTarget(Text(input, "to"), source, overwrite, move);
            var same = string.Equals(source, target, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (_files.DryRun)
            {
                if (!same)
                {
                    _files.DryPut(read, target);
                    if (move) _files.DryDelete(source);
                }
                await LogAsync($"would {verb} {source} -> {target}");
                return target;
            }
            if (same) return target;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!move) File.Copy(source, target, overwrite);
            else if (!string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(source, target, overwrite);
                File.Delete(source);
            }
            else File.Move(source, target, overwrite);
            return target;
        }

        /// <summary>An optional true/false input (absent: false); text "true"/"false" from a template counts.</summary>
        private static bool Bool(JsonNode? input, string name) => WorkflowValues.FromNode(input?[name]) switch
        {
            null => false,
            bool b => b,
            string s when bool.TryParse(s.Trim(), out var b) => b,
            var other => throw new WorkflowStepException($"'{name}' is {WorkflowValues.Describe(other)}, not true or false"),
        };

        private JsonNode? SetVar(string name, JsonNode? value)
        {
            if (!_varTypes.TryGetValue(name, out var type)) throw new WorkflowStepException($"variable '{name}' is not declared");
            JsonNode? converted;
            try { converted = WorkflowValues.Convert(WorkflowValues.FromNode(value), type, Find); }
            catch (WorkflowValueException e) { throw new WorkflowStepException($"variable '{name}' ({type}): {e.Message}"); }
            _vars[name] = converted;
            return converted;
        }

        /// <summary>A step's result: kept as <c>steps.&lt;id&gt;</c> and, with <c>into</c>, in that variable.</summary>
        private JsonNode? Result(WorkflowStep step, JsonNode? value)
        {
            var stored = step.Text("into") is { } into ? SetVar(into, value) : value;
            _steps[step.Id] = stored?.DeepClone();
            return stored?.DeepClone();
        }

        /// <summary>
        /// A tell: the cap first (the next tell past maxTells fails the run), then a dry run logs what it would tell and sends nothing;
        /// otherwise the teller starts the deliverable. Without wait, into gets { outcome: sent, deliverable }; with wait the run parks on the
        /// deliverable until it ends or the timeout passes.
        /// </summary>
        private async Task<JsonNode?> TellAsync(WorkflowStep step, JsonObject input)
        {
            var member = OptText(input, "member") ?? WorkflowTell.DefaultMember;
            var team = OptText(input, "team");
            var text = Text(input, "text");
            if (text.Trim().Length == 0) throw new WorkflowStepException("tell has no text to send");
            var wait = input["wait"] is JsonValue w && w.TryGetValue<bool>(out var b) && b;
            var timeout = WorkflowTell.DefaultTimeout;
            if (wait && input["timeout"] is { } t)
            {
                if (!WorkflowTell.TryParseTimeout(t, out timeout) || timeout <= TimeSpan.Zero)
                    throw new WorkflowStepException($"tell's timeout '{t.ToJsonString()}' is not a duration");
                if (timeout > WorkflowTell.MaxTimeout) throw new WorkflowStepException("tell's timeout is over 7 days");
            }
            var replyName = wait ? step.Text("reply") : null;
            var replySchema = replyName is null ? null : Find(replyName) ?? throw new WorkflowStepException($"reply: no schema named '{replyName}'");

            if (_tells >= _req.MaxTells)
                throw new WorkflowStepException($"a run may tell at most {_req.MaxTells} time{(_req.MaxTells == 1 ? "" : "s")} (the member's maxTells is {_req.MaxTells}); this would be tell {_tells + 1}");
            _tells++;

            if (_req.DryRun || _req.DryTells)
            {
                await LogAsync($"would tell {member} of {team ?? _req.Team ?? "its team"}: {text}");
                string reply = "";
                JsonNode? data = null;
                if (wait && _req.TellReplies is { } replies && replies.TryGetPropertyValue(step.Id, out var madeUp))
                    (reply, data) = MadeUpReply(madeUp, replyName, replySchema);
                return Result(step, WorkflowTell.Result(WorkflowTell.Outcomes.DryRun, 0, reply, data));
            }

            var teller = _req.Teller ?? throw new WorkflowStepException("this run has no team to tell");
            WorkflowTellSent sent;
            try { sent = await teller.TellAsync(new WorkflowTellCall(step.Id, member, team, text, wait, replyName, (JsonObject?)replySchema?.Schema.DeepClone()), _ct); }
            catch (Exception e) when (e is not OperationCanceledException and not WorkflowActionException)
            {
                throw new WorkflowActionException($"tell {member}: {e.Message}");
            }
            if (!wait) return Result(step, WorkflowTell.Result(WorkflowTell.Outcomes.Sent, sent.Deliverable, "", null));

            var now = Now;
            _parking = new WorkflowWait
            {
                Kind = WorkflowWait.DeliverableKind,
                StepId = step.Id,
                Deliverable = sent.Deliverable,
                Since = now,
                Until = now + timeout,
                Data = new JsonObject { ["member"] = sent.Member, ["team"] = sent.Team, ["reply"] = replyName },
                Input = input.DeepClone(),
            };
            return new JsonObject { ["waiting"] = true, ["deliverable"] = sent.Deliverable, ["member"] = sent.Member, ["team"] = sent.Team };
        }

        /// <summary>A waiting tell's end: its outcome, the closing summary, and for done with a reply schema the reply's checked data.</summary>
        private JsonNode? EndTell(WorkflowStep step, WorkflowWait wait, WorkflowWaitResult result)
        {
            if (!WorkflowTell.Outcomes.All.Contains(result.Outcome)) throw new WorkflowStepException($"'{result.Outcome}' is not how a tell ends");
            var replyName = wait.Data?["reply"] is JsonValue r && r.TryGetValue<string>(out var n) ? n : null;
            JsonNode? data = null;
            if (result.Outcome == WorkflowTell.Outcomes.Done && replyName is not null)
            {
                var schema = Find(replyName) ?? throw new WorkflowStepException($"reply: no schema named '{replyName}'");
                data = CheckedReply(WorkflowTell.ReplyJson(result.Text), replyName, schema);
            }
            return Result(step, WorkflowTell.Result(result.Outcome, wait.Deliverable ?? 0, result.Text, data));
        }

        /// <summary>A Test run's made-up reply: text (its JSON, when it ends with some, is the data) or JSON (the data itself).</summary>
        private (string Reply, JsonNode? Data) MadeUpReply(JsonNode? madeUp, string? replyName, WorkflowSchema? schema)
        {
            string reply;
            JsonNode? json;
            if (madeUp is JsonValue v && v.TryGetValue<string>(out var s))
            {
                reply = s;
                json = WorkflowTell.ReplyJson(s);
            }
            else
            {
                reply = madeUp?.ToJsonString() ?? "";
                json = madeUp?.DeepClone();
            }
            return (reply, replyName is null || schema is null ? null : CheckedReply(json, replyName, schema));
        }

        private JsonNode CheckedReply(JsonNode? json, string replyName, WorkflowSchema schema)
        {
            if (json is null) throw new WorkflowStepException($"the reply does not end with JSON of {replyName}");
            var errors = schema.Validate(json, Find);
            if (errors.Count > 0) throw new WorkflowStepException($"the reply does not match {replyName}: {string.Join("; ", errors.Take(3))}");
            return json;
        }

        /// <summary>
        /// A while: reached from outside it starts at pass 0; reached again at the end of a pass (its frame on top) it counts that
        /// pass. Its condition false goes to then (the frame gone); true runs the body again, unless the passes reached max or the
        /// loop cap, which fails the run.
        /// </summary>
        private (JsonNode? Output, string? Next) While(WorkflowStep step)
        {
            var frame = _frames.Count > 0 && _frames[^1] is { IsWhile: true } top && top.StepId == step.Id ? top : null;
            if (frame is not null) frame.Index++;
            var passes = frame?.Index ?? 0;
            var max = step["max"] is { } m && WorkflowValues.FromNode(m) is long l ? (int)Math.Clamp(l, 1, WorkflowLoop.MaxLimit) : WorkflowLoop.DefaultMax;
            var holds = WorkflowCondition.Parse(step["when"]).Evaluate(Evaluator());
            var output = new JsonObject { ["result"] = holds, ["passes"] = passes };
            if (!holds)
            {
                if (frame is not null) _frames.RemoveAt(_frames.Count - 1);
                return (output, step.Text("then"));
            }
            if (passes >= max) throw new WorkflowStepException(WorkflowLoop.RanMax(step.Id, max));
            if (passes >= _req.LoopCap) throw new WorkflowStepException($"the loop '{step.Id}' reached the loop cap of {_req.LoopCap} passes");
            var body = step.Text("body") ?? throw new WorkflowStepException("while has no body");
            if (frame is null) _frames.Add(new WorkflowLoopFrame { StepId = step.Id, IsWhile = true, Index = 0 });
            return (output, body);
        }

        /// <summary>
        /// A code step: runs the function on its evaluated inputs under the code limits (off the run's thread, so the run's
        /// cancellation reaches it), writes what it logged, then checks the returned value against returns and keeps it.
        /// </summary>
        private async Task<JsonNode?> CodeAsync(WorkflowStep step, JsonObject input)
        {
            var code = step.Text("code") ?? throw new WorkflowStepException("code has no code");
            var inputs = input["inputs"] as JsonObject ?? [];
            WorkflowCodeOutcome outcome;
            try
            {
                outcome = await Task.Run(() => WorkflowCode.Run(code, inputs, _req.CodeLimits, _ct), _ct);
            }
            catch (WorkflowCodeException e)
            {
                foreach (var line in WorkflowCode.LogOf(e)) await LogAsync(line);
                throw new WorkflowStepException(e.Message);
            }
            foreach (var line in outcome.Log) await LogAsync(line);

            var returns = step.Text("returns") is { Length: > 0 } text
                ? WorkflowType.Parse(text, Find, out var why) ?? throw new WorkflowStepException($"returns: {why}")
                : WorkflowType.Any;
            JsonNode? value;
            try { value = WorkflowValues.Convert(WorkflowValues.FromNode(outcome.Result), returns, Find); }
            catch (WorkflowValueException e) { throw new WorkflowStepException($"the code returned {WorkflowValues.Describe(WorkflowValues.FromNode(outcome.Result))}, not {returns}: {e.Message}"); }
            return Result(step, value);
        }

        private static string? OptText(JsonNode? input, string name) =>
            input?[name] is null ? null : Text(input, name).Trim() is { Length: > 0 } s ? s : null;

        private async Task<JsonNode?> IntegrationAsync(WorkflowStep step, JsonObject input)
        {
            var use = step.Text("use") ?? throw new WorkflowStepException("integration has no use");
            var action = step.Text("action") ?? throw new WorkflowStepException("integration has no action");
            var requirement = _doc.Requires.FirstOrDefault(r => r.Name == use) ?? throw new WorkflowStepException($"'{use}' is not in requires");
            var args = input["args"] as JsonObject ?? [];
            var call = new WorkflowActionCall(use, requirement.Kind, action, (JsonObject)args.DeepClone(), _req.DryRun && _req.Catalog is null);

            if (_req.DryRun && _req.Catalog?.IsChanging(requirement.Kind, action) == true)
            {
                await LogAsync($"would {action} on {use} with {args.ToJsonString(Compact)}");
                _steps[step.Id] = null;
                return new JsonObject { ["dryRun"] = true, ["wouldCall"] = action };
            }

            JsonNode? result;
            try { result = await _req.Actions.InvokeAsync(call, _ct); }
            catch (Exception e) when (e is not OperationCanceledException and not WorkflowActionException)
            {
                throw new WorkflowActionException($"{use} {action}: {e.Message}");
            }
            return Result(step, result);
        }
    }
}

/// <summary>An integration action that failed (refused, needs approval, the service failed); the step fails with its message.</summary>
public sealed class WorkflowActionException(string message) : Exception(message);
