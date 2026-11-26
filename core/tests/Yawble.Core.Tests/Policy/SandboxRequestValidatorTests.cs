using System.Text.Json.Nodes;
using Yawble.Core.Policy;
using Yawble.Core.Sandboxing;
using Xunit;

namespace Yawble.Core.Tests.Policy;

/// <summary>
/// The dry run never asks the sandbox to bind a granted path that is missing on this host. Sandbox release 1.0.1
/// cannot: bwrap exits during setup and the network supervisor prints "failed to query Bubblewrap loopback
/// readiness" - once per Linux golden file (fictional /home/alice paths) on a host with bwrap. A fake sandbox here
/// records what the validator submits, so this runs on every OS.
/// </summary>
public class SandboxRequestValidatorTests
{
    private static JsonObject Request(string cwd, string[] readonlyPaths, string[] readwritePaths, string[] denied) => new()
    {
        ["version"] = SandboxPin.RequestContract,
        ["containment"] = TargetOsExtensions.Host.Containment(),
        ["process"] = new JsonObject { ["commandLine"] = "agent --run", ["cwd"] = cwd, ["env"] = new JsonArray("PATH=/usr/bin:/bin"), ["timeout"] = 600_000 },
        ["filesystem"] = new JsonObject
        {
            ["readonlyPaths"] = new JsonArray(readonlyPaths.Select(p => (JsonNode)p).ToArray()),
            ["readwritePaths"] = new JsonArray(readwritePaths.Select(p => (JsonNode)p).ToArray()),
            ["deniedPaths"] = new JsonArray(denied.Select(p => (JsonNode)p).ToArray()),
        },
        ["network"] = new JsonObject { ["egress"] = new JsonObject { ["default"] = "deny" } },
    };

    private static (ValidationResult Result, JsonObject Submitted) Validate(JsonObject request, bool backendAvailable = true)
    {
        string? submitted = null;
        var result = SandboxRequestValidator.Validate(request.ToJsonString(),
            json => { submitted = json; return new NativeSandbox.Outcome(NativeSandbox.StatusSuccess, 0, null); },
            windowsProbe: null, backendAvailable: _ => backendAvailable);
        return (result, JsonNode.Parse(submitted!)!.AsObject());
    }

    private static string[] Paths(JsonObject request, string key) =>
        request["filesystem"]![key]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    [Fact]
    public void AMissingGrantIsLeftOutOfTheNoOpLaunchAndNamed()
    {
        var present = Directory.CreateTempSubdirectory("yawble-validate-").FullName;
        try
        {
            var missingRo = Path.Combine(present, "gone", "versions");
            var missingRw = Path.Combine(present, "gone", "repo");
            var missingDenied = Path.Combine(present, "gone", ".ssh");
            var request = Request(missingRw, [present, missingRo], [missingRw, present], [missingDenied]);

            var (result, submitted) = Validate(request);

            Assert.Equal([present], Paths(submitted, "readonlyPaths"));
            Assert.Equal([present], Paths(submitted, "readwritePaths"));
            Assert.Equal([missingDenied], Paths(submitted, "deniedPaths")); // a missing denied path is fine to the sandbox
            Assert.True(Path.Exists(submitted["process"]!["cwd"]!.GetValue<string>()));
            Assert.NotEqual("agent --run", submitted["process"]!["commandLine"]!.GetValue<string>());
            Assert.Equal(request["network"]!.ToJsonString(), submitted["network"]!.ToJsonString());
            // Accepted, but not BackendAccepted: the backend did not see the exact policy.
            Assert.True(result.Accepted);
            Assert.Equal(ValidationLevel.ContractParsed, result.Level);
            Assert.Contains(missingRo, result.Detail);
            Assert.Contains(missingRw, result.Detail);
            Assert.DoesNotContain(missingDenied, result.Detail);
        }
        finally
        {
            Directory.Delete(present, true);
        }
    }

    [Fact]
    public void WithEveryGrantPresentTheNoOpCarriesThePolicyUnchanged()
    {
        var present = Directory.CreateTempSubdirectory("yawble-validate-").FullName;
        try
        {
            var request = Request(present, [present], [present], []);
            var (result, submitted) = Validate(request);
            Assert.Equal([present], Paths(submitted, "readonlyPaths"));
            Assert.Equal([present], Paths(submitted, "readwritePaths"));
            Assert.Equal(present, submitted["process"]!["cwd"]!.GetValue<string>());
            Assert.Equal(ValidationLevel.BackendAccepted, result.Level);
        }
        finally
        {
            Directory.Delete(present, true);
        }
    }

    /// <summary>
    /// A model of sandbox release 1.0.1's Bubblewrap launch, from its network-supervisor script: bwrap binds every
    /// readonly/readwrite path and the cwd; a missing one makes it exit during setup. The supervisor then polls
    /// `nsenter --net=/proc/$child/ns/net -- ip -4 -o address show dev lo up` every 10 ms until 127.0.0.1/8 is up;
    /// a poll that finds the loopback still down is retried, and only a poll that cannot enter the namespace (the
    /// child has exited, or was cancelled, first) prints the line and fails the launch.
    /// </summary>
    private sealed class FakeBubblewrap(int pollsUntilLoopbackUp = 0, bool exitsAtSetup = false)
    {
        /// <summary>The line the supervisor prints, without the sandbox's own prefix.</summary>
        public const string Line = "failed to query Bubblewrap loopback readiness";
        public int Lines { get; private set; }
        public int Polls { get; private set; }

