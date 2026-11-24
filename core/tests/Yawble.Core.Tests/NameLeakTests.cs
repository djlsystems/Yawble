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
            File.WriteAllText(Path.Combine(root, "tests", "B135Tests.cs"), "class ScriptToolsTests { }\n");
            File.WriteAllText(Path.Combine(root, "colours.css"), "a { color: #B100FF; }\n/* the item, e.g. B006; no card 9999 */\n");

            var flagged = Scan(root, ["notes.md", "tests/B135Tests.cs", "colours.css"]);

            Assert.Equal(["notes.md:2: fixed in B118 (card 586)", "tests/B135Tests.cs: the path"], flagged.Order());
            Assert.Matches(History(), "fixed in B118");
            Assert.Matches(History(), "see card 586");
            Assert.DoesNotMatch(History(), "#B100FF");
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
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (Vendor().IsMatch(VendorNames().Replace(line, ""))) problems.Add($"{file}:{i + 1}: {Trim(line)}");
                else if (!ThirdParty.Contains(file) && History().IsMatch(line)) problems.Add($"{file}:{i + 1}: {Trim(line)}");
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
