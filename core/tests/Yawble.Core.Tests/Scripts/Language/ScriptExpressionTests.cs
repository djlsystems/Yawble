using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Scripts.Language;

namespace Yawble.Core.Tests.Scripts.Language;

public sealed class ScriptExpressionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 18, 0, 0, TimeSpan.Zero);

    private static ScriptEvaluator Evaluator() => new(new ScriptScope(new Dictionary<string, JsonNode?>
    {
        ["input"] = JsonNode.Parse("""{ "triggerId": "t1", "kind": "every", "firedAt": "2026-11-23T17:55:00Z" }"""),
        ["settings"] = JsonNode.Parse("""{ "folder": "INBOX", "recipients": ["+1555", "+1666"], "deadline": 24 }"""),
        ["vars"] = JsonNode.Parse("""
            { "sent": 2, "ratio": 1.5, "name": "Ann", "empty": "", "none": null, "list": [3, 1, 2],
              "messages": [
                { "subject": "Order 1", "from": { "address": "a@x.com" }, "receivedAt": "2026-11-23T10:00:00Z" },
                { "subject": "Proof ok", "from": { "address": "b@x.com" }, "receivedAt": "2026-11-23T12:30:00Z" } ],
              "odd-name": 7 }
            """),
        ["steps"] = JsonNode.Parse("""{ "read": "file text", "lines": ["a", "b"] }"""),
        ["m"] = JsonNode.Parse("""{ "subject": "Hello", "from": { "address": "c@x.com" } }"""),
    }), new FakeClock(Now));

    private static object? Eval(string template) => Evaluator().EvaluateTemplate(template);

    // ---- templates ----------------------------------------------------------------------------------------------------

    [Fact]
    public void AWholeExpressionKeepsItsType_AMixedTemplateIsText()
    {
        Assert.Equal(2L, Eval("{vars.sent}"));
        Assert.Equal("Sent 2 to Ann", Eval("Sent {vars.sent} to {vars.name}"));
        Assert.Equal("plain", Eval("plain"));
        Assert.Equal("{literal} braces", Eval("{{literal}} braces"));
        Assert.Equal("x", Eval("{vars.none}x"));
        Assert.Equal("""[3,1,2]""", Eval("list: {vars.list}")?.ToString()?[6..]);
    }

    [Theory]
    [InlineData("{vars.sent")]
    [InlineData("vars.sent}")]
    [InlineData("{}")]
    [InlineData("{vars.}")]
    [InlineData("{1 +}")]
    [InlineData("{'open}")]
    [InlineData("{nope(1)}")]
    [InlineData("{count()}")]
    [InlineData("{count(1, 2)}")]
    [InlineData("{a = b}")]
    public void AMalformedTemplateIsRefused(string template) =>
        Assert.Throws<ScriptExpressionException>(() => ScriptTemplate.Parse(template));

    [Fact]
    public void BracesInsideQuotesAreText()
    {
        Assert.Equal(true, Eval("{matches('abc123', '[a-z]{3}\\d+')}"));
        Assert.Equal("it's", Eval("{'it\\'s'}"));
    }

    // ---- paths --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("{input.kind}", "every")]
    [InlineData("{settings.folder}", "INBOX")]
    [InlineData("{settings.recipients[1]}", "+1666")]
    [InlineData("{vars.messages[0].from.address}", "a@x.com")]
    [InlineData("{vars['odd-name']}", 7L)]
    [InlineData("{m.subject}", "Hello")]
    [InlineData("{m.from.address}", "c@x.com")]
    [InlineData("{steps.read}", "file text")]
    [InlineData("{steps.lines[1]}", "b")]
    public void PathsResolve(string template, object expected) => Assert.Equal(expected, Eval(template));

    [Fact]
    public void AMissingFieldOrItemIsNull_AnUnknownRootFails()
    {
        Assert.Null(Eval("{vars.nothing}"));
        Assert.Null(Eval("{vars.list[9]}"));
        Assert.Null(Eval("{vars.none.deeper}"));
        Assert.Throws<ScriptValueException>(() => Eval("{nowhere.x}"));
        Assert.Throws<ScriptValueException>(() => Eval("{vars.sent.field}"));
        Assert.Throws<ScriptValueException>(() => Eval("{vars.list.field}"));
    }

    // ---- operators ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("{vars.sent + 1}", 3L)]
    [InlineData("{vars.sent - 5}", -3L)]
    [InlineData("{vars.sent * 4}", 8L)]
    [InlineData("{vars.sent / 2}", 1L)]
    [InlineData("{5 / 2}", 2.5)]
    [InlineData("{vars.ratio * 2}", 3.0)]
    [InlineData("{1 + 2 * 3}", 7L)]
    [InlineData("{(1 + 2) * 3}", 9L)]
    [InlineData("{-vars.sent + 1}", -1L)]
    [InlineData("{10 - 2 - 3}", 5L)]
    [InlineData("{'a' + 1}", "a1")]
    [InlineData("{vars.name + ' and Bob'}", "Ann and Bob")]
    public void Arithmetic(string template, object expected) => Assert.Equal(expected, Eval(template));

    [Fact]
    public void TimeArithmetic()
    {
        Assert.Equal(Now.AddHours(26), Eval("{now() + 'PT26H'}"));
        Assert.Equal(TimeSpan.FromMinutes(5), Eval("{now() - input.firedAt}"));
        Assert.Equal(TimeSpan.FromHours(2), Eval("{'2026-11-23T12:00:00Z' - '2026-11-23T10:00:00Z'}"));
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 17, 0, 0, TimeSpan.Zero), Eval("{input.firedAt - 'PT55M'}"));
    }

    [Theory]
    [InlineData("{1 / 0}")]
    [InlineData("{vars.name * 2}")]
    [InlineData("{vars.list - 1}")]
    [InlineData("{-vars.name}")]
    [InlineData("{vars.name < 3}")]
    public void ABadOperationFails(string template) => Assert.Throws<ScriptValueException>(() => Eval(template));

    [Theory]
    [InlineData("{vars.sent == 2}", true)]
    [InlineData("{vars.sent != 2}", false)]
    [InlineData("{vars.sent > 1}", true)]
    [InlineData("{vars.sent >= 3}", false)]
    [InlineData("{vars.sent < 2.5}", true)]
    [InlineData("{vars.sent <= 2}", true)]
    [InlineData("{vars.name == 'Ann'}", true)]
    [InlineData("{vars.none == null}", true)]
    [InlineData("{'b' > 'a'}", true)]
    [InlineData("{input.firedAt < now()}", true)]
    [InlineData("{'2026-11-23T10:00:00+01:00' < '2026-11-23T09:30:00Z'}", true)]
    [InlineData("{2 + 1 == 3}", true)]
    public void Comparisons(string template, bool expected) => Assert.Equal(expected, Eval(template));

    // ---- functions ----------------------------------------------------------------------------------------------------

    [Fact]
    public void NowAndToday()
    {
        Assert.Equal(Now, Eval("{now()}"));
        Assert.Equal(new DateOnly(2026, 11, 23), Eval("{today()}"));
        Assert.Equal("at 2026-11-23T18:00:00Z", Eval("at {now()}"));
    }

    [Theory]
    [InlineData("{count(vars.list)}", 3L)]
    [InlineData("{count(vars.messages)}", 2L)]
    [InlineData("{count(vars.none)}", 0L)]
    [InlineData("{count(vars.name)}", 3L)]
    [InlineData("{first(vars.list)}", 3L)]
    [InlineData("{last(vars.list)}", 2L)]
    [InlineData("{max(vars.list)}", 3L)]
    [InlineData("{min(vars.list)}", 1L)]
    [InlineData("{max(vars.messages, 'receivedAt')}", "2026-11-23T12:30:00Z")]
    [InlineData("{min(vars.messages, 'receivedAt')}", "2026-11-23T10:00:00Z")]
    [InlineData("{max(vars.messages, 'subject')}", "Proof ok")]
    [InlineData("{lower('MiXed')}", "mixed")]
    [InlineData("{upper(vars.name)}", "ANN")]
    [InlineData("{contains(vars.name, 'nn')}", true)]
    [InlineData("{contains(vars.list, 2)}", true)]
    [InlineData("{contains(vars.list, 9)}", false)]
    [InlineData("{contains(vars.none, 'x')}", false)]
    [InlineData("{startsWith(vars.name, 'An')}", true)]
    [InlineData("{startsWith(vars.name, 'nn')}", false)]
    [InlineData("{matches(vars.messages[0].subject, '^Order \\d+$')}", true)]
    [InlineData("{matches(vars.name, '^B')}", false)]
    [InlineData("{join(settings.recipients, ', ')}", "+1555, +1666")]
    [InlineData("{join(vars.list, '-')}", "3-1-2")]
    [InlineData("{coalesce(vars.none, 'fallback')}", "fallback")]
    [InlineData("{coalesce(vars.name, 'fallback')}", "Ann")]
    [InlineData("{coalesce(vars.none, vars.nothing, 5)}", 5L)]
    [InlineData("{formatDuration('PT26H')}", "26h")]
    [InlineData("{formatDuration('PT1H5M')}", "1h 5m")]
    [InlineData("{formatDuration('PT12M')}", "12m")]
    [InlineData("{formatDuration('PT40S')}", "40s")]
    [InlineData("{formatDuration(1.5)}", "1h 30m")]
    [InlineData("No response for {formatDuration(now() - '2026-11-22T16:00:00Z')}: x", "No response for 26h: x")]
    public void Functions(string template, object? expected) => Assert.Equal(expected, Eval(template));

    [Fact]
    public void TimeFunctions()
    {
        Assert.Equal(Now.AddHours(3), Eval("{addHours(now(), 3)}"));
        Assert.Equal(Now.AddHours(-1.5), Eval("{addHours(now(), -1.5)}"));
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 17, 55, 0, TimeSpan.Zero), Eval("{addDays(input.firedAt, 1)}"));
        Assert.Equal(new DateOnly(2026, 11, 30), Eval("{addDays(today(), 7)}"));
        Assert.Equal(new DateOnly(2026, 11, 22), Eval("{addDays('2026-11-23', -1)}"));
        Assert.Equal(8.0, Eval("{hoursSince(vars.messages[0].receivedAt)}"));
        Assert.Null(Eval("{hoursSince(vars.none)}"));
        Assert.Equal("26h", Eval("{formatDuration(now() - addHours(now(), -26))}"));
    }

    [Theory]
    [InlineData("{first(vars.name)}")]
    [InlineData("{max(vars.list, 1)}")]
    [InlineData("{addHours(vars.name, 1)}")]
    [InlineData("{addDays(today(), 1.5)}")]
    [InlineData("{matches(vars.name, '(')}")]
    [InlineData("{count(vars.sent)}")]
    [InlineData("{formatDuration(vars.name)}")]
    public void AFunctionOnTheWrongValueFails(string template) => Assert.Throws<ScriptValueException>(() => Eval(template));

    // ---- values -------------------------------------------------------------------------------------------------------

    [Fact]
    public void AValueEvaluatesEveryTemplateInIt()
    {
        var value = Evaluator().EvaluateValue(JsonNode.Parse("""
            { "To": "{settings.recipients[0]}", "Count": "{vars.sent}", "Nested": { "List": ["{vars.name}", 1, true, null] }, "Plain": 5 }
            """));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            { "To": "+1555", "Count": 2, "Nested": { "List": ["Ann", 1, true, null] }, "Plain": 5 }
            """), value));
    }

    [Fact]
    public void TimesAreStoredAsIsoText()
    {
        Assert.Equal("2026-11-23T18:00:00Z", ScriptValues.ToNode(Now)!.GetValue<string>());
        Assert.Equal("2026-11-23", ScriptValues.ToNode(new DateOnly(2026, 11, 23))!.GetValue<string>());
        Assert.Equal("P1DT2H", ScriptValues.ToNode(TimeSpan.FromHours(26))!.GetValue<string>());
        Assert.True(ScriptValues.TryParseDuration("PT26H", out var d) && d == TimeSpan.FromHours(26));
    }
}

public sealed class ScriptConditionTests
{
    private static bool When(string condition)
    {
        var ev = new ScriptEvaluator(new ScriptScope(new Dictionary<string, JsonNode?>
        {
            ["vars"] = JsonNode.Parse("""{ "n": 3, "s": "Order 42", "list": ["a", "b"], "empty": [], "blank": "", "none": null, "at": "2026-11-23T10:00:00Z" }"""),
        }));
        return ScriptCondition.Parse(JsonNode.Parse(condition)).Evaluate(ev);
    }

    [Theory]
    [InlineData("""{ "left": "{vars.n}", "op": "eq", "right": 3 }""", true)]
    [InlineData("""{ "left": "{vars.n}", "op": "ne", "right": 3 }""", false)]
    [InlineData("""{ "left": "{vars.n}", "op": "gt", "right": 2 }""", true)]
    [InlineData("""{ "left": "{vars.n}", "op": "ge", "right": 3 }""", true)]
    [InlineData("""{ "left": "{vars.n}", "op": "lt", "right": 3 }""", false)]
    [InlineData("""{ "left": "{vars.n}", "op": "le", "right": 3 }""", true)]
    [InlineData("""{ "left": "{vars.at}", "op": "lt", "right": "2026-11-23T11:00:00Z" }""", true)]
    [InlineData("""{ "left": "{vars.s}", "op": "contains", "right": "42" }""", true)]
    [InlineData("""{ "left": "{vars.list}", "op": "contains", "right": "b" }""", true)]
    [InlineData("""{ "left": "{vars.list}", "op": "contains", "right": "c" }""", false)]
    [InlineData("""{ "left": "{vars.s}", "op": "startsWith", "right": "Order" }""", true)]
    [InlineData("""{ "left": "{vars.s}", "op": "matches", "right": "^Order \\d+$" }""", true)]
    [InlineData("""{ "left": "{vars.s}", "op": "exists" }""", true)]
    [InlineData("""{ "left": "{vars.none}", "op": "exists" }""", false)]
    [InlineData("""{ "left": "{vars.missing}", "op": "exists" }""", false)]
    [InlineData("""{ "left": "{vars.empty}", "op": "empty" }""", true)]
    [InlineData("""{ "left": "{vars.blank}", "op": "empty" }""", true)]
    [InlineData("""{ "left": "{vars.none}", "op": "empty" }""", true)]
    [InlineData("""{ "left": "{vars.list}", "op": "empty" }""", false)]
    [InlineData("""{ "left": "{vars.none}", "op": "gt", "right": 1 }""", false)]
    [InlineData("""{ "left": "Order {vars.n}", "op": "eq", "right": "Order 3" }""", true)]
    public void EachOperator(string condition, bool expected) => Assert.Equal(expected, When(condition));

    [Theory]
    [InlineData("""{ "all": [ { "left": "{vars.n}", "op": "gt", "right": 1 }, { "left": "{vars.s}", "op": "exists" } ] }""", true)]
    [InlineData("""{ "all": [ { "left": "{vars.n}", "op": "gt", "right": 1 }, { "left": "{vars.none}", "op": "exists" } ] }""", false)]
    [InlineData("""{ "any": [ { "left": "{vars.n}", "op": "gt", "right": 5 }, { "left": "{vars.s}", "op": "exists" } ] }""", true)]
    [InlineData("""{ "any": [ { "left": "{vars.n}", "op": "gt", "right": 5 }, { "left": "{vars.none}", "op": "exists" } ] }""", false)]
    [InlineData("""{ "not": { "left": "{vars.n}", "op": "gt", "right": 5 } }""", true)]
    [InlineData("""{ "all": [] }""", true)]
    [InlineData("""{ "any": [] }""", false)]
    [InlineData("""{ "not": { "any": [ { "all": [ { "left": 1, "op": "eq", "right": 1 } ] } ] } }""", false)]
    [InlineData("\"{vars.n > 2}\"", true)]
    [InlineData("false", false)]
    public void AllAnyNotAndTemplates(string condition, bool expected) => Assert.Equal(expected, When(condition));

    [Theory]
    [InlineData("""{ "left": 1, "op": "is", "right": 1 }""")]
    [InlineData("""{ "left": 1, "op": "eq" }""")]
    [InlineData("""{ "op": "exists" }""")]
    [InlineData("""{ "something": 1 }""")]
    [InlineData("3")]
    public void AMalformedConditionIsRefused(string condition) =>
        Assert.Throws<ScriptExpressionException>(() => ScriptCondition.Parse(JsonNode.Parse(condition)));

    [Fact]
    public void ComparingWhatDoesNotCompareFails() =>
        Assert.Throws<ScriptValueException>(() => When("""{ "left": "{vars.list}", "op": "gt", "right": 1 }"""));
}
