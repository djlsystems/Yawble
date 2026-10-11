namespace Harness.Contracts;

/// <summary>
/// A container's identity. Names are global in the message log, which outlives any team, so a bare
/// name is not an identity - two teams each creating a `Manager` produced one container, one mailbox
/// and one ledger identity shared between them. The pair is the fix.
/// </summary>
/// <remarks>
/// The canonical string form is <c>Team/Name</c>. `/` is deliberate: the addressed-instruction type
/// is dot-separated, so `container.instruction.Alpha/Manager` stays unambiguous. It is NOT usable as
/// a single path segment - see FileTranscriptStore, which nests instead.
/// </remarks>
public sealed record ContainerId
{
    public ContainerId(string team, string name)
    {
        if (!IsLegalName(team)) throw new ArgumentException($"'{team}' is not a legal team name.", nameof(team));
        if (!IsLegalName(name)) throw new ArgumentException($"'{name}' is not a legal container name.", nameof(name));

        Team = team;
        Name = name;
    }

    public string Team { get; }

    public string Name { get; }

    /// <summary>
    /// Ordinal rather than culture-aware on purpose: these are identifiers, and a Turkish dotless i
    /// deciding whether two containers are the same is not a behaviour anyone wants.
    /// </summary>
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public bool Equals(ContainerId? other) =>
        other is not null && Comparer.Equals(Team, other.Team) && Comparer.Equals(Name, other.Name);

    public override int GetHashCode() =>
        HashCode.Combine(Comparer.GetHashCode(Team), Comparer.GetHashCode(Name));

    public override string ToString() => $"{Team}/{Name}";

    public static ContainerId Parse(string qualified)
    {
        if (!TryParse(qualified, out var id))
        {
            throw new FormatException($"'{qualified}' is not a qualified container id.");
        }

        return id;
    }

    public static bool TryParse(string? qualified, out ContainerId id)
    {
        id = null!;
        if (string.IsNullOrWhiteSpace(qualified)) return false;

        var parts = qualified.Split('/');
        if (parts.Length != 2 || !IsLegalName(parts[0]) || !IsLegalName(parts[1])) return false;

        id = new ContainerId(parts[0], parts[1]);
        return true;
    }

    /// <summary>The longest a team or container name may be.</summary>
    /// <remarks>
    /// A bound rather than a limit anyone will meet: two of these stack inside one path
    /// (<c>&lt;dataRoot&gt;/teams/&lt;team&gt;/&lt;name&gt;/…</c>, and transcripts nest a third
    /// level under that), and git and node still fail on paths that run too long. Affordable
    /// because the id is not what anyone reads - <c>TeamSummary.Name</c> is - so this bounds
    /// an identifier, not a description.
    /// </remarks>
    public const int MaxNameLength = 32;

    // NO SENTENCE FORM OF THE RULE. Nobody supplies an identifier: a person names a team whatever
    // they like and DeriveName below turns it into one of these, so there is no call site at which
    // a human meets this rule and has to be told it. Adding a sentence form means adding that call
    // site, which is the thing worth noticing.
    //
    // NO WINDOWS DEVICE NAMES (`CON`, `NUL`, `COM1`…) ARE REFUSED. The host runs only in a Linux
    // container, where every one of them is an ordinary directory name.

    /// <summary>
    /// A team or container name must survive being a directory name, a message-type suffix, a URL
    /// route value and a SignalR group name - so it is an ALLOWLIST, and deliberately narrower than
    /// any one of those sinks requires.
    ///
    /// It was a denylist (invalid filename chars, `..`, `/`, `\`) and that is the wrong shape here:
    /// a denylist is only ever as current as the last sink someone thought of, and it silently
    /// admitted every character that breaks the others. `.` produced a message type that parses
    /// wrong under `container.instruction.&lt;team&gt;/&lt;name&gt;` and, because types are matched
    /// under SQLite's BINARY collation, returned 200 while waking nothing. A space or `%` made the
    /// name encoding-sensitive, so the string stored in the database and the string arriving on the wire
    /// stopped being the same string.
    ///
    /// ASCII only, and that is not an oversight: these strings are compared ordinally and
    /// case-insensitively across four stores, and Unicode gives two visually identical names
    /// (NFC vs NFD) that are different strings everywhere - the same class of defect the
    /// case-insensitivity exists to prevent, one layer down. The human-readable form belongs in a
    /// team's LABEL, which is free-text and carries any script.
    /// </summary>
    public static bool IsLegalName(string candidate)
    {
        if (string.IsNullOrEmpty(candidate) || candidate.Length > MaxNameLength) return false;

        // Leading character checked separately: a name opening with `-` reads as a flag to the CLI,
        // and one opening with `_` is conventionally private. Both are legal further in.
        if (!char.IsAsciiLetterOrDigit(candidate[0])) return false;

        foreach (var character in candidate)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')) return false;
        }

        return true;
    }

    // ONE RULE FOR BOTH HALVES. There is no reserved team id and no separate team-name check: both
    // halves of a container id are under `IsLegalName`, so "is this a legal team name" has one
    // answer and no caller has to remember which one to ask.

    /// <summary>
    /// An identifier derived from what a person typed, or null when nothing usable can be got out
    /// of it.
    ///
    /// Lives HERE, beside the rule it has to satisfy, because a derivation that can produce an
    /// illegal name is worse than no derivation at all - it would push the refusal down to the
    /// directory create, where the name has already reached three other places. The last line
    /// asserts the result against <see cref="IsLegalName"/> rather than trusting the walk above it.
    ///
    /// Deliberately LOSSY and deliberately not reversible. It keeps ASCII letters, digits, `-` and
    /// `_` and drops everything else, so "Platform Engineering" becomes "PlatformEngineering" and
    /// "R&amp;D / Tooling" becomes "RDTooling". Case is preserved rather than title-cased: names
    /// compare case-insensitively everywhere, so changing it would be a cosmetic edit to a string
    /// the person never sees.
    ///
    /// Null is an ORDINARY answer, not a failure. A label in a non-Latin script ("チーム") has no
    /// ASCII to keep. The caller's job in that case is to invent an identifier of its own - which is safe precisely
    /// because nobody reads it. Returning null rather than inventing one here keeps this function
    /// free of any notion of what other names are already taken.
    /// </summary>
    public static string? DeriveName(string display)
    {
        var derived = new string([.. display
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')]);

        // Leading `-`/`_` are legal further in but not first, so they are trimmed rather than
        // making the whole label underivable: "- Platform" is a person's punctuation, not a name.
        derived = derived.TrimStart('-', '_');

        if (derived.Length > MaxNameLength) derived = derived[..MaxNameLength];

        return IsLegalName(derived) ? derived : null;
    }
}
