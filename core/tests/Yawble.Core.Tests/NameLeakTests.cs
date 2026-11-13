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

    [GeneratedRegex(@"openshrimp|(?:design|spec) §|\bphase ?\d|\bslice \d|\bspikes?\b|-design\.md|\bw29833\b", RegexOptions.IgnoreCase)]
    private static partial Regex History();

    [Fact]
    public void NoFileCarriesTheVendorsNameOutsideItsOwnNamesOrAnyProjectHistory()
    {
        var root = Binaries.RepoRoot;
        var problems = new List<string>();
        foreach (var file in RepositoryFiles(root))
        {
            if (Vendor().IsMatch(VendorNames().Replace(file, ""))) problems.Add($"{file}: the path");
            if (file == Self || file == "LICENSE") continue;
            var bytes = File.ReadAllBytes(Path.Combine(root, file));
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0) continue; // binary
            var lines = System.Text.Encoding.UTF8.GetString(bytes).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (Vendor().IsMatch(VendorNames().Replace(line, ""))) problems.Add($"{file}:{i + 1}: {Trim(line)}");
                else if (History().IsMatch(line)) problems.Add($"{file}:{i + 1}: {Trim(line)}");
            }
        }
        Assert.True(problems.Count == 0, $"{problems.Count} lines:\n" + string.Join("\n", problems.Take(200)));
    }

    private static string Trim(string line) => line.Trim() is { Length: > 160 } t ? t[..160] + "…" : line.Trim();

    private static IEnumerable<string> RepositoryFiles(string root)
    {
        var psi = new ProcessStartInfo("git", ["-C", root, "ls-files", "-z", "--cached", "--others", "--exclude-standard"])
        {
            RedirectStandardOutput = true, UseShellExecute = false,
        };
        using var git = Process.Start(psi)!;
        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        Assert.Equal(0, git.ExitCode);
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(f => File.Exists(Path.Combine(root, f)));
    }
}
