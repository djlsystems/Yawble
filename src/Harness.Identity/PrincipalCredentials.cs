using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Identity;

/// <summary>
/// WHAT A PRINCIPAL STORE DOES THAT IS NOT SQL, whichever database holds the rows: the credential it
/// mints, the hash it keeps, the prefix the roster shows, the permits it writes and the refusals it
/// reads them back with. Both providers' principal stores call these, so a credential minted by
/// either resolves on the other: the stored text is the same string.
///
/// A credential is 256 bits of CSPRNG output stored as a SHA-256 hash, and a FAST hash is correct
/// here where a password hash would not be. There is no dictionary to attack and nothing for a work
/// factor to slow down - while a password hash on this path would put a deliberate delay on every
/// CLI call every agent makes.
/// </summary>
public static class PrincipalCredentials
{
    private const int PrefixLength = 8;

    /// <summary>The columns <see cref="ReadApiKey"/> reads, in its order.</summary>
    public const string ApiKeyColumns = "id, label, prefix, owner_user_id, created_at, last_used_at";

    /// <summary>A generated credential when none is supplied, else the supplied one trimmed.</summary>
    public static string Credential(string? supplied) =>
        string.IsNullOrWhiteSpace(supplied)
            ? Base64UrlEncode(RandomNumberGenerator.GetBytes(32))
            : supplied.Trim();

    /// <summary>The text stored in <c>principals.hash</c>: the SHA-256 of the credential's UTF-8, as
    /// upper-case hex.</summary>
    public static string Hash(string credential) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));

    /// <summary>What the roster shows of a credential. Min() because a SUPPLIED credential may be
    /// shorter than the prefix a generated one has.</summary>
    public static string Prefix(string credential) => credential[..Math.Min(PrefixLength, credential.Length)];

    /// <summary>The text stored in <c>principals.permits</c>.</summary>
    public static string PermitsJson(IReadOnlySet<string> permits) => JsonSerializer.Serialize(permits.ToArray());

    /// <summary>
    /// The principal a row describes, or null when its permits or its kind cannot be read.
    ///
    /// A permits value we cannot read is a REFUSAL, never an assumption of freedom. Treating
    /// authority we cannot parse as unlimited is how one corrupt row becomes an escalation.
    ///
    /// A kind we cannot read is refused for the same reason, and as a REFUSAL rather than an
    /// unhandled exception. Enum.Parse threw, which reached the caller as a 500 - a fault,
    /// where "this credential is not usable" is the honest answer and the one the handler
    /// already knows how to give.
    /// </summary>
    public static Principal? Read(string id, string kind, string permits, string? owner)
    {
        string[]? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<string[]>(permits);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is null) return null;

        if (!Enum.TryParse<PrincipalKind>(kind, out var parsedKind)) return null;

        return new Principal(id, parsedKind, new HashSet<string>(parsed, StringComparer.Ordinal), owner);
    }

    /// <summary>One API key's roster row, from the columns <see cref="ApiKeyColumns"/> names.</summary>
    public static ApiKeySummary ReadApiKey(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
            reader.IsDBNull(5)
                ? null
                : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// WHEN A PRINCIPAL'S <c>last_used_at</c> IS WRITTEN AGAIN, per process: at most once per
/// <see cref="Interval"/> per principal, unless somebody read the value since. Every authenticated
/// request touched it under a synchronous write: a member's run makes hundreds. The value is a signal
/// that a credential is in use, not a clock of each call.
/// </summary>
public sealed class PrincipalTouches(TimeProvider? clock = null)
{
    /// <summary>How long a principal's <c>last_used_at</c> stands before another request writes it again.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>When each principal's <c>last_used_at</c> was last written by this process.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _touched = new(StringComparer.Ordinal);

    /// <summary>The instant to write when <paramref name="id"/> is due a touch, else null.</summary>
    public DateTimeOffset? Due(string id)
    {
        var now = _clock.GetUtcNow();
        return !_touched.TryGetValue(id, out var last) || now - last >= Interval ? now : null;
    }

    /// <summary>Records that <paramref name="id"/>'s <c>last_used_at</c> was written at <paramref name="at"/>.</summary>
    public void Touched(string id, DateTimeOffset at) => _touched[id] = at;

    /// <summary>A reader is watching for the next use: it is written, not held for the interval.</summary>
    public void Watched(string id) => _touched.TryRemove(id, out _);
}
