using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yawble.Core.Credentials;

/// <summary>The access token and account of the person's own Codex sign-in on this computer, and when it expires.</summary>
public sealed record CodexHostToken(string AccessToken, string? AccountId, DateTimeOffset? ExpiresAt)
{
    public bool ExpiresWithin(TimeSpan margin, DateTimeOffset now) => ExpiresAt is { } at && at - now <= margin;

    public override string ToString() => $"CodexHostToken(<redacted>, expires {ExpiresAt:O})";
}

/// <summary>
/// The gateway's side of Codex's sign-in (the gateway holds it): the core reads the
/// access token of the person's OWN Codex sign-in on the host (CODEX_HOME/auth.json or ~/.codex/auth.json, written by
/// the person's codex) at every model request and puts it on the request to chatgpt.com. The sandbox gets a
/// placeholder auth.json whose tokens are the run token (<see cref="PlaceholderAuthJson"/>). The app never refreshes
/// the sign-in itself: OpenAI rotates refresh tokens, so the person's codex stays the only refresher. When the access
/// token is close to expiry, <see cref="RefreshAsync"/> runs the person's codex once with a tiny prompt, which renews it
/// the way codex renews it for the person (it refreshes a sign-in older than its refresh interval at start).
/// </summary>
public sealed class CodexHostSession
{
    private readonly string _authFile;
    private readonly Func<string?> _codexBinary;
    private readonly Action<string>? _log;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task> _runCodex;

    /// <summary>
    /// Renew this long before the access token expires. A Codex access token lasts about ten
    /// days; codex refreshes its sign-in itself when it is old (assumed: at start, past its refresh interval), so one run
    /// of codex inside this margin is expected to renew it. A renewal that did not happen is logged, not hidden.
    /// </summary>
    public static readonly TimeSpan RenewMargin = TimeSpan.FromHours(36);

    public CodexHostSession(string authFile, Func<string?> codexBinary, Action<string>? log = null, TimeProvider? clock = null,
        Func<string, IReadOnlyList<string>, CancellationToken, Task>? runCodex = null)
    {
        _authFile = authFile;
        _codexBinary = codexBinary;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _runCodex = runCodex ?? RunHostCodexAsync;
    }

    /// <summary>The host's file: CODEX_HOME's, or ~/.codex's.</summary>
    public static string DefaultAuthFile(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var dir = env("CODEX_HOME") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        return Path.Combine(dir, "auth.json");
    }

    public string AuthFile => _authFile;

    /// <summary>The current token, read fresh from the host's file; null when there is none (not signed in, an API key, unreadable).</summary>
    public CodexHostToken? Read()
    {
        if (ReadTokens() is not { } tokens) return null;
        if (tokens["access_token"]?.GetValue<string>() is not { Length: > 0 } access) return null;
        return new CodexHostToken(access, tokens["account_id"]?.GetValue<string>(), JwtExpiry(access));
    }

