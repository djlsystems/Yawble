using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yawble.Core.Companion;

/// <summary>What a running core tells yawble-companion: where its control endpoint listens. Never a key or a token.</summary>
public sealed record ControlFile(int Version, string Address, int Pid, string CoreVersion, DateTimeOffset StartedAt);

/// <summary>
/// The per-user companion home (YAWBLE_HOME, else %APPDATA%\yawble-companion on Windows, ~/Library/Application
/// Support/yawble-companion on macOS, $XDG_CONFIG_HOME/yawble-companion or ~/.config/yawble-companion on Linux) and its
/// user-only control.json. A folder of its own, apart from the app's data. Independent of the app's product
/// name and YAWBLE_DATA_DIR. Link-compiled into yawble-companion: BCL only, JSON through Utf8JsonWriter / JsonNode (AOT-clean).
/// </summary>
public static class ControlDiscovery
{
    public const string HomeVariable = "YAWBLE_HOME";
    public const string FileName = "control.json";

    /// <summary>The default home's folder name.</summary>
    public const string DefaultFolderName = "yawble-companion";

    /// <summary>The companion home for this user (computed only; nothing is created).</summary>
    public static string Home(Func<string, string?> env)
    {
        if (env(HomeVariable) is { } custom && !string.IsNullOrWhiteSpace(custom)) return Path.GetFullPath(custom.Trim());
        string? Var(string name) => env(name) is { } v && !string.IsNullOrWhiteSpace(v) ? v : null;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Var("APPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DefaultFolderName);
        var home = Var("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Application Support", DefaultFolderName);
        return Var("XDG_CONFIG_HOME") is { } xdg && Path.IsPathRooted(xdg) ? Path.Combine(xdg, DefaultFolderName) : Path.Combine(home, ".config", DefaultFolderName);
    }

    public static string FilePath(string home) => Path.Combine(home, FileName);

    /// <summary>Where the client keeps one user-only key file per paired name (&lt;name&gt;.key).</summary>
    public static string ClientsDir(string home) => Path.Combine(home, "clients");

    /// <summary>Writes control.json (the home made user-only first), replacing whatever another core wrote.</summary>
    public static void Write(string home, ControlFile f)
    {
        UserOnlyFile.EnsureDirectory(home);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", f.Version);
            w.WriteString("address", f.Address);
            w.WriteNumber("pid", f.Pid);
            w.WriteString("coreVersion", f.CoreVersion);
            w.WriteString("startedAt", f.StartedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            w.WriteEndObject();
        }
        UserOnlyFile.WriteAllBytes(FilePath(home), buffer.ToArray());
    }

    /// <summary>The control file, or null when there is none or it is not one (missing or mistyped fields, not JSON).</summary>
    public static ControlFile? Read(string home)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(FilePath(home)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        try
        {
            if (JsonNode.Parse(bytes, null, new JsonDocumentOptions { AllowDuplicateProperties = false }) is not JsonObject o) return null;
            if (Int(o["version"]) is not { } version || Int(o["pid"]) is not { } pid) return null;
            if (Text(o["address"]) is not { Length: > 0 } address || Text(o["coreVersion"]) is not { } coreVersion) return null;
            if (Text(o["startedAt"]) is not { } started
                || !DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startedAt)) return null;
            return new ControlFile(version, address, pid, coreVersion, startedAt);
        }
        // A duplicate key may also surface as ArgumentException when the object is first read: still "not a control file".
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException) { return null; }
    }

    /// <summary>Deletes control.json only while it still names <paramref name="address"/> (with two cores, the last writer's stays).</summary>
    public static void RemoveIfOurs(string home, string address)
    {
        if (Read(home) is not { } current || !string.Equals(current.Address, address, StringComparison.Ordinal)) return;
        try { File.Delete(FilePath(home)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static int? Int(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<int>(out var i) ? i : null;

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.TryGetValue<string>(out var s) ? s : null;
}
