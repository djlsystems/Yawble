using Yawble.Core.Policy;

namespace Yawble.Core.Agents;

/// <summary>How the sandbox may use one sign-in file of the host.</summary>
public enum SignInAccess
{
    /// <summary>Readable, never writable: the CLI uses the host's sign-in but cannot refresh it in place.</summary>
    ReadOnly,

    /// <summary>Readable and writable: the CLI may rewrite it (a refresh). Unused by <see cref="SignInCatalog"/>.</summary>
    ReadWrite,

    /// <summary>
    /// Copied into the member's own folder before each run, never granted; a newer copy (the CLI refreshed it) is
    /// written back to the host after the run. For a CLI that must be able to write its sign-in and has no variable
    /// that points at the file alone (Codex: auth.json lives in CODEX_HOME with everything else).
    /// </summary>
    CopyIn,

    /// <summary>In the OS credential store: read by the host at launch and given to the run (Copilot).</summary>
    HostStore,
}

/// <summary>One sign-in file of the host, granted to the CLI's sandbox (by exact path, never its folder).</summary>
public sealed record CliSignInFile(
    string Path,          // the host's own file: ~/.claude/.credentials.json, ~/.grok/auth.json
    SignInAccess Access,  // from SignInCatalog: one setting per file
    bool Present,         // exists on the host (never opened, never read)
    string What);         // plain words for the Agents dialog

/// <summary>
/// The CLI's own sign-in on the host, as the sandbox uses it: the exact files granted, how the CLI inside
/// is pointed at them, and whether the person is signed in, from file presence only (no value is ever read or shown).
/// </summary>
public sealed record CliSignIn(
    string Status,                                    // SignInStatus.*
    string Detail,                                    // plain words for the Agents dialog
    IReadOnlyList<CliSignInFile> Files,               // what the sandbox is granted (empty when Pending)
    IReadOnlyDictionary<string, string> Environment); // how the CLI inside finds the host's files

public static class SignInStatus
{
    public const string SignedIn = "signed-in";
    public const string NotSignedIn = "not-signed-in";

    /// <summary>The sandbox has no way to use this sign-in (macOS Claude: it is in the Keychain). Runs refuse with Detail.</summary>
    public const string Pending = "pending";
}

/// <summary>
/// The sign-in files each CLI needs, per OS, and the access each gets. The access is ONE setting per file
/// (<see cref="ClaudeCredentialsAccess"/>, <see cref="GrokAuthAccess"/>): read-only.
/// <para>
/// Nothing else of the config folder is granted. The sandbox HOME stays the member's own; the CLI is pointed at the
/// host file by its own variable, so no symlink and no copy is made:
/// </para>
/// <list type="bullet">
/// <item>Claude Code (Linux, Windows): CLAUDE_SECURESTORAGE_CONFIG_DIR = the folder holding .credentials.json (default
/// ~/.claude, or $CLAUDE_CONFIG_DIR). Claude Code reads its plaintext credential store from
/// &lt;dir&gt;/.credentials.json and nothing else from that folder; CLAUDE_CONFIG_DIR stays the member's own
/// (settings, sessions, ~/.claude.json state). A prompt runs with only that file visible, read-only.
/// ~/.claude.json (oauthAccount) is not needed for the sign-in.</item>
/// <item>Claude Code (macOS): the sign-in is in the login Keychain ("Claude Code-credentials"); the sandbox has no
/// Keychain access (seatbelt keychainAccess false), so Claude members do not run on macOS.</item>
/// <item>Grok (all OS): GROK_AUTH_PATH = ~/.grok/auth.json (or $GROK_HOME/auth.json). GROK_HOME stays the member's
/// own. grok keeps auth.json.lock beside it; it is not granted.</item>
/// </list>
/// <para>
/// The grant is the FILE, not its folder. Under bwrap (how the sandbox binds on Linux), a folder grant with the
/// other entries denied shows a denied file's NEW content once the host replaces it by rename, and any entry created
/// during the run (Grok's mcp_credentials.json, transcripts); a file grant keeps showing the file as it was at launch
/// when the host CLI renews it by rename, so a run that spans the host's renewal stops with "sign in again" (the CLIs
/// renew about 5 minutes before expiry). The second is a reliability cost, the first a leak, so the file it is.
/// Read-only: neither CLI refreshes inside the sandbox (no lock, no write), so a sandbox can never use up the person's
/// rotating refresh token; the vendors' token endpoints stay blocked by the floor rules as well.
/// </para>
/// </summary>
public static class SignInCatalog
{
    /// <summary>~/.claude/.credentials.json: the access token, refresh token and expiry.</summary>
    public const SignInAccess ClaudeCredentialsAccess = SignInAccess.ReadOnly;

