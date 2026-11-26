using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;
using Yawble.Core.Tests.Gateway;

namespace Yawble.Core.Tests;

/// <summary>
/// Every file in the repository (tracked, or new and not ignored) is free of names and text that do not belong in it:
/// the sandbox vendor's name outside its own package, library, function and type names, working names, references to
/// design documents, and project-history markers. The check covers file paths as well as contents.
/// </summary>
public sealed partial class NameLeakTests
{
    private const string Self = "core/tests/Yawble.Core.Tests/NameLeakTests.cs";

    /// <summary>The sandbox vendor's own names, which the code must use as they are.</summary>
    [GeneratedRegex(
        @"Microsoft\.Mxc\.Sdk(?:\.V1)?|@microsoft/mxc-sdk|microsoft/mxc\b|libmxc_ffi|mxc_ffi|mxc-release-binaries|mxc_engine|" +
        @"MXC_STATUS_\w*|\bmxc_[a-z_]+|\bMxc(?:Container|Exception|Platform|ReadStream|WriteStream)\b|Microsoft(?:'s)? MXC\b")]
    private static partial Regex VendorNames();

    [GeneratedRegex(@"mxc", RegexOptions.IgnoreCase)]
    private static partial Regex Vendor();

    /// <summary>
    /// Project-history markers: working names, design-document references, phases and slices, and backlog or card numbers
    /// ("B118", "card 586", "Card478", "card-445"). A B-number right after a letter, a digit or '#' is not one (a hex
    /// colour such as #B100FF, a commit hash). B000 to B009 are not either: the app's own backlog labels use them as
    /// examples and test data ("the item, e.g. B006").
    /// </summary>
    [GeneratedRegex(
        @"openshrimp|(?:design|spec) §|\bphase ?\d|\bslice \d|\bspikes?\b|-design\.md|\bw29833\b|" +
        @"(?<![0-9A-Za-z#])B(?!00)\d{3}(?!\d)|(?<![0-9A-Za-z])card[ _-]?#?\d{3}(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex History();

    /// <summary>
    /// Backlog labels as the product itself writes them, which are product data and not history: a label that is a whole
    /// quoted string ("B001" or 'B001', the quote right next to it on both sides, as in "label": "B001"), and the
    /// product's phrase "Backlog item B001". They are taken out of a line before <see cref="History"/> looks at it; any
    /// other B-number in the same line is still flagged. Paths are not exempted.
    /// </summary>
    [GeneratedRegex(@"([""'])B\d{3}\1|\bBacklog item B\d{3}(?!\d)")]
    private static partial Regex ProductLabels();

    /// <summary>Whether a line carries project history, once the product's own backlog labels are taken out.</summary>
    private static bool HasHistory(string line) => History().IsMatch(ProductLabels().Replace(line, " "));

    /// <summary>
    /// Files from elsewhere, kept as they come, whose lines are not checked for project history (the vendor's name still
    /// is): the public suffix list, whose GUIDs ("…-a08b-4241-b536-…") read as B-numbers.
    /// </summary>
    private static readonly HashSet<string> ThirdParty = ["core/src/Yawble.Core/Policy/public_suffix_list.dat"];

    /// <summary>
    /// Test-output files a run leaves in the repository root (core-full.xml, results.xml, full-skips.txt, a .trx): their
    /// test names and messages quote what this test looks for. Only an untracked file directly in the root is passed
    /// over; a tracked one, or one in a subfolder, is checked like any other file.
    /// </summary>
    [GeneratedRegex(@"^(?:[^/]+\.trx|[^/]*(?:full|results?|skips?)[^/]*\.(?:xml|txt))$", RegexOptions.IgnoreCase)]
    private static partial Regex StrayTestOutput();

    /// <summary>
    /// Wording in a comment that tells how the code came to be instead of what it does: a numbered round of checking,
    /// "fixed in" a commit or a card, a re-verification, and a tester's or developer's own checks or findings. The
    /// product's Tester role ("the tester hands the card back") is not that.
    /// </summary>
    [GeneratedRegex(
        @"\bround [1-9]\b|\b(?:first|second|third) round\b|\bfixed in (?!the core\b|place\b)|\(fixed[,):]|\bre-?verif|" +
        @"\bthe (?:tester|developers?|reviewer)(?:'s|') (?:own )?(?:gap |break )?(?:checks?|tests?|attempts?|re-?checks?|re-?tests?|rounds?|findings?)\b|" +
        @"\b(?:tester|developer|reviewer) (?:found|reported|noticed)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReviewWording();

    /// <summary>A commit id in a comment: 7 to 40 hex digits, with a letter and a digit among them, after a word such as "main", "commit" or "at".</summary>
    [GeneratedRegex(@"\b(?:main|commit|at|against|since|merged|fixed in|from)[ (]+(?=[0-9a-f]*[a-f])(?=[0-9a-f]*\d)[0-9a-f]{7,40}\b(?![./-])")]
    private static partial Regex CommitId();

    /// <summary>The given names of the agents that have worked on the code.</summary>
    private const string Authors = "Kofi|Wren|Gary|Marlo|Ines|Tomas|Noah";

    /// <summary>
    /// The name of an agent that worked on the code, given as the author of something in a comment: with its role in
    /// front ("TesterKofi"), or as an owner or a subject ("Ines's", "Marlo found").
    /// </summary>
    [GeneratedRegex(
        @"\b(?:Tester|Developer|Reviewer|Manager)(?:" + Authors + @")\b|\b(?:" + Authors + @")(?:'s\b| (?:found|fixed|[0-9a-f]{7}\b))")]
    private static partial Regex AuthorName();

    /// <summary>
    /// A C# test whose method name or display name starts with the number of a finding or a plan item ("P1_", "Item4_",
    /// "F2_") instead of stating the behaviour.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:void|Task|ValueTask)(?:<[^>]+>)?\s+(?:P\d|Item\d+|F\d+[a-z]?)_\w*\s*\(|DisplayName\s*=\s*""(?:P\d|Item ?\d+|F\d+[a-z]?)[_: ]")]
    private static partial Regex NumberedTestName();

    /// <summary>A comment that starts after code on the same line ("x = 1; // why"); the "//" of an address is not one.</summary>
    [GeneratedRegex(@"(?<![:'""`/])//\s(.*)$")]
    private static partial Regex TrailingComment();

    /// <summary>
    /// Files whose comments and test names are not checked for review traces: files that state the product's own rule
    /// on member names such as "TesterWren", and files that still carry such wording. A file of the second group
    /// leaves the list when its comments are rewritten.
    /// </summary>
    private static readonly HashSet<string> ReviewTracesAllowed =
    [
        "core/src/Yawble.Core/Board/TesterRule.cs",
        "shell/src/renderer/src/commitView.ts",
        "shell/src/renderer/src/workflows/types.ts",
        "shell/tests/e2e/git-tidy-core-plan.spec.ts",
        "shell/tests/e2e/overall-time-timeline-verify.spec.ts",
        "shell/tests/e2e/phone-remote.spec.ts",
        "shell/tests/e2e/team-spacing.spec.ts",
        "shell/tests/unit/board.test.ts",
        "shell/tests/unit/commit-view-verify.test.ts",
        "shell/tests/unit/concierge-default-agent.test.ts",
        "shell/tests/unit/fake-core-expiry.test.ts",
        "shell/tests/unit/phone-member-pane.test.ts",
        "shell/tests/unit/phone-settings.test.ts",
        "shell/tests/unit/remote-access-break.test.ts",
        "shell/tests/unit/stopped-run-verify.test.ts",
        "shell/tests/unit/sync-banner-verify.test.ts",
        "shell/tests/unit/toolAsks.test.ts",
        "shell/tests/unit/workflows-place.test.ts",
    ];

    /// <summary>Shipped migrations are fixed text; their comments are not checked for review traces.</summary>
    private const string Migrations = "core/src/Yawble.Core/Storage/Migrations/";

    /// <summary>The comment on a line of a source file, or null when the line has none or the file is not source code.</summary>
    private static string? CommentOf(string file, string line)
    {
        var text = line.Trim();
        switch (Path.GetExtension(file).ToLowerInvariant())
        {
            case ".cs" or ".ts" or ".mts" or ".vue" or ".js" or ".mjs" or ".cjs" or ".css":
                if (text.StartsWith("//") || text.StartsWith("/*") || text.StartsWith('*') || text.StartsWith("<!--")) return text;
                return TrailingComment().Match(line) is { Success: true } trailing ? trailing.Groups[1].Value : null;
            case ".ps1" or ".sh" or ".yml" or ".yaml" or ".py" or ".cmd" or ".nsh" or ".bicep":
                return text.StartsWith('#') || text.StartsWith("::") || text.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) ? text : null;
            case ".sql":
                return text.StartsWith("--") ? text : null;
            default:
                return null;
        }
    }

    /// <summary>The kind of review trace a line of a file carries, or null: a numbered test name, or one of the comment patterns.</summary>
    private static string? ReviewTrace(string file, string line)
    {
        if (file.EndsWith(".cs", StringComparison.Ordinal) && NumberedTestName().IsMatch(line)) return "a numbered test name";
        if (CommentOf(file, line) is not { } comment) return null;
        if (ReviewWording().IsMatch(comment)) return "review wording in a comment";
        if (CommitId().IsMatch(comment)) return "a commit id in a comment";
        if (AuthorName().IsMatch(comment)) return "an author's name in a comment";
        return null;
    }

    [Fact]
    public void NoFileCarriesTheVendorsNameOutsideItsOwnNamesOrAnyProjectHistory()
    {
        var problems = Scan(Binaries.RepoRoot);
        Assert.True(problems.Count == 0, $"{problems.Count} lines:\n" + string.Join("\n", problems.Take(200)));
    }

    /// <summary>A stray test-output file left untracked in the root does not fail the check; the same name
    /// tracked, the same name in a subfolder, and any other untracked file still do.</summary>
    [Fact]
    public void AStrayTestOutputFileInTheRepoRoot_IsPassedOver_ButNotWhenTrackedOrInASubfolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "yawble-nameleak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        try
        {
            Git(root, "init", "-q");
            const string leak = "<test name=\"phase 2 of the mxc spike\" />\n";
            File.WriteAllText(Path.Combine(root, "clean.txt"), "nothing here\n");
            File.WriteAllText(Path.Combine(root, "tracked-results.xml"), leak);
            Git(root, "add", "clean.txt", "tracked-results.xml");
            foreach (var stray in new[] { "core-full.xml", "results.xml", "full-skips.txt", "run.trx" })
                File.WriteAllText(Path.Combine(root, stray), leak);
            File.WriteAllText(Path.Combine(root, "sub", "results.xml"), leak);
            File.WriteAllText(Path.Combine(root, "notes.md"), leak);

            var flagged = Scan(root).Select(p => p[..p.IndexOf(':')]).Distinct().Order().ToList();

            Assert.Equal(["notes.md", "sub/results.xml", "tracked-results.xml"], flagged);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>
    /// Backlog and card numbers are history in a line and in a path; a hex colour, a backlog label the app gives as an
    /// example (B001 to B009) and a four-digit card number are not.
    /// </summary>
    [Fact]
    public void BacklogAndCardNumbers_AreFlaggedInLinesAndPaths_ButNotAHexColour()
    {
        var root = Path.Combine(Path.GetTempPath(), "yawble-nameleak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        try
        {
            File.WriteAllText(Path.Combine(root, "notes.md"), "intro\nfixed in B118 (card 586)\n");
            File.WriteAllText(Path.Combine(root, "tests", "B135Tests.cs"), "class WorkflowToolsTests { }\n");
            File.WriteAllText(Path.Combine(root, "colours.css"), "a { color: #B100FF; }\n/* the item, e.g. B006; no card 9999 */\n");

            var flagged = Scan(root, ["notes.md", "tests/B135Tests.cs", "colours.css"]);

            Assert.Equal(["notes.md:2: fixed in B118 (card 586)", "tests/B135Tests.cs: the path"], flagged.Order());
            Assert.Matches(History(), "fixed in B118");
            Assert.Matches(History(), "see card 586");
            Assert.DoesNotMatch(History(), "#B100FF");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>
    /// A backlog label as the product writes it (a whole quoted string, or "Backlog item B001") is product data and is not
    /// flagged; a B-number anywhere else in the line, or not quoted on both sides, still is.
    /// </summary>
    [Theory]
    [InlineData("\"label\": \"B001\",", false)]
    [InlineData("{ label: 'B118', title: 'x' }", false)]
    [InlineData("\"id\": \"B048\"", false)]
    [InlineData("Backlog item B001 is dispatched", false)]
    [InlineData("\"Backlog item B118 is dispatched\"", false)]
    [InlineData("fixed in B118", true)]
    [InlineData("\"label\": \"B001\", // fixed in B118", true)]
    [InlineData("\"B118 fixed\"", true)]
    [InlineData("\"B1180\" and B118", true)]
    [InlineData("backlog B118", true)]
    public void AProductBacklogLabel_IsNotHistory_ButOtherBNumbersStillAre(string line, bool flagged) =>
        Assert.Equal(flagged, HasHistory(line));

    /// <summary>
    /// Review traces are flagged in comments and in C# test names: a numbered round, "fixed in", a re-verification, a
    /// tester's or developer's own checks, a commit id, an author's name, a finding number in front of a test name.
    /// The same words in code or in a Markdown page, and plain facts that share a word, are not.
    /// </summary>
    [Theory]
    [InlineData("a.cs", "// Round 2 check of stopped runs.", true)]
    [InlineData("a.cs", "/// A branch named like a commit id (fixed in f669165).", true)]
    [InlineData("a.ts", "// F4b (fixed, 82de5bb): the banner asks git.status.", true)]
    [InlineData("a.cs", "// Re-verified against the real engine.", true)]
    [InlineData("a.cs", "// The tester's own checks of the refusal count.", true)]
    [InlineData("a.cs", "// The gaps the developers' tests leave.", true)]
    [InlineData("a.cs", "var x = 1; // the reviewer found this", true)]
    [InlineData("a.cs", "// Against main 52485c5 the layout is the same.", true)]
    [InlineData("a.ps1", "# built from commit 0123abc4", true)]
    [InlineData("a.cs", "// TesterKofi's check of the lease wait.", true)]
    [InlineData("a.vue", "<!-- Ines's banner -->", true)]
    [InlineData("a.cs", "    public void F1_AMergeOnMainIsShown()", true)]
    [InlineData("a.cs", "    public async Task Item4_TheCardMoves()", true)]
    [InlineData("a.cs", "    [Fact(DisplayName = \"P1: fifty requests are one row\")]", true)]
    [InlineData("a.cs", "// The tester hands the card back; the developer's run starts.", false)]
    [InlineData("a.cs", "// The actions are fixed in the core.", false)]
    [InlineData("a.cs", "// The canonical definition (fixed key order).", false)]
    [InlineData("a.cs", "// Rounds the size up to 1024.", false)]
    [InlineData("a.cs", "var note = \"fixed in round 2 by TesterKofi\";", false)]
    [InlineData("a.cs", "// The run starts at 1234567 ticks; see https://example.com/at/abc1234", false)]
    [InlineData("a.cs", "    public void F1Key_OpensHelp()", false)]
    [InlineData("a.cs", "    public void P1Plan_IsKept()", false)]
    [InlineData("notes.md", "The tester's own checks, round 2, fixed in f669165.", false)]
    public void ReviewTraces_AreFlaggedInCommentsAndTestNames_ButNotInCodeOrPlainFacts(string file, string line, bool flagged) =>
        Assert.Equal(flagged, ReviewTrace(file, line) is not null);

    /// <summary>Review traces are reported by file and line; a shipped migration is not checked for them.</summary>
    [Fact]
    public void ReviewTraces_AreReportedByFileAndLine_ExceptInShippedMigrations()
    {
        var root = Path.Combine(Path.GetTempPath(), "yawble-nameleak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, Migrations));
        try
        {
            File.WriteAllText(Path.Combine(root, "a.cs"), "// A fact.\n// The tester's own checks.\nclass A { public void F2_Works() { } }\n");
            File.WriteAllText(Path.Combine(root, Migrations, "0001_x.sql"), "-- fixed in round 2 (\"DeveloperTomas\" -> 'Tomas')\n");

            var flagged = Scan(root, ["a.cs", Migrations + "0001_x.sql"]);

            Assert.Equal(
                ["a.cs:2: review wording in a comment: // The tester's own checks.",
                 "a.cs:3: a numbered test name: class A { public void F2_Works() { } }"], flagged);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static List<string> Scan(string root) => Scan(root, RepositoryFiles(root));

    private static List<string> Scan(string root, IEnumerable<string> files)
    {
        var problems = new List<string>();
        foreach (var file in files)
        {
            if (Vendor().IsMatch(VendorNames().Replace(file, "")) || History().IsMatch(file)) problems.Add($"{file}: the path");
            if (file == Self || file == "LICENSE") continue;
            var bytes = File.ReadAllBytes(Path.Combine(root, file));
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0) continue; // binary
            var lines = System.Text.Encoding.UTF8.GetString(bytes).Split('\n');
            var traces = !ReviewTracesAllowed.Contains(file) && !file.StartsWith(Migrations, StringComparison.Ordinal);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (Vendor().IsMatch(VendorNames().Replace(line, ""))) problems.Add($"{file}:{i + 1}: {Trim(line)}");
                else if (!ThirdParty.Contains(file) && HasHistory(line)) problems.Add($"{file}:{i + 1}: {Trim(line)}");
                else if (traces && ReviewTrace(file, line) is { } trace) problems.Add($"{file}:{i + 1}: {trace}: {Trim(line)}");
            }
        }
        return problems;
    }

    private static string Git(string root, params string[] args)
    {
        var psi = new ProcessStartInfo("git", ["-C", root, .. args])
        {
            RedirectStandardOutput = true, UseShellExecute = false,
        };
        using var git = Process.Start(psi)!;
        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, $"git {string.Join(' ', args)} exited {git.ExitCode}");
        return output;
    }

    private static string Trim(string line) => line.Trim() is { Length: > 160 } t ? t[..160] + "…" : line.Trim();

    private static IEnumerable<string> RepositoryFiles(string root)
    {
        string[] List(params string[] which) =>
            Git(root, ["ls-files", "-z", .. which]).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var untracked = List("--others", "--exclude-standard").Where(f => !StrayTestOutput().IsMatch(f));
        return List("--cached").Concat(untracked).Distinct()
            .Where(f => File.Exists(Path.Combine(root, f)));
    }
}
