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

    /// <summary>
    /// A small neutral package under <paramref name="parent"/>: one plugin member whose plugin
    /// <paramref name="pluginId"/> reads <paramref name="reads"/> (site/collection pairs), and the
    /// sites <paramref name="sites"/>, each a one-page folder. Answers the package folder.
    /// </summary>
    public static string Neutral(
        string parent, string pluginId, IReadOnlyList<string> sites, IReadOnlyList<(string Site, string Collection)> reads,
        string version = "1.0.0")
    {
        var target = Path.Combine(parent, $"{pluginId}-{version}");
        var plugin = Path.Combine(target, "plugins", pluginId);
        Directory.CreateDirectory(Path.Combine(plugin, "bin"));

        File.WriteAllText(Path.Combine(target, "solution.json"), new JsonObject
        {
            ["format"] = 1,
            ["id"] = pluginId + "-pack",
            ["name"] = "Pack " + pluginId,
            ["version"] = version,
            ["description"] = "A neutral test package.",
            ["team"] = new JsonObject { ["name"] = "Pack " + pluginId, ["instructions"] = "Keep the board." },
            ["members"] = new JsonArray(new JsonObject { ["name"] = "Keeper", ["pluginId"] = pluginId }),
            ["sites"] = new JsonArray([.. sites.Select(site => (JsonNode)site)]),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        File.WriteAllText(Path.Combine(plugin, "plugin.json"), new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = pluginId,
            ["name"] = "Keeper " + pluginId,
            ["description"] = "Reads its team's board.",
            ["version"] = "0.1.0",
            ["protocol"] = "harness.member/1",
            ["executable"] = new JsonObject { ["path"] = "bin/run" },
            ["reads"] = new JsonArray([.. reads.Select(r => (JsonNode)new JsonObject { ["site"] = r.Site, ["collection"] = r.Collection })]),
        }.ToJsonString());

        var executable = Path.Combine(plugin, "bin", "run");
        File.WriteAllText(executable, "#!/bin/sh\ncat >/dev/null; echo '{\"t\":\"result\",\"ok\":true}'\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        foreach (var site in sites)
        {
            Directory.CreateDirectory(Path.Combine(target, "sites", site));
            File.WriteAllText(Path.Combine(target, "sites", site, "index.html"), "<!doctype html><title>Board</title>\n");
        }

        return target;
    }

    /// <summary>
    /// <see cref="Neutral"/> with one site, <c>board</c>, whose page links to a file in the site's files
    /// folder both ways a page can: a static <c>&lt;a href="_api/files/out/a.txt"&gt;</c> and
    /// <c>site.files.url</c> from its script.
    /// </summary>
    public static string NeutralLinkingToFiles(string parent, string pluginId)
    {
        var target = Neutral(parent, pluginId, ["board"], []);
        var page = Path.Combine(target, "sites", "board");

        File.WriteAllText(Path.Combine(page, "index.html"),
            "<!doctype html><title>Board</title>\n"
            + "<a id=\"static\" href=\"_api/files/out/a.txt\">A file</a>\n"
            + "<a id=\"made\">A file</a>\n"
            + "<script src=\"/sites/_sdk/site.js\"></script>\n<script src=\"app.js\"></script>\n");
        File.WriteAllText(Path.Combine(page, "app.js"),
            "document.getElementById('made').href = site.files.url('out/a.txt');\n");

        // Its panel lists the site's files folder among the folders the team writes results into.
        Edit(target, m => m["panel"] = new JsonObject { ["primarySite"] = "board", ["outputs"] = new JsonArray("sites/board/files") });

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