    /// <summary>~/.grok/auth.json: the session key, refresh token and expiry.</summary>
    public const SignInAccess GrokAuthAccess = SignInAccess.ReadOnly;

    public const string ClaudeStoreVariable = "CLAUDE_SECURESTORAGE_CONFIG_DIR";
    public const string GrokAuthVariable = "GROK_AUTH_PATH";

    public const string MacClaudePending =
        "Claude Code keeps its sign-in in the macOS Keychain, which the sandbox cannot use without opening the whole login "
        + "Keychain to every agent, so Claude members cannot run on macOS; Grok members can.";

    public static CliSignIn Claude(DetectionHost h)
    {
        if (h.Os == TargetOs.MacOS)
            return new CliSignIn(SignInStatus.Pending, MacClaudePending, [], new Dictionary<string, string>());
        var dir = h.FromVar("CLAUDE_CONFIG_DIR") ?? h.Combine(h.Home, ".claude");
        var file = h.Combine(dir, ".credentials.json");
        var how = "run 'claude' in your own terminal and sign in (/login)";
        // Claude Code on Windows moves the sign-in into Windows Credential Manager when Anthropic's rollout flag is on,
        // and deletes the file. The sandbox cannot use that store.
        if (h.Os == TargetOs.Windows)
            how += ". If you are signed in and this file is still missing, Claude Code keeps your sign-in in Windows Credential "
                   + "Manager, which agents cannot use";
        return Make([new CliSignInFile(file, ClaudeCredentialsAccess, h.Fs.FileExists(file), "Claude Code's sign-in (.credentials.json)")],
            new Dictionary<string, string> { [ClaudeStoreVariable] = dir },
            "claude", how, gatewayHeld: true);
    }

    public static CliSignIn Grok(DetectionHost h)
    {
        var dir = h.FromVar("GROK_HOME") ?? h.Combine(h.Home, ".grok");
        var file = h.Combine(dir, "auth.json");
        return Make([new CliSignInFile(file, GrokAuthAccess, h.Fs.FileExists(file), "Grok's sign-in (auth.json)")],
            new Dictionary<string, string> { [GrokAuthVariable] = file },
            "grok", "run 'grok login' in your own terminal", gatewayHeld: true);
    }

    /// <summary>The host's credential store, for the CLIs that keep their sign-in there (tests replace it).</summary>
    public static Credentials.IHostCredentialReader Store { get; set; } = Credentials.HostCredentials.ForThisHost();

    /// <summary>
    /// Codex: ~/.codex/auth.json (or $CODEX_HOME/auth.json), COPIED into the member's CODEX_HOME at launch and written
    /// back when the run refreshed it (<see cref="SignInAccess.CopyIn"/>). A run
    /// in the sandbox with a copy older than Codex's refresh interval refreshes it at auth.openai.com and rotates the refresh
    /// token; the host's copy then no longer works until the new one is copied back.
    /// </summary>
    public static CliSignIn Codex(DetectionHost h)
    {
        var dir = h.FromVar("CODEX_HOME") ?? h.Combine(h.Home, ".codex");
        var file = h.Combine(dir, "auth.json");
        return Make([new CliSignInFile(file, SignInAccess.CopyIn, h.Fs.FileExists(file), "Codex's sign-in (auth.json)")],
            new Dictionary<string, string>(), "codex", "run 'codex' in your own terminal and sign in (codex login)", gatewayHeld: true);
    }

    /// <summary>The Credential Manager targets of GitHub Copilot CLI's own sign-in: one per GitHub account.</summary>
    public const string CopilotTargetFilter = "https://github.com:*";

    public const string CopilotTargetSuffix = ".copilot-cli";

