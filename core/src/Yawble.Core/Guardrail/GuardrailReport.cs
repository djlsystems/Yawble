using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Yawble.Core.Guardrail;

/// <summary>A verdict as the person reads it.</summary>
public static class Verdicts
{
    public const string Blocked = "BLOCKED";
    public const string Allowed = "ALLOWED";
    public const string Skip = "SKIP";
    public const string Info = "INFO";

    /// <summary>The probe never reported this attempt (it crashed or was killed first).</summary>
    public const string Missing = "MISSING";
}

/// <summary>Whose policy the check ran under. Synthetic: no member was named, so the global policy alone.</summary>
public sealed record GuardrailSubject(string Team, string Member, string Cli, bool Synthetic);

/// <summary>
/// One attempt's result. <see cref="Expected"/> is null for INFO lines; <see cref="AsExpected"/> is false when the
/// verdict differs from it, or when a refusal the gateway must log was not found in the gateway log.
/// <see cref="SandboxOnly"/>: a write that succeeded inside the sandbox but never reached the host (the sandbox's
/// writable tmpfs mask over a denied folder); its verdict is BLOCKED.
/// </summary>
public sealed record GuardrailResult(
    string Id,
    string Category,
    string Verdict,
    string? Expected,
    bool AsExpected,
    string Evidence,
    bool SandboxOnly = false,
    bool? Logged = null);

/// <summary>
/// The launch the probe rode on: the real launcher's run (run_records.run_key; RunNumber keys gateway_events), how it ended, the backend, and the
/// credential mode its gateway was set up for (the probe never makes a model call, so no credential is used).
/// </summary>
public sealed record GuardrailRun(string RunKey, long? RunNumber, string End, string? Reason, string Backend, string CredentialMode, bool CredentialConfigured,
    long? KillLatencyMs = null);

/// <summary>How the sandbox answered the request itself (a refusal means no probe ran).</summary>
public sealed record GuardrailSandbox(string Backend, int Status, int ExitCode, bool TimedOut, string? Refusal, string? Stderr);

public sealed record GuardrailCounts(int Blocked, int Allowed, int Unexpected, int Skipped, int Info);

/// <summary>
/// The guardrail check's report (IPC: guardrail.run / guardrail.last; CLI: yawble-core guardrail-check --json).
/// <see cref="Passed"/>: the sandbox ran the probe and every attempt was as expected (escapes BLOCKED, positive controls
/// ALLOWED, refusals logged).
/// </summary>
public sealed record GuardrailReport(
    string Id,
    GuardrailSubject Subject,
    string Os,
    DateTimeOffset StartedAt,
    long DurationMs,
    bool Passed,
    GuardrailCounts Counts,
    GuardrailSandbox Sandbox,
    IReadOnlyList<string> AllowHosts,
    IReadOnlyList<string> Loosened,
    IReadOnlyList<string> Notes,
    IReadOnlyList<GuardrailResult> Results,
    GuardrailRun? Run = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public JsonNode ToJson() => JsonSerializer.SerializeToNode(this, Json)!;

    public static GuardrailReport? FromJson(string json) => JsonSerializer.Deserialize<GuardrailReport>(json, Json);

    /// <summary>A fixed-width text table for the CLI.</summary>
    public string ToText()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"guardrail check {Id}: {(Subject.Synthetic ? "global policy" : $"{Subject.Team}/{Subject.Member}")} ({Subject.Cli}) on {Os}, sandbox {Sandbox.Backend}\n");
        if (Run is not null) sb.Append($"  launched by the sandbox launcher: run {Run.RunKey} ({Run.Backend}), ended {Run.End}{(Run.Reason is null ? "" : ": " + Run.Reason)}\n");
        if (Sandbox.Refusal is not null) sb.Append($"the sandbox REFUSED the request: {Sandbox.Refusal}\n");
        foreach (var r in Results)
        {
            var mark = r.Expected is null ? " " : r.AsExpected ? " " : "!";
            sb.Append($"{mark} {r.Verdict,-8} {r.Id,-40} {r.Evidence}\n");
        }
        sb.Append($"{(Passed ? "PASSED" : "FAILED")}: {Counts.Blocked} blocked, {Counts.Allowed} allowed, {Counts.Unexpected} unexpected, {Counts.Skipped} skipped ({DurationMs} ms)\n");
        foreach (var n in Notes) sb.Append($"note: {n}\n");
        return sb.ToString();
    }
}
