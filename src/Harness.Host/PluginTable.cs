using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Harness.Host;

/// <summary>
/// A <c>"type": "table"</c> CONFIG FIELD: rows a person adds, edits, duplicates and deletes on the
/// member's settings, each a row of typed columns, keyed by one string column (<see cref="Key"/>)
/// unique per member ignoring case.
///
/// <list type="bullet">
/// <item>A column is <c>string</c>, <c>number</c> (<c>min</c>, <c>max</c>, <c>integer</c>),
/// <c>bool</c>, <c>enum</c> (its <c>enum</c> list) or <c>secret</c> (<c>masked</c> unless it says
/// <c>false</c>). Any column may be <c>readOnly</c>: written only by the plugin's <c>row.status</c>
/// record (or, for a secret column, by the platform), never by a person's save.</item>
/// <item>The rows are stored as the field's config value, an array of row objects holding every cell
/// BUT a secret one. A secret cell is the member's own secret (<see cref="PluginMemberSecretStore"/>,
/// <see cref="PluginMemberSecretStore.CellField"/>): never in <c>config</c>, never answered once
/// saved, handed to a run under <c>secrets.&lt;field&gt;.&lt;row&gt;.&lt;column&gt;</c>.</item>
/// <item>A table is ALWAYS person-only, as <c>"setBy": "person"</c>: an agent's hire, a Manager and a
/// package may leave it empty and nothing else, and only the HumansOnly settings route changes it.</item>
/// </list>
/// </summary>
public sealed partial record PluginTable(string Key, IReadOnlyList<PluginTableColumn> Columns, int MaxRows)
{
    /// <summary>The most rows a table holds when its manifest says nothing.</summary>
    public const int DefaultMaxRows = 100;

    /// <summary>The most rows a manifest may let a table hold.</summary>
    public const int MostMaxRows = 500;

    /// <summary>The longest text a string cell holds.</summary>
    public const int MaxTextLength = 2048;

    /// <summary>The column types a table takes.</summary>
    public static readonly IReadOnlyList<string> ColumnTypes = ["string", "number", "bool", "enum", "secret"];

    /// <summary>The per-row actions the platform knows, by id. <c>sign-in</c> is the interactive
    /// sign-in page's link for the row.</summary>
    public static readonly IReadOnlyList<string> KnownRowActions = ["sign-in"];

    /// <summary>The manifest's <c>rowActions</c>: a link the settings screen shows on each SAVED row
    /// whose <see cref="PluginTableRowAction.Column"/> holds <see cref="PluginTableRowAction.EqualTo"/>.</summary>
    public IReadOnlyList<PluginTableRowAction> RowActions { get; init; } = [];

    /// <summary>A row's key: a letter or digit, then letters, digits, <c>-</c> and <c>_</c>, at most
    /// 64 characters.</summary>
    public static bool IsRowName(string? name) => name is not null && RowNamePattern().IsMatch(name);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex RowNamePattern();

    public PluginTableColumn? Column(string name) => Columns.FirstOrDefault(c => c.Name == name);

    /// <summary>The secret columns, in the manifest's order.</summary>
    public IEnumerable<PluginTableColumn> SecretColumns => Columns.Where(c => c.IsSecret);

    /// <summary>The row's key, or null when the row has none that is a usable name.</summary>
    public string? RowName(JsonElement row) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(Key, out var key) && key.ValueKind == JsonValueKind.String
            && IsRowName(key.GetString()) ? key.GetString() : null;

    /// <summary>The row names of a stored or sent value, in order; none for a value that is not an array.</summary>
    public IReadOnlyList<string> RowNames(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Array } rows ? [.. rows.EnumerateArray().Select(RowName).OfType<string>()] : [];

    /// <summary>The stored row named <paramref name="row"/> (ignoring case), or null.</summary>
    public JsonElement? Row(JsonElement? value, string row) =>
        value is { ValueKind: JsonValueKind.Array } rows
            ? rows.EnumerateArray().Where(r => string.Equals(RowName(r), row, StringComparison.OrdinalIgnoreCase)).Select(r => (JsonElement?)r).FirstOrDefault()
            : null;

