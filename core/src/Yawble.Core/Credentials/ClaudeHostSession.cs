using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Yawble.Core.Credentials;

/// <summary>The access token of the person's own Claude sign-in on this computer, and when it expires.</summary>
public sealed record ClaudeHostToken(string AccessToken, DateTimeOffset? ExpiresAt)
{
    public bool ExpiresWithin(TimeSpan margin, DateTimeOffset now) => ExpiresAt is { } at && at - now <= margin;
}

/// <summary>
/// The gateway's side of Claude's sign-in (the gateway holds it): the core reads the
/// access token of the person's OWN Claude Code sign-in on the host (~/.claude/.credentials.json, written by the
/// person's claude; on macOS the login Keychain item "Claude Code-credentials", which holds the same JSON, read with
/// macOS's `security` tool) and puts it on each model request. The file is read at every request; the Keychain item is
/// kept in memory and read again only within <see cref="RenewMargin"/> of expiry, during a renewal, or after a 401. The sandbox gets only a run token, never the sign-in file, the Keychain
/// or the token. The value is never logged. The app never asks for a token and never refreshes one itself: Anthropic's refresh
/// tokens rotate, so the host's claude stays the ONLY refresher (two refreshers would sign the person out). When the
/// access token is about to expire, <see cref="RefreshAsync"/> asks the host's claude to renew it the way it renews
/// it for the person: `claude auth status` first, and if that did not renew it, one tiny headless prompt on the
/// smallest model (a little quota), with no MCP servers and no hooks.
/// </summary>
public sealed class ClaudeHostSession
{
    private readonly string _credentialsFile;
    private readonly IHostCredentialReader? _keychain;
    private readonly Func<string?> _claudeBinary;
    private readonly Action<string>? _log;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly Lock _keptLock = new();
    private ClaudeHostToken? _kept;
    // Counts every change of _kept: a read keeps its token only if nothing was kept or dropped since it began, so a
    // slow read never overwrites a newer token (or brings back one a 401 dropped).
    private long _keptGeneration;
    // The token CurrentAsync last handed out: the one a 401 refuses.
    private ClaudeHostToken? _lastGiven;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task> _runClaude;

    /// <summary>Renew this long before the access token expires.</summary>
    public static readonly TimeSpan RenewMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// macOS: the service of the login Keychain generic password Claude Code keeps its sign-in in (account = the user
    /// name). It holds the same JSON as ~/.claude/.credentials.json on Linux and Windows.
    /// </summary>
    public const string KeychainService = "Claude Code-credentials";

