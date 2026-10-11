namespace Harness.Skills;

/// <summary>
/// A `SKILL.md` file on the volume: YAML-ish frontmatter (`name`, `description`, an optional
/// `roles`, and a `metadata:` block) then the body. Read by the first-start import and by a team
/// skill's registration from a file (<c>TeamSkills.RegisterFileAsync</c>); nothing writes this format.
/// </summary>
public static class SkillFile
{
    public sealed record Parsed(string Name, string Description, string Body, IReadOnlyList<string>? Roles);

    public static Parsed Parse(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != "---")
        {
            throw new ArgumentException("SKILL.md must start with frontmatter delimited by ---.");
        }

        var closing = Array.FindIndex(lines, 1, line => line.Trim() == "---");
        if (closing <= 0) throw new ArgumentException("SKILL.md frontmatter is missing its closing --- line.");

        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 1; index < closing; index++)
        {
            var line = lines[index];

            // Nested blocks (`metadata:` and its indented pairs) carry version and gating, and
            // nothing reads either.
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(' ') || line.StartsWith('\t')) continue;

            var separator = line.IndexOf(':');
            if (separator <= 0) continue;

            map[line[..separator].Trim()] = Unquote(line[(separator + 1)..].Trim());
        }

        if (!map.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("SKILL.md frontmatter must include a non-empty name.");
        }

        map.TryGetValue("description", out var description);

        IReadOnlyList<string>? roles = null;
        if (map.TryGetValue("roles", out var rawRoles) && rawRoles.Trim('[', ']', ' ').Length > 0)
        {
            roles = rawRoles.Trim('[', ']').Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Select(Unquote)
                .ToList();
        }

        var body = string.Join('\n', lines[(closing + 1)..]).TrimStart('\n');
        return new Parsed(name.Trim(), (description ?? "").Trim(), body, roles);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }
}
