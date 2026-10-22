using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Harness.Host;

public sealed partial class PlatformMcpTools
{
    [McpServerTool(Name = "preview"), Description(
        "For the Concierge, when the person asks to try a team's web app. action env writes the team's "
        + "environment to a file in your own folder, mode 0600, and answers its path and the variable "
        + "names, never a value: load it in the shell that starts the app (. <path>) and never print it. "
        + "It is removed when your session ends. Refused to a Manager and a member. The ports are in "
        + "HARNESS_PREVIEW_PORTS (the person's link, on HARNESS_PREVIEW_URL) and HARNESS_PREVIEW_APP_PORTS "
        + "(what the app binds, in the same order). This is harness preview. Do not request /api yourself.")]
    public async Task<string> Preview(
        [Description("env.")] string action,
        [Description("Team id. Required.")] string? team = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(action?.Trim(), "env", StringComparison.OrdinalIgnoreCase))
        {
            return "Refused: the preview tool takes action env.";
        }

        if (string.IsNullOrWhiteSpace(team)) return "Refused: name a team. A Concierge has no default team.";

        return await SendAsync(
            HttpMethod.Post,
            $"/api/teams/{Uri.EscapeDataString(team.Trim())}/preview-env",
            null,
            cancellationToken);
    }
}
