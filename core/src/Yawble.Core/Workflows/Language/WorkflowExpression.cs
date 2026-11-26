using System.Globalization;
using System.Text;

namespace Yawble.Core.Workflows.Language;

/// <summary>A malformed expression or template; the checker reports it, a run fails the step with it.</summary>
public sealed class WorkflowExpressionException(string message) : Exception(message);

/// <summary>An expression inside <c>{...}</c>: a literal, a path, a function call, an operator.</summary>
public abstract record WorkflowExpr;

public sealed record LiteralExpr(object? Value) : WorkflowExpr;

/// <summary>A path: a root name (<c>input</c>, <c>settings</c>, <c>vars</c>, <c>steps</c>, a loop's name) and its fields
/// (<c>.name</c> or <c>['name']</c>) and list items (<c>[0]</c>).</summary>
public sealed record PathExpr(string Root, IReadOnlyList<object> Segments) : WorkflowExpr
{
    public override string ToString() =>
        Root + string.Concat(Segments.Select(s => s is int i ? $"[{i}]" : "." + s));
}

public sealed record CallExpr(string Function, IReadOnlyList<WorkflowExpr> Args) : WorkflowExpr;

/// <summary><c>+ - * /</c>, or a comparison <c>== != &lt; &lt;= &gt; &gt;=</c>.</summary>
public sealed record BinaryExpr(string Op, WorkflowExpr Left, WorkflowExpr Right) : WorkflowExpr;

public sealed record NegateExpr(WorkflowExpr Operand) : WorkflowExpr;

/// <summary>
/// A template string: text with <c>{expression}</c> parts (<c>{{</c> and <c>}}</c> are literal braces). A template that is
/// exactly one <c>{expression}</c> gives that expression's value with its type; any other gives text.
/// </summary>
public sealed class WorkflowTemplate
{
    /// <summary>Each part is a string (literal text) or a <see cref="WorkflowExpr"/>.</summary>
    public IReadOnlyList<object> Parts { get; }

    public bool IsSingleExpression => Parts.Count == 1 && Parts[0] is WorkflowExpr;
    public bool HasExpressions => Parts.Any(p => p is WorkflowExpr);
    public IEnumerable<WorkflowExpr> Expressions => Parts.OfType<WorkflowExpr>();

    private WorkflowTemplate(IReadOnlyList<object> parts) => Parts = parts;

    public static WorkflowTemplate Parse(string text)
    {
        var parts = new List<object>();
        var literal = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '{' && i + 1 < text.Length && text[i + 1] == '{') { literal.Append('{'); i += 2; continue; }
            if (c == '}' && i + 1 < text.Length && text[i + 1] == '}') { literal.Append('}'); i += 2; continue; }
            if (c == '}') throw new WorkflowExpressionException($"'}}' without '{{' at {i} in \"{text}\" (write }}}} for a brace)");
            if (c != '{') { literal.Append(c); i++; continue; }

            var end = FindClose(text, i + 1);
            if (literal.Length > 0) { parts.Add(literal.ToString()); literal.Clear(); }
            parts.Add(WorkflowExpressionParser.Parse(text[(i + 1)..end]));
            i = end + 1;
        }
        if (literal.Length > 0) parts.Add(literal.ToString());
        return new WorkflowTemplate(parts);
    }

    /// <summary>The '}' closing the expression that starts at <paramref name="start"/>, skipping quoted text.</summary>
    private static int FindClose(string text, int start)
    {
        char? quote = null;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is { } q)
            {
                if (c == '\\') { i++; continue; }
                if (c == q) quote = null;
                continue;
            }
            if (c is '\'' or '"') quote = c;
            else if (c == '{') throw new WorkflowExpressionException($"'{{' inside an expression in \"{text}\"");
            else if (c == '}') return i;
        }
        throw new WorkflowExpressionException($"'{{' without '}}' in \"{text}\"");
    }
}

/// <summary>Parses the expression language: paths, literals, <c>+ - * /</c>, comparisons and the fixed functions.</summary>
public static class WorkflowExpressionParser
{
    /// <summary>The functions an expression can call, with how many arguments each takes (min, max).</summary>
    public static readonly IReadOnlyDictionary<string, (int Min, int Max)> Functions = new Dictionary<string, (int, int)>
    {
        ["now"] = (0, 0), ["today"] = (0, 0),
        ["count"] = (1, 1), ["first"] = (1, 1), ["last"] = (1, 1),
        ["max"] = (1, 2), ["min"] = (1, 2),
        ["addHours"] = (2, 2), ["addDays"] = (2, 2), ["hoursSince"] = (1, 1), ["formatDuration"] = (1, 1),
        ["lower"] = (1, 1), ["upper"] = (1, 1),
        ["contains"] = (2, 2), ["startsWith"] = (2, 2), ["matches"] = (2, 2),
        ["join"] = (2, 2), ["coalesce"] = (2, 8),
    };

    public static WorkflowExpr Parse(string text)
    {
        var p = new Parser(text);
        var e = p.Expression();
        p.ExpectEnd();
        return e;
    }

    private sealed class Parser(string text)
    {
        private int _i;

        public void ExpectEnd()
        {
            Skip();
            if (_i < text.Length) throw Error($"unexpected '{text[_i]}'");
        }

