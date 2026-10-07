using System.Runtime.InteropServices;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// A plugin's <c>plugin.json</c>, schema version 1: what a local install needs to register a plugin
/// member with no marketplace. See <c>docs/plugins.md</c>.
///
/// REQUIRED NOW: <c>schemaVersion</c>, <c>id</c>, <c>name</c>, <c>description</c>, <c>version</c>,
/// <c>protocol</c>, <c>executable</c>. OPTIONAL NOW: <c>timeoutSeconds</c>, <c>config</c>,
/// <c>secrets</c>, <c>events.publishes</c>, <c>skills</c>, <c>platforms</c>, <c>requires</c>,
/// <c>connections</c>, <c>reads</c>.
/// RESERVED - kept as raw JSON, validated by nothing, acted on by nothing yet: <c>actions</c>,
/// <c>consumes</c>, <c>health</c>, <c>signature</c>, <c>publisher</c>, <c>minHostVersion</c>,
/// <c>permits</c>. A key this version does not know is IGNORED and named in
/// <see cref="Ignored"/>, so a Host reads a manifest written for a later one; <c>schemaVersion</c> is
/// the hard gate.
/// </summary>
public sealed record PluginManifest(
    string Id,
    string Name,
    string Description,
    string Version,
    string Protocol,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    int TimeoutSeconds,
    IReadOnlyDictionary<string, PluginConfigField> Config,
    IReadOnlyDictionary<string, PluginSecret> Secrets,
    IReadOnlyList<PluginPublishedEvent> Publishes,
    IReadOnlyList<string> Skills,
    IReadOnlyDictionary<string, string> Platforms,
    IReadOnlyDictionary<string, JsonElement> Reserved,
    IReadOnlyList<string> Ignored)
{
    /// <summary>The runtimes this plugin needs from the image (<see cref="Runtimes"/>), checked on
    /// the Host's PATH when the catalog loads it. Empty for a self-contained binary.</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>The accounts at OAuth providers this plugin acts on, by slot: each bound per member to
    /// a person's connection, whose fresh access token the run receives on stdin. Empty when the
    /// manifest has no <c>connections</c>, which behaves exactly as before.</summary>
    public IReadOnlyDictionary<string, PluginConnectionSlot> Connections { get; init; } =
        new Dictionary<string, PluginConnectionSlot>(StringComparer.Ordinal);

    /// <summary>The collections of its own team's sites this plugin reads, in declaration order: each
    /// run receives them on stdin as <c>sites</c>. Empty when the manifest has no <c>reads</c>, which
    /// behaves exactly as before.</summary>
    public IReadOnlyList<PluginSiteRead> Reads { get; init; } = [];

    /// <summary>The most collections one plugin may declare in <c>reads</c>. A sanity bound: it also
    /// bounds what the run's request spends on their envelopes.</summary>
    public const int MaxReads = 32;

    /// <summary>
    /// What the image guarantees and a manifest's <c>requires</c> may name. Everything else a plugin
    /// needs lives in its own folder; nothing a plugin needs is ever added to the image.
    /// </summary>
    public static readonly IReadOnlyList<string> Runtimes = ["dotnet", "node", "python3"];

    public const string FileName = "plugin.json";

    public const int SchemaVersion = 1;

    /// <summary>The one protocol this Host speaks: one JSON request on stdin, JSON Lines out.</summary>
    public const string ProtocolV1 = "harness.member/1";

    /// <summary>How long a plugin may go without a progress record when its manifest says nothing.
    /// Bounded, unlike an Agent preset's default: a plugin has no progress convention to rely on.</summary>
    public const int DefaultTimeoutSeconds = 300;

    public static readonly IReadOnlySet<string> ReservedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "actions", "consumes", "health", "signature", "publisher", "minHostVersion", "permits",
    };

    private static readonly IReadOnlySet<string> KnownKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "schemaVersion", "id", "name", "description", "version", "protocol", "executable",
        "timeoutSeconds", "config", "secrets", "events", "skills", "platforms", "requires", "connections",
        "reads",
    };

    /// <summary>This machine's platform key, as the <c>platforms</c> map names it.</summary>
    public static string ThisPlatform => "linux-" + RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        var other => other.ToString().ToLowerInvariant(),
    };

    /// <summary>The executable for this machine: its <c>platforms</c> entry when there is one,
    /// otherwise <c>executable.path</c>. Relative to the version directory.</summary>
    public string ExecutableForThisPlatform =>
        Platforms.TryGetValue(ThisPlatform, out var specific) ? specific : ExecutablePath;

    /// <summary>
    /// Reads a manifest, or says in one sentence which field is wrong. Never throws on content.
    /// </summary>
    public static (PluginManifest? Manifest, string? Refusal) Parse(string json)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException exception)
        {
            return (null, $"{FileName} is not JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return (null, $"{FileName} must be a JSON object.");

            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out var schemaVersion))
            {
                return (null, "`schemaVersion` is required and must be a number.");
            }

            if (schemaVersion != SchemaVersion)
            {
                return (null, $"`schemaVersion` {schemaVersion} is not one this Host reads (it reads {SchemaVersion}).");
            }

            if (Text(root, "id") is not { } id) return (null, "`id` is required.");
            if (!MemberRef.IsValidPluginId(id))
            {
                return (null, $"`id` '{id}' must be lowercase letters, digits and hyphens, starting with a letter or digit, at most 48 characters.");
            }

            if (Text(root, "name") is not { } name) return (null, "`name` is required.");
            if (Text(root, "description") is not { } description) return (null, "`description` is required.");
            if (Text(root, "version") is not { } version) return (null, "`version` is required.");
            if (!IsSafeSegment(version)) return (null, $"`version` '{version}' must be a plain version such as 0.1.0.");
            if (Text(root, "protocol") is not { } protocol) return (null, "`protocol` is required.");
            if (protocol != ProtocolV1) return (null, $"`protocol` '{protocol}' is not one this Host speaks (it speaks {ProtocolV1}).");

            if (!root.TryGetProperty("executable", out var executable) || executable.ValueKind != JsonValueKind.Object
                || Text(executable, "path") is not { } path)
            {
                return (null, "`executable.path` is required.");
            }

            if (RelativeRefusal("executable.path", path) is { } badPath) return (null, badPath);

            var arguments = new List<string>();

            if (executable.TryGetProperty("args", out var args))
            {
                if (args.ValueKind != JsonValueKind.Array || args.EnumerateArray().Any(a => a.ValueKind != JsonValueKind.String))
                {
                    return (null, "`executable.args` must be an array of strings.");
                }

                arguments.AddRange(args.EnumerateArray().Select(a => a.GetString()!));
            }

            var timeout = DefaultTimeoutSeconds;

            if (root.TryGetProperty("timeoutSeconds", out var timeoutElement))
            {
                if (timeoutElement.ValueKind != JsonValueKind.Number || !timeoutElement.TryGetInt32(out timeout) || timeout <= 0)
                {
                    return (null, "`timeoutSeconds` must be a positive whole number.");
                }
            }

            var config = new Dictionary<string, PluginConfigField>(StringComparer.Ordinal);

            if (root.TryGetProperty("config", out var configElement))
            {
                if (configElement.ValueKind != JsonValueKind.Object) return (null, "`config` must be an object.");

                foreach (var field in configElement.EnumerateObject())
                {
                    var (parsed, refusal) = PluginConfigField.Parse(field.Name, field.Value);
                    if (refusal is not null) return (null, refusal);
                    config[field.Name] = parsed!;
                }
            }

            var secrets = new Dictionary<string, PluginSecret>(StringComparer.Ordinal);

            if (root.TryGetProperty("secrets", out var secretsElement))
            {
                if (secretsElement.ValueKind != JsonValueKind.Object) return (null, "`secrets` must be an object.");

                foreach (var secret in secretsElement.EnumerateObject())
                {
                    if (!IsConfigName(secret.Name)) return (null, $"`secrets.{secret.Name}` is not a usable name.");
                    if (secret.Value.ValueKind != JsonValueKind.Object) return (null, $"`secrets.{secret.Name}` must be an object.");

                    // A VALUE IS NEVER HERE. A manifest ships with a package; a credential in it would
                    // travel with every copy.
                    if (secret.Value.TryGetProperty("value", out _))
                    {
                        return (null, $"`secrets.{secret.Name}` carries a value. A manifest names a secret; the value is set with `secret set` and bound by a logical key.");
                    }

                    // WHEN IT IS NEEDED, optionally: only while a list or choice setting holds one value
                    // (`"when": {"sources": "adzuna"}`), so an install can say a secret for a source the
                    // person left off is not needed.
                    PluginSecretWhen? when = null;

                    if (secret.Value.TryGetProperty("when", out var whenElement) && whenElement.ValueKind != JsonValueKind.Null)
                    {
                        var at = $"`secrets.{secret.Name}.when`";
                        if (whenElement.ValueKind != JsonValueKind.Object || whenElement.EnumerateObject().Count() != 1)
                        {
                            return (null, $"{at} must name one setting and one value, as in {{\"sources\": \"adzuna\"}}.");
                        }

                        var condition = whenElement.EnumerateObject().Single();

                        if (!config.TryGetValue(condition.Name, out var setting))
                        {
                            return (null, $"{at} names '{condition.Name}', which is not a `config` field of this plugin.");
                        }

                        if (condition.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(condition.Value.GetString()))
                        {
                            return (null, $"`secrets.{secret.Name}.when.{condition.Name}` must be one of the setting's values, as a string.");
                        }

                        if (setting.Enum is { } choices && !choices.Contains(condition.Value.GetString()!, StringComparer.Ordinal))
                        {
                            return (null, $"`secrets.{secret.Name}.when.{condition.Name}` must be one of: {string.Join(", ", choices)}.");
                        }

                        when = new PluginSecretWhen(condition.Name, condition.Value.GetString()!);
                    }

                    secrets[secret.Name] = new PluginSecret(
                        Text(secret.Value, "description") ?? "",
                        secret.Value.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True)
                    {
                        When = when,
                    };
                }
            }

            var publishes = new List<PluginPublishedEvent>();

            if (root.TryGetProperty("events", out var events))
            {
                if (events.ValueKind != JsonValueKind.Object) return (null, "`events` must be an object.");

                if (events.TryGetProperty("publishes", out var published))
                {
                    if (published.ValueKind != JsonValueKind.Array) return (null, "`events.publishes` must be an array.");

                    foreach (var item in published.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object || Text(item, "type") is not { } type || !IsEventSuffix(type))
                        {
                            return (null, "Each of `events.publishes` needs a `type`: lowercase letters, digits, hyphens and dots.");
                        }

                        if (publishes.Any(e => e.Type == type))
                        {
                            return (null, $"`events.publishes` declares `{type}` twice.");
                        }

                        var fields = new List<EventField>();

                        if (item.TryGetProperty("fields", out var fieldsElement) && fieldsElement.ValueKind != JsonValueKind.Null)
                        {
                            if (fieldsElement.ValueKind != JsonValueKind.Array) return (null, $"`events.publishes` `{type}`: `fields` must be an array.");

                            foreach (var field in fieldsElement.EnumerateArray())
                            {
                                if (field.ValueKind != JsonValueKind.Object || Text(field, "name") is not { } fieldName || !IsConfigName(fieldName))
                                {
                                    return (null, $"`events.publishes` `{type}`: each field needs a `name`.");
                                }

                                // THE ENVELOPE FIELD is the platform's: resolved from the row's source, never the payload.
                                if (fieldName == PayloadFields.Source)
                                {
                                    return (null, $"`events.publishes` `{type}`: `{PayloadFields.Source}` is the platform's own field, stamped from the member; it cannot be declared.");
                                }

                                if (FieldKind(Text(field, "kind")) is not { } kind)
                                {
                                    return (null, $"`events.publishes` `{type}`: field `{fieldName}` has kind '{Text(field, "kind")}'; use string, number, boolean or list.");
                                }

                                fields.Add(new EventField(fieldName, kind, Text(field, "summary") ?? ""));
                            }
                        }

                        publishes.Add(new PluginPublishedEvent(
                            type, Text(item, "summary") ?? "",
                            item.TryGetProperty("highVolume", out var highVolume) && highVolume.ValueKind == JsonValueKind.True,
                            fields));
                    }
                }
            }

            var skills = new List<string>();

            if (root.TryGetProperty("skills", out var skillsElement))
            {
                if (skillsElement.ValueKind != JsonValueKind.Array || skillsElement.EnumerateArray().Any(s => s.ValueKind != JsonValueKind.String))
                {
                    return (null, "`skills` must be an array of relative paths.");
                }

                foreach (var skill in skillsElement.EnumerateArray().Select(s => s.GetString()!))
                {
                    if (RelativeRefusal("skills", skill) is { } badSkill) return (null, badSkill);
                    skills.Add(skill);
                }
            }

            // RESERVED FOR ARCHITECTURE-SPECIFIC PACKAGES, and validated NOW so a marketplace
            // package works on this Host unchanged: `{"linux-x64": "bin/x64/p", ...}`.
            var platforms = new Dictionary<string, string>(StringComparer.Ordinal);

            if (root.TryGetProperty("platforms", out var platformsElement) && platformsElement.ValueKind != JsonValueKind.Null)
            {
                if (platformsElement.ValueKind != JsonValueKind.Object) return (null, "`platforms` must be an object of platform to path.");

                foreach (var platform in platformsElement.EnumerateObject())
                {
                    if (platform.Value.ValueKind != JsonValueKind.String) return (null, $"`platforms.{platform.Name}` must be a path.");
                    if (RelativeRefusal($"platforms.{platform.Name}", platform.Value.GetString()!) is { } badPlatform) return (null, badPlatform);
                    platforms[platform.Name] = platform.Value.GetString()!;
                }
            }

            var requires = new List<string>();

            if (root.TryGetProperty("requires", out var requiresElement) && requiresElement.ValueKind != JsonValueKind.Null)
            {
                if (requiresElement.ValueKind != JsonValueKind.Array || requiresElement.EnumerateArray().Any(r => r.ValueKind != JsonValueKind.String))
                {
                    return (null, "`requires` must be an array of runtime names.");
                }

                foreach (var runtime in requiresElement.EnumerateArray().Select(r => r.GetString()!))
                {
                    if (!Runtimes.Contains(runtime, StringComparer.Ordinal))
                    {
                        return (null, $"`requires` names '{runtime}', which is not a runtime this Host knows ({string.Join(", ", Runtimes)}).");
                    }

                    if (!requires.Contains(runtime, StringComparer.Ordinal)) requires.Add(runtime);
                }
            }

            var connections = new Dictionary<string, PluginConnectionSlot>(StringComparer.Ordinal);

            if (root.TryGetProperty("connections", out var connectionsElement) && connectionsElement.ValueKind != JsonValueKind.Null)
            {
                if (connectionsElement.ValueKind != JsonValueKind.Object) return (null, "`connections` must be an object of slot name to slot.");

                foreach (var slot in connectionsElement.EnumerateObject())
                {
                    var (parsed, refusal) = PluginConnectionSlot.Parse(slot.Name, slot.Value);
                    if (refusal is not null) return (null, refusal);
                    connections[slot.Name] = parsed!;
                }
            }

            var reads = new List<PluginSiteRead>();

            if (root.TryGetProperty("reads", out var readsElement) && readsElement.ValueKind != JsonValueKind.Null)
            {
                var (parsed, refusal) = PluginSiteRead.ParseAll(readsElement);
                if (refusal is not null) return (null, refusal);
                reads.AddRange(parsed);
            }

            var reserved = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var ignored = new List<string>();

            foreach (var property in root.EnumerateObject())
            {
                if (ReservedKeys.Contains(property.Name))
                {
                    if (property.Value.ValueKind != JsonValueKind.Null) reserved[property.Name] = property.Value.Clone();
                }
                else if (!KnownKeys.Contains(property.Name))
                {
                    ignored.Add(property.Name);
                }
            }

            return (new PluginManifest(
                id, name, description, version, protocol, path, arguments, timeout, config, secrets,
                publishes, skills, platforms, reserved, ignored) { Requires = requires, Connections = connections, Reads = reads }, null);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : null;

    /// <summary>A path inside the plugin's own directory: relative, no `..`, no rooted form. The
    /// catalog checks again after resolving symlinks.</summary>
    private static string? RelativeRefusal(string field, string path)
    {
        if (Path.IsPathRooted(path) || path.Split('/', '\\').Any(part => part is ".." or ""))
        {
            return $"`{field}` '{path}' must be a relative path inside the plugin's directory.";
        }

        return null;
    }

    private static bool IsSafeSegment(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+' or '_')
        && value is not ("." or "..");

    internal static bool IsConfigName(string name) =>
        name.Length is > 0 and <= 64 && char.IsAsciiLetter(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static bool IsEventSuffix(string type) =>
        type.Length is > 0 and <= 64 && char.IsAsciiLetterLower(type[0])
        && type.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.');

    /// <summary>A declared event field's kind: <c>number</c> is an integer, as every platform count is.</summary>
    private static EventFieldKind? FieldKind(string? kind) => kind switch
    {
        null or "string" => EventFieldKind.String,
        "number" or "integer" => EventFieldKind.Integer,
        "boolean" or "bool" => EventFieldKind.Boolean,
        "list" => EventFieldKind.List,
        _ => null,
    };
}

/// <summary>One ordinary configuration field. Flat in v1: string, number, bool, or list (a list of
/// strings, default <c>[]</c>, whose optional <c>enum</c> limits each item).</summary>
/// <param name="PersonOnly">
/// <c>"setBy": "person"</c> in the manifest: only a person's hire may set this field to anything but
/// its default. It is how a plugin that acts outward (sends, posts, pays) keeps its real mode and its
/// allowlist out of an agent's reach: a Manager's or a Concierge's hire may leave the field at its
/// default and nothing else, because incoming content that reaches an agent's context must not be
/// able to widen what the plugin may do.
/// </param>
public sealed record PluginConfigField(
    string Type,
    string Description,
    bool Required,
    JsonElement? Default,
    IReadOnlyList<string>? Enum,
    bool PersonOnly = false)
{
    private static readonly JsonElement EmptyList = JsonDocument.Parse("[]").RootElement.Clone();

    /// <summary>A number field's smallest allowed value, inclusive; null for none.</summary>
    public double? Min { get; init; }

    /// <summary>A number field's largest allowed value, inclusive; null for none.</summary>
    public double? Max { get; init; }

    /// <summary>A number field that takes whole numbers only.</summary>
    public bool Integer { get; init; }

    public static (PluginConfigField? Field, string? Refusal) Parse(string name, JsonElement element)
    {
        if (!PluginManifest.IsConfigName(name)) return (null, $"`config.{name}` is not a usable name.");
        if (element.ValueKind != JsonValueKind.Object) return (null, $"`config.{name}` must be an object.");

        var type = element.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "string";

        if (type is not ("string" or "number" or "bool" or "list"))
        {
            return (null, $"`config.{name}.type` must be string, number, bool or list - v1 has no nested configuration.");
        }

        IReadOnlyList<string>? choices = null;

        if (element.TryGetProperty("enum", out var e))
        {
            if (e.ValueKind != JsonValueKind.Array || e.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
            {
                return (null, $"`config.{name}.enum` must be an array of strings.");
            }

            choices = [.. e.EnumerateArray().Select(v => v.GetString()!)];
        }

        JsonElement? fallback = element.TryGetProperty("default", out var d) ? d.Clone() : null;

        // A LIST WITH NO DEFAULT IS EMPTY, never absent: the plugin always receives an array.
        if (type == "list" && fallback is null) fallback = EmptyList;

        var personOnly = false;

        if (element.TryGetProperty("setBy", out var setBy))
        {
            if (setBy.ValueKind != JsonValueKind.String || setBy.GetString() is not ("person" or "anyone"))
            {
                return (null, $"`config.{name}.setBy` must be \"person\" or \"anyone\".");
            }

            personOnly = setBy.GetString() == "person";
        }

        // BOUNDS ARE A NUMBER'S: min and max inclusive, integer for whole numbers only.
        double? min = null, max = null;
        var integer = false;

        foreach (var key in (string[])["min", "max", "integer"])
        {
            if (!element.TryGetProperty(key, out var bound)) continue;
            if (type != "number") return (null, $"`config.{name}.{key}` applies only to a number field.");

            if (key == "integer")
            {
                if (bound.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return (null, $"`config.{name}.integer` must be true or false.");
                integer = bound.ValueKind == JsonValueKind.True;
            }
            else if (bound.ValueKind != JsonValueKind.Number)
            {
                return (null, $"`config.{name}.{key}` must be a number.");
            }
            else if (!double.IsFinite(bound.GetDouble()))
            {
                return (null, $"`config.{name}.{key}` must be a finite number; {bound.GetRawText()} is too large to hold.");
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

        if (min > max) return (null, $"`config.{name}.min` ({Show(min!.Value)}) is greater than its `max` ({Show(max!.Value)}).");

        var field = new PluginConfigField(
            type,
            element.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String ? desc.GetString()! : "",
            element.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True,
            fallback,
            choices,
            personOnly)
        {
            Min = min,
            Max = max,
            Integer = integer,
        };

        if (fallback is { } value && field.Refusal(name, value) is { } badDefault)
        {
            return (null, field.BoundsRefusal(name, value) is null ? badDefault : $"`config.{name}.default`: {badDefault}");
        }

        return (field, null);
    }

    /// <summary>
    /// Why an agent's hire (a Manager or a Concierge) may not set this field to
    /// <paramref name="value"/>, or null. Only a <see cref="PersonOnly"/> field refuses, and only a
    /// value other than its default.
    /// </summary>
    public string? AgentRefusal(string name, JsonElement value)
    {
        if (!PersonOnly || (Default is { } fallback && JsonElement.DeepEquals(fallback, value))) return null;

        var keep = Default is { } d ? $"leave it at its default ({d.GetRawText()}) or omit it" : "hire this plugin yourself";
        return $"`{name}` is set by a person only: an agent's hire may {keep}. A person sets it when hiring, "
            + "in the Add member dialog.";
    }

    /// <summary>Why <paramref name="value"/> is not a valid value for this field, or null. Its
    /// bounds are checked too unless <paramref name="bounds"/> is false.</summary>
    public string? Refusal(string name, JsonElement value, bool bounds = true)
    {
        var fits = Type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "list" => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String),
            _ => false,
        };

        if (!fits) return Type == "list" ? $"`{name}` must be a list of strings." : $"`{name}` must be a {Type}.";

        if (Type == "list")
        {
            if (Enum is { } allowed
                && value.EnumerateArray().Select(v => v.GetString()!).FirstOrDefault(v => !allowed.Contains(v, StringComparer.Ordinal)) is { } outside)
            {
                return $"`{name}` holds '{outside}'; each item must be one of: {string.Join(", ", allowed)}.";
            }

            return null;
        }

        if (Enum is { } choices && value.ValueKind == JsonValueKind.String
            && !choices.Contains(value.GetString()!, StringComparer.Ordinal))
        {
            return $"`{name}` must be one of: {string.Join(", ", choices)}.";
        }

        return bounds ? BoundsRefusal(name, value) : null;
    }

    /// <summary>
    /// Why a number <paramref name="value"/> is outside this field's <see cref="Min"/>,
    /// <see cref="Max"/> or <see cref="Integer"/>, naming the field and the bound; null when it is
    /// inside them, has no bounds, or is not a number. A number too large to hold (1e400 reads as
    /// infinity) is refused whatever the bounds. A run does not ask: a value stored before its
    /// bounds existed keeps running, and the settings read reports it as out of range.
    /// </summary>
    public string? BoundsRefusal(string name, JsonElement value)
    {
        if (Type != "number" || value.ValueKind != JsonValueKind.Number) return null;

        var number = value.GetDouble();

        if (!double.IsFinite(number)) return $"`{name}` must be a finite number; {value.GetRawText()} is too large to hold.";
        if (Min is { } min && number < min) return $"`{name}` must be at least {Show(min)}; {value.GetRawText()} is below it.";
        if (Max is { } max && number > max) return $"`{name}` must be at most {Show(max)}; {value.GetRawText()} is above it.";
        if (Integer && Math.Floor(number) != number) return $"`{name}` must be a whole number; {value.GetRawText()} is not.";

        return null;
    }

    private static string Show(double number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A secret the plugin needs, by name. The manifest never holds its value.</summary>
public sealed record PluginSecret(string Description, bool Required)
{
    /// <summary>Needed only while this setting holds this value; null when always needed.</summary>
    public PluginSecretWhen? When { get; init; }
}

/// <summary>
/// A secret needed only while <paramref name="Setting"/> holds <paramref name="Value"/>: equals it,
/// or for a list, contains it.
/// </summary>
public sealed record PluginSecretWhen(string Setting, string Value)
{
    public bool HoldsIn(JsonElement? current) => current switch
    {
        { ValueKind: JsonValueKind.String } text => text.GetString() == Value,
        { ValueKind: JsonValueKind.Array } list => list.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == Value),
        _ => false,
    };
}

/// <summary>An event the plugin declares it may publish, as a suffix of <c>plugin.&lt;id&gt;.</c>:
/// the only suffixes a <c>publish</c> record may name.</summary>
/// <param name="HighVolume">Published once per status line rather than once per run, so a
/// language-model member may not subscribe to it - the platform's own rule, read off the union.</param>
/// <param name="Fields">The payload fields a trigger's filter and `{event.*}` tokens may read. The
/// envelope's <c>source</c> is always there and is never declared.</param>
public sealed record PluginPublishedEvent(
    string Type, string Summary, bool HighVolume = false, IReadOnlyList<EventField>? Fields = null)
{
    /// <summary>Its definition as <see cref="EventCatalog"/> answers it for plugin <paramref name="id"/>.
    /// Always in the ledger: a manifest's <c>inLedger</c> is not read in v1.</summary>
    public EventDefinition Definition(string id) => new(
        EventCatalog.PluginType(id, Type), EventPublisher.Plugin, HighVolume, InLedger: true,
        [EventCatalog.SourceField, .. Fields ?? []],
        Summary);
}

/// <summary>
/// One <c>connections</c> slot: the account a plugin acts on, bound per member to a person's
/// connection. <paramref name="Providers"/> are <c>google</c>, <c>microsoft</c>, <c>custom</c> (any
/// custom provider) or one <c>custom-&lt;id&gt;</c>. <paramref name="Scopes"/> is keyed by those same
/// words: what a connection of that provider must have been granted to be bound here.
/// </summary>
public sealed record PluginConnectionSlot(
    string Description,
    IReadOnlyList<string> Providers,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Scopes,
    bool Required)
{
    /// <summary>The scopes this slot asks of a connection of <paramref name="providerId"/>: its own
    /// entry, or the <c>custom</c> entry for a custom provider.</summary>
    public IReadOnlyList<string> ScopesFor(string providerId) =>
        Scopes.TryGetValue(providerId, out var own) ? own
        : ConnectionProviders.IsCustomId(providerId) && Scopes.TryGetValue(ConnectionProviders.Custom, out var custom) ? custom
        : [];

    /// <summary>Whether a connection of <paramref name="providerId"/> may be bound here.</summary>
    public bool Admits(string providerId) => Providers.Any(p => ConnectionProviders.Admits(p, providerId));

    /// <summary>"needs a Google or Microsoft connection", as the Plugins screen says it.</summary>
    public string Summary
    {
        get
        {
            var names = Providers.Select(ConnectionProviders.Display).ToList();
            var joined = names.Count <= 1 ? string.Concat(names)
                : string.Join(", ", names.Take(names.Count - 1)) + " or " + names[^1];
            return (Required ? "needs" : "may use") + $" a {joined} connection";
        }
    }

    public static (PluginConnectionSlot? Slot, string? Refusal) Parse(string name, JsonElement element)
    {
        if (!PluginManifest.IsConfigName(name)) return (null, $"`connections.{name}` is not a usable slot name.");
        if (element.ValueKind != JsonValueKind.Object) return (null, $"`connections.{name}` must be an object.");

        if (!element.TryGetProperty("providers", out var providersElement) || providersElement.ValueKind != JsonValueKind.Array
            || providersElement.GetArrayLength() == 0
            || providersElement.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String))
        {
            return (null, $"`connections.{name}.providers` must be a non-empty array of provider names (google, microsoft, custom, imap).");
        }

        var providers = new List<string>();

        foreach (var provider in providersElement.EnumerateArray().Select(p => p.GetString()!))
        {
            if (provider is not (ConnectionProviders.Google or ConnectionProviders.Microsoft or ConnectionProviders.Custom or MailboxSettings.Kind)
                && !ConnectionProviders.IsCustomId(provider))
            {
                return (null, $"`connections.{name}.providers` names '{provider}', which is not a provider this Host knows (google, microsoft, custom, custom-<id>, or imap for a mailbox).");
            }

            if (!providers.Contains(provider, StringComparer.Ordinal)) providers.Add(provider);
        }

        var scopes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        if (element.TryGetProperty("scopes", out var scopesElement) && scopesElement.ValueKind != JsonValueKind.Null)
        {
            if (scopesElement.ValueKind == JsonValueKind.Array)
            {
                if (ScopeList(scopesElement) is not { } all) return (null, $"`connections.{name}.scopes` must hold scope strings.");

                // A MAILBOX HAS NO SCOPES: a list is every OAuth provider's.
                foreach (var provider in providers.Where(p => p != MailboxSettings.Kind)) scopes[provider] = all;
            }
            else if (scopesElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in scopesElement.EnumerateObject())
                {
                    if (entry.Name == MailboxSettings.Kind)
                    {
                        return (null, $"`connections.{name}.scopes` has an entry for 'imap', but scopes do not apply to an imap connection: a mailbox signs in with its app password.");
                    }

                    if (!providers.Contains(entry.Name, StringComparer.Ordinal))
                    {
                        return (null, $"`connections.{name}.scopes` has an entry for '{entry.Name}', which `providers` does not name.");
                    }

                    if (entry.Value.ValueKind != JsonValueKind.Array || ScopeList(entry.Value) is not { } list)
                    {
                        return (null, $"`connections.{name}.scopes.{entry.Name}` must be an array of scope strings.");
                    }

                    scopes[entry.Name] = list;
                }
            }
            else
            {
                return (null, $"`connections.{name}.scopes` must be an array of scopes, or an object of provider to scopes.");
            }
        }

        if (element.TryGetProperty("required", out var required) && required.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return (null, $"`connections.{name}.required` must be true or false.");
        }

        if (element.TryGetProperty("description", out var description) && description.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            return (null, $"`connections.{name}.description` must be a string.");
        }

        return (new PluginConnectionSlot(
            description.ValueKind == JsonValueKind.String ? description.GetString()! : "",
            providers, scopes, required.ValueKind == JsonValueKind.True), null);
    }

    private static IReadOnlyList<string>? ScopeList(JsonElement array)
    {
        var list = new List<string>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()) || item.GetString()!.Any(char.IsWhiteSpace))
            {
                return null;
            }

            if (!list.Contains(item.GetString()!, StringComparer.Ordinal)) list.Add(item.GetString()!);
        }

        return list;
    }
}

