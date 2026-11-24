using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Scripts.Language;

namespace Yawble.Core.Scripts.Engine;

/// <summary>
/// Runs a script: binds the input, settings and saved variables (the implicit start), then runs steps from
/// <c>start</c> until a body ends at the top level or an <c>end</c> step. Every completed step goes to the journal with a
/// checkpoint before the next one starts, so a run that was cut off resumes from its last checkpoint. A failed step,
/// the timeout, the step budget or the loop cap fails the run with the step and the reason; saved variables come back
/// only from a run that succeeded. A caller's cancellation (the app closing) throws, leaving the run to resume.
/// </summary>
public static class ScriptEngine
{
    public const string StartStepId = "start";
    private static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    /// <summary>Runs <paramref name="request"/>. Holds no state between calls: concurrent runs of one document are independent.</summary>
    public static async Task<ScriptRunResult> RunAsync(ScriptRunRequest request, IScriptRunJournal journal, CancellationToken ct)
    {
        var clock = request.Clock ?? TimeProvider.System;
        using var timeout = new CancellationTokenSource(request.Timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var run = new Run(clock, request, journal, linked.Token, clock.GetUtcNow());
        try
        {
            return await run.ExecuteAsync();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return await run.FailAsync(run.CurrentStep, $"the run timed out after {ScriptValues.FormatDuration(request.Timeout)}");
        }
    }

    private sealed class Run : IScriptScope
    {
        private readonly TimeProvider _clock;
        private readonly ScriptRunRequest _req;
        private readonly IScriptRunJournal _journal;
        private readonly CancellationToken _ct;
        private readonly DateTimeOffset _began;
        private readonly ScriptDocument _doc;
        private readonly ScriptFiles _files;
        private readonly List<string> _log = [];
        private readonly Dictionary<string, ScriptType> _varTypes = new(StringComparer.Ordinal);

        // The state a checkpoint holds.
        private string? _next;
        private List<ScriptLoopFrame> _frames = [];
        private JsonObject _vars = [];
        private JsonObject _settings = [];
        private JsonNode? _input;
        private JsonObject _steps = [];
        private JsonObject? _outputs;
        private int _stepsRun;
        private bool _ended;
        private ScriptRunCheckpoint? _last;

        public string? CurrentStep { get; private set; }

        public Run(TimeProvider clock, ScriptRunRequest req, IScriptRunJournal journal, CancellationToken ct, DateTimeOffset began)
        {
            _clock = clock;
            _req = req;
            _journal = journal;
            _ct = ct;
            _began = began;
            _doc = req.Document;
            _files = new ScriptFiles(req.DataFolder, req.AllowedFolders,
                req.DryRun ? req.DryRunFolder ?? Path.Combine(req.DataFolder, "dry-run") : null);
        }

        private ScriptSchema? Find(string name) => BuiltInSchemas.Find(name) ?? _req.Schemas?.Find(name);

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
                if (_frames[i].As == name)
                {
                    var f = _frames[i];
                    value = f.Index < f.Items.Count ? f.Items[f.Index] : null;
                    return true;
                }
            value = null;
            return false;
        }

        public async Task<ScriptRunResult> ExecuteAsync()
        {
            foreach (var v in _doc.Variables)
                _varTypes[v.Name] = ScriptType.Parse(v.Type, Find, out _) ?? ScriptType.Any;

            if (_req.ResumeFrom is { } cp) Restore(cp);
            else if (await StartAsync() is { } failed) return failed;

            while (!_ended && !(_next is null && _frames.Count == 0))
            {
                _ct.ThrowIfCancellationRequested();
                var stepId = _next!;
                CurrentStep = stepId;
                if (Now - _began > _req.Timeout)
                    return await FailAsync(stepId, $"the run timed out after {ScriptValues.FormatDuration(_req.Timeout)}");
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
                    _stepsRun++;
                    _next = next;
                    Advance();
                    var record = new ScriptStepRecord(step.Id, step.Type, input, output, "succeeded", started, Now);
                    await Checkpoint(record);
                }
                catch (Exception e) when (e is ScriptValueException or ScriptExpressionException or ScriptStepException
                                              or IOException or UnauthorizedAccessException or ScriptActionException)
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
            return new ScriptRunResult(ScriptRunStatus.Succeeded, null, null, (JsonObject?)_outputs?.DeepClone() ?? [], saved)
            {
                Log = _log.ToList(),
                StepsRun = _stepsRun,
            };
        }

        public async Task<ScriptRunResult> FailAsync(string? stepId, string reason, string? type = null, JsonNode? input = null, DateTimeOffset? started = null)
        {
            var now = Now;
            if (stepId is not null && _last is not null)
            {
                var record = new ScriptStepRecord(stepId, type ?? _doc.Step(stepId)?.Type ?? "", input, null, "failed", started ?? now, now, reason);
                await _journal.StepCompletedAsync(record, _last);
            }
            var line = stepId is null ? $"run failed: {reason}" : $"run failed at step '{stepId}': {reason}";
            await LogAsync(line);
            return new ScriptRunResult(ScriptRunStatus.Failed, stepId, reason, null, null) { Log = _log.ToList(), StepsRun = _stepsRun };
        }

        // ---- start, checkpoints, loops --------------------------------------------------------------------------------

        private async Task<ScriptRunResult?> StartAsync()
        {
            var started = Now;
            await _journal.StepStartedAsync(StartStepId, StartStepId, _req.Input?.DeepClone());
            try
            {
                var inputType = ScriptChecker.InputType(_doc, Find);
                _input = ScriptValues.Convert(ScriptValues.FromNode(_req.Input), inputType, Find);

                foreach (var s in _doc.Settings)
                {
                    var type = ScriptType.Parse(s.Type, Find, out var error) ?? throw new ScriptStepException($"setting '{s.Name}': {error}");
                    var given = _req.Settings?[s.Name] ?? s.Default;
                    if (given is null) throw new ScriptStepException($"setting '{s.Name}' is not set");
                    try { _settings[s.Name] = ScriptValues.Convert(ScriptValues.FromNode(given), type, Find); }
                    catch (ScriptValueException e) { throw new ScriptStepException($"setting '{s.Name}': {e.Message}"); }
                }

                foreach (var v in _doc.Variables)
                {
                    var type = _varTypes[v.Name];
                    var value = v.IsSaved && _req.SavedVariables is { } saved && saved.ContainsKey(v.Name) ? saved[v.Name] : v.Initial;
                    try { _vars[v.Name] = ScriptValues.Convert(ScriptValues.FromNode(value), type, Find); }
                    catch (ScriptValueException e) { throw new ScriptStepException($"variable '{v.Name}': {e.Message}"); }
                }
            }
            catch (Exception e) when (e is ScriptStepException or ScriptValueException)
            {
                var now = Now;
                _last = Snapshot();
                await _journal.StepCompletedAsync(new ScriptStepRecord(StartStepId, StartStepId, _req.Input?.DeepClone(), null, "failed", started, now, e.Message), _last);
                await LogAsync($"run failed at the start: {e.Message}");
                return new ScriptRunResult(ScriptRunStatus.Failed, StartStepId, e.Message, null, null) { Log = _log.ToList() };
            }

            _next = _doc.Start;
            var output = new JsonObject { ["settings"] = _settings.DeepClone(), ["vars"] = _vars.DeepClone() };
            await Checkpoint(new ScriptStepRecord(StartStepId, StartStepId, _req.Input?.DeepClone(), output, "succeeded", started, Now));
            return null;
        }

        private async Task Checkpoint(ScriptStepRecord record)
        {
            _last = Snapshot();
            await _journal.StepCompletedAsync(record, _last);
        }

        private ScriptRunCheckpoint Snapshot() => new()
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
        };

