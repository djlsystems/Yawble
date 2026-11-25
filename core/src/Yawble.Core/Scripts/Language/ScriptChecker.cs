using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Yawble.Core.Scripts.Language;

/// <summary>The actions each integration kind offers (the integrations fill it); a script's <c>requires</c> are checked against it.</summary>
public interface IScriptActionCatalog
{
    bool HasAction(string kind, string action);

    /// <summary>Whether the action changes something (not a GET, not a mail read): a dry run does not perform it.</summary>
    bool IsChanging(string kind, string action);
}

/// <summary>
/// A catalog that also knows its actions' arguments and results (the built-in kinds, such as email). The check then refuses an argument the
/// action does not take, a missing required one or one of the wrong type, and types the step's result (its <c>into</c> and <c>steps.&lt;id&gt;</c>).
/// </summary>
public interface IScriptActionSignatures
{
    /// <summary>The action's signature, or null when this catalog does not describe it (then any args are accepted and the result is any).</summary>
    ScriptActionSignature? Signature(string kind, string action);
}

/// <summary>
/// An action's arguments as a JSON Schema object (<c>properties</c>, <c>required</c>; no other argument is accepted) and its result as a
/// script type (<c>list&lt;EmailMessage&gt;</c>, <c>EmailMessage</c>).
/// </summary>
public sealed record ScriptActionSignature(JsonObject Args, string Result);

/// <summary>One problem the check found: the step it is in (null for the script's declarations), a code and a message.</summary>
public sealed record ScriptCheckIssue(string? StepId, string Code, string Message);

/// <summary>
/// A use of a schema's field by a script: <see cref="FieldPath"/> is the field (<c>from</c>, <c>items.name</c> for a field of
/// an inline object), or empty when the script uses the schema as a whole (a variable typed <c>list&lt;EmailMessage&gt;</c>).
/// <see cref="StepId"/> is null for a declaration (a setting, variable, output or the input).
/// </summary>
public sealed record ScriptSchemaReference(string Schema, string FieldPath, string? StepId);

public sealed record ScriptCheckResult(IReadOnlyList<ScriptCheckIssue> Issues, IReadOnlyList<ScriptSchemaReference> SchemaReferences)
{
    public bool Ok => Issues.Count == 0;
}

/// <summary>
/// The file step types. Code names them through these constants: a dotted name quoted in a switch reads as an RPC
/// call to the audit classification, and these are step types, not calls.
/// </summary>
public static class ScriptStepTypes
{
    public const string FileWriteAllText = "file.writeAllText";
    public const string FileAppendLine = "file.appendLine";
    public const string FileReadAllText = "file.readAllText";
    public const string FileReadLines = "file.readLines";
    public const string FileExists = "file.exists";
    public const string FileDelete = "file.delete";
}

/// <summary>The codes a <see cref="ScriptCheckIssue"/> carries.</summary>
public static class ScriptCheckCodes
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
/// Checks a script before it can run: every reference resolves, every type matches, every step is reachable from
/// <c>start</c>, and every <c>requires</c> action exists in the catalog for its kind.
/// </summary>
public static class ScriptChecker
{
    public static readonly IReadOnlyList<string> StepTypes =
    [
        "set", "if", "switch", "forEach", "integration", "log", "end", ScriptTell.StepType,
        ScriptStepTypes.FileWriteAllText, ScriptStepTypes.FileAppendLine, ScriptStepTypes.FileReadAllText, ScriptStepTypes.FileReadLines, ScriptStepTypes.FileExists, ScriptStepTypes.FileDelete,
    ];

    private static readonly string[] RootNames = ["input", "settings", "vars", "steps"];

    /// <summary>Parses and checks a document; a document that does not parse gives one <c>invalid-document</c> issue.</summary>
    public static ScriptCheckResult Check(string json, IScriptSchemaLookup schemas, IScriptActionCatalog catalog)
    {
        ScriptDocument doc;
        try { doc = ScriptDocument.Parse(json); }
        catch (ScriptDocumentException e) { return new([new(null, ScriptCheckCodes.InvalidDocument, e.Message)], []); }
        return Check(doc, schemas, catalog);
    }

    public static ScriptCheckResult Check(ScriptDocument document, IScriptSchemaLookup schemas, IScriptActionCatalog catalog) =>
        new Run(document, schemas, catalog).Check();

