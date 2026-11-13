namespace Yawble.Core.Teams;

/// <summary>
/// Members are named for their job, a space, then a first name: "Developer Priya", "Tester Wren" (the Manager's
/// hiring text). The member's id - what tell, its folder and its branch go by - is that name with everything but
/// ASCII letters, digits, '-' and '_' removed, at most 32 characters: "DeveloperPriya". The app shows it as words again.
/// </summary>
public static class MemberNames
{
    public const int MaxLabel = 60;
    public const int MaxId = 32;

    /// <summary>The id for a name as typed or chosen, or an <see cref="ArgumentException"/> saying why there is none.</summary>
    public static string IdFor(string? name)
    {
        var label = (name ?? "").Trim();
        if (label.Length == 0) throw new ArgumentException("a member needs a name");
        if (label.Length > MaxLabel) throw new ArgumentException($"a member's name is at most {MaxLabel} characters");
        var id = new string(label.Where(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_').ToArray())
            .TrimStart('-', '_');
        if (id.Length == 0) throw new ArgumentException($"'{label}' has no letters or digits to make an id from");
        if (id.Length > MaxId) id = id[..MaxId];
        return TeamRepos.Segment(id);
    }
}
