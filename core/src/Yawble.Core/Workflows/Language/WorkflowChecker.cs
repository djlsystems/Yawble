using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Yawble.Core.Workflows.Language;

/// <summary>The actions each integration kind offers (the integrations fill it); a workflow's <c>requires</c> are checked against it.</summary>
public interface IWorkflowActionCatalog
{
    bool HasAction(string kind, string action);

    /// <summary>Whether the action changes something (not a GET, not a mail read): a dry run does not perform it.</summary>
    bool IsChanging(string kind, string action);
}

/// <summary>
/// A catalog that also knows its actions' arguments and results (the built-in kinds, such as email). The check then refuses an argument the
/// action does not take, a missing required one or one of the wrong type, and types the step's result (its <c>into</c> and <c>steps.&lt;id&gt;</c>).
/// </summary>
public interface IWorkflowActionSignatures
{
    /// <summary>The action's signature, or null when this catalog does not describe it (then any args are accepted and the result is any).</summary>
    WorkflowActionSignature? Signature(string kind, string action);
}

/// <summary>
/// An action's arguments as a JSON Schema object (<c>properties</c>, <c>required</c>; no other argument is accepted) and its result as a
/// workflow type (<c>list&lt;EmailMessage&gt;</c>, <c>EmailMessage</c>).
/// </summary>
public sealed record WorkflowActionSignature(JsonObject Args, string Result);

/// <summary>One problem the check found: the step it is in (null for the workflow's declarations), a code and a message.</summary>
public sealed record WorkflowCheckIssue(string? StepId, string Code, string Message);

/// <summary>
/// A use of a schema's field by a workflow: <see cref="FieldPath"/> is the field (<c>from</c>, <c>items.name</c> for a field of
/// an inline object), or empty when the workflow uses the schema as a whole (a variable typed <c>list&lt;EmailMessage&gt;</c>).
/// <see cref="StepId"/> is null for a declaration (a setting, variable, output or the input).
/// </summary>
public sealed record WorkflowSchemaReference(string Schema, string FieldPath, string? StepId);

public sealed record WorkflowCheckResult(IReadOnlyList<WorkflowCheckIssue> Issues, IReadOnlyList<WorkflowSchemaReference> SchemaReferences)
{
    public bool Ok => Issues.Count == 0;
}

/// <summary>
/// The file step types. Code names them through these constants: a dotted name quoted in a switch reads as an RPC
/// call to the audit classification, and these are step types, not calls.
/// </summary>
public static class WorkflowStepTypes
{
    public const string FileWriteAllText = "file.writeAllText";
    public const string FileAppendLine = "file.appendLine";
    public const string FileReadAllText = "file.readAllText";
    public const string FileReadLines = "file.readLines";
    public const string FileExists = "file.exists";
    public const string FileDelete = "file.delete";
    public const string FileMove = "file.move";
    public const string FileCopy = "file.copy";
    public const string FileList = "file.list";
}

/// <summary>The codes a <see cref="WorkflowCheckIssue"/> carries.</summary>
public static class WorkflowCheckCodes
{
    public const string InvalidDocument = "invalid-document";
    public const string Duplicate = "duplicate";
    public const string UnknownTrigger = "unknown-trigger";
    public const string UnknownType = "unknown-type";
    public const string UnknownAction = "unknown-action";
    public const string UnknownRequirement = "unknown-requirement";
    public const string UnknownStep = "unknown-step";
    public const string UnknownStepType = "unknown-step-type";
    public const string Unreachable = "unreachable";
    public const string LoopStructure = "loop-structure";
    public const string MissingField = "missing-field";
    public const string BadExpression = "bad-expression";
    public const string UnknownReference = "unknown-reference";
    public const string TypeMismatch = "type-mismatch";
    /// <summary>An integration step passes an argument its action does not take (a catalog that knows the action's signature).</summary>
    public const string UnknownArgument = "unknown-argument";
    /// <summary>A step's option is out of range or does not go with the others (a tell's timeout over 7 days, a reply without wait).</summary>
    public const string BadValue = "bad-value";
}

/// <summary>
/// Checks a workflow before it can run: every reference resolves, every type matches, every step is reachable from
/// <c>start</c>, and every <c>requires</c> action exists in the catalog for its kind.
/// </summary>
public static class WorkflowChecker
{
    public static readonly IReadOnlyList<string> StepTypes =
    [
        "set", "if", "switch", "forEach", WorkflowLoop.StepType, "integration", "log", "end", WorkflowTell.StepType, WorkflowCode.StepType,
        WorkflowStepTypes.FileWriteAllText, WorkflowStepTypes.FileAppendLine, WorkflowStepTypes.FileReadAllText, WorkflowStepTypes.FileReadLines, WorkflowStepTypes.FileExists, WorkflowStepTypes.FileDelete,
        WorkflowStepTypes.FileMove, WorkflowStepTypes.FileCopy, WorkflowStepTypes.FileList,
    ];

    private static readonly string[] RootNames = ["input", "settings", "vars", "steps"];

    /// <summary>What file.list gives: a list of { path, name, size, modifiedAt }, one per file, sorted by path.</summary>
    public static WorkflowType FileListType { get; } = WorkflowType.ListOf(WorkflowType.ObjectOf(null, JsonNode.Parse("""
        { "type": "object", "properties": {
            "path": { "type": "string", "description": "The file's full path." },
            "name": { "type": "string", "description": "The file's name, e.g. IMG_0001.jpg." },
            "size": { "type": "integer", "description": "Its size in bytes." },
            "modifiedAt": { "type": "string", "format": "date-time", "description": "When it was last written (UTC)." } },
          "required": ["path", "name", "size", "modifiedAt"] }
        """)!.AsObject()));

    /// <summary>Parses and checks a document; a document that does not parse gives one <c>invalid-document</c> issue.</summary>
    public static WorkflowCheckResult Check(string json, IWorkflowSchemaLookup schemas, IWorkflowActionCatalog catalog)
    {
        WorkflowDocument doc;
        try { doc = WorkflowDocument.Parse(json); }
        catch (WorkflowDocumentException e) { return new([new(null, WorkflowCheckCodes.InvalidDocument, e.Message)], []); }
        return Check(doc, schemas, catalog);
    }