        private void Restore(ScriptRunCheckpoint cp)
        {
            // Through JSON, so a checkpoint read back from storage and one handed over in memory behave the same.
            cp = ScriptRunCheckpoint.FromJson(cp.ToJson());
            _next = cp.Next;
            _frames = cp.Frames;
            _vars = cp.Vars;
            _settings = cp.Settings;
            _input = cp.Input;
            _steps = cp.Steps;
            _outputs = cp.Outputs;
            _stepsRun = cp.StepsRun;
            _ended = cp.Ended;
            _last = Snapshot();
            // A variable the script declares that the checkpoint lacks (it cannot, unless the script changed): its initial value.
            foreach (var v in _doc.Variables.Where(v => !_vars.ContainsKey(v.Name)))
                _vars[v.Name] = ScriptValues.Convert(ScriptValues.FromNode(v.Initial), _varTypes[v.Name], Find);
        }

        /// <summary>After a step: a null successor ends the innermost body, which moves its loop to the next item or past it.</summary>
        private void Advance()
        {
            while (_next is null && _frames.Count > 0)
            {
                var frame = _frames[^1];
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

        private ScriptEvaluator Evaluator() => new(this, _clock);

        /// <summary>The step's input as the run view shows it: its evaluated values.</summary>
        private JsonNode? Input(ScriptStep step)
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
                ScriptStepTypes.FileWriteAllText or ScriptStepTypes.FileAppendLine => Pick("path", "text"),
                ScriptStepTypes.FileReadAllText or ScriptStepTypes.FileReadLines or ScriptStepTypes.FileExists or ScriptStepTypes.FileDelete => Pick("path"),
                "log" => Pick("text"),
                "end" => step["outputs"] is { } o ? new JsonObject { ["outputs"] = ev.EvaluateValue(o) } : null,
                _ => null,
            };
        }

