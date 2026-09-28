using System.Text.RegularExpressions;

namespace Harness.Tests;

/// <summary>
/// The code does not cite the work that produced it. A backlog item or a card number in a comment
/// is true for the week it was written and a riddle after: the item is archived, the card long
/// settled, and the next reader has nothing to look up. Say what the code does and why instead.
/// </summary>
public sealed class CodeCitationTests
{
    private static readonly string[] Scanned = ["src", "tests", "web/src", "cli"];

    // A real item's citation: the prefix, two zeros, two more characters. Split so the pattern's
    // own text is not an example of it.
    private static readonly Regex Item = new(@"\bB0" + @"0[0-9A-Za-z]{2}\b");

    private static readonly Regex Card = new(@"\bcard\s+#?\d+\b", RegexOptions.IgnoreCase);

    /// <summary>The format examples the documentation uses; none of them names a real item.</summary>
    private static readonly string[] DocumentedExamples = ["B000H", "B001F", "B0O1H"];

    /// <summary>Fixtures that quote a member's transcript word for word, citations included.</summary>
    private static readonly string[] TranscriptFixtures = ["tests/Harness.Tests/LiveViewTests.cs"];

    [Fact]
    public void Backlog_codes_and_card_numbers_stay_out_of_the_code()
    {
        var root = FindRepoRoot();
        var hits = new List<string>();

        foreach (var scanned in Scanned)
        {
            var directory = Path.Combine(root, scanned);
            if (!Directory.Exists(directory)) continue;

            foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
            {
                if (Skip(file)) continue;

                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                hits.AddRange(Citations(relative, File.ReadAllLines(file)));
            }
        }

        Assert.True(hits.Count == 0, string.Join(Environment.NewLine, hits));
    }

    [Fact]
    public void A_citation_is_named_by_file_and_line_and_the_documented_examples_pass()
    {
        var planted = new[]
        {
            "// Fixed for B00" + "17.",
            "var x = 1;",
            "// See card " + "847 for the reason.",
        };

        Assert.Equal(
            ["src/Harness.Host/Planted.cs:1: cites B00" + "17", "src/Harness.Host/Planted.cs:3: cites card " + "847"],
            Citations("src/Harness.Host/Planted.cs", planted));

        var examples = new[]
        {
            "/// Rendered as <c>B000H</c>; stored as <c>B001F</c>; typed as <c>B0O1H</c> off a screenshot.",
            "// `card 2310_Manager` is an instruction card's id, not a number.",
        };

        Assert.Empty(Citations("src/Harness.Host/Examples.cs", examples));
    }

    private static IEnumerable<string> Citations(string relative, IEnumerable<string> lines)
    {
        // The backlog's own code renders and parses citations, and the board's card labels itself
        // `card <id>`: in those files that is the feature, not a citation.
        var backlogOwn = relative.Contains("backlog", StringComparison.OrdinalIgnoreCase);
        var kanbanOwn = relative.Contains("kanban", StringComparison.OrdinalIgnoreCase);
        if (TranscriptFixtures.Contains(relative, StringComparer.Ordinal)) yield break;

        var number = 0;
        foreach (var line in lines)
        {
            number++;

            if (!backlogOwn)
            {
                foreach (Match match in Item.Matches(line))
                {
                    if (!DocumentedExamples.Contains(match.Value, StringComparer.Ordinal))
                        yield return $"{relative}:{number}: cites {match.Value}";
                }
            }

            if (!kanbanOwn)
            {
                foreach (Match match in Card.Matches(line)) yield return $"{relative}:{number}: cites {match.Value}";
            }
        }
    }

    private static bool Skip(string file)
    {
        var path = file.Replace('\\', '/');
        if (path.Contains("/bin/") || path.Contains("/obj/") || path.Contains("/node_modules/")) return true;

        var extension = Path.GetExtension(file);
        return extension is not (".cs" or ".ts" or ".vue" or ".js" or ".json" or ".scss" or ".html" or ".csproj"
            or ".props" or ".go" or ".sh" or ".ps1" or ".md");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Harness.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the repository root.");
    }
}
