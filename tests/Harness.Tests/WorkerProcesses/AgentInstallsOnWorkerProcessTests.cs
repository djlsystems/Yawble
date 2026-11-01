using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A FRESH VOLUME, AS REAL PROCESSES: control and a worker with none of the agent CLIs on its PATH, and an
/// installer (a stub of the image's ensure-agent-clis.sh) that takes a second per CLI and cannot reach one
/// vendor. The worker connects at once - nothing is installed before it - and then control installs every
/// CLI through its gate, on that worker, all at the same time; the unreachable one fails alone and the worker
/// stays connected.
/// </summary>
[Collection("worker processes")]
public sealed class AgentInstallsOnWorkerProcessTests
{
    private const string SystemPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
    // Grok's own updater is one of the suite's update stubs, which every child's PATH carries (AgentCliIsolation):
    // grok is measured installed here, and updated - by the stub, which refuses - rather than installed.
    private static readonly string[] Npm = ["claude", "codex", "copilot"];

    [Fact]
    public async Task A_fresh_worker_connects_at_once_then_every_cli_installs_in_parallel_and_an_unreachable_vendor_fails_alone()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Control and workers as processes run on Linux.");

        await using var bed = new ProcessBed();
        var bin = Directory.CreateDirectory(Path.Combine(bed.Work, "agent-bin")).FullName;
        var log = Path.Combine(bed.Work, "installs.log");
        var installer = Path.Combine(bed.Work, "installer.sh");
        File.WriteAllText(installer, $$"""
            #!/bin/sh
            # A stub of ensure-agent-clis.sh <name>: a second's download each, codex's vendor unreachable.
            echo "start $1 $(date +%s.%N) update=$HARNESS_UPDATE_AGENTS" >> '{{log}}'
            sleep 1
            if [ "$1" = codex ]; then echo "agent cli: FAILED to install codex; npm ERR! network unreachable"; exit 1; fi
            printf '#!/bin/sh\necho "%s 9.9.9"\n' "$1" > '{{bin}}'/"$1" && chmod 755 '{{bin}}'/"$1"
            echo "end $1 $(date +%s.%N)" >> '{{log}}'
            echo "agent cli: $1 installed"
            """);

        await bed.StartControlAsync(more: new Dictionary<string, string> { [AgentInstallsAtStart.InstallerSetting] = installer });
        var started = Stopwatch.StartNew();
        bed.StartWorker("w1", more: new Dictionary<string, string> { ["PATH"] = $"{bin}:{SystemPath}" });
        await bed.UntilAsync("w1 is connected", async () =>
            (await bed.WorkersAsync()).Any(w => w.GetProperty("state").GetString() == "connected"));
        var connected = started.Elapsed;
        Assert.True(connected < TimeSpan.FromSeconds(30), $"w1 took {connected} to connect\n{bed.Logs()}");

        var finished = new Dictionary<string, JsonElement>();
        await bed.UntilAsync("every install control asked when w1 joined has finished", async () =>
        {
            foreach (var command in Npm.Append("grok"))
            {
                var state = await bed.UpdateStateAsync(command);
                if (state.GetProperty("phase").GetString() is AgentUpdatePhases.Done or AgentUpdatePhases.Failed) finished[command] = state;
            }

            return finished.Count == Npm.Length + 1;
        }, TimeSpan.FromSeconds(120));

        Assert.Equal("update", finished["grok"].GetProperty("kind").GetString());
        finished.Remove("grok");
        foreach (var (command, state) in finished)
        {
            Assert.Equal("install", state.GetProperty("kind").GetString());
            var result = state.GetProperty("result");
            Assert.Equal(command != "codex", result.GetProperty("updated").GetBoolean());
        }

        Assert.Contains("network unreachable", finished["codex"].GetProperty("result").GetProperty("detail").GetString());
        Assert.Equal("9.9.9", finished["claude"].GetProperty("result").GetProperty("versionAfter").GetString()?.Split(' ')[^1]);

        // All four ran at once: every one had started before any had ended.
        var lines = File.ReadAllLines(log).Select(l => l.Split(' ')).ToList();
        var lastStart = lines.Where(l => l[0] == "start").Max(l => double.Parse(l[2], CultureInfo.InvariantCulture));
        var firstEnd = lines.Where(l => l[0] == "end").Min(l => double.Parse(l[2], CultureInfo.InvariantCulture));
        Assert.Equal(3, lines.Count(l => l[0] == "start"));
        Assert.True(lastStart < firstEnd, string.Join('\n', File.ReadAllLines(log)));
        // The installer installs only; an installed CLI is updated by the preset's own update, on its interval.
        Assert.All(lines.Where(l => l[0] == "start"), l => Assert.Equal("update=0", l[3]));

        // The worker stayed connected throughout, and the record says which failed.
        Assert.Contains(await bed.WorkersAsync(), w => w.GetProperty("state").GetString() == "connected");
        var record = AgentInstallsRecord.Read(bed.Root)!;
        Assert.Equal("w1", record.Worker);
        Assert.Equal(AgentInstallWords.Failed, Assert.Single(record.Items, i => i.Command == "codex").State);
        Assert.All(record.Items.Where(i => i.Command != "codex"), i => Assert.Equal(AgentInstallWords.Ready, i.State));
    }
}
