namespace Harness.Tests;

/// <summary>
/// THE REPOSITORY HOLDS NO PACKAGE TO INSTALL. What a person installs is on the marketplace; this repository keeps only test data under <c>tests/</c>, never listed, built into
/// an image or shipped. So no <c>solution.json</c> and no plugin <c>plugin.json</c> lives outside
/// <c>tests/</c>, except the Go plugin template's own manifest, allowed by name: the template is how
/// a team builds a plugin, not a package to install.
/// </summary>
public sealed class RepositoryPackagesTests
{
    /// <summary>The one manifest allowed outside <c>tests/</c>, relative to the repository root.</summary>
    public static readonly IReadOnlyList<string> Allowed = ["templates/plugin-go/plugin.json"];

    private static readonly string[] Manifests = ["solution.json", "plugin.json"];

    /// <summary>Build output, dependencies and git, wherever they are: nothing the repository holds.</summary>
    private static readonly string[] SkippedAnywhere = [".git", "bin", "obj", "node_modules"];

    /// <summary>Top-level folders that are test data or ignored local state (<c>.gitignore</c>).</summary>
    private static readonly string[] SkippedAtTheRoot = ["tests", "data", "TestResults", ".test-history", ".superpowers"];

    [Fact]
    public void No_solution_or_plugin_manifest_is_outside_tests_but_the_Go_templates()
    {
        var found = PackagesOutsideTests(SolutionSamples.RepoRoot());

        Assert.True(found.Count == 0,
            "A package manifest is outside tests/: " + string.Join(", ", found) + ". Packages to install go to the "
            + "marketplace's packages repository; a test's package goes under tests/Fixtures/Packages.");
    }

    [Fact]
    public void The_Go_template_is_where_the_allowance_names_it()
    {
        foreach (var path in Allowed)
        {
            Assert.True(File.Exists(Path.Combine(SolutionSamples.RepoRoot(), path)), $"{path} is allowed but not there.");
        }
    }

    [Fact]
    public void The_scan_finds_a_manifest_planted_outside_tests_and_nothing_under_tests_or_build_output()
    {
        var root = Path.Combine(Path.GetTempPath(), $"repo-scan-{Guid.NewGuid():N}");
        try
        {
            foreach (var file in new[]
            {
                "docs/a/solution.json", "src/x/plugin.json", "templates/plugin-go/plugin.json",
                "tests/Fixtures/Packages/p/plugin.json", "tests/solution.json",
                "src/x/bin/Debug/plugin.json", "web/node_modules/m/plugin.json", "data/plugins/p/plugin.json",
            })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, file))!);
                File.WriteAllText(Path.Combine(root, file), "{}");
            }

            Assert.Equal(["docs/a/solution.json", "src/x/plugin.json"], PackagesOutsideTests(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The built-in skills a team reads to build a plugin, a site or a package name no
    /// <c>samples/</c> folder: the team cannot read this repository, and the samples moved to the marketplace.</summary>
    [Theory]
    [InlineData("authoring-plugins")]
    [InlineData("building-sites")]
    [InlineData("packaging-solutions")]
    public void A_built_in_skill_names_no_samples_folder(string name)
    {
        var skill = Harness.Host.BuiltInSkills.Find(name)!;

        Assert.DoesNotContain("samples/", skill.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void No_doc_tells_a_person_to_install_from_a_samples_folder()
    {
        var root = SolutionSamples.RepoRoot();
        var docs = Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
            .Append(Path.Combine(root, "README.md"))
            .Append(Path.Combine(root, "cli", "README.md"));

        Assert.Empty(docs.Where(file => File.ReadAllText(file).Contains("samples/", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file)));
    }

    /// <summary>Every <c>solution.json</c> and <c>plugin.json</c> under <paramref name="root"/> outside
    /// <c>tests/</c>, the skipped folders and <see cref="Allowed"/>, as sorted paths relative to it.</summary>
    public static IReadOnlyList<string> PackagesOutsideTests(string root)
    {
        var found = new List<string>();
        Walk(new DirectoryInfo(root), atRoot: true);
        found.Sort(StringComparer.Ordinal);
        return found;

        void Walk(DirectoryInfo directory, bool atRoot)
        {
            foreach (var file in directory.EnumerateFiles())
            {
                if (!Manifests.Contains(file.Name, StringComparer.Ordinal)) continue;
                var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                if (!Allowed.Contains(relative, StringComparer.Ordinal)) found.Add(relative);
            }

            foreach (var child in directory.EnumerateDirectories())
            {
                if (SkippedAnywhere.Contains(child.Name, StringComparer.Ordinal)) continue;
                if (atRoot && SkippedAtTheRoot.Contains(child.Name, StringComparer.Ordinal)) continue;
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                Walk(child, atRoot: false);
            }
        }
    }
}
