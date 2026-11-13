using System.Text.Json.Nodes;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using PolicyMember = Yawble.Core.Policy.MemberRef;

namespace Yawble.Core.Launch;

/// <summary>
/// A launch that runs a probe instead of the member's CLI (the guardrail check). Everything else is the
/// member's real launch: the layout, the effective policy, the per-run gateway and run token, the compiled request,
/// the folders and seeds, the backend, the supervisor, the post-run checks and the run record. The probe changes only
/// the request's command line and a tighter process.timeout (<see cref="Rewrite"/>), and receives stdout and stderr
/// separately instead of the terminal stream.
/// </summary>
public interface ILaunchProbe
{
    /// <summary>Folders the launcher must deny as if they were members (decoys the probe plants canaries in).</summary>
    IReadOnlyList<PolicyMember> ExtraMembers { get; }

    /// <summary>
    /// A subject that is not in the members table: it gets the global policy alone (no override, no loosen). Null for
    /// a real member, which is looked up as for any run.
    /// </summary>
    LaunchMember? Synthetic { get; }

    /// <summary>The supervisor's wall-clock bound for the probe (the member's own limit, if lower, still applies).</summary>
    TimeSpan MaxWall { get; }

    /// <summary>
    /// Called after the request is compiled and the folders prepared, before the spawn. Returns the request to submit:
    /// the compiled one with only the command line and process.timeout changed. Throw
    /// <see cref="LaunchProbeRefusedException"/> to end the launch as start_failed with that reason.
    /// </summary>
    JsonObject Rewrite(LaunchProbeContext context);
}

/// <summary>What the probe sees of the launch it rides on.</summary>
public sealed record LaunchProbeContext(
    string RunKey,
    long RunNumber,
    RunLayout Layout,
    EffectivePolicy Policy,
    CredentialMode Credential,
    bool CredentialConfigured,
    int GatewayPort,
    JsonObject Request);

/// <summary>The probe's own output, kept apart (stdout is its result lines).</summary>
public sealed record LaunchProbeOutput(long RunNumber, CredentialMode Credential, bool CredentialConfigured, string Stdout, string Stderr, JsonObject? Request);

public sealed class LaunchProbeRefusedException(string message) : Exception(message);
