using System.Globalization;
using System.Text.Json.Nodes;
using Acornima;
using Jint;
using Jint.Native;
using Jint.Native.Json;
using Jint.Runtime;
using Jint.Runtime.Interop;
using JsEngine = Jint.Engine;

namespace Yawble.Core.Scripts.Language;

/// <summary>
/// The limits one <c>code</c> step runs under: wall time, memory, statements and call depth. Hitting one fails the step with
/// that reason. <see cref="Default"/> is what a run uses; tests may pass smaller ones.
/// </summary>
public sealed record ScriptCodeLimits(TimeSpan Timeout, long MemoryBytes, int MaxStatements, int MaxRecursion)
{
    public static readonly ScriptCodeLimits Default = new(
        Timeout: TimeSpan.FromSeconds(2),
        MemoryBytes: 64L * 1024 * 1024,
        MaxStatements: 1_000_000,
        MaxRecursion: 256);

    /// <summary>The most <c>log(text)</c> lines one code step writes, and the longest line (longer ones are cut).</summary>
    public const int MaxLogLines = 200;
    public const int MaxLogLineLength = 4_000;
}

/// <summary>A code step failed: a limit was hit, the code threw, or it does not parse.</summary>
public sealed class ScriptCodeException(string message) : Exception(message);

/// <summary>A syntax error in a code step's code: its message and where it is in the code (line and column from 1).</summary>
public sealed record ScriptCodeSyntaxError(string Message, int Line, int Column);

/// <summary>What a code step's function gave back (its returned value as JSON, null for no return) and the lines it logged.</summary>
public sealed record ScriptCodeOutcome(JsonNode? Result, IReadOnlyList<string> Log);

/// <summary>
/// The <c>code</c> step (design section 5.2): the body of a JavaScript function, run by Jint in strict mode. In scope are only
/// <c>inputs</c> (the evaluated inputs, as plain JSON) and <c>log(text)</c>; the engine is built without CLR access, modules or
/// host objects, so there is no require/import, file, network, timer or .NET type to reach.
/// </summary>
public static class ScriptCode
{
    public const string StepType = "code";

    // The function body goes on its own lines, so the code's line N is the script's line N + 1 and its columns are unchanged.
    private const string Prefix = "(function () {\n";
    private const string Suffix = "\n})()";
    private const int LineOffset = 1;

    /// <summary>Parses the code as a function body in strict mode; null when it parses.</summary>
    public static ScriptCodeSyntaxError? Parse(string code)
    {
        try
        {
            JsEngine.PrepareScript(Wrap(code), source: null, strict: true);
            return null;
        }
        catch (Exception e) when (SyntaxError(e) is { } error)
        {
            return error;
        }
    }

    private static string Wrap(string code) => Prefix + code + Suffix;

    private static ScriptCodeSyntaxError? SyntaxError(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
            if (x is ParseErrorException p)
            {
                var line = Math.Max(1, p.LineNumber - LineOffset);
                return new ScriptCodeSyntaxError(p.Description, line, p.Column + 1); // Acornima counts columns from 0
            }
        return null;
    }

    /// <summary>
    /// Runs the code with <paramref name="inputs"/> under <paramref name="limits"/>. The returned value comes back as JSON
    /// (undefined and no return give null). A limit, a throw or a syntax error throws <see cref="ScriptCodeException"/> with
    /// the reason; <paramref name="ct"/> (the run's timeout, the app closing) throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public static ScriptCodeOutcome Run(string code, JsonObject inputs, ScriptCodeLimits limits, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var log = new List<string>();
        var engine = new JsEngine(o => o
            .Strict()
            .TimeoutInterval(limits.Timeout)
            .LimitMemory(limits.MemoryBytes)
            .MaxStatements(limits.MaxStatements)
            .LimitRecursion(limits.MaxRecursion)
            .CancellationToken(ct));
        try
        {
            engine.SetValue("inputs", new JsonParser(engine).Parse(inputs.ToJsonString()));
            engine.SetValue("log", new ClrFunction(engine, "log", (_, args) =>
            {
                if (log.Count < ScriptCodeLimits.MaxLogLines)
                {
                    var line = string.Join(" ", args.Select(a => Text(engine, a)));
                    log.Add(line.Length > ScriptCodeLimits.MaxLogLineLength ? line[..ScriptCodeLimits.MaxLogLineLength] + "â€¦" : line);
                }
                return JsValue.Undefined;
            }));

            // Prepared first, so a syntax error is reported as one (with its line and column), not as a thrown SyntaxError.
            var script = JsEngine.PrepareScript(Wrap(code), source: null, strict: true);
            var value = engine.Evaluate(script);
            JsonNode? result = null;
            if (!value.IsUndefined() && !value.IsNull())
            {
                var json = new JsonSerializer(engine).Serialize(value);
                result = json.IsUndefined() ? null : JsonNode.Parse(json.AsString());
            }
            return new ScriptCodeOutcome(result, log);
        }
        catch (ExecutionCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Exception e) when (Reason(e, limits) is { } reason)
        {
            throw new ScriptCodeException(reason) { Data = { ["log"] = log } };
        }
    }

    /// <summary>The lines a failed run logged before it failed (kept on the exception).</summary>
    public static IReadOnlyList<string> LogOf(ScriptCodeException e) => e.Data["log"] as IReadOnlyList<string> ?? [];

    private static string? Reason(Exception e, ScriptCodeLimits limits) => e switch
    {
        TimeoutException => $"the code ran longer than {Duration(limits.Timeout)}",
        MemoryLimitExceededException => $"the code used more than {limits.MemoryBytes / (1024 * 1024)} MB of memory",
        StatementsCountOverflowException => $"the code ran more than {limits.MaxStatements.ToString("N0", CultureInfo.InvariantCulture)} statements",
        RecursionDepthOverflowException => $"the code went deeper than {limits.MaxRecursion} nested calls",
        JavaScriptException js => $"the code threw {js.Message}{At(js.Location.Start.Line)}",
        _ when SyntaxError(e) is { } s => $"the code does not parse: {s.Message} (line {s.Line}, column {s.Column})",
        ExecutionCanceledException => "the code was stopped",
        // Anything else the engine throws (an internal limit, a host error) fails the step too; a cancellation goes on up.
        OperationCanceledException => null,
        _ => $"the code failed: {e.Message}",
    };

    private static string At(int scriptLine) => scriptLine > LineOffset ? $" (line {scriptLine - LineOffset})" : "";

    private static string Duration(TimeSpan t) =>
        t.TotalSeconds >= 1 && t.TotalMilliseconds % 1000 == 0
            ? $"{t.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s"
            : $"{t.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms";

    private static string Text(JsEngine engine, JsValue value)
    {
        if (value.IsString()) return value.AsString();
        if (value.IsUndefined()) return "undefined";
        if (value.IsObject() && !value.IsArray() && value.AsObject() is Jint.Native.Function.Function) return value.ToString();
        try
        {
            var json = new JsonSerializer(engine).Serialize(value);
            return json.IsUndefined() ? value.ToString() : json.AsString();
        }
        catch (JavaScriptException)
        {
            return value.ToString();
        }
    }
}
