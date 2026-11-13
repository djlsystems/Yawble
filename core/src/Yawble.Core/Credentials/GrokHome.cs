using System.Globalization;
using System.Text.Json;

namespace Yawble.Core.Credentials;

/// <summary>The current Grok session as the gateway needs it. Never logged.</summary>
public sealed record GrokSession(string AccessToken, DateTimeOffset? ExpiresAt, string? AuthMode)
{
    public bool ExpiredAt(DateTimeOffset now, TimeSpan? margin = null) => ExpiresAt is { } at && now + (margin ?? TimeSpan.Zero) >= at;

    public override string ToString() => $"GrokSession(<redacted>, expires {ExpiresAt:O}, mode {AuthMode})";
}

/// <summary>
/// The app-private host Grok home:&lt;app data&gt;/grok-home, owner-only (0700 folder; xAI's CLI
/// writes auth.json 0600 itself). The person signs in here once with xAI's own `grok login`, run unsandboxed on the
/// host; the <see cref="GrokKeeper"/> keeps it fresh; the gateway reads the access token from it on each request.
/// No sandbox policy may grant this folder, and it is never the person's own ~/.grok.
///
/// auth.json's format is undocumented: observed as {"&lt;issuer&gt;::&lt;uuid&gt;": {"key"|"access_token": …, "auth_mode": …,
/// "expires_at": …}}, so the grok CLI version is pinned and the reader is lenient.
/// </summary>
public sealed class GrokHome
{
    public const string FolderName = "grok-home";
    public const string AuthFileName = "auth.json";

    public GrokHome(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public string AuthFile => System.IO.Path.Combine(Path, AuthFileName);

    public static GrokHome InDataDir(string dataDir) => new(System.IO.Path.Combine(dataDir, FolderName));

    /// <summary>
    /// True for the person's OWN Grok home on this computer (GROK_HOME, else ~/.grok), which the person's grok signed in
    /// to outside the app. The app only reads it and lets the person's own grok renew it; it never creates, widens or
    /// deletes anything in it.
    /// </summary>
    public bool PersonsOwn { get; private init; }

    /// <summary>The person's own Grok home (the gateway holds Grok's sign-in from it).</summary>
    public static GrokHome OfThisComputer(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var dir = env("GROK_HOME") is { Length: > 0 } d
            ? d
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok");
        return new GrokHome(dir) { PersonsOwn = true };
    }

    /// <summary>Creates the folder owner-only (0700 on Unix) and narrows it back if someone widened it.</summary>
    public GrokHome EnsureCreated()
    {
        if (OperatingSystem.IsWindows())
        {
            // An owner-only ACL on Windows is assumed; %LOCALAPPDATA% is per-user already.
            Directory.CreateDirectory(Path);
        }
        else
        {
            Directory.CreateDirectory(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return this;
    }

    public bool SignedIn => ReadSession() is not null;

    /// <summary>The session in auth.json, or null when there is none (not signed in, or a file we cannot read).</summary>
    public GrokSession? ReadSession()
    {
        string text;
        try
        {
            text = File.ReadAllText(AuthFile);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null; // mid-rewrite by xAI's CLI: the caller retries on the next request
        }
        try
        {
            using var doc = JsonDocument.Parse(text);
            return Pick(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static GrokSession? Pick(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        // A flat {"access_token": …} file, or entries keyed by issuer: take the entry with a token that expires last.
        var flat = FromEntry(root);
        if (flat is not null) return flat;
        GrokSession? best = null;
        foreach (var property in root.EnumerateObject())
        {
            if (FromEntry(property.Value) is not { } session) continue;
            if (best is null || (session.ExpiresAt ?? DateTimeOffset.MaxValue) > (best.ExpiresAt ?? DateTimeOffset.MaxValue)) best = session;
        }
        return best;
    }

    private static GrokSession? FromEntry(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        var token = Text(entry, "access_token") ?? Text(entry, "key");
        if (string.IsNullOrEmpty(token)) return null;
        return new GrokSession(token, Expiry(entry), Text(entry, "auth_mode"));
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Expiry(JsonElement entry)
    {
        if (!entry.TryGetProperty("expires_at", out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
        {
            return n > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(n) : DateTimeOffset.FromUnixTimeSeconds(n);
        }
        if (v.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            return at;
        }
        return null;
    }
}
