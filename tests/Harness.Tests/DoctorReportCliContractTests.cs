using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Host;
using Microsoft.Extensions.Configuration;

namespace Harness.Tests;

/// <summary>
/// THE --doctor REPORT IS THE OPERATOR CLI'S CONTRACT, ON BOTH SIDES. The report here is the Host's
/// own: a real launch check of a fake CLI that aborts under the run memory limit through the member
/// launch path, leaking a credential on stderr, and the wip figures recorded from the Host's own
/// decision under cgroup, rlimit and not enforced, control's record of its workers, and its record of the
/// running Concierge sessions. Its shape must equal the fixtures under
/// <c>cli/internal/doctor/testdata</c>, which the Go side decodes (<c>TestTheHostsOwnDoctorReportDecodes</c>),
/// so a field renamed on either side fails a test. Set <c>HARNESS_WRITE_CLI_FIXTURES=1</c> to rewrite them.
/// </summary>
public sealed class DoctorReportCliContractTests : IDisposable
{
    private const string Secret = "sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789abcdef";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-doctor-contract-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task The_hosts_doctor_report_has_the_shape_the_cli_decodes()
    {
        var prlimit = SystemCommand.Find("prlimit");
        Assert.SkipWhen(!OperatingSystem.IsLinux() || prlimit is null,
            "Needs Linux and prlimit in a root-owned system directory to apply RLIMIT_DATA.");
        var ct = TestContext.Current.CancellationToken;

        // A doctor command name, so the doctor lists the fake CLI's launch for it.
        var command = AgentAuthProbe.LoadSpecs().Keys.First();
        var program = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName, command);
        await TestExecutable.WriteAsync(program,
            "#!/bin/sh\n"
            + "limit=$(ulimit -d)\n"
            + "if [ \"$limit\" != unlimited ] && [ \"$limit\" -lt 1048576 ]; then\n"
            + $"  echo \"debug: ANTHROPIC_API_KEY={Secret}\" >&2\n"
            + "  echo \"fatal: cannot reserve the heap under a data limit of ${limit} kB\" >&2\n"
            + "  kill -ABRT $$\n"
            + "fi\n"
            + "echo 'fake 1.0.0'\n");

        var catalog = new AgentCatalog(
        [
            new AgentDefinition("fake", AgentMode.Headless, new AgentLaunch(program, ["-p", "{userPrompt}"]), LaunchCheck: ["--version"]),
        ]);
        var limited = RunMemoryLimits.Decide(new CgroupFacts(null, "not used in this test"), prlimit,
            () => new RunMemoryLimit(64, "runs.memoryLimitMb is set to 64 MB", Set: true));
        var runner = new ProcessAgentRunner(catalog, new RunHeartbeat(), memory: limited);

        var launch = await runner.CheckLaunchAsync("fake", ct);
        Assert.Equal(AgentLaunchReport.Failed, launch.Result);
        Assert.DoesNotContain(Secret, launch.StderrTail);
        new AgentLaunchChecksRecord(DateTimeOffset.UtcNow, [new PresetLaunchCheck("fake", program, launch)]).Write(_root);