    /// <summary>The type of <c>input</c>: the declared one, or the payload of the triggers the script accepts.</summary>
    public static ScriptType InputType(ScriptDocument document, Func<string, ScriptSchema?> find)
    {
        if (document.Input is { } declared) return ScriptType.Parse(declared, find, out _) ?? ScriptType.Any;
        var accepts = document.Trigger.Accepts;
        if (accepts.Count == 0) return ScriptType.Any;
        if (accepts.All(a => a is "every" or "cron" or "once")) return ScriptType.FromSchema(BuiltInSchemas.TriggerFired);
        if (accepts.All(a => a == "folder")) return ScriptType.FromSchema(BuiltInSchemas.FolderChange);
        if (accepts.All(a => a == "event")) return ScriptType.FromSchema(BuiltInSchemas.EventItem);
        if (accepts.All(a => a == "tell"))
            return ScriptType.ObjectOf(null, JsonNode.Parse("""{ "type": "object", "properties": { "text": { "type": "string" } } }""")!.AsObject());
        return ScriptType.Any;
    }

    private sealed class Run(ScriptDocument doc, IScriptSchemaLookup schemas, IScriptActionCatalog catalog)
    {
        private readonly List<ScriptCheckIssue> _issues = [];
        private readonly List<ScriptSchemaReference> _refs = [];
        private readonly Dictionary<string, ScriptType> _settings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ScriptType> _vars = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ScriptStep> _steps = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ImmutableList<string>> _context = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ScriptType> _elementTypes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _elementTypeInProgress = new(StringComparer.Ordinal);
        private ScriptType _input = ScriptType.Any;
        private string? _currentStep;

        private ScriptSchema? Find(string name) => BuiltInSchemas.Find(name) ?? schemas.Find(name);

        private void Issue(string? step, string code, string message) => _issues.Add(new(step, code, message));

        public ScriptCheckResult Check()
        {
            Declarations();
            Graph();
            foreach (var step in doc.Steps)
                if (_context.ContainsKey(step.Id) && ReferenceEquals(_steps.GetValueOrDefault(step.Id), step))
                    CheckStep(step);
            var refs = _refs.Distinct().ToList();
            return new ScriptCheckResult(_issues.Distinct().ToList(), refs);
        }

        // ---- declarations ---------------------------------------------------------------------------------------------

        private ScriptType? DeclaredType(string text, string where)
        {
            var type = ScriptType.Parse(text, Find, out var error);
            if (type is null) { Issue(null, ScriptCheckCodes.UnknownType, $"{where}: {error}"); return null; }
            ReferenceTypeSchemas(type, null);
            return type;
        }

        private void ReferenceTypeSchemas(ScriptType type, string? stepId)
        {
            if (type.Kind == ScriptTypeKind.List) ReferenceTypeSchemas(type.Element!, stepId);
            else if (type.Kind is ScriptTypeKind.Object or ScriptTypeKind.Enum && type.Name is { } name) _refs.Add(new(name, "", stepId));
        }

        private void Declarations()
        {
            if (doc.Format != ScriptDocument.CurrentFormat) Issue(null, ScriptCheckCodes.InvalidDocument, $"format is not {ScriptDocument.CurrentFormat}");
            foreach (var kind in doc.Trigger.Accepts.Where(k => !ScriptTriggerSpec.Kinds.Contains(k)))
                Issue(null, ScriptCheckCodes.UnknownTrigger, $"trigger kind '{kind}' is not one of {string.Join(", ", ScriptTriggerSpec.Kinds)}");

            if (doc.Input is { } input) _input = DeclaredType(input, "input") ?? ScriptType.Any;
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
                    Issue(null, ScriptCheckCodes.InvalidDocument, $"variable '{v.Name}': scope '{v.Scope}' is not run or saved");
                var type = DeclaredType(v.Type, $"variable '{v.Name}'");
                if (type is null) { _vars.TryAdd(v.Name, ScriptType.Any); continue; }
                _vars.TryAdd(v.Name, type);
                LiteralFits(v.Initial, type, null, $"the initial value of variable '{v.Name}'");
            }
            // A TellResult variable that only tells with one reply schema fill: its data is typed by that schema (vars.x.data.field).
            foreach (var (name, type) in _vars.ToList())
            {
                if (type.Kind != ScriptTypeKind.Object || type.Name != BuiltInSchemas.TellResult.Name) continue;
                var replies = doc.Steps.Where(s => s.Type == ScriptTell.StepType && s.Text("into") == name).Select(s => s.Text("reply")).Distinct().ToList();
                if (replies is [{ Length: > 0 } reply] && Find(reply) is not null) _vars[name] = ScriptTell.ResultType(reply);
            }
            foreach (var o in doc.Outputs) DeclaredType(o.Type, $"output '{o.Name}'");
            foreach (var r in doc.Requires)
            {
                if (r.Actions.Count == 0) Issue(null, ScriptCheckCodes.MissingField, $"requirement '{r.Name}' lists no actions");
                foreach (var a in r.Actions.Where(a => !catalog.HasAction(r.Kind, a)))
                    Issue(null, ScriptCheckCodes.UnknownAction, $"requirement '{r.Name}': kind '{r.Kind}' has no action '{a}'");
            }
        }

