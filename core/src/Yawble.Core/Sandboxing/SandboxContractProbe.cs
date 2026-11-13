using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Mxc.Sdk;
using Microsoft.Mxc.Sdk.V1;

namespace Yawble.Core.Sandboxing;

/// <summary>
/// Asks the sandbox whether it still accepts our pinned request (contract 1.0.0, telemetry off). Release 1.0.1 has no
/// one-shot dry run, so each probe submits a harmless request: the native layer parses the closed contract
/// first and answers MalformedRequest (status 1) when it rejects it. Any later outcome - it ran, or the
/// backend is unavailable on this host - means the contract was accepted.
/// Two controls prove the probe discriminates: an unknown version and an unknown telemetry field must both
/// be rejected as malformed. A further check covers the typed SDK path the core uses on Linux and macOS.
/// </summary>
public static class SandboxContractProbe
{
    public sealed record Attempt(string Name, bool Accepted, string Outcome);

    public sealed record Verdict(bool Ok, IReadOnlyList<Attempt> Attempts, string Summary);

    public static Verdict Check(Func<string, NativeSandbox.Outcome> runJson, Func<int?>? runTyped = null)
    {
        var unknownTelemetryField = SandboxPin.ProbeRequest();
        unknownTelemetryField["telemetry"]!.AsObject()["mandatory"] = true;

        var attempts = new List<Attempt>
        {
            SubmitJson($"exact JSON {SandboxPin.RequestContract}, telemetry off", SandboxPin.ProbeRequest(), runJson),
            SubmitJson("control: version 999.0.0", SandboxPin.ProbeRequest("999.0.0"), runJson),
            SubmitJson("control: unknown telemetry field", unknownTelemetryField, runJson),
        };
        var sdkContract = SdkContract();
        attempts.Add(new Attempt("typed SDK emits contract", sdkContract == SandboxPin.RequestContract,
            $"Microsoft.Mxc.Sdk V1 emits {sdkContract ?? "(unknown: SchemaVersions.SdkContract not found)"}"));
        attempts.Add(SubmitTyped(runTyped ?? RunTypedProbe));

        var pinnedAccepted = attempts[0].Accepted && attempts[3].Accepted && attempts[4].Accepted;
        var controlsRejected = !attempts[1].Accepted && !attempts[2].Accepted;
        var ok = pinnedAccepted && controlsRejected;
        var summary = ok
            ? $"the sandbox accepts a {SandboxPin.RequestContract} request with telemetry off (exact JSON: {attempts[0].Outcome}; typed SDK: {attempts[4].Outcome})"
            : !pinnedAccepted
                ? $"the sandbox no longer accepts a {SandboxPin.RequestContract} request with telemetry off: " +
                  string.Join("; ", new[] { attempts[0], attempts[3], attempts[4] }.Where(a => !a.Accepted).Select(a => $"{a.Name}: {a.Outcome}"))
                : "the probe cannot discriminate: a control request was accepted, so acceptance proves nothing";
        return new Verdict(ok, attempts, summary);
    }

    /// <summary>The contract the typed V1 SDK writes (internal const SchemaVersions.SdkContract).</summary>
    public static string? SdkContract() =>
        typeof(SchemaVersions).GetField("SdkContract", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            ?.GetRawConstantValue() as string;

    private static Attempt SubmitJson(string name, JsonObject request, Func<string, NativeSandbox.Outcome> runJson)
    {
        var outcome = runJson(request.ToJsonString());
        return outcome.Status switch
        {
            NativeSandbox.StatusSuccess => new Attempt(name, true, $"ran, exit code {outcome.ExitCode}"),
            NativeSandbox.StatusMalformedRequest => new Attempt(name, false, $"rejected as malformed: {outcome.Message}"),
            _ => new Attempt(name, true, $"parsed, then {(ErrorCode)outcome.Status}: {outcome.Message}"),
        };
    }

    private static Attempt SubmitTyped(Func<int?> runTyped)
    {
        const string name = "typed SDK request, telemetry off";
        try
        {
            var exit = runTyped();
            return new Attempt(name, true, $"ran, exit code {exit}");
        }
        catch (MxcException e) when (e.Code == ErrorCode.MalformedRequest)
        {
            return new Attempt(name, false, $"rejected as malformed: {e.Message}");
        }
        catch (MxcException e)
        {
            return new Attempt(name, true, $"parsed, then {e.Code}: {e.Message}");
        }
    }

    private static int? RunTypedProbe()
    {
        var command = SandboxPin.ProbeRequest()["process"]!["commandLine"]!.GetValue<string>();
        var result = MxcContainer.Run(
            new ContainerRequest(command) { TimeoutMs = 10_000 },
            new RunOptions { Telemetry = new TelemetryConfig { Enabled = SandboxPin.TelemetryEnabled } });
        return result.ExitCode;
    }
}