        public NativeSandbox.Outcome Run(string json)
        {
            var request = JsonNode.Parse(json)!.AsObject();
            var binds = Paths(request, "readonlyPaths").Concat(Paths(request, "readwritePaths"))
                .Append(request["process"]!["cwd"]!.GetValue<string>());
            var childAlive = !exitsAtSetup && binds.All(Path.Exists);
            for (var poll = 0; ; poll++)
            {
                Polls++;
                if (!childAlive)
                {
                    Lines++; // nsenter: no /proc/<pid>/ns/net
                    return new NativeSandbox.Outcome(NativeSandbox.StatusBackendError, 1, Line);
                }
                if (poll >= pollsUntilLoopbackUp) return new NativeSandbox.Outcome(NativeSandbox.StatusSuccess, 0, null);
            }
        }
    }

    private static IEnumerable<string> LinuxGoldens => GoldenCases.All.Keys.Where(k => k.StartsWith("linux-", StringComparison.Ordinal));

    /// <summary>The golden file as this host would launch it: only the containment changed to the host's.</summary>
    private static string OnThisHost(string name)
    {
        var request = JsonNode.Parse(GoldenCases.Read(name + ".request.json"))!.AsObject();
        request["containment"] = TargetOsExtensions.Host.Containment();
        return request.ToJsonString();
    }

    [Fact]
    public void TheLinuxGoldenFiles_DoNotMakeBwrapExitBeforeTheReadinessQuery()
    {
        Assert.Equal(10, LinuxGoldens.Count());
        // A golden policy submitted unchanged: its fictional /home/alice grants make bwrap exit at mount, once per
        // Linux golden file. The validator's dry run must not submit the golden file's own binds.
        var before = new FakeBubblewrap();
        foreach (var name in LinuxGoldens) before.Run(OnThisHost(name));
        Assert.Equal(10, before.Lines);

        var after = new FakeBubblewrap(pollsUntilLoopbackUp: 3);
        foreach (var name in LinuxGoldens)
        {
            var result = SandboxRequestValidator.Validate(OnThisHost(name), after.Run, windowsProbe: null, backendAvailable: _ => true);
            Assert.True(result.Accepted, $"{name}: {result.Detail}");
            Assert.Equal(ValidationLevel.ContractParsed, result.Level); // the backend did not see the /home/alice grants
            Assert.Contains("/home/alice", result.Detail);
        }
        Assert.Equal(0, after.Lines);
    }

    [Fact]
    public void ASlowLoopbackIsRetriedNotReported()
    {
        // The "race" reading: the namespace exists but lo is not up yet. The supervisor retries; nothing is printed.
        var present = Directory.CreateTempSubdirectory("yawble-validate-").FullName;
        try
        {
            var bwrap = new FakeBubblewrap(pollsUntilLoopbackUp: 500);
            var result = SandboxRequestValidator.Validate(Request(present, [present], [present], []).ToJsonString(),
                bwrap.Run, windowsProbe: null, backendAvailable: _ => true);
            Assert.Equal(ValidationLevel.BackendAccepted, result.Level);
            Assert.Equal(501, bwrap.Polls);
            Assert.Equal(0, bwrap.Lines);
        }
        finally
        {
            Directory.Delete(present, true);
        }
    }

    [Fact]
    public void ASandboxThatExitsOrIsCancelledBeforeReadinessStillSurfacesAsALaunchFailure()
    {
        // Every path present, but the child dies first for another reason: the line is real then, and the validator
        // does not call it BackendAccepted.
        var present = Directory.CreateTempSubdirectory("yawble-validate-").FullName;
        try
        {
            var bwrap = new FakeBubblewrap(exitsAtSetup: true);
            var result = SandboxRequestValidator.Validate(Request(present, [present], [present], []).ToJsonString(),
                bwrap.Run, windowsProbe: null, backendAvailable: _ => true);
            Assert.Equal(1, bwrap.Lines);
            Assert.Equal(ValidationLevel.ContractParsed, result.Level);
            Assert.Equal(NativeSandbox.StatusBackendError, result.Status);
            Assert.Contains(FakeBubblewrap.Line, result.Detail);
        }
        finally
        {
            Directory.Delete(present, true);
        }
    }

    [Fact]
    public void WhereTheBackendCannotRunTheExactJsonIsSubmitted()
    {
        var missing = Path.Combine(Path.GetTempPath(), "yawble-validate-never-" + Guid.NewGuid().ToString("N"), "repo");
        var request = Request(missing, [missing], [missing], []);
        var (_, submitted) = Validate(request, backendAvailable: false);
        Assert.Equal(request.ToJsonString(), submitted.ToJsonString());
    }
}