    public static WorkflowCheckResult Check(WorkflowDocument document, IWorkflowSchemaLookup schemas, IWorkflowActionCatalog catalog) =>
        new Run(document, schemas, catalog).Check();

    /// <summary>The type of <c>input</c>: the declared one, or the payload of the triggers the workflow accepts.</summary>
    public static WorkflowType InputType(WorkflowDocument document, Func<string, WorkflowSchema?> find)
    {
        if (document.Input is { } declared) return WorkflowType.Parse(declared, find, out _) ?? WorkflowType.Any;
        var accepts = document.Trigger.Accepts;
        if (accepts.Count == 0) return WorkflowType.Any;
        if (accepts.All(a => a is "every" or "cron" or "once")) return WorkflowType.FromSchema(BuiltInSchemas.TriggerFired);
        if (accepts.All(a => a == "folder")) return WorkflowType.FromSchema(BuiltInSchemas.FolderChange);
        if (accepts.All(a => a == "event")) return WorkflowType.FromSchema(BuiltInSchemas.EventItem);
        if (accepts.All(a => a == "tell"))
            return WorkflowType.ObjectOf(null, JsonNode.Parse("""{ "type": "object", "properties": { "text": { "type": "string" } } }""")!.AsObject());
        return WorkflowType.Any;
    }

    private sealed class Run(WorkflowDocument doc, IWorkflowSchemaLookup schemas, IWorkflowActionCatalog catalog)
    {
        private readonly List<WorkflowCheckIssue> _issues = [];
        private readonly List<WorkflowSchemaReference> _refs = [];
        private readonly Dictionary<string, WorkflowType> _settings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, WorkflowType> _vars = new(StringComparer.Ordinal);
        private readonly Dictionary<string, WorkflowStep> _steps = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ImmutableList<string>> _context = new(StringComparer.Ordinal);
        private readonly Dictionary<string, WorkflowType> _elementTypes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _elementTypeInProgress = new(StringComparer.Ordinal);
        private WorkflowType _input = WorkflowType.Any;
        private string? _currentStep;

        private WorkflowSchema? Find(string name) => BuiltInSchemas.Find(name) ?? schemas.Find(name);

        private void Issue(string? step, string code, string message) => _issues.Add(new(step, code, message));

        public WorkflowCheckResult Check()
        {
            Declarations();
            Graph();
            foreach (var step in doc.Steps)
                if (_context.ContainsKey(step.Id) && ReferenceEquals(_steps.GetValueOrDefault(step.Id), step))
                    CheckStep(step);
            var refs = _refs.Distinct().ToList();
            return new WorkflowCheckResult(_issues.Distinct().ToList(), refs);
        }

        // ---- declarations ---------------------------------------------------------------------------------------------

        private WorkflowType? DeclaredType(string text, string where)
        {
            var type = WorkflowType.Parse(text, Find, out var error);
            if (type is null) { Issue(null, WorkflowCheckCodes.UnknownType, $"{where}: {error}"); return null; }
            ReferenceTypeSchemas(type, null);
            return type;
        }

        private void ReferenceTypeSchemas(WorkflowType type, string? stepId)
        {
            if (type.Kind == WorkflowTypeKind.List) ReferenceTypeSchemas(type.Element!, stepId);
            else if (type.Kind is WorkflowTypeKind.Object or WorkflowTypeKind.Enum && type.Name is { } name) _refs.Add(new(name, "", stepId));
        }

        private void Declarations()
        {
            if (doc.Format != WorkflowDocument.CurrentFormat) Issue(null, WorkflowCheckCodes.InvalidDocument, $"format is not {WorkflowDocument.CurrentFormat}");
            foreach (var kind in doc.Trigger.Accepts.Where(k => !WorkflowTriggerSpec.Kinds.Contains(k)))
                Issue(null, WorkflowCheckCodes.UnknownTrigger, $"trigger kind '{kind}' is not one of {string.Join(", ", WorkflowTriggerSpec.Kinds)}");

            if (doc.Input is { } input) _input = DeclaredType(input, "input") ?? WorkflowType.Any;
            else
            {
                _input = InputType(doc, Find);
                ReferenceTypeSchemas(_input, null);
            }

            Duplicates(doc.Settings.Select(s => s.Name), "setting");
            Duplicates(doc.Variables.Select(s => s.Name), "variable");
            Duplicates(doc.Requires.Select(s => s.Name), "requirement");
            Duplicates(doc.Outputs.Select(s => s.Name), "output");
            Duplicates(doc.Steps.Select(s => s.Id), "step id");

            foreach (var s in doc.Settings)
            {
                var type = DeclaredType(s.Type, $"setting '{s.Name}'");
                if (type is null) continue;
                _settings.TryAdd(s.Name, type);
                LiteralFits(s.Default, type, null, $"the default of setting '{s.Name}'");
            }
            foreach (var v in doc.Variables)
            {
                if (v.Scope is not ("run" or "saved"))
                    Issue(null, WorkflowCheckCodes.InvalidDocument, $"variable '{v.Name}': scope '{v.Scope}' is not run or saved");
                var type = DeclaredType(v.Type, $"variable '{v.Name}'");
                if (type is null) { _vars.TryAdd(v.Name, WorkflowType.Any); continue; }
                _vars.TryAdd(v.Name, type);
                LiteralFits(v.Initial, type, null, $"the initial value of variable '{v.Name}'");
            }
            // A TellResult variable that only tells with one reply schema fill: its data is typed by that schema (vars.x.data.field).
            foreach (var (name, type) in _vars.ToList())
            {
                if (type.Kind != WorkflowTypeKind.Object || type.Name != BuiltInSchemas.TellResult.Name) continue;
                var replies = doc.Steps.Where(s => s.Type == WorkflowTell.StepType && s.Text("into") == name).Select(s => s.Text("reply")).Distinct().ToList();
                if (replies is [{ Length: > 0 } reply] && Find(reply) is not null) _vars[name] = WorkflowTell.ResultType(reply);
            }
            foreach (var o in doc.Outputs) DeclaredType(o.Type, $"output '{o.Name}'");
            foreach (var r in doc.Requires)
            {
                if (r.Actions.Count == 0) Issue(null, WorkflowCheckCodes.MissingField, $"requirement '{r.Name}' lists no actions");
                foreach (var a in r.Actions.Where(a => !catalog.HasAction(r.Kind, a)))
                    Issue(null, WorkflowCheckCodes.UnknownAction, $"requirement '{r.Name}': kind '{r.Kind}' has no action '{a}'");
            }
        }

