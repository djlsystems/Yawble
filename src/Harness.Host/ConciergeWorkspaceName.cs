using System.Security.Cryptography;
using System.Text;

namespace Harness.Host;

/// <summary>
/// What a Concierge workspace DIRECTORY is called, composed from the person's login.
///
/// **THE NAME IS A LABEL AND NOTHING PARSES IT BACK.** Identity stays on the user id -
/// <c>ConciergeSessionKey</c>, <c>ConciergeLaunchFactory.PrincipalId</c> and every lookup - and the
/// only question this type answers is what a person reading a file manager sees. That is the whole
/// reason the folding below may be lossy: two logins are allowed to land on one segment, because
/// the segment was never the identifier. What keeps them apart is the ownership MARKER inside the
/// folder (<see cref="TeamPaths.ConciergeMarkerFileName"/>), read by
/// <see cref="ConciergeWorkspaces"/>, never the spelling of the folder.
///
/// A login is not a path, so the folding is deliberately narrow:
///
/// <list type="bullet">
/// <item>ASCII letters and digits, <c>-</c>, <c>_</c> and <c>.</c> survive. EVERYTHING else becomes
/// <c>-</c>: <c>/</c> and NUL, which no Linux file name may hold, control characters, spaces, and
/// every non-ASCII character. Non-ASCII is legal in a Linux file name and is folded out anyway,
/// because two logins that look identical (NFC against NFD) are different bytes, and a folder name
/// a person cannot tell apart from another is not a label. THE COST IS REAL AND IS ACCEPTED: a wholly non-Latin login folds to nothing and falls back on <see cref="Fallback"/>
/// plus its suffix, which is unique and usable but not readable. An email login -
/// <c>person@example.com</c> becoming <c>person-example.com</c> - is the case the rule is
/// tuned for.</item>
/// <item>FOLDED TO LOWER CASE. Logins compare case-insensitively, so <c>Alice</c> and <c>alice</c>
/// are one person's spelling twice; lower-casing makes them one segment, which turns the case pair
/// into an ordinary normalisation collision - and a collision is a thing this codebase handles,
/// deterministically, with the suffix.</item>
/// <item>Leading and trailing <c>-</c> and <c>.</c> are trimmed and runs of them collapsed. A
/// leading dot hides the folder from the person it was named for, and the trim is what keeps
/// <c>.</c> and <c>..</c> from ever being composed.</item>
/// </list>
/// </summary>
public static class ConciergeWorkspaceName
{
    /// <summary>
    /// How long the directory's own name may be: Linux's NAME_MAX, 255 bytes, which is the limit a
    /// single path segment actually meets on this host. Every character that survives the folding
    /// is ASCII, so characters and bytes are the same count here.
    ///
    /// There WAS a budget of 40 here, worked backwards from Windows' 260-character MAX_PATH for the
    /// git and node trees an agent grows underneath. The host runs only in a Linux container, whose
    /// PATH_MAX is 4096, so the budget guarded a failure this host cannot have.
    /// </summary>
    public const int MaxSegment = 255;

    /// <summary>Six hex characters - 24 bits. Long enough that two logins colliding AND sharing a
    /// suffix is not a case anybody meets, short enough that <c>alice-7f3a2c</c> still reads as
    /// alice.</summary>
    public const int SuffixHexLength = 6;

    /// <summary>What a login that folds away to nothing is called before its suffix. It is never
    /// used alone - <see cref="CandidatesFor"/> only ever reaches it on the suffixed branch - so
    /// two such logins get <c>user-7f3a2c</c> and <c>user-91b40e</c> rather than one folder.
    /// </summary>
    private const string Fallback = "user";

    /// <summary>
    /// The names this person's workspace may have, MOST READABLE FIRST, and the whole list is a
    /// pure function of the login and the user id.
    ///
    /// THAT PURITY IS THE POINT: never a counter. A counter
    /// records who arrived first and cannot be recomputed from the person alone, so on a rebuilt
    /// instance `alice-2` names nobody in particular. This list can be recomputed for any person
    /// at any time, on any instance, without reading anything: the resolver walks it in order and
    /// takes the first name no OTHER user's marker claims, so the pair <c>alice</c> and
    /// <c>alice-7f3a2c</c> is the same pair of names wherever and whenever it is composed.
    ///
    /// Which of the two a given person ends up holding still depends on who opened a Concierge
    /// first on THIS instance's disk. That is not the counter problem wearing a hat: the marker,
    /// not the name, is what says whose a folder is, so the worst a swap can do is give somebody a
    /// less flattering folder name. It can never hand one person another's workspace.
    ///
    /// ONE candidate rather than two when the readable form folded away to nothing.
    /// </summary>
    public static IReadOnlyList<string> CandidatesFor(string login, string userId)
    {
        var readable = Readable(login);
        var suffix = SuffixFor(userId);
        var basis = readable ?? Fallback;

        // One for the separator. Truncating the BASIS rather than the composed name keeps the
        // suffix whole: a clipped suffix is no longer derived from anything.
        var trimmed = Tidy(Clip(basis, MaxSegment - suffix.Length - 1));

        if (trimmed.Length == 0) trimmed = Fallback;

        var suffixed = $"{trimmed}-{suffix}";

        return readable is not null ? [readable, suffixed] : [suffixed];
    }

    /// <summary>
    /// The short, stable disambiguator: the first <see cref="SuffixHexLength"/> hex characters of
    /// SHA-256 over the USER ID. Derived from the id and from nothing else, so it survives a login
    /// rename, a rebuilt instance and a restored backup - the three places a counter stops
    /// agreeing with itself.
    /// </summary>
    public static string SuffixFor(string userId) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(userId ?? string.Empty))[..(SuffixHexLength / 2)]);

    /// <summary>
    /// The login folded to a path-legal segment, or NULL when nothing usable survives. See this
    /// type's own summary for the rules and why each one is there.
    /// </summary>
    public static string? Readable(string login)
    {
        if (string.IsNullOrWhiteSpace(login)) return null;

        var folded = new StringBuilder(login.Length);

        foreach (var character in login)
        {
            var lower = char.ToLowerInvariant(character);

            folded.Append(
                (lower is >= 'a' and <= 'z') || (lower is >= '0' and <= '9')
                || lower is '-' or '_' or '.'
                    ? lower
                    : '-');
        }

        var segment = Tidy(Clip(Tidy(folded.ToString()), MaxSegment));

        return segment.Length == 0 ? null : segment;
    }

    /// <summary>Runs of separators collapsed to the first of them, and separators trimmed off both
    /// ends. Run twice on the same string by <see cref="Readable"/> - once before the clip and once
    /// after - because clipping can expose a new trailing separator.</summary>
    private static string Tidy(string segment)
    {
        var collapsed = new StringBuilder(segment.Length);

        foreach (var character in segment)
        {
            var separator = character is '-' or '.';
            var previous = collapsed.Length == 0 ? '\0' : collapsed[^1];

            if (separator && previous is '-' or '.') continue;

            collapsed.Append(character);
        }

        return collapsed.ToString().Trim('-', '.');
    }

    private static string Clip(string segment, int length) =>
        segment.Length <= length ? segment : segment[..Math.Max(length, 0)];
}
