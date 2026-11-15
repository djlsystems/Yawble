using System.Text.Json.Nodes;
using Yawble.Core.Policy;
using Yawble.Core.Sandboxing;
using Xunit;

namespace Yawble.Core.Tests.Policy;

/// <summary>
/// B044: the dry run never asks the sandbox to bind a granted path that is missing on this host. Sandbox release 1.0.1
/// cannot: bwrap exits during setup and the network supervisor prints "mxc: failed to query Bubblewrap loopback
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

    [Fact]
    public void WhereTheBackendCannotRunTheExactJsonIsSubmitted()
    {
        var missing = Path.Combine(Path.GetTempPath(), "yawble-validate-never-" + Guid.NewGuid().ToString("N"), "repo");
        var request = Request(missing, [missing], [missing], []);
        var (_, submitted) = Validate(request, backendAvailable: false);
        Assert.Equal(request.ToJsonString(), submitted.ToJsonString());
    }
}
