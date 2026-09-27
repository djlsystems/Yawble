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
/// <c>secrets</c>, <c>events.publishes</c>, <c>skills</c>, <c>platforms</c>.
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
        "timeoutSeconds", "config", "secrets", "events", "skills", "platforms",
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

                    secrets[secret.Name] = new PluginSecret(
                        Text(secret.Value, "description") ?? "",
                        secret.Value.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True);
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

                        publishes.Add(new PluginPublishedEvent(type, Text(item, "summary") ?? ""));
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
                publishes, skills, platforms, reserved, ignored), null);
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
}

/// <summary>One ordinary configuration field. Flat in v1: string, number or bool.</summary>
public sealed record PluginConfigField(
    string Type,
    string Description,
    bool Required,
    JsonElement? Default,
    IReadOnlyList<string>? Enum)
{
    public static (PluginConfigField? Field, string? Refusal) Parse(string name, JsonElement element)
    {
        if (!PluginManifest.IsConfigName(name)) return (null, $"`config.{name}` is not a usable name.");
        if (element.ValueKind != JsonValueKind.Object) return (null, $"`config.{name}` must be an object.");

        var type = element.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "string";

        if (type is not ("string" or "number" or "bool"))
        {
            return (null, $"`config.{name}.type` must be string, number or bool - v1 has no nested configuration.");
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

        var field = new PluginConfigField(
            type,
            element.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String ? desc.GetString()! : "",
            element.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True,
            fallback,
            choices);

        if (fallback is { } value && field.Refusal(name, value) is { } badDefault) return (null, badDefault);

        return (field, null);
    }

    /// <summary>Why <paramref name="value"/> is not a valid value for this field, or null.</summary>
    public string? Refusal(string name, JsonElement value)
    {
        var fits = Type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => false,
        };

        if (!fits) return $"`{name}` must be a {Type}.";

        if (Enum is { } choices && value.ValueKind == JsonValueKind.String
            && !choices.Contains(value.GetString()!, StringComparer.Ordinal))
        {
            return $"`{name}` must be one of: {string.Join(", ", choices)}.";
        }

        return null;
    }
}

/// <summary>A secret the plugin needs, by name. The manifest never holds its value.</summary>
public sealed record PluginSecret(string Description, bool Required);

/// <summary>An event the plugin declares it may publish, as a suffix of <c>plugin.&lt;id&gt;.</c>.
/// Declared in v1; publishing is reserved.</summary>
public sealed record PluginPublishedEvent(string Type, string Summary);
