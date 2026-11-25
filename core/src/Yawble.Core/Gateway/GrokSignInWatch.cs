using System.Security.Cryptography;
using System.Text;
using Yawble.Core.Credentials;

namespace Yawble.Core.Gateway;

/// <summary>The gateway's latest grok.yawble.internal request was refused as a credential problem (a 401).</summary>
/// <param name="At">When (the gateway log's clock).</param>
/// <param name="Reason">The card reason the gateway logged (no token in it).</param>
public sealed record GrokRefusal(DateTimeOffset At, string Reason);

/// <summary>
/// The outcome of the gateway's latest Grok sign-in request (grok.yawble.internal), across every run's gateway: a 401
/// credential-problem (the gateway had no usable session, or xAI refused the one it sent) or a success. The Agents tile
/// shows a refusal while it is the latest outcome, with the same fix as a signed-out auth.json.
/// <para>
/// A refusal is tied to the session it was about (a SHA-256 of its access token, in memory only; none for "no
/// session"): once auth.json holds a different session (the person signed in again, or the keeper renewed it) the
/// refusal is about a sign-in that is gone and is no longer shown.
/// </para>
/// </summary>
public sealed class GrokSignInWatch
{
    private readonly Lock _lock = new();
    private GrokRefusal? _refusal;
    private string? _refusedSession;

    /// <summary>A grok request was refused: <paramref name="session"/> is the one the gateway sent or found (null: none).</summary>
    public void Refused(DateTimeOffset at, string reason, GrokSession? session)
    {
        lock (_lock)
        {
            _refusal = new GrokRefusal(at, reason);
            _refusedSession = Fingerprint(session);
        }
    }

    /// <summary>A grok request went through (the upstream answered with a 2xx): any refusal is history.</summary>
    public void Succeeded()
    {
        lock (_lock)
        {
            _refusal = null;
            _refusedSession = null;
        }
    }

    /// <summary>
    /// The refusal to show now, given the session auth.json holds now (null: none); null when the latest outcome was a
    /// success, or the refusal was about another session than the current one.
    /// </summary>
    public GrokRefusal? Current(GrokSession? now)
    {
        lock (_lock)
        {
            if (_refusal is null) return null;
            return string.Equals(_refusedSession, Fingerprint(now), StringComparison.Ordinal) ? _refusal : null;
        }
    }

    private static string? Fingerprint(GrokSession? session) =>
        session is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session.AccessToken)));
}
