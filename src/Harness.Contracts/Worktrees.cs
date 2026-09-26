namespace Harness.Contracts;

/// <summary>
/// How a member's worktree for one card is named: <c>wt_&lt;Member&gt;_&lt;key&gt;</c>, one tree per
/// card rather than one per member.
///
/// The key is the planned card's id when the waking instruction names one (<c>card</c> on
/// <c>tell</c>), and <c>w&lt;correlation&gt;</c> otherwise. A resumed card is told with the same
/// <c>card</c>, so it lands in the same tree with its uncommitted edits as they were left.
///
/// **A KEY NEVER CONTAINS <c>_</c>.** Member names may, so a key that could hold one would make
/// <c>wt_Dev_2_x</c> ambiguous between member <c>Dev</c> (key <c>2_x</c>) and member <c>Dev_2</c>
/// (key <c>x</c>). With <c>_</c> mapped to <c>-</c> in the key, the LAST <c>_</c> of a tree's name
/// is always the member/key boundary, which is what lets <see cref="MemberOf"/> list one member's
/// trees without catching another member whose name it is a prefix of.
/// </summary>
public static class Worktrees
{
    public const string Prefix = "wt_";

    /// <summary>Long enough for any card id or correlation; short enough to stay a sane path.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>The key for one invocation: the card when there is a usable one, else
    /// <c>w&lt;correlation&gt;</c>.</summary>
    public static string KeyFor(string? card, long correlation) =>
        Sanitise(card) ?? $"w{correlation.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The key as a single path segment of ASCII letters, digits and <c>-</c>, or null when nothing
    /// usable is left. Any other character becomes <c>-</c> (so <c>.</c>, <c>/</c> and <c>_</c>
    /// cannot survive); leading and trailing <c>-</c> are trimmed.
    /// </summary>
    public static string? Sanitise(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        var chars = key.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-').ToArray();
        var cleaned = new string(chars).Trim('-');

        if (cleaned.Length > MaxKeyLength) cleaned = cleaned[..MaxKeyLength].TrimEnd('-');

        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>The directory name for this member's tree for this key. Throws on a key that
    /// sanitises to nothing - the caller is expected to have derived it with <see cref="KeyFor"/>.</summary>
    public static string DirectoryName(string member, string key) =>
        Sanitise(key) is { } clean
            ? $"{Prefix}{member}_{clean}"
            : throw new ArgumentException($"'{key}' is not a usable worktree key.", nameof(key));

    /// <summary>The suggested branch for this member's work on this key, lower case.</summary>
    public static string BranchHint(string member, string key) =>
        $"{member}/{Sanitise(key) ?? key}".ToLowerInvariant();

    /// <summary>The member a tree's directory name belongs to, or null when it is not a per-card
    /// tree (the clone's <c>main</c>, a bare <c>wt_&lt;Member&gt;</c>, anything else).</summary>
    public static string? MemberOf(string directoryName)
    {
        if (!directoryName.StartsWith(Prefix, StringComparison.Ordinal)) return null;

        var cut = directoryName.LastIndexOf('_');
        if (cut < Prefix.Length + 1 || cut == directoryName.Length - 1) return null;

        var member = directoryName[Prefix.Length..cut];
        var key = directoryName[(cut + 1)..];

        return Sanitise(key) == key && ContainerId.IsLegalName(member) ? member : null;
    }

    /// <summary>The key a per-card tree's directory name carries, or null when it is not one - the
    /// same rule as <see cref="MemberOf"/>, so the two always agree.</summary>
    public static string? KeyOf(string directoryName) =>
        MemberOf(directoryName) is null ? null : directoryName[(directoryName.LastIndexOf('_') + 1)..];

    /// <summary>The correlation a <c>w&lt;correlation&gt;</c> key names, or null for a card key.</summary>
    public static long? CorrelationOf(string key) =>
        key.Length > 1 && key[0] == 'w'
            && long.TryParse(key.AsSpan(1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var correlation)
            ? correlation
            : null;
}

/// <summary>One repository's tree for the card an invocation is about: the repository name, its
/// main clone, and the absolute path of the tree (which may not exist yet - the member cuts it).</summary>
public sealed record RepoWorktree(string Repo, string ClonePath, string Path);
