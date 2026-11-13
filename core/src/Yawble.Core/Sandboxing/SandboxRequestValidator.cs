using System.Text.Json.Nodes;
using Yawble.Core.Policy;

namespace Yawble.Core.Sandboxing;

/// <summary>How far the sandbox itself got with a request.</summary>
public enum ValidationLevel
{
    /// <summary>The sandbox refused it: malformed (the closed 1.0.0 contract), unsupported containment, or policy validation.</summary>
    Rejected,

    /// <summary>
    /// The sandbox's real parser accepted the exact JSON against the closed 1.0.0 contract, including the
    /// containment-specific checks it does while parsing (for example ProcessContainer's proxy rules), but the
    /// backend's own checks did not run because that backend is not on this host.
    /// </summary>
    ContractParsed,

    /// <summary>The backend on this host accepted it too (the Windows request-aware probe, or a real launch).</summary>
    BackendAccepted,
}

public sealed record ValidationResult(ValidationLevel Level, int Status, string Detail)
{
    public bool Accepted => Level != ValidationLevel.Rejected;
}

/// <summary>
/// Validation through the sandbox's own native library: every policy save is dry-run through it, and so is every
/// launch. The 1.0.1 managed SDK has no ValidateProcessJson, so this goes to mxc_ffi:
/// <list type="bullet">
/// <item>Windows, for a ProcessContainer request: <c>mxc_probe_request_json_with_error</c>, a true dry run
/// (parse plus the host's tier checks, no sandbox).</item>
/// <item>Everywhere else: <c>mxc_run_json</c>, which parses the exact JSON first and answers
/// MalformedRequest when the contract rejects it. 1.0.1 exports no parse-only entry point on Linux or macOS.
/// When the request's backend is not available on this host, the exact JSON is submitted: it is parsed and
/// then refused with BackendUnavailable, so nothing runs. When the backend IS available (a real launch would
/// follow the parse), a copy with only the command replaced by a no-op and a 10-second timeout is submitted
/// (<see cref="ProbeCopy"/>), so validation never starts the agent CLI.</item>
/// </list>
/// </summary>
public static class SandboxRequestValidator
{
    public static ValidationResult Validate(string requestJson) =>
        Validate(requestJson, NativeSandbox.RunJson, OperatingSystem.IsWindows() ? NativeSandbox.ProbeRequestJson : null, HostBackendAvailable);

    /// <summary>Whether the sandbox reports this OS's default backend as available here.</summary>
    public static bool HostBackendAvailable(string containment)
    {
        var wanted = containment switch
        {
            "processcontainer" => Microsoft.Mxc.Sdk.V1.ContainmentBackend.ProcessContainer,
            "seatbelt" => Microsoft.Mxc.Sdk.V1.ContainmentBackend.Seatbelt,
            "bubblewrap" => Microsoft.Mxc.Sdk.V1.ContainmentBackend.Bubblewrap,
            _ => Microsoft.Mxc.Sdk.V1.ContainmentBackend.Unknown,
        };
        try
        {
            return Microsoft.Mxc.Sdk.V1.MxcPlatform.GetAvailableBackends().Any(b => b.Backend == wanted);
        }
        catch (Exception)
        {
            return true; // unknown: assume a launch could follow, so submit the no-op copy
        }
    }

    public static ValidationResult Validate(
        string requestJson,
        Func<string, NativeSandbox.Outcome> runJson,
        Func<string, NativeSandbox.ProbeOutcome>? windowsProbe,
        Func<string, bool> backendAvailable)
    {
        var request = JsonNode.Parse(requestJson) as JsonObject ?? throw new ArgumentException("request is not a JSON object");
        var containment = request["containment"]?.GetValue<string>();
        var host = TargetOsExtensions.Host;

        if (containment == "processcontainer" && host == TargetOs.Windows && windowsProbe is not null)
        {
            var probe = windowsProbe(requestJson);
            if (probe.Status is NativeSandbox.StatusMalformedRequest or NativeSandbox.StatusPolicyValidation)
                return new ValidationResult(ValidationLevel.Rejected, probe.Status, $"probe refused: {probe.Message}");
            if (probe.Status != NativeSandbox.StatusSuccess)
                return new ValidationResult(ValidationLevel.ContractParsed, probe.Status, $"parsed; the ProcessContainer probe could not run here (status {probe.Status}): {probe.Message}");
            var output = probe.OutputJson is null ? null : JsonNode.Parse(probe.OutputJson);
            var error = output?["error"]?.GetValue<string>();
            var tier = output?["tier"]?.GetValue<string>();
            if (error is null && tier is not null)
                return new ValidationResult(ValidationLevel.BackendAccepted, 0, $"ProcessContainer probe: tier {tier}");
            // A Windows without the BaseContainer API (Windows 10, Server, a 24H2 without the Aug-2026 update)
            // cannot run ProcessContainer at all; that says nothing about the request.
            var apiPresent = output?["probes"]?["baseContainerApiPresent"]?.GetValue<bool>();
            return apiPresent == false
                ? new ValidationResult(ValidationLevel.ContractParsed, 0, $"parsed; this Windows has no BaseContainer API ({error ?? "no tier"})")
                : new ValidationResult(ValidationLevel.Rejected, 0, $"ProcessContainer probe: {error ?? "no tier can enforce this request"}");
        }

        var launchable = containment is not null && containment == host.Containment() && backendAvailable(containment);
        var submitted = launchable ? ProbeCopy(request).ToJsonString(SandboxJson.Compact) : requestJson;
        var outcome = runJson(submitted);
        return outcome.Status switch
        {
            NativeSandbox.StatusSuccess => new ValidationResult(ValidationLevel.BackendAccepted, 0,
                launchable ? $"launched a no-op under {containment} with this policy, exit code {outcome.ExitCode}" : $"ran, exit code {outcome.ExitCode}"),
            // A foreign backend is refused by dispatch only after the full parse (microsoft/mxc mxc_engine/dispatch.rs:
            // "Seatbelt is only available on macOS"); parse errors are always MalformedRequest.
            NativeSandbox.StatusUnsupportedContainment or NativeSandbox.StatusBackendUnavailable when containment != host.Containment()
                => new ValidationResult(ValidationLevel.ContractParsed, outcome.Status, $"exact JSON parsed by the sandbox; {containment} cannot run on this host ({outcome.Message})"),
            NativeSandbox.StatusMalformedRequest or NativeSandbox.StatusUnsupportedContainment or NativeSandbox.StatusPolicyValidation
                => new ValidationResult(ValidationLevel.Rejected, outcome.Status, outcome.Message ?? "rejected"),
            NativeSandbox.StatusBackendUnavailable
                => new ValidationResult(ValidationLevel.ContractParsed, outcome.Status, $"exact JSON parsed by the sandbox; {containment} is not available on this host ({outcome.Message})"),
            _ => new ValidationResult(ValidationLevel.ContractParsed, outcome.Status, $"parsed, then failed at launch (status {outcome.Status}): {outcome.Message}"),
        };
    }

    /// <summary>The same request with a no-op command and a short timeout: every policy field unchanged.</summary>
    public static JsonObject ProbeCopy(JsonObject request)
    {
        var copy = (JsonObject)request.DeepClone();
        var process = (JsonObject)copy["process"]!;
        process["commandLine"] = TargetOsExtensions.Host switch
        {
            TargetOs.Windows => @"C:\Windows\System32\cmd.exe /c exit 0",
            TargetOs.MacOS => "/usr/bin/true",
            _ => "/bin/true",
        };
        process["timeout"] = 10_000;
        return copy;
    }
}
