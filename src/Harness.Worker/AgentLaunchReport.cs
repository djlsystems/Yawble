namespace Harness.Host;

/// <summary>
/// What the launch check says about one preset, as <c>GET /api/agents/auth</c> carries it in
/// <c>launch</c>. <see cref="Result"/> is <see cref="Ok"/>, <see cref="Failed"/> or
/// <see cref="NotChecked"/>; <see cref="ExitCode"/> and <see cref="StderrTail"/> (redacted, see
/// <see cref="AgentCrash.StderrTail"/>) are set when a process ran; <see cref="Detail"/> says
/// what was run, or why nothing was.
/// </summary>
public sealed record AgentLaunchReport(string Result, int? ExitCode, string? StderrTail, string? Detail)
{
    public const string Ok = "ok";
    public const string Failed = "failed";
    public const string NotChecked = "not checked";

    public static AgentLaunchReport Unchecked(string detail) => new(NotChecked, null, null, detail);
}
