namespace Yawble.Core.Credentials;

/// <summary>
/// The credentials the app holds for the person. Each is swapped in by the per-run gateway and
/// never enters a sandbox. The Grok LOGIN is not here: xAI's CLI must rewrite its own auth.json, so it lives in
/// the app-private host Grok home (<see cref="GrokHome"/>).
/// </summary>
public enum CredentialSlot
{
    /// <summary>The one-year inference-only token from `claude setup-token`, sent as Authorization: Bearer.</summary>
    ClaudeSetupToken,

    /// <summary>An Anthropic Console API key, sent as x-api-key.</summary>
    AnthropicApiKey,

    /// <summary>The xAI API key fallback for Grok, sent as Authorization: Bearer to api.x.ai.</summary>
    XaiApiKey,
}

public static class CredentialSlots
{
    /// <summary>The account name under the app's own service name. Never "Claude Code-credentials" or the like.</summary>
    public static string Key(CredentialSlot slot) => slot switch
    {
        CredentialSlot.ClaudeSetupToken => "claude.setup-token",
        CredentialSlot.AnthropicApiKey => "anthropic.api-key",
        CredentialSlot.XaiApiKey => "xai.api-key",
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    public static IReadOnlyList<CredentialSlot> All { get; } = Enum.GetValues<CredentialSlot>();
}

/// <summary>
/// One place secrets are kept: an OS store, or the owner-only file fallback. Implementations never log a value.
/// </summary>
public interface ICredentialStore
{
    /// <summary>"windows-credential-manager", "macos-keychain", "linux-secret-service" or "file-0600".</summary>
    string Kind { get; }

    string? Get(string key);

    void Set(string key, string value);

    /// <summary>Removes the entry; true when there was one.</summary>
    bool Delete(string key);
}

/// <summary>A store operation the OS refused, with its native code. The message never contains a secret.</summary>
public sealed class CredentialStoreException(string message) : Exception(message);
