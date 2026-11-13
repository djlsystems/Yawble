using Yawble.Core.Bus;
using Yawble.Core.Policy;

namespace Yawble.Core.Agents;

/// <summary>
/// Whether each agent CLI preset can run a member on this computer now, from the live detection (installed, signed
/// in on the host, and a sign-in the sandbox can use), for the Manager's hiring tool.
/// </summary>
public static class AgentReadiness
{
    public static async Task<IReadOnlyList<AgentChoice>> ChoicesAsync(ICliDetector clis, CancellationToken ct)
    {
        var detected = await clis.DetectAllAsync(ct);
        var choices = AgentClis.Ids.Select(id => Of(id, detected.FirstOrDefault(d => string.Equals(d.CliId, id, StringComparison.OrdinalIgnoreCase)))).ToList();
        // Copilot's token can be present yet rejected by GitHub: check it, so the Manager does not hire a CLI that fails.
        var copilot = choices.FindIndex(c => c.Cli == AgentClis.Copilot && c.Ready);
        if (copilot >= 0 && detected.FirstOrDefault(d => d.CliId == AgentClis.Copilot)?.SignIn?.Files.FirstOrDefault() is { } entry
            && SignInCatalog.Store.Read(entry.Path) is { Length: > 0 } token && await Credentials.CopilotSignInCheck.IsRejectedAsync(token, ct))
            choices[copilot] = new AgentChoice(AgentClis.Copilot, false, Credentials.CopilotSignInCheck.Expired);
        return choices;
    }

    public static AgentChoice Of(string id, DetectedCli? d)
    {
        if (d is null) return new AgentChoice(id, false, "this app does not support it on this computer");
        if (!d.Found) return new AgentChoice(id, false, "not installed on this computer");
        // Antigravity (agy): its own command tool cannot run in an app container (exebox calls CreateDesktop), so
        // the preset denies it and gives it run_shell (yawble-bridge --shell) instead.
        return d.SignIn switch
        {
            null => new AgentChoice(id, false, "the app does not know how to use its sign-in"),
            { Status: SignInStatus.SignedIn } => new AgentChoice(id, true, $"installed{(d.Version is { } v ? " (" + v + ")" : "")} and signed in"),
            { Status: SignInStatus.Pending } s => new AgentChoice(id, false, s.Detail),
            var s => new AgentChoice(id, false, s.Detail),
        };
    }
}