/// <summary>
/// One <c>reads</c> entry: a collection of a site of the plugin's OWN team, delivered on stdin at each
/// run. There is no team in it, so a plugin has no way to name another team's site.
/// </summary>
public sealed record PluginSiteRead(string Site, string Collection)
{
    /// <summary>The whole <c>reads</c> list, or the first fault in one sentence naming the field. The
    /// Go CLI refuses in the same order and the same words (cli/internal/plugin).</summary>
    public static (IReadOnlyList<PluginSiteRead> Reads, string? Refusal) ParseAll(JsonElement element)
    {
        var reads = new List<PluginSiteRead>();

        if (element.ValueKind != JsonValueKind.Array) return (reads, "`reads` must be a list of { site, collection }.");

        if (element.GetArrayLength() > PluginManifest.MaxReads)
        {
            return (reads, $"`reads` declares {element.GetArrayLength()} collections; a plugin reads at most {PluginManifest.MaxReads}.");
        }

        var n = 0;

        foreach (var entry in element.EnumerateArray())
        {
            var at = $"`reads[{n}]`";

            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("site", out var site) || site.ValueKind != JsonValueKind.String
                || !entry.TryGetProperty("collection", out var collection) || collection.ValueKind != JsonValueKind.String)
            {
                return (reads, $"{at} must be an object with `site` and `collection`.");
            }

            if (entry.TryGetProperty("team", out _)) return (reads, $"{at} names a team; a plugin reads only its own team's sites.");

            if (!SiteRules.IsSlug(site.GetString())) return (reads, $"`reads[{n}].site`: {SiteRules.NotASlug("site", site.GetString())}");

            if (!SiteRules.IsSlug(collection.GetString()))
            {
                return (reads, $"`reads[{n}].collection`: {SiteRules.NotASlug("collection", collection.GetString())}");
            }

            var read = new PluginSiteRead(site.GetString()!, collection.GetString()!);
            if (reads.Contains(read)) return (reads, $"{at} repeats {read.Site}/{read.Collection}.");

            reads.Add(read);
            n++;
        }

        return (reads, null);
    }
}
