using System.Security.Cryptography;
using System.Text;

namespace Harness.Host;

/// <summary>
/// What tells one instance of this host on a machine from another - the name its session cookie
/// takes, and the application name its Data Protection key ring is filed under.
///
/// It is derived from the DATA ROOT and from nothing else. That is deliberate rather than
/// convenient: "one data root, one database, one process" is already this codebase's instance
/// boundary, so two hosts are two instances exactly when their data roots differ, and there is no
/// second fact here to drift out of step with the first.
/// </summary>
/// <remarks>
/// The problem this solves: a cookie is keyed by (host, path, name) and PORT IS NOT PART OF THAT
/// KEY - RFC 6265 §8.5 says cookies give no isolation by port, in every browser, by design. So
/// <c>core</c> on 8090 and a worktree host on 8095 are one jar on one host, and while every
/// instance named its cookie "harness" they fought over one slot in it: signing in to either
/// signed you out of the other. A separate browser profile worked only because it was a separate
/// jar, which treats the symptom.
/// </remarks>
public static class InstanceIdentity
{
    /// <summary>How much of the folder's own name to keep. A cookie name has no practical length
    /// limit; this is about a jar that stays readable at a glance.</summary>
    private const int ReadableLimit = 24;

    /// <summary>The name this instance's session cookie takes. Every instance on the machine sends
    /// every other instance's cookie - that is what "not scoped by port" means, and it costs a few
    /// hundred bytes a request for the three or four local hosts this is built for. Worth knowing
    /// before somebody runs fifty.</summary>
    public static string CookieNameFor(string dataRoot) => $"harness-{For(dataRoot)}";

    /// <summary>
    /// A stable, legal, readable identifier for the instance rooted at <paramref name="dataRoot"/>.
    ///
    /// STABILITY is the property everything else rests on. An identity that moves between two
    /// starts of one instance signs everybody out on every boot, and presents as a session bug
    /// rather than as a naming one - so a PID, a start timestamp or a GUID is disqualified, and so
    /// is <c>string.GetHashCode</c>, which is randomised per process in .NET Core and would be
    /// stable within a run and different on the next one.
    /// </summary>
    public static string For(string dataRoot)
    {
        var canonical = Canonical(dataRoot);
        var readable = Readable(canonical);

        // The fingerprint alone would be correct. The folder name is carried in front of it because
        // telling two instances apart in a browser's cookie jar is the operator action this feature
        // exists for, and a bare hash makes that a lookup rather than a glance.
        return readable.Length == 0 ? Fingerprint(canonical) : $"{readable}-{Fingerprint(canonical)}";
    }

    /// <summary>
    /// One directory, one string. <c>GetFullPath</c> resolves <c>.</c>, <c>..</c> and a relative
    /// root; the trailing separator is dropped.
    /// </summary>
    private static string Canonical(string dataRoot) =>
        Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>
    /// The folder's own name, reduced to characters that are legal in a cookie name.
    ///
    /// An ALLOWLIST, for the reason <c>ContainerId.IsLegalName</c> gives: a path may hold spaces,
    /// semicolons, equals signs and any Unicode at all, and a denylist is only ever as current as
    /// the last character somebody thought of. A folder name with nothing legal in it comes back
    /// EMPTY rather than refused - the identity is still unique, because the half that makes it
    /// unique is not this half.
    /// </summary>
    private static string Readable(string canonical)
    {
        var leaf = Path.GetFileName(canonical);

        // `/` has no file name and sanitises to nothing; the fingerprint carries the meaning.
        if (leaf.Length == 0) leaf = canonical;

        var kept = new StringBuilder();

        foreach (var c in leaf)
        {
            if (kept.Length == ReadableLimit) break;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_') kept.Append(c);
        }

        return kept.ToString().Trim('-');
    }

    /// <summary>
    /// SHA-256 over the canonical path, first four bytes. Cryptographic strength is beside the
    /// point - what is wanted is a digest that is identical in every process on every run, which
    /// rules out the runtime's own string hash, and wide enough that two data roots on one machine
    /// do not land on one name.
    /// </summary>
    private static string Fingerprint(string canonical) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))[..4]);
}