        var settings = new TenantSettings(null!, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Wip:MaxRunning"] = "4" }).Build(), cpuCount: 8, memoryLimitMb: 8192);
        var mechanisms = new Dictionary<string, RunMemoryLimits>
        {
            ["cgroup"] = RunMemoryLimits.Decide(new CgroupFacts("/sys/fs/cgroup/app", "writable"), null, settings.RunMemoryLimit),
            ["rlimit"] = RunMemoryLimits.Decide(new CgroupFacts(null, "not writable"), prlimit,
                () => new RunMemoryLimit(2048, "runs.memoryLimitMb is set to 2048 MB", Set: true)),
            ["none"] = RunMemoryLimits.NotAvailable("no mechanism on this machine"),
        };

        // The Host's last sign-in probe, as control records it: when, on which worker, and each answer -
        // every agent installed and signed in, as a worker's probe reports a status command that exited 0.
        var probedAt = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        const string signedIn = "The status command exited 0.";
        new AgentAuthRecord(probedAt, "w1", [.. AgentAuthProbe.LoadSpecs().Keys.Select(c => new CommandSignIn(c, true, true, signedIn))])
            .Write(_root);

        // Control's record of its workers: one connected with a run, as the operator CLI shows per worker.
        new WorkersRecord(probedAt,
        [
            new WorkerRecordItem("worker-1", "2026.10.02.1+abc", WorkersView.Connected, false, probedAt, null,
                [new WorkerRecordRun("n1", "alpha", "Dev")],
                new WorkerRecordCapacity(4, 8L * 1024 * 1024 * 1024, 123L * 1024 * 1024, 3, false)),
        ]).Write(_root);

        // The Host's record of its running Concierge sessions: one nobody has open, on worker-1, with
        // when the idle window would end it, and one open in a browser.
        new ConciergeSessionsRecord(probedAt, "01:00:00",
        [
            new ConciergeSessionRecordItem("user-1", "person@example.test", "worker-1", probedAt, false,
                probedAt.AddMinutes(5), probedAt.AddMinutes(20), ConciergeActivity.Call, probedAt.AddMinutes(80), 300L * 1024 * 1024),
            new ConciergeSessionRecordItem("user-2", null, "worker-1", probedAt, true,
                null, probedAt.AddMinutes(30), ConciergeActivity.Typed, null, null),
        ]).Write(_root);

        var fixtures = Path.Combine(FindRepoRoot(), "cli", "internal", "doctor", "testdata");
        foreach (var (name, memory) in mechanisms)
        {
            WipRecord.Of(settings, memory).Write(_root);
            var json = HostDoctor.ToJson(await HostDoctor.ReportAsync(_root, ct));
            Assert.DoesNotContain(Secret, json);

            // Every agent says when its sign-in was measured, and on which worker.
            foreach (var agent in JsonNode.Parse(json)!["agents"]!.AsArray())
            {
                Assert.Equal(probedAt, agent!["measuredAt"]!.GetValue<DateTimeOffset>());
                Assert.Equal("w1", agent["measuredOn"]!.GetValue<string>());
                Assert.True(agent["installed"]!.GetValue<bool>());
                Assert.True(agent["authenticated"]!.GetValue<bool>());
                Assert.Equal(signedIn, agent["detail"]!.GetValue<string>());
            }

            var worker = JsonNode.Parse(json)!["workers"]!["items"]!.AsArray().Single()!;
            Assert.Equal("worker-1", worker["id"]!.GetValue<string>());
            Assert.Equal("connected", worker["state"]!.GetValue<string>());
            Assert.Equal("Dev", worker["runs"]![0]!["member"]!.GetValue<string>());
            Assert.Equal(3, worker["capacity"]!["bound"]!.GetValue<int>());

            var concierge = JsonNode.Parse(json)!["concierge"]!;
            Assert.Equal("01:00:00", concierge["window"]!.GetValue<string>());
            var session = concierge["sessions"]![0]!;
            Assert.Equal("worker-1", session["worker"]!.GetValue<string>());
            Assert.Equal(probedAt.AddMinutes(20), session["lastActivityAt"]!.GetValue<DateTimeOffset>());
            Assert.Equal(probedAt.AddMinutes(80), session["wouldEndAt"]!.GetValue<DateTimeOffset>());

            var path = Path.Combine(fixtures, $"host-doctor-{name}.json");
            if (Environment.GetEnvironmentVariable("HARNESS_WRITE_CLI_FIXTURES") == "1")
            {
                Directory.CreateDirectory(fixtures);
                await File.WriteAllTextAsync(path, json + "\n", ct);
            }

            Assert.True(File.Exists(path), $"{path} is missing; run with HARNESS_WRITE_CLI_FIXTURES=1");
            Assert.Equal(Shape(JsonNode.Parse(await File.ReadAllTextAsync(path, ct))), Shape(JsonNode.Parse(json)));
        }
    }

    /// <summary>
    /// Every property path, objects and arrays marked, arrays read as the union of their elements. A
    /// scalar's kind is not compared: a version or a sign-in depends on the machine the suite runs on.
    /// </summary>
    private static SortedSet<string> Shape(JsonNode? node, string path = "$", SortedSet<string>? into = null)
    {
        into ??= new SortedSet<string>(StringComparer.Ordinal);
        switch (node)
        {
            case JsonObject o:
                into.Add($"{path}:object");
                foreach (var (key, value) in o) Shape(value, $"{path}.{key}", into);
                break;
            case JsonArray a:
                into.Add($"{path}:array");
                foreach (var item in a) Shape(item, $"{path}[]", into);
                break;
            default:
                into.Add(path);
                break;
        }

        return into;
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