        public WorkflowExpr Expression()
        {
            var left = Additive();
            Skip();
            foreach (var op in new[] { "==", "!=", "<=", ">=", "<", ">" })
                if (Accept(op)) return new BinaryExpr(op, left, Additive());
            return left;
        }

        private WorkflowExpr Additive()
        {
            var e = Multiplicative();
            while (true)
            {
                Skip();
                if (Accept("+")) e = new BinaryExpr("+", e, Multiplicative());
                else if (Accept("-")) e = new BinaryExpr("-", e, Multiplicative());
                else return e;
            }
        }

        private WorkflowExpr Multiplicative()
        {
            var e = Unary();
            while (true)
            {
                Skip();
                if (Accept("*")) e = new BinaryExpr("*", e, Unary());
                else if (Accept("/")) e = new BinaryExpr("/", e, Unary());
                else return e;
            }
        }

        private WorkflowExpr Unary()
        {
            Skip();
            if (Accept("-")) return new NegateExpr(Unary());
            return Primary();
        }

        private WorkflowExpr Primary()
        {
            Skip();
            if (_i >= text.Length) throw Error("an expression is missing");
            var c = text[_i];
            if (Accept("("))
            {
                var inner = Expression();
                Skip();
                if (!Accept(")")) throw Error("')' expected");
                return inner;
            }
            if (c is '\'' or '"') return new LiteralExpr(String());
            if (char.IsDigit(c)) return new LiteralExpr(Number());
            if (char.IsLetter(c) || c == '_')
            {
                var name = Identifier();
                switch (name)
                {
                    case "true": return new LiteralExpr(true);
                    case "false": return new LiteralExpr(false);
                    case "null": return new LiteralExpr(null);
                }
                Skip();
                if (Accept("("))
                {
                    if (!Functions.TryGetValue(name, out var arity)) throw Error($"'{name}' is not a function (functions: {string.Join(", ", Functions.Keys)})");
                    var args = new List<WorkflowExpr>();
                    Skip();
                    if (!Accept(")"))
                    {
                        do args.Add(Expression()); while (SkipThen(","));
                        Skip();
                        if (!Accept(")")) throw Error("')' or ',' expected");
                    }
                    if (args.Count < arity.Min || args.Count > arity.Max)
                        throw Error($"{name}() takes {(arity.Min == arity.Max ? arity.Min.ToString(CultureInfo.InvariantCulture) : $"{arity.Min} to {arity.Max}")} argument(s), not {args.Count}");
                    return new CallExpr(name, args);
                }
                var segments = new List<object>();
                while (true)
                {
                    if (_i < text.Length && text[_i] == '.')
                    {
                        _i++;
                        if (_i >= text.Length || !(char.IsLetter(text[_i]) || text[_i] == '_')) throw Error("a field name is expected after '.'");
                        segments.Add(Identifier());
                    }
                    else if (_i < text.Length && text[_i] == '[')
                    {
                        _i++;
                        Skip();
                        if (_i < text.Length && text[_i] is '\'' or '"') segments.Add(String());
                        else if (_i < text.Length && char.IsDigit(text[_i]) && Number() is long n and <= int.MaxValue) segments.Add((int)n);
                        else throw Error("a list index or a quoted field name is expected in [ ]");
                        Skip();
                        if (!Accept("]")) throw Error("']' expected");
                    }
                    else break;
                }
                return new PathExpr(name, segments);
            }
            throw Error($"unexpected '{c}'");
        }

        private bool SkipThen(string s) { Skip(); return Accept(s); }

        private string Identifier()
        {
            var start = _i;
            while (_i < text.Length && (char.IsLetterOrDigit(text[_i]) || text[_i] == '_')) _i++;
            return text[start.._i];
        }

        private object Number()
        {
            var start = _i;
            while (_i < text.Length && char.IsDigit(text[_i])) _i++;
            if (_i + 1 < text.Length && text[_i] == '.' && char.IsDigit(text[_i + 1]))
            {
                _i++;
                while (_i < text.Length && char.IsDigit(text[_i])) _i++;
                return double.Parse(text[start.._i], CultureInfo.InvariantCulture);
            }
            return long.TryParse(text[start.._i], NumberStyles.None, CultureInfo.InvariantCulture, out var l)
                ? l : throw Error("the number is too large");
        }

        /// <summary>A quoted string; <c>\'</c>, <c>\"</c> and <c>\\</c> are escapes, any other backslash is kept (regexes).</summary>
        private string String()
        {
            var quote = text[_i++];
            var sb = new StringBuilder();
            while (_i < text.Length && text[_i] != quote)
            {
                if (text[_i] == '\\' && _i + 1 < text.Length && (text[_i + 1] == quote || text[_i + 1] == '\\'))
                {
                    sb.Append(text[_i + 1]);
                    _i += 2;
                    continue;
                }
                sb.Append(text[_i++]);
            }
            if (_i >= text.Length) throw Error("unterminated string");
            _i++;
            return sb.ToString();
        }

        private void Skip() { while (_i < text.Length && char.IsWhiteSpace(text[_i])) _i++; }

        private bool Accept(string s)
        {
            if (string.CompareOrdinal(text, _i, s, 0, s.Length) != 0) return false;
            _i += s.Length;
            return true;
        }

        private WorkflowExpressionException Error(string what) => new($"{what} at {_i} in \"{text}\"");
    }
}
