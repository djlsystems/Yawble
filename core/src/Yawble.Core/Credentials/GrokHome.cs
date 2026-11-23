using System.Globalization;
using System.Text.Json;

namespace Yawble.Core.Credentials;

/// <summary>The current Grok session as the gateway needs it. Never logged.</summary>
public sealed record GrokSession(string AccessToken, DateTimeOffset? ExpiresAt, string? AuthMode)
{
    /// <summary>The same entry carries a refresh token: grok can renew this session itself.</summary>
    public bool HasRefreshToken { get; init; }

    public bool ExpiredAt(DateTimeOffset now, TimeSpan? margin = null) => ExpiresAt is { } at && now + (margin ?? TimeSpan.Zero) >= at;

    /// <summary>
    /// Usable now: unexpired, or expired with a refresh token grok can renew it with. An expired session without one
    /// is a sign-in in name only.
    /// </summary>
    public bool UsableAt(DateTimeOffset now) => HasRefreshToken || !ExpiredAt(now);

    public override string ToString() => $"GrokSession(<redacted>, expires {ExpiresAt:O}, mode {AuthMode}, refresh token {(HasRefreshToken ? "yes" : "no")})";
}

/// <summary>What auth.json holds, for the Agents tile (no value is ever kept or shown).</summary>
public enum GrokAuthState
{
    /// <summary>No auth.json.</summary>
    Missing,

    /// <summary>Not JSON, not a JSON object, or empty: grok cannot use it.</summary>
    Unreadable,

    /// <summary>A JSON object with no access token in it (`{}`: grok cleared its sign-in).</summary>
    NoToken,

    /// <summary>A token that has expired, and no refresh token to renew it with.</summary>
    Expired,

    /// <summary>A token grok can use (unexpired, or renewable with its refresh token).</summary>
    Usable,
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
    /// deletes anything in it. The one write: the keeper puts auth.json back as it was when its own grok run left it
    /// with no token (<see cref="RestoreAuth"/>, B128).
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

    /// <summary>auth.json's bytes as they are now; null when there is none or it cannot be read.</summary>
    public byte[]? ReadAuthBytes()
    {
        try
        {
            return File.ReadAllBytes(AuthFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts auth.json back as <paramref name="bytes"/> (the keeper's copy from before a grok run that left it signed
    /// out). Written beside it and moved over it, so a reader never sees half a file.
    /// </summary>
    public bool RestoreAuth(byte[] bytes)
    {
        var temp = AuthFile + ".yawble-restore";
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, AuthFile, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

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
        return Parse(text);
    }

    /// <summary>The session in auth.json's text; null when it holds none (empty, `{}`, bad JSON, no token).</summary>
    public static GrokSession? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
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

    /// <summary>What auth.json's text holds (null text: there is no file).</summary>
    public static GrokAuthState Inspect(string? text, DateTimeOffset now)
    {
        if (text is null) return GrokAuthState.Missing;
        if (string.IsNullOrWhiteSpace(text)) return GrokAuthState.Unreadable;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return GrokAuthState.Unreadable;
            return Pick(doc.RootElement) switch
            {
                // A refresh token alone (the access token dropped): grok renews the session from it on its next run.
                null => HoldsRefreshToken(doc.RootElement) ? GrokAuthState.Usable : GrokAuthState.NoToken,
                { } s when s.UsableAt(now) => GrokAuthState.Usable,
                _ => GrokAuthState.Expired,
            };
        }
        catch (JsonException)
        {
            return GrokAuthState.Unreadable;
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
        return new GrokSession(token, Expiry(entry), Text(entry, "auth_mode")) { HasRefreshToken = !string.IsNullOrEmpty(Text(entry, "refresh_token")) };
    }

    /// <summary>The root (flat file) or one of its entries carries a refresh token.</summary>
    public static bool HoldsRefreshToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return HoldsRefreshToken(doc.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HoldsRefreshToken(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && (!string.IsNullOrEmpty(Text(root, "refresh_token"))
            || root.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Object && !string.IsNullOrEmpty(Text(p.Value, "refresh_token"))));

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