    /// <param name="runClaude">Runs the host's claude with arguments (tests replace it); default: the real process.</param>
    /// <param name="keychain">
    /// macOS: the reader of the login Keychain; the sign-in is read from <see cref="KeychainService"/> there instead of
    /// <paramref name="credentialsFile"/>. Null (Linux, Windows): the file.
    /// </param>
    public ClaudeHostSession(string credentialsFile, Func<string?> claudeBinary, Action<string>? log = null, TimeProvider? clock = null,
        Func<string, IReadOnlyList<string>, CancellationToken, Task>? runClaude = null, IHostCredentialReader? keychain = null)
    {
        _runClaude = runClaude ?? RunHostClaudeAsync;
        _credentialsFile = credentialsFile;
        _keychain = keychain;
        _claudeBinary = claudeBinary;
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The session for this computer: the login Keychain on macOS, the credentials file elsewhere.</summary>
    public static ClaudeHostSession ForThisHost(Func<string, string?>? env, Func<string?> claudeBinary, Action<string>? log = null) =>
        new(DefaultCredentialsFile(env), claudeBinary, log,
            keychain: OperatingSystem.IsMacOS() ? new MacHostCredentialReader() : null);

    /// <summary>Where the sign-in is read from, for logs and the person (never its value).</summary>
    public string Source => _keychain is null ? _credentialsFile : $"the login Keychain item \"{KeychainService}\"";

    /// <summary>The host's file: CLAUDE_CONFIG_DIR's, or ~/.claude's.</summary>
    public static string DefaultCredentialsFile(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var dir = env("CLAUDE_CONFIG_DIR") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(dir, ".credentials.json");
    }

    public string CredentialsFile => _credentialsFile;

    /// <summary>
    /// The current token, read fresh from the host's file (macOS: the login Keychain item); null when there is none (not
    /// signed in, or unreadable). On macOS the read also becomes the kept token (<see cref="CurrentAsync"/>).
    /// </summary>
    public ClaudeHostToken? Read()
    {
        long generation;
        lock (_keptLock) generation = _keptGeneration;
        try
        {
            if (_keychain is not null)
            {
                ClaudeHostToken? read;
                if (_keychain.Read(KeychainService) is not { Length: > 0 } json) read = null;
                else
                {
                    using var fromKeychain = JsonDocument.Parse(json);
                    read = Parse(fromKeychain);
                }
                KeepRead(read, generation);
                return read;
            }
            using var stream = new FileStream(_credentialsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            return Parse(doc);
        }
        catch (CredentialStoreException e)
        {
            // The reason is the app's own text (an exit code), never the value or the tool's stderr.
            _log?.Invoke($"claude sign-in: {e.Message}");
            KeepRead(null, generation);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            if (_keychain is not null) KeepRead(null, generation);
            return null;
        }
    }

    /// <summary>
    /// Keeps what a read that began at <paramref name="generation"/> found, unless the kept token changed meanwhile
    /// (another read kept a newer one, or a 401 dropped it): then the slower read's result is not kept.
    /// </summary>
    private void KeepRead(ClaudeHostToken? token, long generation)
    {
        lock (_keptLock)
        {
            if (_keptGeneration != generation) return;
            _kept = token;
            _keptGeneration++;
        }
    }

    /// <summary>Drops the kept token (a 401): every read in flight is not kept either.</summary>
    private void Drop()
    {
        lock (_keptLock)
        {
            _kept = null;
            _keptGeneration++;
        }
    }

    /// <summary>
    /// macOS: the token kept in memory from the last read of the Keychain item, while it is good for longer than
    /// <see cref="RenewMargin"/>; null when there is none, it is near expiry, or it was dropped (a refresh, a 401).
    /// Reading the item runs a process, so a model request does not read it while the kept token is good.
    /// </summary>
    private ClaudeHostToken? Kept()
    {
        if (_keychain is null) return null;
        lock (_keptLock)
            return _kept is { } t && !t.ExpiresWithin(RenewMargin, _clock.GetUtcNow()) ? t : null;
    }

    /// <summary>{"claudeAiOauth":{"accessToken":..., "expiresAt": ms}}: the same in the file and the Keychain item.</summary>
    private static ClaudeHostToken? Parse(JsonDocument doc)
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object) return null;
        if (!oauth.TryGetProperty("accessToken", out var token) || token.ValueKind != JsonValueKind.String || token.GetString() is not { Length: > 0 } access) return null;
        DateTimeOffset? expires = oauth.TryGetProperty("expiresAt", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        return new ClaudeHostToken(access, expires);
    }

    /// <summary>A token good for at least <see cref="RenewMargin"/>, renewing it through the host's claude when needed.</summary>
    public async Task<ClaudeHostToken?> CurrentAsync(CancellationToken ct = default)
    {
        if (Kept() is { } kept) return Given(kept);
        var token = Read();
        if (token is not null && !token.ExpiresWithin(RenewMargin, _clock.GetUtcNow())) return Given(token);
        await RefreshAsync(force: false, ct);
        token = Read();
        return token is not null && !token.ExpiresWithin(TimeSpan.Zero, _clock.GetUtcNow()) ? Given(token) : null;
    }

    private ClaudeHostToken Given(ClaudeHostToken token)
    {
        lock (_keptLock) _lastGiven = token;
        return token;
    }

    /// <summary>
    /// Asks the host's claude to renew its sign-in. One at a time; a caller that waited for another's renewal does not
    /// renew again unless <paramref name="force"/> (an upstream 401 says the token was refused although not expired).
    /// </summary>
    public async Task<bool> RefreshAsync(bool force, CancellationToken ct = default)
    {
        // A forced renewal (the API answered 401) drops the kept token: whatever happens next is read again.
        // The refused token is the one last handed out, remembered before it is dropped.
        string? refused = null;
        if (force)
        {
            lock (_keptLock) refused = (_lastGiven ?? _kept)?.AccessToken;
            Drop();
        }
        await _refreshing.WaitAsync(ct);
        try
        {
            // Another caller may have renewed it while this one waited.
            var now = Read();
            var fresh = now is not null && !now.ExpiresWithin(RenewMargin, _clock.GetUtcNow());
            if (!force && fresh) return true;
            // After a 401: the host's claude (or the person) already put a different, fresh token there. That is the
            // renewal; running claude again would spend quota for nothing.
            if (force && fresh && refused is not null && now!.AccessToken != refused) return true;
            if (_claudeBinary() is not { } claude)
            {
                _log?.Invoke("claude sign-in: cannot renew it, Claude Code is not installed on this computer");
                return false;
            }
            var start = now?.AccessToken;
            await _runClaude(claude, ["auth", "status", "--json"], ct);
            if (Renewed(start)) return true;
            _log?.Invoke("claude sign-in: renewing through the host's claude (one small headless prompt)");
            await _runClaude(claude,
                ["-p", "Reply with the single word ok.", "--model", "haiku", "--max-turns", "1", "--strict-mcp-config",
                 "--mcp-config", "{\"mcpServers\":{}}", "--settings", "{\"disableAllHooks\":true}"], ct);
            var renewed = Renewed(start);
            _log?.Invoke(renewed ? "claude sign-in: renewed" : "claude sign-in: not renewed; the person may need to run 'claude' and sign in again");
            // Not renewed after a 401: keep nothing, so the next request reads the item again.
            if (!renewed && force) Drop();
            return renewed;
        }
        finally
        {
            _refreshing.Release();
        }
    }

    private bool Renewed(string? previous) =>
        Read() is { } t && t.AccessToken != previous && !t.ExpiresWithin(RenewMargin, _clock.GetUtcNow());

    /// <summary>The person's own claude on the host, in an empty temp folder, prompts off, two minutes at most.</summary>
    private static async Task RunHostClaudeAsync(string claude, IReadOnlyList<string> args, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "yawble-claude-renew");
        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo(claude)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = dir,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Nothing of the core's own environment steers it to another endpoint or credential.
        foreach (var key in new[] { "ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN" })
            psi.Environment.Remove(key);
        using var process = Process.Start(psi);
        if (process is null) return;
        process.StandardInput.Close();
        var drainOut = process.StandardOutput.ReadToEndAsync(ct);
        var drainErr = process.StandardError.ReadToEndAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(limit.Token);
            await Task.WhenAll(drainOut, drainErr);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }
}