        private void Duplicates(IEnumerable<string> names, string what)
        {
            foreach (var g in names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1))
                Issue(null, ScriptCheckCodes.Duplicate, $"{what} '{g.Key}' is declared {g.Count()} times");
        }

        /// <summary>A literal (a default, an initial value, a template-free value) must convert to its declared type.</summary>
        private void LiteralFits(JsonNode? value, ScriptType type, string? stepId, string what)
        {
            if (value is null) return;
            if (ContainsTemplate(value)) return;
            try { ScriptValues.Convert(ScriptValues.FromNode(value), type, Find); }
            catch (ScriptValueException e) { Issue(stepId, ScriptCheckCodes.TypeMismatch, $"{what}: {e.Message}"); }
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
                if (!StepTypes.Contains(s.Type)) Issue(s.Id, ScriptCheckCodes.UnknownStepType, $"'{s.Type}' is not a step type ({string.Join(", ", StepTypes)})");
                foreach (var (prop, target) in s.Successors())
                    if (target is not null && !_steps.ContainsKey(target))
                        Issue(s.Id, ScriptCheckCodes.UnknownStep, $"{prop} names step '{target}', which does not exist");
                if (s.Type == "forEach" && s.Text("body") is null)
                    Issue(s.Id, ScriptCheckCodes.MissingField, "a forEach needs a body");
            }

            if (!_steps.ContainsKey(doc.Start))
            {
                Issue(null, ScriptCheckCodes.UnknownStep, $"start names step '{doc.Start}', which does not exist");
                return;
            }

            // Breadth first from start; each step's context is the loops (forEach ids, outermost first) it runs inside.
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
                    var next = step.Type == "forEach" && prop == "body" ? ctx.Add(id) : ctx;
                    if (next.Contains(target))
                    {
                        if (conflicts.Add(target)) Issue(id, ScriptCheckCodes.LoopStructure, $"{prop} goes back to the loop '{target}' it is inside (a body ends with null)");
                        continue;
                    }
                    if (_context.TryGetValue(target, out var seen))
                    {
                        if (!seen.SequenceEqual(next) && conflicts.Add(target))
                            Issue(target, ScriptCheckCodes.LoopStructure,
                                $"step '{target}' is reached both inside and outside {Describe(seen.Count > next.Count ? seen : next)}; a loop's body must end with null");
                        continue;
                    }
                    _context[target] = next;
                    queue.Enqueue(target);
                }
            }

            foreach (var s in doc.Steps.Where(s => !_context.ContainsKey(s.Id)).DistinctBy(s => s.Id))
                Issue(s.Id, ScriptCheckCodes.Unreachable, $"step '{s.Id}' cannot be reached from start");
        }

        private static string Describe(ImmutableList<string> ctx) => ctx.Count == 0 ? "any loop" : $"the loop '{ctx[^1]}'";

        // ---- steps ----------------------------------------------------------------------------------------------------

        private void CheckStep(ScriptStep step)
        {
            _currentStep = step.Id;
            var ctx = _context[step.Id];
            switch (step.Type)
            {
                case "set":
                {
                    var name = Required(step, "var");
                    if (!step.Has("value")) Issue(step.Id, ScriptCheckCodes.MissingField, "set needs a value");
                    var valueType = ValueType(step["value"], ctx);
                    if (name is null) break;
                    if (!_vars.TryGetValue(name, out var varType))
                    {
                        Issue(step.Id, ScriptCheckCodes.UnknownReference, $"variable '{name}' is not declared");
                        break;
                    }
                    Assign(varType, valueType, step["value"], $"set '{name}'");
                    break;
                }
                case "if":
                    if (!step.Has("when")) Issue(step.Id, ScriptCheckCodes.MissingField, "if needs a when");
                    else Condition(step["when"], ctx);
                    break;
                case "switch":
                {
                    if (!step.Has("on")) { Issue(step.Id, ScriptCheckCodes.MissingField, "switch needs an on"); break; }
                    var on = ValueType(step["on"], ctx);
                    if (step["cases"] is not JsonObject cases) { Issue(step.Id, ScriptCheckCodes.MissingField, "switch needs cases"); break; }
                    if (on.Kind == ScriptTypeKind.Enum)
                        foreach (var key in cases.Select(c => c.Key).Where(k => !on.EnumValues.Contains(k)))
                            Issue(step.Id, ScriptCheckCodes.TypeMismatch, $"case '{key}' is not a value of {on}");
                    break;
                }
                case "forEach":
                {
                    if (!step.Has("items")) Issue(step.Id, ScriptCheckCodes.MissingField, "forEach needs items");
                    var name = Required(step, "as");
                    if (name is not null)
                    {
                        if (!ScriptType.IsName(name) || RootNames.Contains(name) || ScriptExpressionParser.Functions.ContainsKey(name) || name is "true" or "false" or "null")
                            Issue(step.Id, ScriptCheckCodes.InvalidDocument, $"'{name}' cannot name a loop's item");
                        else if (ctx.Any(outer => _steps[outer].Text("as") == name))
                            Issue(step.Id, ScriptCheckCodes.Duplicate, $"'{name}' is already the item of an enclosing loop");
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
                        if (args is not JsonObject) Issue(step.Id, ScriptCheckCodes.InvalidDocument, "args is an object");
                        ValueType(args, ctx);
                    }
                    if (use is not null)
                    {
                        var req = doc.Requires.FirstOrDefault(r => r.Name == use);
                        if (req is null) Issue(step.Id, ScriptCheckCodes.UnknownRequirement,
                            $"'{use}' is not in requires: name an integration as Settings > Integrations shows it, or list '{use}' in requires with its kind");
                        else if (action is not null && !req.Actions.Contains(action))
                            Issue(step.Id, ScriptCheckCodes.UnknownAction, $"action '{action}' is not listed in requires '{use}'");
                    }
                    var signature = SignatureOf(step);
                    if (signature is not null) Arguments(step, action!, signature, ctx);
                    Into(step, signature is null ? ScriptType.Any : ResultType(signature));
                    break;
                }
                case ScriptStepTypes.FileWriteAllText or ScriptStepTypes.FileAppendLine:
                    Text(step, "path", ctx);
                    Text(step, "text", ctx);
                    break;
                case ScriptStepTypes.FileReadAllText:
                    Text(step, "path", ctx);
                    Into(step, ScriptType.String);
                    break;
                case ScriptStepTypes.FileReadLines:
                    Text(step, "path", ctx);
                    Into(step, ScriptType.ListOf(ScriptType.String));
                    break;
                case ScriptStepTypes.FileExists:
                    Text(step, "path", ctx);
                    Into(step, ScriptType.Bool);
                    break;
                case ScriptStepTypes.FileDelete:
                    Text(step, "path", ctx);
                    break;
                case "log":
                    Text(step, "text", ctx);
                    break;
                case ScriptTell.StepType:
                    Tell(step, ctx);
                    break;
                case "end":
                    if (step["outputs"] is null) break;
                    if (step["outputs"] is not JsonObject outputs) { Issue(step.Id, ScriptCheckCodes.InvalidDocument, "outputs is an object"); break; }
                    foreach (var (name, value) in outputs)
                    {
                        var type = ValueType(value, ctx);
                        if (doc.Outputs.Count == 0) continue;
                        var declared = doc.Outputs.FirstOrDefault(o => o.Name == name);
                        if (declared is null) { Issue(step.Id, ScriptCheckCodes.UnknownReference, $"output '{name}' is not declared"); continue; }
                        if (ScriptType.Parse(declared.Type, Find, out _) is { } t) Assign(t, type, value, $"output '{name}'");
                    }
                    break;
            }
            _currentStep = null;
        }

        /// <summary>
        /// A tell: member and team are text (optional), text is required, wait is true or false, timeout and reply go only with wait
        /// (timeout a duration up to 7 days, reply a schema that exists), and into takes a TellResult.
        /// </summary>
        private void Tell(ScriptStep step, ImmutableList<string> ctx)
        {
            foreach (var optional in new[] { "member", "team" })
                if (step.Has(optional))
                {
                    if (step[optional] is not JsonValue v || v.GetValueKind() != JsonValueKind.String || v.GetValue<string>().Trim().Length == 0)
                        Issue(step.Id, ScriptCheckCodes.InvalidDocument, $"tell's '{optional}' is a name (text)");
                    else Text(step, optional, ctx);
                }
            Text(step, "text", ctx);

            var wait = false;
            if (step.Has("wait"))
            {
                if (step["wait"] is JsonValue w && w.GetValueKind() is JsonValueKind.True or JsonValueKind.False) wait = w.GetValue<bool>();
                else Issue(step.Id, ScriptCheckCodes.InvalidDocument, "tell's 'wait' is true or false");
            }

            if (step.Has("timeout"))
            {
                if (!wait) Issue(step.Id, ScriptCheckCodes.BadValue, "a timeout goes with wait: true (a tell that does not wait has nothing to time out)");
                if (!ScriptTell.TryParseTimeout(step["timeout"], out var timeout))
                    Issue(step.Id, ScriptCheckCodes.TypeMismatch, $"tell's timeout '{step["timeout"]?.ToJsonString()}' is not a duration (e.g. 30m, 4h, 2d)");
                else if (timeout <= TimeSpan.Zero) Issue(step.Id, ScriptCheckCodes.BadValue, "tell's timeout must be more than zero");
                else if (timeout > ScriptTell.MaxTimeout)
                    Issue(step.Id, ScriptCheckCodes.BadValue, $"tell's timeout is {ScriptValues.FormatDuration(timeout)}; the most is 7 days");
            }

            if (step.Has("reply"))
            {
                if (!wait) Issue(step.Id, ScriptCheckCodes.BadValue, "a reply goes with wait: true (a tell that does not wait gets no reply)");
                if (step.Text("reply") is not { Length: > 0 } reply) Issue(step.Id, ScriptCheckCodes.InvalidDocument, "tell's 'reply' names a schema");
                else if (Find(reply) is null) Issue(step.Id, ScriptCheckCodes.UnknownType, $"reply: no schema named '{reply}'");
                else _refs.Add(new(reply, "", step.Id));
            }

            Into(step, TellResultOf(step));
        }

        /// <summary>The tell's result type: TellResult with data typed by its reply schema, when that schema exists.</summary>
        private ScriptType TellResultOf(ScriptStep tell) =>
            ScriptTell.ResultType(tell.Text("reply") is { Length: > 0 } reply && Find(reply) is not null ? reply : null);

        private string? Required(ScriptStep step, string property)
        {
            var value = step.Text(property);
            if (string.IsNullOrEmpty(value)) Issue(step.Id, ScriptCheckCodes.MissingField, $"{step.Type} needs '{property}'");
            return value;
        }

        private void Text(ScriptStep step, string property, ImmutableList<string> ctx)
        {
            if (!step.Has(property)) { Issue(step.Id, ScriptCheckCodes.MissingField, $"{step.Type} needs '{property}'"); return; }
            var type = ValueType(step[property], ctx);
            if (type.Kind is ScriptTypeKind.List or ScriptTypeKind.Object)
                Issue(step.Id, ScriptCheckCodes.TypeMismatch, $"'{property}' is {type}, not text");
        }

        /// <summary>The integration step's action signature, when its requirement's kind is described by the catalog.</summary>
        private ScriptActionSignature? SignatureOf(ScriptStep step)
        {
            if (catalog is not IScriptActionSignatures sigs || step.Text("use") is not { } use || step.Text("action") is not { } action) return null;
            var req = doc.Requires.FirstOrDefault(r => r.Name == use);
            return req is null || !req.Actions.Contains(action) ? null : sigs.Signature(req.Kind, action);
        }

        private ScriptType ResultType(ScriptActionSignature signature) => ScriptType.Parse(signature.Result, Find, out _) ?? ScriptType.Any;

        /// <summary>The step's args against the action's: no unknown one, every required one, each of its type.</summary>
        private void Arguments(ScriptStep step, string action, ScriptActionSignature signature, ImmutableList<string> ctx)
        {
            var args = step["args"] as JsonObject ?? [];
            var props = signature.Args["properties"] as JsonObject ?? [];
            var required = (signature.Args["required"] as JsonArray)?.Select(r => (string?)r).OfType<string>() ?? [];
            foreach (var r in required.Where(r => !args.ContainsKey(r)))
                Issue(step.Id, ScriptCheckCodes.MissingField, $"{action} needs the argument '{r}'");
            foreach (var (name, value) in args)
            {
                if (props[name] is not JsonObject schema)
                {
                    Issue(step.Id, ScriptCheckCodes.UnknownArgument,
                        $"{action} has no argument '{name}' (it takes {(props.Count == 0 ? "none" : string.Join(", ", props.Select(p => p.Key)))})");
                    continue;
                }
                var type = ScriptType.FromJsonSchema(schema, Find);
                var given = ValueType(value, ctx);
                if (!ScriptType.IsAssignable(type, given))
                    Issue(step.Id, ScriptCheckCodes.TypeMismatch, $"{action}'s argument '{name}' is {type}, not {given}");
                else LiteralFits(value, type, step.Id, $"{action}'s argument '{name}'");
            }
        }

        private void Into(ScriptStep step, ScriptType result)
        {
            if (step["into"] is null) return;
            var name = step.Text("into");
            if (name is null) { Issue(step.Id, ScriptCheckCodes.InvalidDocument, "into names a variable"); return; }
            if (!_vars.TryGetValue(name, out var type)) { Issue(step.Id, ScriptCheckCodes.UnknownReference, $"variable '{name}' is not declared"); return; }
            if (!ScriptType.IsAssignable(type, result))
                Issue(step.Id, ScriptCheckCodes.TypeMismatch, $"the result is {result}; variable '{name}' is {type}");
        }

        private void Assign(ScriptType target, ScriptType source, JsonNode? literal, string what)
        {
            if (!ScriptType.IsAssignable(target, source))
                Issue(_currentStep, ScriptCheckCodes.TypeMismatch, $"{what}: the value is {source}, the variable is {target}");
            else LiteralFits(literal, target, _currentStep, what);
        }

        private ScriptType ElementType(ScriptStep forEach)
        {
            if (_elementTypes.TryGetValue(forEach.Id, out var known)) return known;
            if (!_elementTypeInProgress.Add(forEach.Id)) return ScriptType.Any;
            var saved = _currentStep;
            _currentStep = forEach.Id;
            var items = _context.TryGetValue(forEach.Id, out var ctx) ? ValueType(forEach["items"], ctx) : ScriptType.Any;
            _currentStep = saved;
            var element = items.Kind switch
            {
                ScriptTypeKind.List => items.Element!,
                ScriptTypeKind.Any or ScriptTypeKind.Null => ScriptType.Any,
                _ => Mismatch(forEach.Id, $"items is {items}, not a list"),
            };
            _elementTypes[forEach.Id] = element;
            return element;
        }

        private ScriptType Mismatch(string? stepId, string message)
        {
            Issue(stepId, ScriptCheckCodes.TypeMismatch, message);
            return ScriptType.Any;
        }

        // ---- values, conditions and expressions -----------------------------------------------------------------------

        private ScriptType ValueType(JsonNode? node, ImmutableList<string> ctx)
        {
            switch (node)
            {
                case null: return ScriptType.Null;
                case JsonObject o:
                    foreach (var p in o) ValueType(p.Value, ctx);
                    return ScriptType.ObjectOf(null, null);
                case JsonArray a:
                {
                    var types = a.Select(i => ValueType(i, ctx)).ToList();
                    return ScriptType.ListOf(types.Count > 0 && types.All(t => t.Kind == types[0].Kind && t.Kind is not (ScriptTypeKind.Object or ScriptTypeKind.List or ScriptTypeKind.Enum)) ? types[0] : ScriptType.Any);
                }
                case JsonValue v:
                    switch (v.GetValueKind())
                    {
                        case JsonValueKind.String:
                        {
                            ScriptTemplate template;
                            try { template = ScriptTemplate.Parse(v.GetValue<string>()); }
                            catch (ScriptExpressionException e) { Issue(_currentStep, ScriptCheckCodes.BadExpression, e.Message); return ScriptType.Any; }
                            if (template.IsSingleExpression) return Infer((ScriptExpr)template.Parts[0], ctx);
                            foreach (var e in template.Expressions) Infer(e, ctx);
                            return ScriptType.String;
                        }
                        case JsonValueKind.True or JsonValueKind.False: return ScriptType.Bool;
                        case JsonValueKind.Number: return ScriptValues.FromNode(v) is long ? ScriptType.Int : ScriptType.Float;
                        default: return ScriptType.Null;
                    }
                default: return ScriptType.Any;
            }
        }

        private void Condition(JsonNode? node, ImmutableList<string> ctx)
        {
            ScriptCondition cond;
            try { cond = ScriptCondition.Parse(node); }
            catch (ScriptExpressionException e) { Issue(_currentStep, ScriptCheckCodes.BadExpression, e.Message); return; }
            Condition(cond, ctx);
        }

        private void Condition(ScriptCondition cond, ImmutableList<string> ctx)
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
                    if (ScriptCondition.IsUnary(c.Op)) break;
                    var right = ValueType(c.Right, ctx);
                    switch (c.Op)
                    {
                        case "gt" or "ge" or "lt" or "le" or "eq" or "ne":
                            if (!Comparable(left, right)) Mismatch(_currentStep, $"'{c.Op}' compares {left} with {right}");
                            break;
                        case "contains":
                            if (left.Kind is not (ScriptTypeKind.List or ScriptTypeKind.String or ScriptTypeKind.Any or ScriptTypeKind.Null or ScriptTypeKind.Enum))
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
            catch (ArgumentException e) { Issue(_currentStep, ScriptCheckCodes.BadExpression, $"'{pattern}' is not a regular expression: {e.Message}"); }
        }

        private static bool Comparable(ScriptType a, ScriptType b)
        {
            if (a.Kind is ScriptTypeKind.Any or ScriptTypeKind.Null || b.Kind is ScriptTypeKind.Any or ScriptTypeKind.Null) return true;
            if (a.IsNumber && b.IsNumber) return true;
            if (a.Kind == b.Kind) return a.Kind is not (ScriptTypeKind.List or ScriptTypeKind.Object);
            static bool TextLike(ScriptType t) => t.Kind is ScriptTypeKind.String or ScriptTypeKind.Enum or ScriptTypeKind.Date or ScriptTypeKind.DateTime or ScriptTypeKind.Duration;
            if (a.Kind == ScriptTypeKind.String && TextLike(b) || b.Kind == ScriptTypeKind.String && TextLike(a)) return true;
            return a.Kind is ScriptTypeKind.Date or ScriptTypeKind.DateTime && b.Kind is ScriptTypeKind.Date or ScriptTypeKind.DateTime;
        }

        private ScriptType Infer(ScriptExpr expr, ImmutableList<string> ctx) => expr switch
        {
            LiteralExpr { Value: null } => ScriptType.Null,
            LiteralExpr { Value: long } => ScriptType.Int,
            LiteralExpr { Value: double } => ScriptType.Float,
            LiteralExpr { Value: bool } => ScriptType.Bool,
            LiteralExpr => ScriptType.String,
            PathExpr p => Path(p, ctx),
            NegateExpr n => Infer(n.Operand, ctx) switch
            {
                { IsNumber: true } t => t,
                { Kind: ScriptTypeKind.Duration or ScriptTypeKind.Any } t => t,
                var t => Mismatch(_currentStep, $"cannot negate {t}"),
            },
            BinaryExpr b => Binary(b, ctx),
            CallExpr c => Call(c, ctx),
            _ => ScriptType.Any,
        };

        private ScriptType Binary(BinaryExpr b, ImmutableList<string> ctx)
        {
            var l = Infer(b.Left, ctx);
            var r = Infer(b.Right, ctx);
            if (b.Op is "==" or "!=" or "<" or "<=" or ">" or ">=")
            {
                if (!Comparable(l, r)) Mismatch(_currentStep, $"'{b.Op}' compares {l} with {r}");
                return ScriptType.Bool;
            }
            if (b.Op == "+" && (l.Kind == ScriptTypeKind.String || r.Kind == ScriptTypeKind.String)) return ScriptType.String;
            if (l.Kind == ScriptTypeKind.Any || r.Kind == ScriptTypeKind.Any) return ScriptType.Any;
            if (l.IsNumber && r.IsNumber)
                return b.Op == "/" || l.Kind == ScriptTypeKind.Float || r.Kind == ScriptTypeKind.Float ? ScriptType.Float : ScriptType.Int;
            bool Time(ScriptType t) => t.Kind is ScriptTypeKind.DateTime or ScriptTypeKind.Date;
            switch (b.Op)
            {
                case "+" or "-" when Time(l) && r.Kind == ScriptTypeKind.Duration: return ScriptType.DateTime;
                case "+" when l.Kind == ScriptTypeKind.Duration && Time(r): return ScriptType.DateTime;
                case "-" when Time(l) && Time(r): return ScriptType.Duration;
                case "+" or "-" when l.Kind == ScriptTypeKind.Duration && r.Kind == ScriptTypeKind.Duration: return ScriptType.Duration;
                case "*" or "/" when l.Kind == ScriptTypeKind.Duration && r.IsNumber: return ScriptType.Duration;
            }
            return Mismatch(_currentStep, $"cannot apply '{b.Op}' to {l} and {r}");
        }

        private ScriptType Path(PathExpr p, ImmutableList<string> ctx)
        {
            ScriptType type;
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
                if (type.Kind == ScriptTypeKind.Any) return ScriptType.Any;
                if (segment is int)
                {
                    if (type.Kind != ScriptTypeKind.List) return Mismatch(_currentStep, $"'{p}': {type} is not a list");
                    type = type.Element!;
                }
                else
                {
                    var field = (string)segment;
                    if (type.Kind != ScriptTypeKind.Object) return Mismatch(_currentStep, $"'{p}': {type} has no field '{field}'");
                    if (type.Schema is null) return ScriptType.Any;
                    var fieldPath = ownerPath.Length == 0 ? field : ownerPath + "." + field;
                    if (!type.TryGetField(field, Find, out var fieldType))
                        return Unknown($"'{p}': {owner ?? "the object"} has no field '{fieldPath}'");
                    if (owner is not null) _refs.Add(new(owner, fieldPath, _currentStep));
                    type = fieldType;
                    ownerPath = fieldPath;
                }
                var named = type.Kind == ScriptTypeKind.List ? Innermost(type).Name : type.Name;
                if (named is not null) { owner = named; ownerPath = ""; }
            }
            return type;
        }

        private static ScriptType Innermost(ScriptType t) => t.Kind == ScriptTypeKind.List ? Innermost(t.Element!) : t;

        private ScriptType Unknown(string message)
        {
            Issue(_currentStep, ScriptCheckCodes.UnknownReference, message);
            return ScriptType.Any;
        }

        private ScriptType? StepResult(ScriptStep step) => step.Type switch
        {
            "integration" => SignatureOf(step) is { } sig ? ResultType(sig)
                : step.Text("into") is { } into && _vars.TryGetValue(into, out var t) ? t : ScriptType.Any,
            ScriptStepTypes.FileReadAllText => ScriptType.String,
            ScriptStepTypes.FileReadLines => ScriptType.ListOf(ScriptType.String),
            ScriptStepTypes.FileExists => ScriptType.Bool,
            ScriptTell.StepType => TellResultOf(step),
            _ => null,
        };

        private ScriptType Call(CallExpr call, ImmutableList<string> ctx)
        {
            var args = call.Args.Select(a => Infer(a, ctx)).ToList();
            bool Is(int i, params ScriptTypeKind[] kinds) =>
                args[i].Kind is ScriptTypeKind.Any or ScriptTypeKind.Null || kinds.Contains(args[i].Kind);
            ScriptType Expect(int i, string what, params ScriptTypeKind[] kinds)
            {
                if (!Is(i, kinds)) Mismatch(_currentStep, $"{call.Function}(): argument {i + 1} is {args[i]}, not {what}");
                return args[i];
            }
            const ScriptTypeKind Str = ScriptTypeKind.String, En = ScriptTypeKind.Enum;
            ScriptTypeKind[] numbers = [ScriptTypeKind.Int, ScriptTypeKind.Float];
            ScriptTypeKind[] times = [ScriptTypeKind.DateTime, ScriptTypeKind.Date, Str];
            ScriptTypeKind[] scalars = [Str, En, ScriptTypeKind.Int, ScriptTypeKind.Float, ScriptTypeKind.Bool, ScriptTypeKind.Date, ScriptTypeKind.DateTime, ScriptTypeKind.Duration];

            switch (call.Function)
            {
                case "now": return ScriptType.DateTime;
                case "today": return ScriptType.Date;
                case "count":
                    Expect(0, "a list", ScriptTypeKind.List, Str, ScriptTypeKind.Object);
                    return ScriptType.Int;
                case "first" or "last":
                    return Expect(0, "a list", ScriptTypeKind.List).Kind == ScriptTypeKind.List ? args[0].Element! : ScriptType.Any;
                case "max" or "min":
                {
                    var list = Expect(0, "a list", ScriptTypeKind.List);
                    var element = list.Kind == ScriptTypeKind.List ? list.Element! : ScriptType.Any;
                    if (call.Args.Count == 1) return element;
                    if (call.Args[1] is not LiteralExpr { Value: string field })
                        return Mismatch(_currentStep, $"{call.Function}(): the field is a quoted name, e.g. 'receivedAt'");
                    if (element.Kind == ScriptTypeKind.Any) return ScriptType.Any;
                    if (element.Kind != ScriptTypeKind.Object) return Mismatch(_currentStep, $"{call.Function}(): the items are {element}, not objects with fields");
                    if (element.Schema is null) return ScriptType.Any;
                    if (!element.TryGetField(field, Find, out var fieldType))
                        return Unknown($"{call.Function}(): {element} has no field '{field}'");
                    if (element.Name is { } owner) _refs.Add(new(owner, field, _currentStep));
                    return fieldType;
                }
                case "addHours":
                    Expect(0, "a datetime", times);
                    Expect(1, "a number", numbers);
                    return ScriptType.DateTime;
                case "addDays":
                    Expect(0, "a date or datetime", times);
                    Expect(1, "a number", numbers);
                    return args[0].Kind == ScriptTypeKind.Date ? ScriptType.Date : args[0].Kind == ScriptTypeKind.DateTime ? ScriptType.DateTime : ScriptType.Any;
                case "hoursSince":
                    Expect(0, "a datetime", times);
                    return ScriptType.Float;
                case "formatDuration":
                    Expect(0, "a duration", ScriptTypeKind.Duration, Str, ScriptTypeKind.Int, ScriptTypeKind.Float);
                    return ScriptType.String;
                case "lower" or "upper":
                    Expect(0, "text", scalars);
                    return ScriptType.String;
                case "contains":
                    Expect(0, "text or a list", Str, En, ScriptTypeKind.List, ScriptTypeKind.Object);
                    return ScriptType.Bool;
                case "startsWith":
                    Expect(0, "text", Str, En);
                    Expect(1, "text", scalars);
                    return ScriptType.Bool;
                case "matches":
                    Expect(0, "text", scalars);
                    Expect(1, "text", Str);
                    if (call.Args[1] is LiteralExpr { Value: string pattern }) CheckRegex(pattern);
                    return ScriptType.Bool;
                case "join":
                    Expect(0, "a list", ScriptTypeKind.List);
                    Expect(1, "text", Str);
                    return ScriptType.String;
                case "coalesce":
                    return args.FirstOrDefault(a => a.Kind != ScriptTypeKind.Null) ?? ScriptType.Null;
            }
            return Unknown($"'{call.Function}' is not a function");
        }
    }
}
