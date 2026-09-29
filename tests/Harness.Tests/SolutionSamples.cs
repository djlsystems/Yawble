using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harness.Tests;

/// <summary>
/// The committed Job Tracker package, copied somewhere a test may change it. <c>1.1.0</c> is the
/// 1.0.0 folder with <c>samples/solutions/job-tracker-1.1.0.overlay</c> copied over it, exactly as
/// the sample's README says to make it by hand - so the update tests and a person get the same 1.1.0.
/// </summary>
public static class SolutionSamples
{
    public static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("No Harness.slnx above the test output.");
    }

    public static string SampleFolder => Path.Combine(RepoRoot(), "samples", "solutions", "job-tracker");

    public static string OverlayFolder => Path.Combine(RepoRoot(), "samples", "solutions", "job-tracker-1.1.0.overlay");

    /// <summary>A fresh copy of Job Tracker <paramref name="version"/> (1.0.0 or 1.1.0) under
    /// <paramref name="parent"/>, or a new temp folder; answers the package folder.</summary>
    public static string JobTracker(string version = "1.0.0", string? parent = null)
    {
        var target = Path.Combine(parent ?? Path.Combine(Path.GetTempPath(), $"solution-{Guid.NewGuid():N}"), $"job-tracker-{version}");
        Copy(SampleFolder, target);

        switch (version)
        {
            case "1.0.0":
                break;
            case "1.1.0":
                Copy(OverlayFolder, target);
                break;
            default:
                throw new ArgumentException($"There is no Job Tracker {version}; there are 1.0.0 and 1.1.0.", nameof(version));
        }

        return target;
    }

    /// <summary>Changes the copy's <c>solution.json</c> in place.</summary>
    public static void Edit(string package, Action<JsonObject> change) =>
        EditJson(Path.Combine(package, "solution.json"), change);

    public static void EditJson(string file, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        change(node);
        File.WriteAllText(file, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (var file in Directory.EnumerateFiles(from))
        {
            var destination = Path.Combine(to, Path.GetFileName(file));
            File.Copy(file, destination, overwrite: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, File.GetUnixFileMode(file));
        }

        foreach (var directory in Directory.EnumerateDirectories(from))
        {
            Copy(directory, Path.Combine(to, Path.GetFileName(directory)));
        }
    }
}