    /// <summary>
    /// GitHub Copilot CLI: its token is in the OS credential store (Windows: Credential Manager, target
    /// https://github.com:&lt;login&gt;.copilot-cli). The sandbox cannot read the store, so the launcher reads the token
    /// at launch and gives it as COPILOT_GITHUB_TOKEN. Copilot's fallback to the GitHub CLI's (gh) token is NOT used:
    /// that token reaches every repository of the person.
    /// </summary>
    public static CliSignIn Copilot(DetectionHost h)
    {
        var how = "run 'copilot' in your own terminal and sign in with /login";
        if (h.Os != TargetOs.Windows)
            return new CliSignIn(SignInStatus.Pending,
                "GitHub Copilot keeps its sign-in in the system keychain, which agents cannot use on this OS.", [], new Dictionary<string, string>());
        // CredEnumerate takes a wildcard only at the end: list github.com's entries, keep Copilot CLI's own.
        var target = Store.Targets(CopilotTargetFilter).FirstOrDefault(t => t.EndsWith(CopilotTargetSuffix, StringComparison.OrdinalIgnoreCase));
        return Make([new CliSignInFile(target ?? "https://github.com:<account>" + CopilotTargetSuffix, SignInAccess.HostStore, target is not null,
                "GitHub Copilot's sign-in (Windows Credential Manager)")],
            new Dictionary<string, string>(), "copilot", how);
    }

    /// <summary>Copilot CLI's own token from the OS credential store, read now (the gateway, per request); null when none.</summary>
    public static string? CopilotToken()
    {
        var target = Store.Targets(CopilotTargetFilter).FirstOrDefault(t => t.EndsWith(CopilotTargetSuffix, StringComparison.OrdinalIgnoreCase));
        return target is null ? null : Store.Read(target) is { Length: > 0 } token ? token : null;
    }

    public const string AntigravityPending =
        "Antigravity keeps its sign-in in the system keychain, which agents cannot use on this OS.";

    /// <summary>Antigravity CLI's own sign-in in Windows Credential Manager (go-keyring: service "gemini", user "antigravity").</summary>
    public const string AntigravityTarget = "gemini:antigravity";

    /// <summary>
    /// Antigravity (agy): its sign-in is in Windows Credential Manager, which the sandbox cannot read. When the
    /// store is denied, agy falls back to a file, ~/.gemini/antigravity-cli/antigravity-oauth-token, in the same JSON form
    /// (agy's auth.NewCLITokenStorage). The launcher reads the entry on the host at each launch and
    /// writes it there, in the member's own home; agy refreshes the access token itself (Google's refresh tokens do not
    /// rotate, so nothing is copied back).
    /// </summary>
    public static CliSignIn Antigravity(DetectionHost h)
    {
        if (h.Os != TargetOs.Windows)
            return new CliSignIn(SignInStatus.Pending, AntigravityPending, [], new Dictionary<string, string>());
        var present = Store.Targets(AntigravityTarget).Any(t => string.Equals(t, AntigravityTarget, StringComparison.OrdinalIgnoreCase));
        return Make([new CliSignInFile(AntigravityTarget, SignInAccess.HostStore, present, "Antigravity's sign-in (Windows Credential Manager)")],
            new Dictionary<string, string>(), "antigravity", "run 'agy' in your own terminal and sign in");
    }

    /// <param name="gatewayHeld">The gateway holds this sign-in (Claude, Grok, Codex): agents never see it.</param>
    private static CliSignIn Make(IReadOnlyList<CliSignInFile> files, IReadOnlyDictionary<string, string> env, string cli, string how, bool gatewayHeld = false)
    {
        var missing = files.Where(f => !f.Present).ToList();
        if (missing.Count == 0 && gatewayHeld)
            return new CliSignIn(SignInStatus.SignedIn, $"Signed in on this computer ({string.Join(", ", files.Select(f => f.Path))}). Agents use this sign-in "
                + $"through the app's gateway, which adds it to each model request: agents never see it, and your own {cli} renews it.", files, env);
        return missing.Count == 0
            ? new CliSignIn(SignInStatus.SignedIn, $"Signed in on this computer ({string.Join(", ", files.Select(f => f.Path))}). Agents use this sign-in, "
                + string.Join(", ", files.Select(f => f.Access switch
                {
                    SignInAccess.ReadOnly => "read-only",
                    SignInAccess.CopyIn => "as a copy for each run (a refreshed sign-in is copied back)",
                    SignInAccess.HostStore => "read by the app at each launch and given to that run only",
                    _ => "read-write",
                })) + ".", files, env)
            : new CliSignIn(SignInStatus.NotSignedIn, $"Not signed in: {string.Join(", ", missing.Select(f => f.Path))} is missing. To sign in, {how}, then reopen this dialog.", files, env);
    }
}