    private JsonObject? ReadTokens()
    {
        try
        {
            using var stream = new FileStream(_authFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonNode.Parse(stream) is JsonObject root && root["tokens"] is JsonObject tokens ? tokens : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A token good for at least a few minutes, renewing it through the host's codex when it is close to expiry.</summary>
    public async Task<CodexHostToken?> CurrentAsync(CancellationToken ct = default)
    {
        var token = Read();
        if (token is not null && !token.ExpiresWithin(RenewMargin, _clock.GetUtcNow())) return token;
        // Close to expiry but still valid: serve it now and renew in the background, so a request never waits on codex.
        if (token is not null && !token.ExpiresWithin(TimeSpan.FromMinutes(5), _clock.GetUtcNow()))
        {
            _ = RefreshAsync(force: false, CancellationToken.None);
            return token;
        }
        await RefreshAsync(force: false, ct);
        token = Read();
        return token is not null && !token.ExpiresWithin(TimeSpan.Zero, _clock.GetUtcNow()) ? token : null;
    }

    /// <summary>Asks the host's codex to renew its sign-in. One at a time; skipped when another caller just renewed it.</summary>
    public async Task<bool> RefreshAsync(bool force, CancellationToken ct = default)
    {
        await _refreshing.WaitAsync(ct);
        try
        {
            var now = Read();
            if (!force && now is not null && !now.ExpiresWithin(RenewMargin, _clock.GetUtcNow())) return true;
            if (_codexBinary() is not { } codex)
            {
                _log?.Invoke("codex sign-in: cannot renew it, Codex is not installed on this computer");
                return false;
            }
            _log?.Invoke("codex sign-in: renewing through the host's codex (one small headless prompt)");
            await _runCodex(codex, ["exec", "--skip-git-repo-check", "Reply with the single word ok."], ct);
            var after = Read();
            var renewed = after is not null && after.AccessToken != now?.AccessToken;
            _log?.Invoke(renewed ? "codex sign-in: renewed" : "codex sign-in: not renewed; the person may need to run 'codex login' again");
            return renewed;
        }
        finally
        {
            _refreshing.Release();
        }
    }

    /// <summary>
    /// The sandbox's auth.json: the person's sign-in shape with no secret in it. Its access and refresh tokens are the
    /// run token (the gateway checks it, then puts the person's token on the request), and its id_token is an UNSIGNED
    /// token carrying only the claims codex reads (email, plan, account), so the CLI shows the right account and plan.
    /// Null when the person is not signed in with ChatGPT.
    /// </summary>
    public string? PlaceholderAuthJson(string runToken)
    {
        if (ReadTokens() is not { } tokens || tokens["id_token"]?.GetValue<string>() is not { } idToken) return null;
        if (JwtClaims(idToken) is not { } claims) return null;
        var now = _clock.GetUtcNow();
        var keep = new JsonObject
        {
            ["exp"] = now.AddDays(30).ToUnixTimeSeconds(),
            ["iat"] = now.ToUnixTimeSeconds(),
        };
        foreach (var name in new[] { "email", "https://api.openai.com/auth" })
            if (claims[name] is { } value) keep[name] = value.DeepClone();
        var placeholderId = B64(new JsonObject { ["alg"] = "none", ["typ"] = "JWT" }) + "." + B64(keep) + ".yawble";
        var auth = new JsonObject
        {
            ["auth_mode"] = "chatgpt",
            ["OPENAI_API_KEY"] = null,
            ["tokens"] = new JsonObject
            {
                ["id_token"] = placeholderId,
                ["access_token"] = runToken,
                ["refresh_token"] = runToken,
                ["account_id"] = tokens["account_id"]?.GetValue<string>(),
            },
            ["last_refresh"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
        };
        return auth.ToJsonString();
    }

    /// <summary>Whether a member's auth.json holds nothing but this run's placeholder (the post-run check).</summary>
    public static bool IsPlaceholderOnly(string json, string runToken)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["tokens"] is not JsonObject tokens) return false;
            if (root["OPENAI_API_KEY"] is { } key && key.GetValueKind() != JsonValueKind.Null) return false;
            return tokens["access_token"]?.GetValue<string>() == runToken && tokens["refresh_token"]?.GetValue<string>() == runToken
                   && tokens["id_token"]?.GetValue<string>() is { } id && id.EndsWith(".yawble", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string B64(JsonObject o) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(o.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static JsonObject? JwtClaims(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var p = parts[1].Replace('-', '+').Replace('_', '/');
            p += new string('=', (4 - p.Length % 4) % 4);
            return JsonNode.Parse(Convert.FromBase64String(p)) as JsonObject;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? JwtExpiry(string jwt) =>
        JwtClaims(jwt)?["exp"] is JsonValue v && v.TryGetValue<long>(out var s) ? DateTimeOffset.FromUnixTimeSeconds(s) : null;

    /// <summary>The person's own codex on the host, in an empty temp folder, three minutes at most.</summary>
    private static async Task RunHostCodexAsync(string codex, IReadOnlyList<string> args, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "yawble-codex-renew");
        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo(codex)
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
        foreach (var key in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "OPENAI_BASE_URL", "CODEX_ACCESS_TOKEN" })
            psi.Environment.Remove(key);
        using var process = Process.Start(psi);
        if (process is null) return;
        process.StandardInput.Close();
        var drainOut = process.StandardOutput.ReadToEndAsync(ct);
        var drainErr = process.StandardError.ReadToEndAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(3));
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
