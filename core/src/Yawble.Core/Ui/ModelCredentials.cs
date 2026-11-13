using Yawble.Core.Bus;
using Yawble.Core.Credentials;

namespace Yawble.Core.Ui;

/// <summary>
/// Settings → Model credentials over the real credential service: the Claude setup-token
/// and Anthropic API key with their switch, the xAI key with Grok's switch, and the person's Grok session in the
/// app-private host Grok home. Status never carries a value, a prefix or a hash: only presence and dates.
/// </summary>
public sealed class ModelCredentials(CredentialService service, GrokHome grokHome, GrokKeeper? keeper = null, TimeProvider? clock = null)
    : IModelCredentials
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public CredentialStatus Status()
    {
        var now = _clock.GetUtcNow();
        var file = service.Store.Kind == "file-0600";

        var issued = service.SetupTokenIssuedAt;
        var subscription = service.Get(CredentialSlot.ClaudeSetupToken) is null
            ? new SecretStatus(false)
            : new SecretStatus(true, issued, issued + CredentialService.SetupTokenLifetime, null,
                issued is { } at && now >= at + CredentialService.SetupTokenLifetime - CredentialService.RenewWarning);

        return new CredentialStatus(
            "os-store",
            file ? "file" : "keychain",
            file ? service.FallbackWarning : null,
            new ClaudeCredentials(service.ClaudeMode == ClaudeMode.ApiKey ? "apiKey" : "subscription",
                subscription, Present(CredentialSlot.AnthropicApiKey)),
            new GrokCredentials(service.GrokMode == GrokMode.ApiKey ? "apiKey" : "login",
                GrokLogin(now), Present(CredentialSlot.XaiApiKey)));
    }

    private SecretStatus Present(CredentialSlot slot) => new(service.Get(slot) is not null);

    private GrokLoginStatus GrokLogin(DateTimeOffset now)
    {
        var session = grokHome.ReadSession();
        if (session is null) return new GrokLoginStatus(false);
        DateTimeOffset? lastRefresh = File.Exists(grokHome.AuthFile) ? new DateTimeOffset(File.GetLastWriteTimeUtc(grokHome.AuthFile)) : null;
        // An expired session is still "signed in": the keeper refreshes it with xAI's own grok, or the person signs in again.
        var problem = session.ExpiredAt(now) ? "session expired; the keeper refreshes it on the next use, or sign in again" : null;
        return new GrokLoginStatus(true, session.ExpiresAt, lastRefresh, problem);
    }

    public void SetClaudeSetupToken(string token) => Store(CredentialSlot.ClaudeSetupToken, token, "the setup-token");

    public void SetAnthropicApiKey(string key) => Store(CredentialSlot.AnthropicApiKey, key, "the Anthropic API key");

    public void SetXaiApiKey(string key) => Store(CredentialSlot.XaiApiKey, key, "the xAI API key");

    public void Remove(string which)
    {
        switch (which)
        {
            case "claudeSubscription": service.Clear(CredentialSlot.ClaudeSetupToken); break;
            case "anthropicApiKey": service.Clear(CredentialSlot.AnthropicApiKey); break;
            case "xaiApiKey": service.Clear(CredentialSlot.XaiApiKey); break;
            case "grokLogin":
                // Signing out removes the session xAI's grok wrote into the app-private home; nothing else is there.
                // Never the person's own Grok home: that sign-in is theirs, outside the app.
                if (grokHome.PersonsOwn) throw new BusRefusal("not_app_owned", "this is your own Grok sign-in: sign out with 'grok' in your own terminal");
                try { File.Delete(grokHome.AuthFile); }
                catch (DirectoryNotFoundException) { }
                break;
            default: throw new BusRefusal("bad_credential", $"no credential named '{which}'");
        }
    }

    public void SetMode(string cli, string mode)
    {
        switch (cli, mode)
        {
            case ("claude", "subscription"):
                Require(CredentialSlot.ClaudeSetupToken, "connect a Claude subscription before switching to it");
                service.ClaudeMode = ClaudeMode.Subscription;
                break;
            case ("claude", "apiKey"):
                Require(CredentialSlot.AnthropicApiKey, "add an Anthropic API key before switching to it");
                service.ClaudeMode = ClaudeMode.ApiKey;
                break;
            case ("grok", "login"):
                if (!grokHome.SignedIn) throw new BusRefusal("not_connected", "sign in to Grok before switching to it");
                service.GrokMode = GrokMode.Session;
                break;
            case ("grok", "apiKey"):
                Require(CredentialSlot.XaiApiKey, "add an xAI API key before switching to it");
                service.GrokMode = GrokMode.ApiKey;
                break;
            default:
                throw new BusRefusal("bad_mode", $"'{mode}' is not a credential mode of {cli}");
        }
    }

    /// <summary>The status reads auth.json itself; a successful sign-in only (re)starts the keeper.</summary>
    public void GrokLoginFinished(int exitCode)
    {
        if (exitCode == 0 && grokHome.SignedIn) keeper?.Start();
    }

    private void Require(CredentialSlot slot, string message)
    {
        if (service.Get(slot) is null) throw new BusRefusal("not_connected", message);
    }

    private void Store(CredentialSlot slot, string? value, string what)
    {
        CheckShape(value, what);
        service.Set(slot, value!.Trim());
    }

    /// <summary>The same shape check as the screens show: one line, no spaces, at least 8 characters.</summary>
    public static void CheckShape(string? value, string what)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new BusRefusal("empty", $"paste {what}");
        var v = value.Trim();
        if (v.Length < 8 || v.Any(char.IsWhiteSpace) || v.Any(char.IsControl))
            throw new BusRefusal("bad_format", $"that does not look like {what}: one line, no spaces, at least 8 characters");
    }
}
