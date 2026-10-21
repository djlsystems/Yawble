using System.Reflection;
using System.Text.RegularExpressions;

namespace Harness.Tests;

/// <summary>
/// AGENTS.md pins each rule to a test by name. A citation that names a test, a class, a spec file or a
/// Go test that no longer exists pins nothing, so every one it makes is checked here: a C# test by
/// reflection over this assembly, a spec file on disk under web/src, a Go test by its declaration
/// under cli/.
/// </summary>
public sealed class AgentsMdCitationsTests
{
    [Fact]
    public void Every_citation_in_AGENTS_md_names_a_test_a_class_a_spec_file_or_a_go_test_that_exists()
    {
        var root = SolutionSamples.RepoRoot();
        var text = File.ReadAllText(Path.Combine(root, "AGENTS.md"));
        var checker = new Checker(root);

        var missing = checker.Missing(text);

        Assert.True(missing.Count == 0, "AGENTS.md cites what does not exist:\n" + string.Join("\n", missing));
        Assert.True(checker.Checked > 500, $"only {checker.Checked} citations were recognised; the parser lost the file");
    }

    [Fact]
    public void The_check_catches_a_planted_citation_of_each_kind()
    {
        var checker = new Checker(SolutionSamples.RepoRoot());
        const string planted = "Pinned by `NoSuchTests.Nothing_is_here`, `AgentsMdCitationsTests.Nothing_is_here`, `NoSuchTests` " +
                               "(`Nothing_is_here_either`), `no-such-thing.mount.spec.ts`, `web/src/lib/__tests__/no-such.spec.ts`, " +
                               "`TestNoSuchThing` and `cli/internal/cli/no_such_test.go`.";

        var missing = checker.Missing(planted);

        // Two class-and-test, one class, one bare test name, two spec files, one Go test and one Go test file.
        Assert.Equal(8, missing.Count);
        Assert.Empty(checker.Missing("Pinned by `AgentsMdCitationsTests.The_check_catches_a_planted_citation_of_each_kind` " +
                                     "and `AgentsMdCitationsTests` (`Every_citation_in_AGENTS_md_names_a_test_a_class_a_spec_file_or_a_go_test_that_exists`)."));
    }

    /// <summary>What a backtick span in AGENTS.md may cite, and whether it exists.</summary>
    private sealed class Checker
    {
        private static readonly Regex Span = new("`([^`]+)`", RegexOptions.Compiled);
        private static readonly Regex ClassAndMethod = new(@"^(?:[A-Za-z0-9_.]*\.)?([A-Z][A-Za-z0-9]*)\.([A-Z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+)$", RegexOptions.Compiled);
        private static readonly Regex TestClass = new(@"^(?:[A-Za-z0-9_.]*\.)?([A-Z][A-Za-z0-9]*Tests)$", RegexOptions.Compiled);
        private static readonly Regex BareMethod = new(@"^[A-Z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+$", RegexOptions.Compiled);
        private static readonly Regex GoTest = new(@"^Test[A-Z][A-Za-z0-9]+$", RegexOptions.Compiled);

        private readonly string _root;
        private readonly ILookup<string, Type> _types;
        private readonly HashSet<string> _specFiles;
        private readonly HashSet<string> _goTests;

        public Checker(string root)
        {
            _root = root;
            _types = typeof(AgentsMdCitationsTests).Assembly.GetTypes().ToLookup(t => t.Name, StringComparer.Ordinal);
            _specFiles = Directory.EnumerateFiles(Path.Combine(root, "web", "src"), "*.spec.ts", SearchOption.AllDirectories)
                .Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal)!;
            var declaration = new Regex(@"^func (Test[A-Z][A-Za-z0-9]+)\(", RegexOptions.Multiline);
            _goTests = Directory.EnumerateFiles(Path.Combine(root, "cli"), "*_test.go", SearchOption.AllDirectories)
                .SelectMany(f => declaration.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
                .ToHashSet(StringComparer.Ordinal);
        }

        public int Checked { get; private set; }

        public List<string> Missing(string text)
        {
            var missing = new List<string>();
            string? lastClass = null;
            foreach (Match span in Span.Matches(text))
            {
                var token = span.Groups[1].Value;
                if (ClassAndMethod.Match(token) is { Success: true } cm)
                {
                    Checked++;
                    var cls = cm.Groups[1].Value;
                    lastClass = cls;
                    if (!_types.Contains(cls)) missing.Add($"{token}: no class {cls} in the test assembly");
                    else if (!HasMethod(cls, cm.Groups[2].Value)) missing.Add($"{token}: {cls} has no test {cm.Groups[2].Value}");
                }
                else if (TestClass.Match(token) is { Success: true } tc)
                {
                    Checked++;
                    lastClass = tc.Groups[1].Value;
                    if (!_types.Contains(lastClass)) missing.Add($"{token}: no class in the test assembly");
                }
                else if (BareMethod.IsMatch(token) && token.Any(char.IsLower) && !token.Contains("__"))
                {
                    Checked++;
                    if (lastClass is null) missing.Add($"{token}: a test name with no class cited before it");
                    else if (!HasMethod(lastClass, token)) missing.Add($"{token}: {lastClass} (the class cited before it) has no such test");
                }
                else if (token.EndsWith(".spec.ts", StringComparison.Ordinal) && !token.Contains('*'))
                {
                    // A path from the repository root, a path from web/src, or a bare file name.
                    Checked++;
                    var exists = token.Contains('/')
                        ? File.Exists(Path.Combine(_root, token)) || File.Exists(Path.Combine(_root, "web", "src", token))
                        : _specFiles.Contains(token);
                    if (!exists) missing.Add($"{token}: no such spec file under web/src");
                }
                else if (token.EndsWith("_test.go", StringComparison.Ordinal) && token.Contains('/'))
                {
                    Checked++;
                    if (!File.Exists(Path.Combine(_root, token))) missing.Add($"{token}: no such Go test file");
                }
                else if (GoTest.IsMatch(token))
                {
                    Checked++;
                    if (!_goTests.Contains(token)) missing.Add($"{token}: no such Go test under cli/");
                }
            }
            return missing;
        }

        private bool HasMethod(string cls, string method) =>
            _types[cls].Any(t => t.GetMethod(method, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly) is not null);
    }
}
