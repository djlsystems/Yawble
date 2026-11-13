using Yawble.Core.Bus;

namespace Yawble.Core.Ui;

// The contracts the screens are built against. The credential screens are wired to the credential service
// (Ui/ModelCredentials.cs, Ui/HostPaneTerminals.cs); runs and Open interactive CLI to the launcher
// (Ui/LaunchTerminals.cs). The fakes in Ui/Fakes.cs remain for YAWBLE_RUNNER=fake-pty (the UI e2e) and unit tests.

/// <summary>When a stored credential is in use and how fresh it is. Never a value, a prefix or a hash.</summary>
public sealed record SecretStatus(
    bool Connected,
    DateTimeOffset? AddedAt = null,
    DateTimeOffset? ExpiresAt = null,
    DateTimeOffset? LastUsedAt = null,
    bool RenewSoon = false,
    string? Problem = null);

/// <summary>The person's Grok session in the app-private host Grok home.</summary>
public sealed record GrokLoginStatus(bool SignedIn, DateTimeOffset? ExpiresAt = null, DateTimeOffset? LastRefreshAt = null, string? Problem = null);

/// <summary>Claude Code: the switch (subscription | apiKey) and both credentials behind it.</summary>
public sealed record ClaudeCredentials(string Mode, SecretStatus Subscription, SecretStatus ApiKey);

/// <summary>Grok: the switch (login | apiKey), the host session and the xAI key.</summary>
public sealed record GrokCredentials(string Mode, GrokLoginStatus Login, SecretStatus ApiKey);

/// <summary>
/// What Settings → Model credentials shows. <c>Implementation</c> is "os-store" for the real service and
/// "fake" for the unit tests' stand-in, which the screen labels. <c>Storage</c>: "keychain" (Credential Manager, Keychain, Secret Service) or "file" (the
/// owner-only 0600 fallback) with its one-time warning.
/// </summary>
public sealed record CredentialStatus(string Implementation, string Storage, string? StorageWarning, ClaudeCredentials Claude, GrokCredentials Grok);

/// <summary>The credential service as the screens use it (Credentials/ implements it).</summary>
public interface IModelCredentials
{
    CredentialStatus Status();

    /// <summary>The token `claude setup-token` printed, pasted by the person. Stored; never returned.</summary>
    void SetClaudeSetupToken(string token);

    void SetAnthropicApiKey(string key);

    void SetXaiApiKey(string key);

    /// <summary>claudeSubscription | anthropicApiKey | xaiApiKey | grokLogin.</summary>
    void Remove(string which);

    /// <summary>Flips a CLI's switch: claude → subscription | apiKey; grok → login | apiKey.</summary>
    void SetMode(string cli, string mode);

    /// <summary>The host pane running `grok login` against the host Grok home ended with this exit code.</summary>
    void GrokLoginFinished(int exitCode);
}

/// <summary>
/// The unsandboxed host panes of Settings → Model credentials: `claude setup-token` and `grok login` (against
/// the app-private host Grok home), each on a host PTY whose output goes to the hub channel host:&lt;kind&gt;.
/// </summary>
public interface IHostTerminals
{
    public const string ClaudeSetupToken = "claude-setup-token";
    public const string GrokLogin = "grok-login";

    /// <summary>Starts the pane's command. <paramref name="onExit"/> gets its exit code.</summary>
    Task StartAsync(string kind, string channel, Action<int> onExit, CancellationToken ct);
}

/// <summary>
/// Open interactive CLI: the member's own CLI TUI in a fresh sandbox under the same effective policy,
/// with the same bridge. The launcher implements it (a global run slot first, then SpawnWithPty / ConPTY).
/// </summary>
public interface IInteractiveSessions
{
    /// <summary><paramref name="size"/>: the window's fitted terminal size (the PTY starts at it); null: the default.</summary>
    Task StartAsync(MemberRow member, string channel, CancellationToken ct, Launch.PtySize? size = null);
}
