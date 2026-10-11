using System.Text.Json;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// CONTROL AND A WORKER AS REAL PROCESSES, with the built-in catalog's own update commands: the update
/// control asks of its gate when a worker joins (in the control image) reaches only the suite's stub,
/// and npm's prefix in the update's process is the suite's scratch one - so no test Host or worker writes
/// the instance's npm install (see <see cref="AgentCliIsolation"/>).
/// </summary>
/// <remarks>
/// The "real" prefix here is a folder of the bed's, never the instance's: the worker is handed it, and a
/// PATH of the system's folders alone, as an instance's environment would hand them. With the guard
/// broken a real <c>npm</c> would run - offline - and write that folder, which the test then sees.
/// </remarks>
[Collection("worker processes")]
public sealed class AgentCliUpdateIsolationTests
{
    private const string SystemPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

    [Fact]
    public async Task The_built_in_update_control_asks_when_a_worker_joins_never_writes_the_npm_prefix_the_worker_was_given()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Control and workers as processes run on Linux.");

        await using var bed = new ProcessBed();
        var real = Plant(Path.Combine(bed.Work, "real-npm-global"));
        var before = Snapshot(real);

        // One preset per command whose platform update is npm's, as control asks them.
        var npmPresets = AgentCatalogFile.BuiltIns()
            .Where(preset => preset.Updates?.Update is ["npm", ..])
            .GroupBy(preset => preset.Launch.FileName, StringComparer.Ordinal)
            .Select(group => group.First().Name)
            .ToList();
        Assert.NotEmpty(npmPresets);

        var given = new Dictionary<string, string>
        {
            ["NPM_CONFIG_PREFIX"] = real,
            ["npm_config_prefix"] = real,
            ["npm_config_offline"] = "true",
            ["npm_config_cache"] = Path.Combine(bed.Work, "npm-cache"),
        };

        await bed.StartControlAsync(more: new Dictionary<string, string>(given) { ["HARNESS_IMAGE"] = AgentUpdatesAtStart.ControlImage });
        bed.StartWorker("w1", more: new Dictionary<string, string>(given) { ["PATH"] = SystemPath });

        var finished = new Dictionary<string, JsonElement>();
        await bed.UntilAsync("each npm update control asked when w1 joined has finished", async () =>
        {
            foreach (var preset in npmPresets)
            {
                var state = await bed.UpdateStateAsync(preset);
                if (state.GetProperty("phase").GetString() is AgentUpdatePhases.Done or AgentUpdatePhases.Failed) finished[preset] = state;
            }

            return finished.Count == npmPresets.Count;
        }, TimeSpan.FromSeconds(120));

        foreach (var (preset, state) in finished)
        {
            Assert.True(state.GetProperty("phase").GetString() == AgentUpdatePhases.Done, $"{preset}: {state}\n{bed.Logs()}");
            var result = state.GetProperty("result");
            Assert.False(result.GetProperty("updated").GetBoolean(), $"{preset}: {state}");

            // The stub answered, and the update's process had the scratch prefix, not the one it was given.
            var detail = result.GetProperty("detail").GetString()!;
            Assert.Contains(AgentCliIsolation.UpdateStubMessage, detail);
            Assert.Contains($"(npm prefix: {AgentCliIsolation.NpmPrefix})", detail);
        }

        Assert.Equal(before, Snapshot(real));
        Assert.NotEqual("/data/npm-global", AgentCliIsolation.NpmPrefix);
    }

    /// <summary>An npm global prefix with one installed CLI in it, as an instance's has.</summary>
    private static string Plant(string prefix)
    {
        var package = Directory.CreateDirectory(Path.Combine(prefix, "lib", "node_modules", "@anthropic-ai", "claude-code", "bin")).FullName;
        File.WriteAllText(Path.Combine(prefix, "lib", "node_modules", "@anthropic-ai", "claude-code", "package.json"), """{"name":"@anthropic-ai/claude-code","version":"0.0.1-bed"}""");
        File.WriteAllText(Path.Combine(package, "claude.exe"), "#!/bin/sh\necho 0.0.1-bed\n");
        var bin = Directory.CreateDirectory(Path.Combine(prefix, "bin")).FullName;
        File.CreateSymbolicLink(Path.Combine(bin, "claude"), "../lib/node_modules/@anthropic-ai/claude-code/bin/claude.exe");
        return prefix;
    }

    /// <summary>Every entry under <paramref name="root"/>, with the root itself: path, kind, size and change time.</summary>
    private static string Snapshot(string root) => string.Join('\n',
        new[] { new DirectoryInfo(root) }
            .Concat<FileSystemInfo>(new DirectoryInfo(root).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            .Select(entry => $"{Path.GetRelativePath(root, entry.FullName)} {(entry is FileInfo file ? file.Length : -1)} {entry.LinkTarget} {entry.LastWriteTimeUtc.Ticks}")
            .Order(StringComparer.Ordinal));
}