        private void Duplicates(IEnumerable<string> names, string what)
        {
            foreach (var g in names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1))
                Issue(null, WorkflowCheckCodes.Duplicate, $"{what} '{g.Key}' is declared {g.Count()} times");
        }

        /// <summary>A literal (a default, an initial value, a template-free value) must convert to its declared type.</summary>
        private void LiteralFits(JsonNode? value, WorkflowType type, string? stepId, string what)
        {
            if (value is null) return;
            if (ContainsTemplate(value)) return;
            try { WorkflowValues.Convert(WorkflowValues.FromNode(value), type, Find); }
            catch (WorkflowValueException e) { Issue(stepId, WorkflowCheckCodes.TypeMismatch, $"{what}: {e.Message}"); }
        }

        private static bool ContainsTemplate(JsonNode? node) => node switch
        {
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>().Contains('{') || v.GetValue<string>().Contains('}'),
            JsonObject o => o.Any(p => ContainsTemplate(p.Value)),
            JsonArray a => a.Any(ContainsTemplate),
            _ => false,
        };

        // ---- the graph ------------------------------------------------------------------------------------------------

        private void Graph()
        {
            foreach (var s in doc.Steps) _steps.TryAdd(s.Id, s);

            foreach (var s in doc.Steps)
            {
                if (!StepTypes.Contains(s.Type)) Issue(s.Id, WorkflowCheckCodes.UnknownStepType, $"'{s.Type}' is not a step type ({string.Join(", ", StepTypes)})");
                foreach (var (prop, target) in s.Successors())
                    if (target is not null && !_steps.ContainsKey(target))
                        Issue(s.Id, WorkflowCheckCodes.UnknownStep, $"{prop} names step '{target}', which does not exist");
                if (WorkflowLoop.IsLoop(s.Type) && s.Text("body") is null)
                    Issue(s.Id, WorkflowCheckCodes.MissingField, $"a {s.Type} needs a body");
            }

            if (!_steps.ContainsKey(doc.Start))
            {
                Issue(null, WorkflowCheckCodes.UnknownStep, $"start names step '{doc.Start}', which does not exist");
                return;
            }

            // Breadth first from start; each step's context is the loops (forEach and while ids, outermost first) it runs inside.
            var queue = new Queue<string>();
            _context[doc.Start] = [];
            queue.Enqueue(doc.Start);
            var conflicts = new HashSet<string>();
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                var step = _steps[id];
                var ctx = _context[id];
                foreach (var (prop, target) in step.Successors())
                {
                    if (target is null || !_steps.ContainsKey(target)) continue;
                    var next = WorkflowLoop.IsLoop(step.Type) && prop == "body" ? ctx.Add(id) : ctx;
                    if (next.Contains(target))
                    {
                        if (conflicts.Add(target)) Issue(id, WorkflowCheckCodes.LoopStructure, $"{prop} goes back to the loop '{target}' it is inside (a body ends with null)");
                        continue;
                    }
                    if (_context.TryGetValue(target, out var seen))
                    {
                        if (!seen.SequenceEqual(next) && conflicts.Add(target))
                            Issue(target, WorkflowCheckCodes.LoopStructure,
                                $"step '{target}' is reached both inside and outside {Describe(seen.Count > next.Count ? seen : next)}; a loop's body must end with null");
                        continue;
                    }
                    _context[target] = next;
                    queue.Enqueue(target);
                }
            }

