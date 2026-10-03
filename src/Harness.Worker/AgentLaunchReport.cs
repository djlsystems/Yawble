namespace Harness.Host;

/// <summary>
/// What the launch check says about one preset, as <c>GET /api/agents/auth</c> carries it in
/// <c>launch</c>. <see cref="Result"/> is <see cref="Ok"/>, <see cref="Failed"/>,
/// <see cref="NotChecked"/> or <see cref="Updating"/> (the platform's update holds the CLI); <see cref="ExitCode"/> and <see cref="StderrTail"/> (redacted, see
/// <see cref="AgentCrash.StderrTail"/>) are set when a process ran; <see cref="Detail"/> says
/// what was run, or why nothing was.
/// </summary>
public sealed record AgentLaunchReport(string Result, int? ExitCode, string? StderrTail, string? Detail)
{
    public const string Ok = "ok";
    public const string Failed = "failed";
    public const string NotChecked = "not checked";
    public const string Updating = "updating";

    public static AgentLaunchReport Unchecked(string detail) => new(NotChecked, null, null, detail);

    /// <summary>Not started because the platform's update holds the CLI: <see cref="UpdatingOn.Text"/>.</summary>
    public static AgentLaunchReport Held(AgentUpdateHold hold, bool onThisMachine = false) =>
        new(Updating, null, null, UpdatingOn.Text(hold, onThisMachine));
}
