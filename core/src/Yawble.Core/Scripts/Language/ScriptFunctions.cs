namespace Yawble.Core.Scripts.Language;

/// <summary>A function of the expression language as the designer offers it: its name, its signature ("count(list) -> int") and what it does.</summary>
public sealed record ScriptFunctionInfo(string Name, string Signature, string Description);

/// <summary>Every function <see cref="ScriptExpressionParser.Functions"/> knows, with the types the checker gives its arguments and result.</summary>
public static class ScriptFunctions
{
    public static readonly IReadOnlyList<ScriptFunctionInfo> All =
    [
        new("now", "now() -> datetime", "The time now (UTC)."),
        new("today", "today() -> date", "Today's date (UTC)."),
        new("count", "count(list) -> int", "How many items a list has (or characters a text has)."),
        new("first", "first(list) -> item", "A list's first item, or null when it is empty."),
        new("last", "last(list) -> item", "A list's last item, or null when it is empty."),
        new("max", "max(list, 'field'?) -> item", "The largest item, or the largest value of an item's field."),
        new("min", "min(list, 'field'?) -> item", "The smallest item, or the smallest value of an item's field."),
        new("addHours", "addHours(datetime, number) -> datetime", "A time some hours later (earlier when negative)."),
        new("addDays", "addDays(date or datetime, number) -> date or datetime", "A date or time some days later (earlier when negative)."),
        new("hoursSince", "hoursSince(datetime) -> float", "Hours from a time until now."),
        new("formatDuration", "formatDuration(duration) -> string", "A duration as short text, e.g. 2h 5m."),
        new("lower", "lower(string) -> string", "Text in lower case."),
        new("upper", "upper(string) -> string", "Text in upper case."),
        new("contains", "contains(string or list, value) -> bool", "Whether text contains other text, or a list contains a value."),
        new("startsWith", "startsWith(string, string) -> bool", "Whether text starts with other text."),
        new("matches", "matches(string, regex) -> bool", "Whether text matches a regular expression."),
        new("join", "join(list, string) -> string", "A list's items as one text, with a separator between them."),
        new("coalesce", "coalesce(a, b, ...) -> value", "The first value that is not null (2 to 8 values)."),
    ];
}