    public static (PluginTable? Table, string? Refusal) Parse(string name, JsonElement element)
    {
        var at = $"config.{name}";

        if (element.TryGetProperty("default", out _)) return (null, $"`{at}.default` is not allowed: a table starts with no rows.");

        if (element.TryGetProperty("setBy", out var setBy) && !(setBy.ValueKind == JsonValueKind.String && setBy.GetString() == "person"))
        {
            return (null, $"`{at}.setBy` must be \"person\" or omitted: only a person adds a table's rows.");
        }

        if (!element.TryGetProperty("columns", out var columnsElement) || columnsElement.ValueKind != JsonValueKind.Array
            || columnsElement.GetArrayLength() == 0)
        {
            return (null, $"`{at}.columns` must be a non-empty array of columns.");
        }

        var columns = new List<PluginTableColumn>();
        var index = 0;

        foreach (var columnElement in columnsElement.EnumerateArray())
        {
            var (column, refusal) = PluginTableColumn.Parse(at, index++, columnElement);
            if (refusal is not null) return (null, refusal);
            if (columns.Any(c => c.Name == column!.Name)) return (null, $"`{at}.columns` names `{column!.Name}` twice.");
            columns.Add(column!);
        }

        if (!element.TryGetProperty("key", out var keyElement) || keyElement.ValueKind != JsonValueKind.String)
        {
            return (null, $"`{at}.key` must name the column that names each row, such as \"name\".");
        }

        var key = keyElement.GetString()!;
        if (columns.FirstOrDefault(c => c.Name == key) is not { } keyColumn)
        {
            return (null, $"`{at}.key` names `{key}`, which is not one of its columns.");
        }

        if (keyColumn.Type != "string" || keyColumn.ReadOnly)
        {
            return (null, $"`{at}.key` must name a string column a person writes; `{key}` is not one.");
        }

        var maxRows = DefaultMaxRows;
        if (element.TryGetProperty("maxRows", out var max))
        {
            if (max.ValueKind != JsonValueKind.Number || !max.TryGetInt32(out maxRows) || maxRows is < 1 or > MostMaxRows)
            {
                return (null, $"`{at}.maxRows` must be a whole number from 1 to {MostMaxRows}.");
            }
        }

        var actions = new List<PluginTableRowAction>();
        if (element.TryGetProperty("rowActions", out var actionsElement))
        {
            if (actionsElement.ValueKind != JsonValueKind.Array) return (null, $"`{at}.rowActions` must be an array.");

            var i = 0;
            foreach (var actionElement in actionsElement.EnumerateArray())
            {
                var (action, refusal) = PluginTableRowAction.Parse($"{at}.rowActions[{i++}]", actionElement, columns);
                if (refusal is not null) return (null, refusal);
                actions.Add(action!);
            }
        }

        return (new PluginTable(key, columns, maxRows) { RowActions = actions }, null);
    }