        private async Task<(JsonNode? Output, string? Next)> ExecuteStepAsync(ScriptStep step, JsonNode? input)
        {
            var next = step.Text("next");
            switch (step.Type)
            {
                case "set":
                {
                    var name = step.Text("var") ?? throw new ScriptStepException("set has no var");
                    var value = SetVar(name, input?["value"]);
                    return (value?.DeepClone(), next);
                }
                case "if":
                {
                    var result = ScriptCondition.Parse(step["when"]).Evaluate(Evaluator());
                    return (new JsonObject { ["result"] = result }, step.Text(result ? "then" : "else"));
                }
                case "switch":
                {
                    var key = ScriptValues.Format(ScriptValues.FromNode(input?["on"]));
                    if (step["cases"] is JsonObject cases && cases.TryGetPropertyValue(key, out var target))
                        return (new JsonObject { ["case"] = key }, target is JsonValue t && t.GetValueKind() == JsonValueKind.String ? t.GetValue<string>() : null);
                    return (new JsonObject { ["case"] = "default" }, step.Text("default"));
                }
                case "forEach":
                {
                    var items = ScriptValues.ToList(ScriptValues.FromNode(input?["items"]));
                    if (items.Count > _req.LoopCap)
                        throw new ScriptStepException($"forEach has {items.Count} items, over the loop cap of {_req.LoopCap}");
                    var name = step.Text("as") ?? throw new ScriptStepException("forEach has no as");
                    var body = step.Text("body") ?? throw new ScriptStepException("forEach has no body");
                    var output = new JsonObject { ["count"] = items.Count };
                    if (items.Count == 0) return (output, step.Text("then"));
                    _frames.Add(new ScriptLoopFrame { StepId = step.Id, As = name, Items = (JsonArray)items.DeepClone(), Index = 0 });
                    return (output, body);
                }
                case "integration":
                    return (await IntegrationAsync(step, (JsonObject)input!), next);
                case ScriptStepTypes.FileWriteAllText or ScriptStepTypes.FileAppendLine:
                {
                    var path = Text(input, "path");
                    var text = Text(input, "text");
                    var (target, real) = _files.ForWrite(path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (step.Type == ScriptStepTypes.FileWriteAllText) await File.WriteAllTextAsync(target, text, _ct);
                    else await File.AppendAllTextAsync(target, text + "\n", _ct);
                    if (_files.DryRun) await LogAsync($"would {(step.Type == ScriptStepTypes.FileWriteAllText ? "write" : "append a line to")} {real} (dry run: {target})");
                    return (new JsonObject { ["path"] = real }, next);
                }
                case ScriptStepTypes.FileReadAllText:
                {
                    var file = _files.ForRead(Text(input, "path"));
                    if (!File.Exists(file)) throw new ScriptStepException($"'{Text(input, "path")}' does not exist");
                    var text = await File.ReadAllTextAsync(file, _ct);
                    return (Result(step, JsonValue.Create(text)), next);
                }
                case ScriptStepTypes.FileReadLines:
                {
                    var file = _files.ForRead(Text(input, "path"));
                    if (!File.Exists(file)) throw new ScriptStepException($"'{Text(input, "path")}' does not exist");
                    var lines = await File.ReadAllLinesAsync(file, _ct);
                    return (Result(step, new JsonArray(lines.Select(l => (JsonNode?)l).ToArray())), next);
                }
                case ScriptStepTypes.FileExists:
                    return (Result(step, JsonValue.Create(File.Exists(_files.ForRead(Text(input, "path"))))), next);
                case ScriptStepTypes.FileDelete:
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
                case "log":
                    await LogAsync(Text(input, "text"));
                    return (null, next);
                case "end":
                {
                    if (input?["outputs"] is JsonObject outputs)
                    {
                        _outputs ??= [];
                        foreach (var (name, value) in outputs)
                        {
                            var declared = _doc.Outputs.FirstOrDefault(o => o.Name == name);
                            var type = declared is null ? ScriptType.Any : ScriptType.Parse(declared.Type, Find, out _) ?? ScriptType.Any;
                            _outputs[name] = ScriptValues.Convert(ScriptValues.FromNode(value), type, Find);
                        }
                    }
                    _ended = true;
                    _frames.Clear();
                    return (_outputs?.DeepClone(), null);
                }
            }
            throw new ScriptStepException($"'{step.Type}' is not a step type");
        }

        private static string Text(JsonNode? input, string name)
        {
            var value = ScriptValues.FromNode(input?[name]);
            if (value is JsonArray or JsonObject) throw new ScriptStepException($"'{name}' is {ScriptValues.Describe(value)}, not text");
            return ScriptValues.Format(value);
        }

        private JsonNode? SetVar(string name, JsonNode? value)
        {
            if (!_varTypes.TryGetValue(name, out var type)) throw new ScriptStepException($"variable '{name}' is not declared");
            JsonNode? converted;
            try { converted = ScriptValues.Convert(ScriptValues.FromNode(value), type, Find); }
            catch (ScriptValueException e) { throw new ScriptStepException($"variable '{name}' ({type}): {e.Message}"); }
            _vars[name] = converted;
            return converted;
        }

        /// <summary>A step's result: kept as <c>steps.&lt;id&gt;</c> and, with <c>into</c>, in that variable.</summary>
        private JsonNode? Result(ScriptStep step, JsonNode? value)
        {
            var stored = step.Text("into") is { } into ? SetVar(into, value) : value;
            _steps[step.Id] = stored?.DeepClone();
            return stored?.DeepClone();
        }

        private async Task<JsonNode?> IntegrationAsync(ScriptStep step, JsonObject input)
        {
            var use = step.Text("use") ?? throw new ScriptStepException("integration has no use");
            var action = step.Text("action") ?? throw new ScriptStepException("integration has no action");
            var requirement = _doc.Requires.FirstOrDefault(r => r.Name == use) ?? throw new ScriptStepException($"'{use}' is not in requires");
            var args = input["args"] as JsonObject ?? [];
            var call = new ScriptActionCall(use, requirement.Kind, action, (JsonObject)args.DeepClone(), _req.DryRun && _req.Catalog is null);

            if (_req.DryRun && _req.Catalog?.IsChanging(requirement.Kind, action) == true)
            {
                await LogAsync($"would {action} on {use} with {args.ToJsonString(Compact)}");
                _steps[step.Id] = null;
                return new JsonObject { ["dryRun"] = true, ["wouldCall"] = action };
            }

            JsonNode? result;
            try { result = await _req.Actions.InvokeAsync(call, _ct); }
            catch (Exception e) when (e is not OperationCanceledException and not ScriptActionException)
            {
                throw new ScriptActionException($"{use} {action}: {e.Message}");
            }
            return Result(step, result);
        }
    }
}

/// <summary>An integration action that failed (refused, needs approval, the service failed); the step fails with its message.</summary>
public sealed class ScriptActionException(string message) : Exception(message);
