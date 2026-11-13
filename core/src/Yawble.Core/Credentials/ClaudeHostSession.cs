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
/// person's claude) at every model request and puts it on the request. The sandbox gets only a run token, never the
/// sign-in file or the token. The app never asks for a token and never refreshes one itself: Anthropic's refresh
/// tokens rotate, so the host's claude stays the ONLY refresher (two refreshers would sign the person out). When the
/// access token is about to expire, <see cref="RefreshAsync"/> asks the host's claude to renew it the way it renews
/// it for the person: `claude auth status` first, and if that did not renew it, one tiny headless prompt on the
/// smallest model (a little quota), with no MCP servers and no hooks.
/// </summary>
public sealed class ClaudeHostSession
{
    private readonly string _credentialsFile;
    private readonly Func<string?> _claudeBinary;
    private readonly Action<string>? _log;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task> _runClaude;

    /// <summary>Renew this long before the access token expires.</summary>
    public static readonly TimeSpan RenewMargin = TimeSpan.FromMinutes(5);

    /// <param name="runClaude">Runs the host's claude with arguments (tests replace it); default: the real process.</param>
    public ClaudeHostSession(string credentialsFile, Func<string?> claudeBinary, Action<string>? log = null, TimeProvider? clock = null,
        Func<string, IReadOnlyList<string>, CancellationToken, Task>? runClaude = null)
    {
        _runClaude = runClaude ?? RunHostClaudeAsync;
        _credentialsFile = credentialsFile;
        _claudeBinary = claudeBinary;
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The host's file: CLAUDE_CONFIG_DIR's, or ~/.claude's.</summary>
    public static string DefaultCredentialsFile(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var dir = env("CLAUDE_CONFIG_DIR") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(dir, ".credentials.json");
    }

    public string CredentialsFile => _credentialsFile;

    /// <summary>The current token, read fresh from the host's file; null when there is none (not signed in, or unreadable).</summary>
    public ClaudeHostToken? Read()
    {
        try
        {
            using var stream = new FileStream(_credentialsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object) return null;
            if (!oauth.TryGetProperty("accessToken", out var token) || token.GetString() is not { Length: > 0 } access) return null;
            DateTimeOffset? expires = oauth.TryGetProperty("expiresAt", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
            return new ClaudeHostToken(access, expires);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>A token good for at least <see cref="RenewMargin"/>, renewing it through the host's claude when needed.</summary>
    public async Task<ClaudeHostToken?> CurrentAsync(CancellationToken ct = default)
    {
        var token = Read();
        if (token is not null && !token.ExpiresWithin(RenewMargin, _clock.GetUtcNow())) return token;
        await RefreshAsync(force: false, ct);
        token = Read();
        return token is not null && !token.ExpiresWithin(TimeSpan.Zero, _clock.GetUtcNow()) ? token : null;
    }

    /// <summary>
    /// Asks the host's claude to renew its sign-in. One at a time; a caller that waited for another's renewal does not
    /// renew again unless <paramref name="force"/> (an upstream 401 says the token was refused although not expired).
    /// </summary>
    public async Task<bool> RefreshAsync(bool force, CancellationToken ct = default)
    {
        await _refreshing.WaitAsync(ct);
        try
        {
            // Another caller may have renewed it while this one waited.
            var now = Read();
            if (!force && now is not null && !now.ExpiresWithin(RenewMargin, _clock.GetUtcNow())) return true;
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
