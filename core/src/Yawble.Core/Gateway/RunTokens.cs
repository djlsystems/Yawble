using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Yawble.Core.Gateway;

/// <summary>A run's bearer token: random, bound to one run, useless after it.</summary>
public sealed record RunToken(long RunId, string Value, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"RunToken(run {RunId}, <redacted>, expires {ExpiresAt:O})";
}

public enum TokenCheck { Valid, Missing, Wrong, Expired, Revoked }

/// <summary>
/// Issues and checks run tokens. A token is valid only for the run it was issued to, only until it expires (the
/// run's wall-clock limit plus a grace) and only until the run ends and the token is revoked. Comparison is constant
/// time. Tokens live only in memory: a core restart marks every open run interrupted, so none needs to survive one.
/// The token is also each CLI's credential placeholder (CLAUDE_CODE_OAUTH_TOKEN, ANTHROPIC_AUTH_TOKEN,
/// XAI_API_KEY, yawble-grok-auth's output), so the CLI's own auth header doubles as the gateway check.
/// </summary>
public sealed class RunTokenRegistry(TimeProvider? clock = null)
{
    public const string Prefix = "yawrt_";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<long, Entry> _tokens = new();

    private sealed record Entry(RunToken Token, bool Revoked);

    public RunToken Issue(long runId, TimeSpan lifetime)
    {
        var value = Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = new RunToken(runId, value, _clock.GetUtcNow() + lifetime);
        _tokens[runId] = new Entry(token, false);
        return token;
    }

    /// <summary>Checks <paramref name="presented"/> against the token of <paramref name="runId"/> only.</summary>
    public TokenCheck Check(long runId, string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return TokenCheck.Missing;
        if (!_tokens.TryGetValue(runId, out var entry)) return TokenCheck.Wrong;
        var match = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(entry.Token.Value));
        if (!match) return TokenCheck.Wrong;
        if (entry.Revoked) return TokenCheck.Revoked;
        return _clock.GetUtcNow() >= entry.Token.ExpiresAt ? TokenCheck.Expired : TokenCheck.Valid;
    }

    /// <summary>The run ended: its token stops working at once. Kept (revoked) so a late caller gets "revoked".</summary>
    public void Revoke(long runId)
    {
        if (_tokens.TryGetValue(runId, out var entry)) _tokens[runId] = entry with { Revoked = true };
    }

    public void Forget(long runId) => _tokens.TryRemove(runId, out _);
}