            foreach (var s in doc.Steps.Where(s => !_context.ContainsKey(s.Id)).DistinctBy(s => s.Id))
                Issue(s.Id, WorkflowCheckCodes.Unreachable, $"step '{s.Id}' cannot be reached from start");
        }

        private static string Describe(ImmutableList<string> ctx) => ctx.Count == 0 ? "any loop" : $"the loop '{ctx[^1]}'";

        // ---- steps ----------------------------------------------------------------------------------------------------

        private void CheckStep(WorkflowStep step)
        {
            _currentStep = step.Id;
            var ctx = _context[step.Id];
            switch (step.Type)
            {
                case "set":
                {
                    var name = Required(step, "var");
                    if (!step.Has("value")) Issue(step.Id, WorkflowCheckCodes.MissingField, "set needs a value");
                    var valueType = ValueType(step["value"], ctx);
                    if (name is null) break;
                    if (!_vars.TryGetValue(name, out var varType))
                    {
                        Issue(step.Id, WorkflowCheckCodes.UnknownReference, $"variable '{name}' is not declared");
                        break;
                    }
                    Assign(varType, valueType, step["value"], $"set '{name}'");
                    break;
                }
                case "if":
                    if (!step.Has("when")) Issue(step.Id, WorkflowCheckCodes.MissingField, "if needs a when");
                    else Condition(step["when"], ctx);
                    break;
                case "switch":
                {
                    if (!step.Has("on")) { Issue(step.Id, WorkflowCheckCodes.MissingField, "switch needs an on"); break; }
                    var on = ValueType(step["on"], ctx);
                    if (step["cases"] is not JsonObject cases) { Issue(step.Id, WorkflowCheckCodes.MissingField, "switch needs cases"); break; }
                    if (on.Kind == WorkflowTypeKind.Enum)
                        foreach (var key in cases.Select(c => c.Key).Where(k => !on.EnumValues.Contains(k)))
                            Issue(step.Id, WorkflowCheckCodes.TypeMismatch, $"case '{key}' is not a value of {on}");
                    break;
                }
                case "forEach":
                {
                    if (!step.Has("items")) Issue(step.Id, WorkflowCheckCodes.MissingField, "forEach needs items");
                    var name = Required(step, "as");
                    if (name is not null)
                    {
                        if (!WorkflowType.IsName(name) || RootNames.Contains(name) || WorkflowExpressionParser.Functions.ContainsKey(name) || name is "true" or "false" or "null")
                            Issue(step.Id, WorkflowCheckCodes.InvalidDocument, $"'{name}' cannot name a loop's item");
                        else if (ctx.Any(outer => _steps[outer].Text("as") == name))
                            Issue(step.Id, WorkflowCheckCodes.Duplicate, $"'{name}' is already the item of an enclosing loop");
                    }
                    ElementType(step);
                    break;
                }
                case "integration":
                {
                    var use = Required(step, "use");
                    var action = Required(step, "action");
                    if (step["args"] is { } args)
                    {
                        if (args is not JsonObject) Issue(step.Id, WorkflowCheckCodes.InvalidDocument, "args is an object");
                        ValueType(args, ctx);
                    }
                    if (use is not null)
                    {
                        var req = doc.Requires.FirstOrDefault(r => r.Name == use);
                        if (req is null) Issue(step.Id, WorkflowCheckCodes.UnknownRequirement,
                            $"'{use}' is not in requires: name an integration as Settings > Integrations shows it, or list '{use}' in requires with its kind");
                        else if (action is not null && !req.Actions.Contains(action))
                            Issue(step.Id, WorkflowCheckCodes.UnknownAction, $"action '{action}' is not listed in requires '{use}'");
                    }
                    var signature = SignatureOf(step);
                    if (signature is not null) Arguments(step, action!, signature, ctx);
                    Into(step, signature is null ? WorkflowType.Any : ResultType(signature));
                    break;
                }
                case WorkflowStepTypes.FileWriteAllText or WorkflowStepTypes.FileAppendLine:
                    Text(step, "path", ctx);
                    Text(step, "text", ctx);
                    break;
                case WorkflowStepTypes.FileReadAllText:
                    Text(step, "path", ctx);
                    Into(step, WorkflowType.String);
                    break;
                case WorkflowStepTypes.FileReadLines:
                    Text(step, "path", ctx);
                    Into(step, WorkflowType.ListOf(WorkflowType.String));
                    break;
                case WorkflowStepTypes.FileExists:
                    Text(step, "path", ctx);
                    Into(step, WorkflowType.Bool);
                    break;
                case WorkflowStepTypes.FileDelete:
                    Text(step, "path", ctx);
                    break;
                case WorkflowStepTypes.FileMove or WorkflowStepTypes.FileCopy:
                    Text(step, "path", ctx);
                    Text(step, "to", ctx);
                    Flag(step, "overwrite", ctx);
                    Into(step, WorkflowType.String);
                    break;
                case WorkflowStepTypes.FileList:
                    Text(step, "path", ctx);
                    if (step.Has("pattern")) Text(step, "pattern", ctx);
                    Flag(step, "recursive", ctx);
                    Into(step, FileListType);
                    break;
                case "log":
                    Text(step, "text", ctx);
                    break;
                case WorkflowTell.StepType:
                    Tell(step, ctx);
                    break;
                case WorkflowLoop.StepType:
                    While(step, ctx);
                    break;
                case WorkflowCode.StepType:
                    Code(step, ctx);
                    break;
                case "end":
                    if (step["outputs"] is null) break;
                    if (step["outputs"] is not JsonObject outputs) { Issue(step.Id, WorkflowCheckCodes.InvalidDocument, "outputs is an object"); break; }
                    foreach (var (name, value) in outputs)
                    {
                        var type = ValueType(value, ctx);
                        if (doc.Outputs.Count == 0) continue;
                        var declared = doc.Outputs.FirstOrDefault(o => o.Name == name);
                        if (declared is null) { Issue(step.Id, WorkflowCheckCodes.UnknownReference, $"output '{name}' is not declared"); continue; }
                        if (WorkflowType.Parse(declared.Type, Find, out _) is { } t) Assign(t, type, value, $"output '{name}'");
                    }
                    break;
            }
            _currentStep = null;
        }

        /// <summary>
        /// A tell: member and team are text (optional), text is required, wait is true or false, timeout and reply go only with wait
        /// (timeout a duration up to 7 days, reply a schema that exists), and into takes a TellResult.
        /// </summary>
        private void Tell(WorkflowStep step, ImmutableList<string> ctx)
        {
            foreach (var optional in new[] { "member", "team" })
                if (step.Has(optional))
                {
                    if (step[optional] is not JsonValue v || v.GetValueKind() != JsonValueKind.String || v.GetValue<string>().Trim().Length == 0)
                        Issue(step.Id, WorkflowCheckCodes.InvalidDocument, $"tell's '{optional}' is a name (text)");
                    else Text(step, optional, ctx);
                }
            Text(step, "text", ctx);

            var wait = false;
            if (step.Has("wait"))
            {
                if (step["wait"] is JsonValue w && w.GetValueKind() is JsonValueKind.True or JsonValueKind.False) wait = w.GetValue<bool>();
                else Issue(step.Id, WorkflowCheckCodes.InvalidDocument, "tell's 'wait' is true or false");
            }

            if (step.Has("timeout"))
            {
                if (!wait) Issue(step.Id, WorkflowCheckCodes.BadValue, "a timeout goes with wait: true (a tell that does not wait has nothing to time out)");
                if (!WorkflowTell.TryParseTimeout(step["timeout"], out var timeout))
                    Issue(step.Id, WorkflowCheckCodes.TypeMismatch, $"tell's timeout '{step["timeout"]?.ToJsonString()}' is not a duration (e.g. 30m, 4h, 2d)");
                else if (timeout <= TimeSpan.Zero) Issue(step.Id, WorkflowCheckCodes.BadValue, "tell's timeout must be more than zero");
                else if (timeout > WorkflowTell.MaxTimeout)
                    Issue(step.Id, WorkflowCheckCodes.BadValue, $"tell's timeout is {WorkflowValues.FormatDuration(timeout)}; the most is 7 days");
            }

            if (step.Has("reply"))
            {
                if (!wait) Issue(step.Id, WorkflowCheckCodes.BadValue, "a reply goes with wait: true (a tell that does not wait gets no reply)");
                if (step.Text("reply") is not { Length: > 0 } reply) Issue(step.Id, WorkflowCheckCodes.InvalidDocument, "tell's 'reply' names a schema");
                else if (Find(reply) is null) Issue(step.Id, WorkflowCheckCodes.UnknownType, $"reply: no schema named '{reply}'");
                else _refs.Add(new(reply, "", step.Id));
            }

            Into(step, TellResultOf(step));
        }

        /// <summary>The tell's result type: TellResult with data typed by its reply schema, when that schema exists.</summary>
        private WorkflowType TellResultOf(WorkflowStep tell) =>
            WorkflowTell.ResultType(tell.Text("reply") is { Length: > 0 } reply && Find(reply) is not null ? reply : null);

        /// <summary>A while: a when (a condition, as if has; body and then were checked with the graph) and max a whole number from 1 to 10,000.</summary>
        private void While(WorkflowStep step, ImmutableList<string> ctx)
        {
            if (!step.Has("when")) Issue(step.Id, WorkflowCheckCodes.MissingField, "while needs a when");
            else Condition(step["when"], ctx);
            if (step["max"] is null) return;
            if (step["max"] is not JsonValue m || m.GetValueKind() != JsonValueKind.Number || WorkflowValues.FromNode(m) is not long max)
            {
                Issue(step.Id, WorkflowCheckCodes.InvalidDocument, "while's 'max' is a whole number");
                return;
            }
            if (max < 1 || max > WorkflowLoop.MaxLimit)
                Issue(step.Id, WorkflowCheckCodes.BadValue, $"while's max is {max}; it is from 1 to {WorkflowLoop.MaxLimit.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// A code step: inputs an object of named values (each typed from its expression), code a function body that parses
        /// (a syntax error is a problem with its line and column), returns a type that exists (default any), typed into <c>into</c>.
        /// </summary>
        private void Code(WorkflowStep step, ImmutableList<string> ctx)
        {
            if (step["inputs"] is { } inputs)
            {
                if (inputs is not JsonObject o) Issue(step.Id, WorkflowCheckCodes.InvalidDocument, "inputs is an object of named values");
                else
                    foreach (var (name, value) in o)
                    {
                        if (!WorkflowType.IsName(name)) Issue(step.Id, WorkflowCheckCodes.InvalidDocument, $"'{name}' cannot name an input (letters, digits and _)");
                        ValueType(value, ctx);
                    }
            }

            if (step["code"] is not JsonValue c || c.GetValueKind() != JsonValueKind.String || c.GetValue<string>().Trim().Length == 0)
                Issue(step.Id, WorkflowCheckCodes.MissingField, "code needs 'code' (the body of a JavaScript function)");
            else if (WorkflowCode.Parse(c.GetValue<string>()) is { } error)
                Issue(step.Id, WorkflowCheckCodes.BadExpression, $"the code does not parse: {error.Message} (line {error.Line}, column {error.Column})");

            var returns = WorkflowType.Any;
            if (step.Has("returns"))
            {
                if (step.Text("returns") is not { Length: > 0 } text) Issue(step.Id, WorkflowCheckCodes.InvalidDocument, "returns names a type or schema");
                else if (WorkflowType.Parse(text, Find, out var why) is { } t)
                {
                    returns = t;
                    ReferenceTypeSchemas(t, step.Id);
                }
                else Issue(step.Id, WorkflowCheckCodes.UnknownType, $"returns: {why}");
            }
            Into(step, returns);
        }

        /// <summary>A code step's result type: its returns (any when not given or not a type).</summary>
        private WorkflowType ReturnsOf(WorkflowStep code) =>
            code.Text("returns") is { Length: > 0 } text ? WorkflowType.Parse(text, Find, out _) ?? WorkflowType.Any : WorkflowType.Any;

        private string? Required(WorkflowStep step, string property)
        {
            var value = step.Text(property);
            if (string.IsNullOrEmpty(value)) Issue(step.Id, WorkflowCheckCodes.MissingField, $"{step.Type} needs '{property}'");
            return value;
        }

        private void Text(WorkflowStep step, string property, ImmutableList<string> ctx)
        {
            if (!step.Has(property)) { Issue(step.Id, WorkflowCheckCodes.MissingField, $"{step.Type} needs '{property}'"); return; }
            var type = ValueType(step[property], ctx);
            if (type.Kind is WorkflowTypeKind.List or WorkflowTypeKind.Object)
                Issue(step.Id, WorkflowCheckCodes.TypeMismatch, $"'{property}' is {type}, not text");
        }

        /// <summary>An optional true/false option (file.move's overwrite, file.list's recursive): a bool, or an expression that can be one.</summary>
        private void Flag(WorkflowStep step, string property, ImmutableList<string> ctx)
        {
            if (!step.Has(property)) return;
            var type = ValueType(step[property], ctx);
            if (type.Kind is not (WorkflowTypeKind.Bool or WorkflowTypeKind.Any or WorkflowTypeKind.Null))
                Issue(step.Id, WorkflowCheckCodes.TypeMismatch, $"'{property}' is {type}, not true or false");
        }

        /// <summary>The integration step's action signature, when its requirement's kind is described by the catalog.</summary>
        private WorkflowActionSignature? SignatureOf(WorkflowStep step)
        {
            if (catalog is not IWorkflowActionSignatures sigs || step.Text("use") is not { } use || step.Text("action") is not { } action) return null;
            var req = doc.Requires.FirstOrDefault(r => r.Name == use);
            return req is null || !req.Actions.Contains(action) ? null : sigs.Signature(req.Kind, action);
        }

        private WorkflowType ResultType(WorkflowActionSignature signature) => WorkflowType.Parse(signature.Result, Find, out _) ?? WorkflowType.Any;

        /// <summary>The step's args against the action's: no unknown one, every required one, each of its type.</summary>
        private void Arguments(WorkflowStep step, string action, WorkflowActionSignature signature, ImmutableList<string> ctx)
        {
            var args = step["args"] as JsonObject ?? [];
            var props = signature.Args["properties"] as JsonObject ?? [];
            var required = (signature.Args["required"] as JsonArray)?.Select(r => (string?)r).OfType<string>() ?? [];
            foreach (var r in required.Where(r => !args.ContainsKey(r)))
                Issue(step.Id, WorkflowCheckCodes.MissingField, $"{action} needs the argument '{r}'");
            foreach (var (name, value) in args)
            {
                if (props[name] is not JsonObject schema)
                {
                    Issue(step.Id, WorkflowCheckCodes.UnknownArgument,
                        $"{action} has no argument '{name}' (it takes {(props.Count == 0 ? "none" : string.Join(", ", props.Select(p => p.Key)))})");
                    continue;
                }
                var type = WorkflowType.FromJsonSchema(schema, Find);
                var given = ValueType(value, ctx);
                if (!WorkflowType.IsAssignable(type, given))
                    Issue(step.Id, WorkflowCheckCodes.TypeMismatch, $"{action}'s argument '{name}' is {type}, not {given}");
                else LiteralFits(value, type, step.Id, $"{action}'s argument '{name}'");
            }
        }

        private void Into(WorkflowStep step, WorkflowType result)
        {
            if (step["into"] is null) return;
            var name = step.Text("into");
            if (name is null) { Issue(step.Id, WorkflowCheckCodes.InvalidDocument, "into names a variable"); return; }
            if (!_vars.TryGetValue(name, out var type)) { Issue(step.Id, WorkflowCheckCodes.UnknownReference, $"variable '{name}' is not declared"); return; }
            if (!WorkflowType.IsAssignable(type, result))
                Issue(step.Id, WorkflowCheckCodes.TypeMismatch, $"the result is {result}; variable '{name}' is {type}");
        }

        private void Assign(WorkflowType target, WorkflowType source, JsonNode? literal, string what)
        {
            if (!WorkflowType.IsAssignable(target, source))
                Issue(_currentStep, WorkflowCheckCodes.TypeMismatch, $"{what}: the value is {source}, the variable is {target}");
            else LiteralFits(literal, target, _currentStep, what);
        }

        private WorkflowType ElementType(WorkflowStep forEach)
        {
            if (_elementTypes.TryGetValue(forEach.Id, out var known)) return known;
            if (!_elementTypeInProgress.Add(forEach.Id)) return WorkflowType.Any;
            var saved = _currentStep;
            _currentStep = forEach.Id;
            var items = _context.TryGetValue(forEach.Id, out var ctx) ? ValueType(forEach["items"], ctx) : WorkflowType.Any;
            _currentStep = saved;
            var element = items.Kind switch
            {
                WorkflowTypeKind.List => items.Element!,
                WorkflowTypeKind.Any or WorkflowTypeKind.Null => WorkflowType.Any,
                _ => Mismatch(forEach.Id, $"items is {items}, not a list"),
            };
            _elementTypes[forEach.Id] = element;
            return element;
        }

        private WorkflowType Mismatch(string? stepId, string message)
        {
            Issue(stepId, WorkflowCheckCodes.TypeMismatch, message);
            return WorkflowType.Any;
        }

        // ---- values, conditions and expressions -----------------------------------------------------------------------

        private WorkflowType ValueType(JsonNode? node, ImmutableList<string> ctx)
        {
            switch (node)
            {
                case null: return WorkflowType.Null;
                case JsonObject o:
                    foreach (var p in o) ValueType(p.Value, ctx);
                    return WorkflowType.ObjectOf(null, null);
                case JsonArray a:
                {
                    var types = a.Select(i => ValueType(i, ctx)).ToList();
                    return WorkflowType.ListOf(types.Count > 0 && types.All(t => t.Kind == types[0].Kind && t.Kind is not (WorkflowTypeKind.Object or WorkflowTypeKind.List or WorkflowTypeKind.Enum)) ? types[0] : WorkflowType.Any);
                }
                case JsonValue v:
                    switch (v.GetValueKind())
                    {
                        case JsonValueKind.String:
                        {
                            WorkflowTemplate template;
                            try { template = WorkflowTemplate.Parse(v.GetValue<string>()); }
                            catch (WorkflowExpressionException e) { Issue(_currentStep, WorkflowCheckCodes.BadExpression, e.Message); return WorkflowType.Any; }
                            if (template.IsSingleExpression) return Infer((WorkflowExpr)template.Parts[0], ctx);
                            foreach (var e in template.Expressions) Infer(e, ctx);
                            return WorkflowType.String;
                        }
                        case JsonValueKind.True or JsonValueKind.False: return WorkflowType.Bool;
                        case JsonValueKind.Number: return WorkflowValues.FromNode(v) is long ? WorkflowType.Int : WorkflowType.Float;
                        default: return WorkflowType.Null;
                    }
                default: return WorkflowType.Any;
            }
        }

        private void Condition(JsonNode? node, ImmutableList<string> ctx)
        {
            WorkflowCondition cond;
            try { cond = WorkflowCondition.Parse(node); }
            catch (WorkflowExpressionException e) { Issue(_currentStep, WorkflowCheckCodes.BadExpression, e.Message); return; }
            Condition(cond, ctx);
        }

        private void Condition(WorkflowCondition cond, ImmutableList<string> ctx)
        {
            switch (cond)
            {
                case TemplateCondition t: ValueType(JsonValue.Create(t.Template), ctx); break;
                case AllCondition a: foreach (var c in a.Items) Condition(c, ctx); break;
                case AnyCondition a: foreach (var c in a.Items) Condition(c, ctx); break;
                case NotCondition n: Condition(n.Inner, ctx); break;
                case CompareCondition c:
                {
                    var left = ValueType(c.Left, ctx);
                    if (WorkflowCondition.IsUnary(c.Op)) break;
                    var right = ValueType(c.Right, ctx);
                    switch (c.Op)
                    {
                        case "gt" or "ge" or "lt" or "le" or "eq" or "ne":
                            if (!Comparable(left, right)) Mismatch(_currentStep, $"'{c.Op}' compares {left} with {right}");
                            break;
                        case "contains":
                            if (left.Kind is not (WorkflowTypeKind.List or WorkflowTypeKind.String or WorkflowTypeKind.Any or WorkflowTypeKind.Null or WorkflowTypeKind.Enum))
                                Mismatch(_currentStep, $"'contains' needs text or a list on the left, not {left}");
                            break;
                        case "matches":
                            if (c.Right is JsonValue rv && rv.GetValueKind() == JsonValueKind.String && !ContainsTemplate(rv)) CheckRegex(rv.GetValue<string>());
                            break;
                    }
                    break;
                }
            }
        }

        private void CheckRegex(string pattern)
        {
            try { _ = new Regex(pattern); }
            catch (ArgumentException e) { Issue(_currentStep, WorkflowCheckCodes.BadExpression, $"'{pattern}' is not a regular expression: {e.Message}"); }
        }

        private static bool Comparable(WorkflowType a, WorkflowType b)
        {
            if (a.Kind is WorkflowTypeKind.Any or WorkflowTypeKind.Null || b.Kind is WorkflowTypeKind.Any or WorkflowTypeKind.Null) return true;
            if (a.IsNumber && b.IsNumber) return true;
            if (a.Kind == b.Kind) return a.Kind is not (WorkflowTypeKind.List or WorkflowTypeKind.Object);
            static bool TextLike(WorkflowType t) => t.Kind is WorkflowTypeKind.String or WorkflowTypeKind.Enum or WorkflowTypeKind.Date or WorkflowTypeKind.DateTime or WorkflowTypeKind.Duration;
            if (a.Kind == WorkflowTypeKind.String && TextLike(b) || b.Kind == WorkflowTypeKind.String && TextLike(a)) return true;
            return a.Kind is WorkflowTypeKind.Date or WorkflowTypeKind.DateTime && b.Kind is WorkflowTypeKind.Date or WorkflowTypeKind.DateTime;
        }

        private WorkflowType Infer(WorkflowExpr expr, ImmutableList<string> ctx) => expr switch
        {
            LiteralExpr { Value: null } => WorkflowType.Null,
            LiteralExpr { Value: long } => WorkflowType.Int,
            LiteralExpr { Value: double } => WorkflowType.Float,
            LiteralExpr { Value: bool } => WorkflowType.Bool,
            LiteralExpr => WorkflowType.String,
            PathExpr p => Path(p, ctx),
            NegateExpr n => Infer(n.Operand, ctx) switch
            {
                { IsNumber: true } t => t,
                { Kind: WorkflowTypeKind.Duration or WorkflowTypeKind.Any } t => t,
                var t => Mismatch(_currentStep, $"cannot negate {t}"),
            },
            BinaryExpr b => Binary(b, ctx),
            CallExpr c => Call(c, ctx),
            _ => WorkflowType.Any,
        };

        private WorkflowType Binary(BinaryExpr b, ImmutableList<string> ctx)
        {
            var l = Infer(b.Left, ctx);
            var r = Infer(b.Right, ctx);
            if (b.Op is "==" or "!=" or "<" or "<=" or ">" or ">=")
            {
                if (!Comparable(l, r)) Mismatch(_currentStep, $"'{b.Op}' compares {l} with {r}");
                return WorkflowType.Bool;
            }
            if (b.Op == "+" && (l.Kind == WorkflowTypeKind.String || r.Kind == WorkflowTypeKind.String)) return WorkflowType.String;
            if (l.Kind == WorkflowTypeKind.Any || r.Kind == WorkflowTypeKind.Any) return WorkflowType.Any;
            if (l.IsNumber && r.IsNumber)
                return b.Op == "/" || l.Kind == WorkflowTypeKind.Float || r.Kind == WorkflowTypeKind.Float ? WorkflowType.Float : WorkflowType.Int;
            bool Time(WorkflowType t) => t.Kind is WorkflowTypeKind.DateTime or WorkflowTypeKind.Date;
            switch (b.Op)
            {
                case "+" or "-" when Time(l) && r.Kind == WorkflowTypeKind.Duration: return WorkflowType.DateTime;
                case "+" when l.Kind == WorkflowTypeKind.Duration && Time(r): return WorkflowType.DateTime;
                case "-" when Time(l) && Time(r): return WorkflowType.Duration;
                case "+" or "-" when l.Kind == WorkflowTypeKind.Duration && r.Kind == WorkflowTypeKind.Duration: return WorkflowType.Duration;
                case "*" or "/" when l.Kind == WorkflowTypeKind.Duration && r.IsNumber: return WorkflowType.Duration;
            }
            return Mismatch(_currentStep, $"cannot apply '{b.Op}' to {l} and {r}");
        }

        private WorkflowType Path(PathExpr p, ImmutableList<string> ctx)
        {
            WorkflowType type;
            var segments = p.Segments;
            var skip = 0;
            switch (p.Root)
            {
                case "input":
                    type = _input;
                    break;
                case "settings" or "vars" or "steps":
                {
                    if (segments.Count == 0 || segments[0] is not string name)
                        return Unknown($"'{p.Root}' needs a name: {p.Root}.<name>");
                    skip = 1;
                    if (p.Root == "settings")
                    {
                        if (!_settings.TryGetValue(name, out var st)) return Unknown($"setting '{name}' is not declared");
                        type = st;
                    }
                    else if (p.Root == "vars")
                    {
                        if (!_vars.TryGetValue(name, out var vt)) return Unknown($"variable '{name}' is not declared");
                        type = vt;
                    }
                    else
                    {
                        if (!_steps.TryGetValue(name, out var target)) return Unknown($"step '{name}' does not exist");
                        type = StepResult(target) ?? Unknown($"step '{name}' ({target.Type}) has no result");
                    }
                    break;
                }
                default:
                {
                    var loop = ctx.LastOrDefault(id => _steps[id].Text("as") == p.Root);
                    if (loop is null)
                        return Unknown($"'{p.Root}' is not input, settings, vars, steps or the item of a loop this step is in");
                    type = ElementType(_steps[loop]);
                    break;
                }
            }

            string? owner = type.Name;
            var ownerPath = "";
            for (var i = skip; i < segments.Count; i++)
            {
                var segment = segments[i];
                if (type.Kind == WorkflowTypeKind.Any) return WorkflowType.Any;
                if (segment is int)
                {
                    if (type.Kind != WorkflowTypeKind.List) return Mismatch(_currentStep, $"'{p}': {type} is not a list");
                    type = type.Element!;
                }
                else
                {
                    var field = (string)segment;
                    if (type.Kind != WorkflowTypeKind.Object) return Mismatch(_currentStep, $"'{p}': {type} has no field '{field}'");
                    if (type.Schema is null) return WorkflowType.Any;
                    var fieldPath = ownerPath.Length == 0 ? field : ownerPath + "." + field;
                    if (!type.TryGetField(field, Find, out var fieldType))
                        return Unknown($"'{p}': {owner ?? "the object"} has no field '{fieldPath}'");
                    if (owner is not null) _refs.Add(new(owner, fieldPath, _currentStep));
                    type = fieldType;
                    ownerPath = fieldPath;
                }
                var named = type.Kind == WorkflowTypeKind.List ? Innermost(type).Name : type.Name;
                if (named is not null) { owner = named; ownerPath = ""; }
            }
            return type;
        }

        private static WorkflowType Innermost(WorkflowType t) => t.Kind == WorkflowTypeKind.List ? Innermost(t.Element!) : t;

        private WorkflowType Unknown(string message)
        {
            Issue(_currentStep, WorkflowCheckCodes.UnknownReference, message);
            return WorkflowType.Any;
        }

        private WorkflowType? StepResult(WorkflowStep step) => step.Type switch
        {
            "integration" => SignatureOf(step) is { } sig ? ResultType(sig)
                : step.Text("into") is { } into && _vars.TryGetValue(into, out var t) ? t : WorkflowType.Any,
            WorkflowStepTypes.FileReadAllText => WorkflowType.String,
            WorkflowStepTypes.FileReadLines => WorkflowType.ListOf(WorkflowType.String),
            WorkflowStepTypes.FileExists => WorkflowType.Bool,
            WorkflowStepTypes.FileMove or WorkflowStepTypes.FileCopy => WorkflowType.String,
            WorkflowStepTypes.FileList => FileListType,
            WorkflowTell.StepType => TellResultOf(step),
            WorkflowCode.StepType => ReturnsOf(step),
            _ => null,
        };

        private WorkflowType Call(CallExpr call, ImmutableList<string> ctx)
        {
            var args = call.Args.Select(a => Infer(a, ctx)).ToList();
            bool Is(int i, params WorkflowTypeKind[] kinds) =>
                args[i].Kind is WorkflowTypeKind.Any or WorkflowTypeKind.Null || kinds.Contains(args[i].Kind);
            WorkflowType Expect(int i, string what, params WorkflowTypeKind[] kinds)
            {
                if (!Is(i, kinds)) Mismatch(_currentStep, $"{call.Function}(): argument {i + 1} is {args[i]}, not {what}");
                return args[i];
            }
            const WorkflowTypeKind Str = WorkflowTypeKind.String, En = WorkflowTypeKind.Enum;
            WorkflowTypeKind[] numbers = [WorkflowTypeKind.Int, WorkflowTypeKind.Float];
            WorkflowTypeKind[] times = [WorkflowTypeKind.DateTime, WorkflowTypeKind.Date, Str];
            WorkflowTypeKind[] scalars = [Str, En, WorkflowTypeKind.Int, WorkflowTypeKind.Float, WorkflowTypeKind.Bool, WorkflowTypeKind.Date, WorkflowTypeKind.DateTime, WorkflowTypeKind.Duration];

            switch (call.Function)
            {
                case "now": return WorkflowType.DateTime;
                case "today": return WorkflowType.Date;
                case "count":
                    Expect(0, "a list", WorkflowTypeKind.List, Str, WorkflowTypeKind.Object);
                    return WorkflowType.Int;
                case "first" or "last":
                    return Expect(0, "a list", WorkflowTypeKind.List).Kind == WorkflowTypeKind.List ? args[0].Element! : WorkflowType.Any;
                case "max" or "min":
                {
                    var list = Expect(0, "a list", WorkflowTypeKind.List);
                    var element = list.Kind == WorkflowTypeKind.List ? list.Element! : WorkflowType.Any;
                    if (call.Args.Count == 1) return element;
                    if (call.Args[1] is not LiteralExpr { Value: string field })
                        return Mismatch(_currentStep, $"{call.Function}(): the field is a quoted name, e.g. 'receivedAt'");
                    if (element.Kind == WorkflowTypeKind.Any) return WorkflowType.Any;
                    if (element.Kind != WorkflowTypeKind.Object) return Mismatch(_currentStep, $"{call.Function}(): the items are {element}, not objects with fields");
                    if (element.Schema is null) return WorkflowType.Any;
                    if (!element.TryGetField(field, Find, out var fieldType))
                        return Unknown($"{call.Function}(): {element} has no field '{field}'");
                    if (element.Name is { } owner) _refs.Add(new(owner, field, _currentStep));
                    return fieldType;
                }
                case "addHours":
                    Expect(0, "a datetime", times);
                    Expect(1, "a number", numbers);
                    return WorkflowType.DateTime;
                case "addDays":
                    Expect(0, "a date or datetime", times);
                    Expect(1, "a number", numbers);
                    return args[0].Kind == WorkflowTypeKind.Date ? WorkflowType.Date : args[0].Kind == WorkflowTypeKind.DateTime ? WorkflowType.DateTime : WorkflowType.Any;
                case "hoursSince":
                    Expect(0, "a datetime", times);
                    return WorkflowType.Float;
                case "formatDuration":
                    Expect(0, "a duration", WorkflowTypeKind.Duration, Str, WorkflowTypeKind.Int, WorkflowTypeKind.Float);
                    return WorkflowType.String;
                case "lower" or "upper":
                    Expect(0, "text", scalars);
                    return WorkflowType.String;
                case "contains":
                    Expect(0, "text or a list", Str, En, WorkflowTypeKind.List, WorkflowTypeKind.Object);
                    return WorkflowType.Bool;
                case "startsWith":
                    Expect(0, "text", Str, En);
                    Expect(1, "text", scalars);
                    return WorkflowType.Bool;
                case "matches":
                    Expect(0, "text", scalars);
                    Expect(1, "text", Str);
                    if (call.Args[1] is LiteralExpr { Value: string pattern }) CheckRegex(pattern);
                    return WorkflowType.Bool;
                case "join":
                    Expect(0, "a list", WorkflowTypeKind.List);
                    Expect(1, "text", Str);
                    return WorkflowType.String;
                case "coalesce":
                    return args.FirstOrDefault(a => a.Kind != WorkflowTypeKind.Null) ?? WorkflowType.Null;
            }
            return Unknown($"'{call.Function}' is not a function");
        }
    }
}
