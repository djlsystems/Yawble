using System.Text.Json.Nodes;

namespace Yawble.Core.Sandboxing;

/// <summary>
/// The sandbox request contract this app speaks: every request is pinned to the
/// published 1.0.0 contract with telemetry explicitly off, whatever SDK release is underneath.
/// </summary>
public static class SandboxPin
{
    public const string RequestContract = "1.0.0";
    public const bool TelemetryEnabled = false;

    /// <summary>
    /// A minimal, harmless one-shot 1.0.0 request: deny-default network, UI off, telemetry off.
    /// Used by the contract check; the request compiler emits the real ones.
    /// </summary>
    public static JsonObject ProbeRequest(string? version = RequestContract)
    {
        var request = new JsonObject
        {
            ["version"] = version,
            ["process"] = new JsonObject
            {
                ["commandLine"] = HarmlessCommand(),
                ["env"] = new JsonArray(),
                ["timeout"] = 10_000,
            },
            ["network"] = new JsonObject { ["egress"] = new JsonObject { ["default"] = "deny" } },
            ["ui"] = new JsonObject { ["disable"] = true, ["clipboard"] = "none" },
            ["telemetry"] = new JsonObject { ["enabled"] = TelemetryEnabled },
        };
        return request;
    }

    private static string HarmlessCommand() =>
        OperatingSystem.IsWindows() ? @"C:\Windows\System32\cmd.exe /c exit 0"
        : OperatingSystem.IsMacOS() ? "/usr/bin/true"
        : "/bin/true";
}
