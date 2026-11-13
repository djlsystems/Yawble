using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Credentials;

public enum ClaudeMode { Subscription, ApiKey }

public enum GrokMode { Session, ApiKey }

/// <summary>What the UI shows about the Claude credential: mode, presence and the renewal warning.</summary>
public sealed record ClaudeCredentialStatus(ClaudeMode Mode, bool Present, DateTimeOffset? IssuedAt, bool RenewSoon);

/// <summary>
/// The core's credential service. Holds the Claude setup-token, the Anthropic API key and the
/// xAI API key in the OS store, with the switch between Claude's two modes and Grok's two modes. The gateway
/// reads the current value on EVERY request, so a reconnect reaches running agents without a restart. Values are
/// never logged and never written to the database.
/// </summary>
public sealed class CredentialService
{
    /// <summary>The app's own service name in every OS store.</summary>
    public const string ServiceName = "Yawble";

    /// <summary>The setup-token is assumed to last a year; the app warns this long before.</summary>
    public static readonly TimeSpan SetupTokenLifetime = TimeSpan.FromDays(365);
    public static readonly TimeSpan RenewWarning = TimeSpan.FromDays(30);

    private readonly string _connectionString;
    private readonly TimeProvider _clock;

    public CredentialService(ICredentialStore store, string connectionString, TimeProvider? clock = null, string? fallbackWarning = null)
    {
        Store = store;
        _connectionString = connectionString;
        _clock = clock ?? TimeProvider.System;
        FallbackWarning = fallbackWarning;
    }

    public ICredentialStore Store { get; }

    /// <summary>
    /// Set when the 0600 file fallback is in use: the sentence the app shows once (it warns, never refuses).
    /// </summary>
    public string? FallbackWarning { get; }

    /// <summary>
    /// Picks the store for this OS: Credential Manager on Windows, the Keychain on macOS, the Secret Service on Linux
    /// when one answers, otherwise the owner-only file in <paramref name="dataDir"/>.
    /// </summary>
    public static CredentialService ForThisMachine(string dataDir, string connectionString, Action<string>? log = null)
    {
        // Test seam: YAWBLE_CREDENTIAL_STORE=file forces the owner-only file, so an e2e run on a machine with a real
        // keychain (a macOS test machine) writes its fake secrets to a throwaway data folder and never into the OS store.
        if (string.Equals(Environment.GetEnvironmentVariable("YAWBLE_CREDENTIAL_STORE"), "file", StringComparison.OrdinalIgnoreCase))
            return FileFallback(dataDir, connectionString, log);
        if (OperatingSystem.IsWindows()) return new CredentialService(new WindowsCredentialStore(), connectionString);
        if (OperatingSystem.IsMacOS()) return new CredentialService(new MacKeychainStore(), connectionString);
        if (OperatingSystem.IsLinux())
        {
            var secretService = LinuxSecretServiceStore.Probe(out var reason);
            if (secretService is not null) return new CredentialService(secretService, connectionString);
            log?.Invoke($"credentials: {reason}; using the owner-only file fallback");
            return FileFallback(dataDir, connectionString, log);
        }
        return FileFallback(dataDir, connectionString, log);
    }

    public static CredentialService FileFallback(string dataDir, string connectionString, Action<string>? log = null)
    {
        var file = new FileCredentialStore(dataDir);
        file.PermissionsRepaired += mode => log?.Invoke($"credentials: {file.FilePath} had mode {Convert.ToString((int)mode, 8)}; narrowed to 0600");
        return new CredentialService(file, connectionString, fallbackWarning:
            "No system keychain (Secret Service) is running, so your model credentials are kept in a file that only your account can read " +
            $"({file.FilePath}). They are protected only by your account's file permissions.");
    }

    // ------------------------------------------------------------------ values

    public string? Get(CredentialSlot slot) => Store.Get(CredentialSlots.Key(slot));

    public void Set(CredentialSlot slot, string value)
    {
        value = value?.Trim() ?? "";
        if (value.Length == 0) throw new ArgumentException("a credential cannot be empty", nameof(value));
        if (value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
        {
            throw new ArgumentException("a credential is a single token with no spaces or line breaks", nameof(value));
        }
        Store.Set(CredentialSlots.Key(slot), value);
        if (slot == CredentialSlot.ClaudeSetupToken) SaveSetting("claude", ModeName(ClaudeMode), issuedAt: _clock.GetUtcNow());
    }

    public bool Clear(CredentialSlot slot) => Store.Delete(CredentialSlots.Key(slot));

    // ------------------------------------------------------------------ the switch

    public ClaudeMode ClaudeMode
    {
        get => LoadSetting("claude")?.Mode == "apikey" ? ClaudeMode.ApiKey : ClaudeMode.Subscription;
        set => SaveSetting("claude", ModeName(value), issuedAt: LoadSetting("claude")?.IssuedAt);
    }

    public GrokMode GrokMode
    {
        get => LoadSetting("grok")?.Mode == "apikey" ? GrokMode.ApiKey : GrokMode.Session;
        set => SaveSetting("grok", value == GrokMode.ApiKey ? "apikey" : "session", issuedAt: null);
    }

    /// <summary>The credential the gateway adds for model.yawble.internal in the current mode, or null when none is set.</summary>
    public (ClaudeMode Mode, string? Value) ClaudeCredential()
    {
        var mode = ClaudeMode;
        return (mode, Get(mode == ClaudeMode.Subscription ? CredentialSlot.ClaudeSetupToken : CredentialSlot.AnthropicApiKey));
    }

    public ClaudeCredentialStatus ClaudeStatus()
    {
        var (mode, value) = ClaudeCredential();
        var issued = mode == ClaudeMode.Subscription ? LoadSetting("claude")?.IssuedAt : null;
        var renewSoon = issued is { } at && _clock.GetUtcNow() >= at + SetupTokenLifetime - RenewWarning;
        return new ClaudeCredentialStatus(mode, value is not null, issued, renewSoon);
    }

    /// <summary>When the stored setup-token was pasted (whatever the current mode), for its expiry and the 30-day prompt.</summary>
    public DateTimeOffset? SetupTokenIssuedAt => Get(CredentialSlot.ClaudeSetupToken) is null ? null : LoadSetting("claude")?.IssuedAt;

    private static string ModeName(ClaudeMode mode) => mode == ClaudeMode.ApiKey ? "apikey" : "subscription";

    private sealed record Setting(string Mode, DateTimeOffset? IssuedAt);

    private Setting? LoadSetting(string provider)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT mode, issued_at FROM credential_settings WHERE provider = $p;";
        cmd.Parameters.AddWithValue("$p", provider);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new Setting(reader.GetString(0),
            reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture));
    }

    private void SaveSetting(string provider, string mode, DateTimeOffset? issuedAt)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO credential_settings (provider, mode, issued_at, updated_at) VALUES ($p, $m, $i, $u)
            ON CONFLICT(provider) DO UPDATE SET mode = excluded.mode, issued_at = excluded.issued_at, updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$p", provider);
        cmd.Parameters.AddWithValue("$m", mode);
        cmd.Parameters.AddWithValue("$i", (object?)issuedAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$u", _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }
}