    /// <summary>
    /// Why <paramref name="value"/> is not a valid table for field <paramref name="name"/>, or null:
    /// an array of at most <see cref="MaxRows"/> objects, each with a usable, unique (ignoring case)
    /// key, only declared columns, NO secret cell, every required column a person writes, and each cell
    /// of its column's type - and inside its bounds unless <paramref name="bounds"/> is false. The
    /// sentence names the field, the row and the column, as a hire's refusal names a field.
    /// </summary>
    public string? Refusal(string name, JsonElement value, bool bounds = true)
    {
        if (value.ValueKind != JsonValueKind.Array) return $"`{name}` must be a list of rows.";
        if (value.GetArrayLength() > MaxRows) return $"`{name}` holds {value.GetArrayLength()} rows; it takes at most {MaxRows}.";

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var position = 0;

        foreach (var row in value.EnumerateArray())
        {
            position++;
            if (row.ValueKind != JsonValueKind.Object) return $"`{name}` row {position} must be an object of cells.";

            var keyCell = row.TryGetProperty(Key, out var k) ? k : (JsonElement?)null;
            if (keyCell is not { ValueKind: JsonValueKind.String } || !IsRowName(keyCell.Value.GetString()))
            {
                return $"`{name}` row {position}: `{Key}` must be 1 to 64 letters, digits, `-` and `_`, starting with a letter or digit.";
            }

            var rowName = keyCell.Value.GetString()!;
            if (!seen.Add(rowName)) return $"`{name}` has two rows named '{rowName}'; each `{Key}` must be unique, ignoring case.";

            foreach (var cell in row.EnumerateObject())
            {
                var at = $"{name}[{rowName}].{cell.Name}";

                if (Column(cell.Name) is not { } column)
                {
                    return $"`{at}` is not a column of `{name}`. It has: {string.Join(", ", Columns.Select(c => c.Name))}.";
                }

                if (column.IsSecret)
                {
                    return $"`{at}` is a secret column: its value is never part of the settings. Type it in the row's "
                        + "secret box (`tableSecretValues`).";
                }

                if (column.CellRefusal(at, cell.Value, bounds) is { } refusal) return refusal;
            }

            foreach (var column in Columns.Where(c => c.Required && !c.ReadOnly && !c.IsSecret && c.Default is null))
            {
                if (!row.TryGetProperty(column.Name, out var cell) || cell.ValueKind == JsonValueKind.Null
                    || (cell.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(cell.GetString())))
                {
                    return $"`{name}[{rowName}].{column.Name}` is required.";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// WHAT A PERSON'S SAVE STORES: the rows <paramref name="sent"/>, every read-only cell dropped from
    /// them and the cells <paramref name="stored"/> holds for that row (matched by key, ignoring case)
    /// put back. A person never writes a read-only column, whatever the body says; a row the person
    /// keeps keeps its status. Answers <paramref name="sent"/> unchanged when it is not an array.
    /// </summary>
    public JsonElement Merge(JsonElement sent, JsonElement? stored)
    {
        if (sent.ValueKind != JsonValueKind.Array) return sent;
        if (!Columns.Any(c => c.ReadOnly)) return sent;

        var rows = new JsonArray();

        foreach (var row in sent.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                rows.Add(JsonNode.Parse(row.GetRawText()));
                continue;
            }

            var merged = new JsonObject();
            foreach (var cell in row.EnumerateObject())
            {
                if (Column(cell.Name) is { ReadOnly: true }) continue;
                merged[cell.Name] = JsonNode.Parse(cell.Value.GetRawText());
            }

            if (RowName(row) is { } rowName && Row(stored, rowName) is { } kept)
            {
                foreach (var cell in kept.EnumerateObject())
                {
                    if (Column(cell.Name) is { ReadOnly: true, IsSecret: false }) merged[cell.Name] = JsonNode.Parse(cell.Value.GetRawText());
                }
            }

            rows.Add(merged);
        }

        return JsonSerializer.SerializeToElement(rows);
    }

    /// <summary>The rows as a run receives them: each row's cells with every non-secret column's
    /// default filled in where the row has no cell. Never a secret cell.</summary>
    public JsonArray ForRun(JsonElement value)
    {
        var rows = new JsonArray();
        if (value.ValueKind != JsonValueKind.Array) return rows;

        foreach (var row in value.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;

            var cells = new JsonObject();
            foreach (var column in Columns.Where(c => !c.IsSecret))
            {
                if (row.TryGetProperty(column.Name, out var cell)) cells[column.Name] = JsonNode.Parse(cell.GetRawText());
                else if (column.Default is { } fallback) cells[column.Name] = JsonNode.Parse(fallback.GetRawText());
            }

            rows.Add(cells);
        }

        return rows;
    }

    /// <summary>The rows of <paramref name="before"/> that <paramref name="after"/> no longer has
    /// (by key, ignoring case): their secret cells go with them.</summary>
    public IReadOnlyList<string> Removed(JsonElement? before, JsonElement? after)
    {
        var kept = RowNames(after).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. RowNames(before).Where(row => !kept.Contains(row))];
    }

    /// <summary>The table as <c>GET /api/plugins</c> and the settings read answer it.</summary>
    public object Describe() => new
    {
        key = Key,
        maxRows = MaxRows,
        columns = Columns.Select(c => new
        {
            name = c.Name,
            type = c.Type,
            label = c.Label,
            description = c.Description,
            required = c.Required,
            @default = c.Default,
            @enum = c.Enum,
            min = c.Min,
            max = c.Max,
            integer = c.Integer,
            readOnly = c.ReadOnly,
            masked = c.IsSecret ? c.Masked : (bool?)null,
        }),
        rowActions = RowActions.Select(a => new { action = a.Action, label = a.Label, column = a.Column, equals = a.EqualTo }),
    };
}

/// <summary>One column of a <see cref="PluginTable"/>.</summary>
public sealed record PluginTableColumn(
    string Name,
    string Type,
    string? Label,
    string Description,
    bool Required,
    JsonElement? Default,
    IReadOnlyList<string>? Enum,
    double? Min,
    double? Max,
    bool Integer,
    bool ReadOnly,
    bool Masked)
{
    public bool IsSecret => Type == "secret";

    /// <summary>The ordinary field rules a non-secret cell is held to: the same type, <c>enum</c> and
    /// bounds sentences a config field's value gets.</summary>
    private PluginConfigField AsField => new(Type == "enum" ? "string" : Type, Description, Required, Default, Enum)
    {
        Min = Min,
        Max = Max,
        Integer = Integer,
    };

    /// <summary>Column <paramref name="index"/> of the table at <paramref name="table"/>
    /// (<c>config.&lt;field&gt;</c>); a refusal names it <c>config.&lt;field&gt;.columns.&lt;name&gt;</c>
    /// once it has a usable name.</summary>
    public static (PluginTableColumn? Column, string? Refusal) Parse(string table, int index, JsonElement element)
    {
        var at = $"{table}.columns[{index}]";
        if (element.ValueKind != JsonValueKind.Object) return (null, $"`{at}` must be an object.");

        if (!element.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String
            || !PluginManifest.IsConfigName(nameElement.GetString()!))
        {
            return (null, $"`{at}.name` must be a letter, then letters, digits, `-` and `_`, at most 64 characters.");
        }

        var name = nameElement.GetString()!;
        at = $"{table}.columns.{name}";

        var type = element.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "string";
        if (!PluginTable.ColumnTypes.Contains(type))
        {
            return (null, $"`{at}.type` must be {string.Join(", ", PluginTable.ColumnTypes.SkipLast(1))} or {PluginTable.ColumnTypes[^1]}.");
        }

        IReadOnlyList<string>? choices = null;
        if (element.TryGetProperty("enum", out var e))
        {
            if (type != "enum") return (null, $"`{at}.enum` applies only to an enum column.");
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() == 0 || e.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
            {
                return (null, $"`{at}.enum` must be a non-empty array of strings.");
            }

            choices = [.. e.EnumerateArray().Select(v => v.GetString()!)];
        }
        else if (type == "enum")
        {
            return (null, $"`{at}.enum` must list the column's choices.");
        }

        double? min = null, max = null;
        var integer = false;
        foreach (var key in (string[])["min", "max", "integer"])
        {
            if (!element.TryGetProperty(key, out var bound)) continue;
            if (type != "number") return (null, $"`{at}.{key}` applies only to a number column.");

            if (key == "integer")
            {
                if (bound.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return (null, $"`{at}.integer` must be true or false.");
                integer = bound.ValueKind == JsonValueKind.True;
            }
            else if (bound.ValueKind != JsonValueKind.Number || !double.IsFinite(bound.GetDouble()))
            {
                return (null, $"`{at}.{key}` must be a finite number.");
            }
            else if (key == "min")
            {
                min = bound.GetDouble();
            }
            else
            {
                max = bound.GetDouble();
            }
        }

        if (min > max) return (null, $"`{at}.min` is greater than its `max`.");

        var readOnly = false;
        if (element.TryGetProperty("readOnly", out var ro))
        {
            if (ro.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return (null, $"`{at}.readOnly` must be true or false.");
            readOnly = ro.ValueKind == JsonValueKind.True;
        }

        var required = false;
        if (element.TryGetProperty("required", out var r))
        {
            if (r.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return (null, $"`{at}.required` must be true or false.");
            required = r.ValueKind == JsonValueKind.True;
        }

        if (required && readOnly) return (null, $"`{at}` cannot be both required and readOnly: a person never writes a read-only column.");
        if (required && type == "secret")
        {
            return (null, $"`{at}.required` is not allowed on a secret column: whether a row needs its secret is the plugin's to say.");
        }

        var masked = true;
        if (element.TryGetProperty("masked", out var m))
        {
            if (type != "secret") return (null, $"`{at}.masked` applies only to a secret column.");
            if (m.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return (null, $"`{at}.masked` must be true or false.");
            masked = m.ValueKind == JsonValueKind.True;
        }

        JsonElement? fallback = null;
        if (element.TryGetProperty("default", out var d))
        {
            if (type == "secret") return (null, $"`{at}.default` is not allowed on a secret column.");
            fallback = d.Clone();
        }

        var (label, labelRefusal) = PluginManifest.Label(at, element);
        if (labelRefusal is not null) return (null, labelRefusal);

        var column = new PluginTableColumn(
            name, type, label,
            element.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String ? desc.GetString()! : "",
            required, fallback, choices, min, max, integer, readOnly, masked);

        if (fallback is { } value && column.CellRefusal($"{at}.default", value, bounds: true) is { } badDefault) return (null, badDefault);

        return (column, null);
    }

    /// <summary>Why <paramref name="value"/> cannot be this column's cell, or null. A secret column's
    /// cell is never a config value, so it is always refused here.</summary>
    public string? CellRefusal(string at, JsonElement value, bool bounds = true)
    {
        if (IsSecret) return $"`{at}` is a secret column.";

        if (AsField.Refusal(at, value, bounds) is { } refusal) return refusal;

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (text.Length > PluginTable.MaxTextLength) return $"`{at}` is longer than {PluginTable.MaxTextLength} characters.";
            if (text.Any(c => c is '\r' or '\n' or '\0')) return $"`{at}` holds a line break or a NUL; it must be one line.";
        }

        return null;
    }
}

/// <summary>
/// A per-row link on the settings screen: <c>{ "action": "sign-in", "label": "Sign in", "when":
/// { "column": "method", "equals": "interactive" } }</c>. The action is one the platform knows
/// (<see cref="PluginTable.KnownRowActions"/>); the screen shows the link only on a saved row whose
/// column holds the value.
/// </summary>
public sealed record PluginTableRowAction(string Action, string Label, string Column, string EqualTo)
{
    public static (PluginTableRowAction? Action, string? Refusal) Parse(string at, JsonElement element, IReadOnlyList<PluginTableColumn> columns)
    {
        if (element.ValueKind != JsonValueKind.Object) return (null, $"`{at}` must be an object.");

        var action = element.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()! : "";
        if (!PluginTable.KnownRowActions.Contains(action))
        {
            return (null, $"`{at}.action` must be one of: {string.Join(", ", PluginTable.KnownRowActions)}.");
        }

        var (label, labelRefusal) = PluginManifest.Label(at, element);
        if (labelRefusal is not null) return (null, labelRefusal);

        if (!element.TryGetProperty("when", out var when) || when.ValueKind != JsonValueKind.Object
            || !when.TryGetProperty("column", out var c) || c.ValueKind != JsonValueKind.String
            || !when.TryGetProperty("equals", out var v) || v.ValueKind != JsonValueKind.String)
        {
            return (null, $"`{at}.when` must be {{ \"column\": \"<column>\", \"equals\": \"<value>\" }}.");
        }

        var column = columns.FirstOrDefault(col => col.Name == c.GetString());
        if (column is null || column.Type is not ("string" or "enum"))
        {
            return (null, $"`{at}.when.column` must name a string or enum column of the table.");
        }

        if (column.Enum is { } choices && !choices.Contains(v.GetString()!, StringComparer.Ordinal))
        {
            return (null, $"`{at}.when.equals` must be one of `{column.Name}`'s choices: {string.Join(", ", choices)}.");
        }

        return (new PluginTableRowAction(action, label ?? "Sign in", column.Name, v.GetString()!), null);
    }
}

/// <summary>
/// ONE WRITER OF A MEMBER'S TABLES AT A TIME, in this Host: a person's settings save and a plugin's
/// <c>row.status</c> each read the stored rows and write them back, so neither may lose the other's
/// change - a person's new row, a plugin's status - to a write that read before it.
/// </summary>
public static class PluginTableWrites
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task<IAsyncDisposable> LockAsync(Harness.Contracts.ContainerId member, CancellationToken ct)
    {
        var gate = Locks.GetOrAdd($"{member.Team}/{member.Name}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Release(gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int _done;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
