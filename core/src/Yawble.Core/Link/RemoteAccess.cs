using System.Text.Json.Nodes;

namespace Yawble.Core.Link;

/// <summary>
/// What a person viewing this instance through the Cloud Link may call. The same screens as the desktop window,
/// minus what only the person at this computer should do: the link's own settings, typing secrets into the app's
/// credential store, and (unless the person at this computer allows it) the host terminals, which are a shell on this
/// computer outside any sandbox.
/// </summary>
public static class RemoteAccess
{
    private static readonly string[] Allowed =
    [
        .. Ui.UiRpc.Prefixes,
        "core.getInfo", "sandbox.getAvailableBackends", "sandbox.getPlatformSupport", "scheduler.getState",
        "guardrail.", "agents.", "runs.", "concierge.", "board.", "backlog.", "integrations.", "tasks.",
        "ops.backups",
        // Network asks: listed and answered like the other policy changes (policy. is a screen prefix above).
        "network.",
        // Host tool asks: listed and answered Not now like the network asks; Install is the window's alone (below).
        "tools.catalog", "tools.asks", "tools.notNow",
        // Whether git is installed (the banner's text); installing it is git.install, refused below.
        "git.check",
        // The Logs dialog, read only (pruning and retention are refused below).
        "logs.",
        // Whether an update is available (installing it is the window's alone, below).
        "updates.status",
        // The read-only code browser (members' clones), unless the person turned it off (Settings > Cloud Link).
        "code.",
    ];

    /// <summary>Null when the call is allowed; otherwise the reason, as the remote person reads it.</summary>
    /// <param name="showCode">"Show members' code to cloud viewers" (Settings > Cloud Link, on by default): off refuses every code. call.</param>
    /// <param name="isIntegration">Whether a CLI name is already an integration detected on this computer (a stored path to a program): remote detect / enable / discover / saveRule of it is allowed.</param>
    public static string? Refusal(string method, JsonObject? parameters, bool allowHostTerminals, Func<string, bool>? isIntegration = null, Func<string, bool>? isRemoteMcp = null,
        bool showCode = true)
    {
        if (method.StartsWith("code.", StringComparison.Ordinal) && !showCode)
            return "members' code is not shown to cloud viewers; the person can turn it on in the app on the computer (Settings > Cloud Link)";
        // yawble-companion is local only: never paired, approved, listed or revoked from the Cloud Link.
        if (method.StartsWith("companion.", StringComparison.Ordinal))
            return "yawble-companion is paired, approved and revoked on the computer itself";
        if (method.StartsWith("link.", StringComparison.Ordinal))
            return "the Cloud Link's settings can only be changed on the computer itself";
        if (method.StartsWith("credentials.", StringComparison.Ordinal) && method != "credentials.status" && method != "credentials.setMode")
            return "credentials can only be entered or removed on the computer itself";
        // An agent's Terminal / Test is a session on this computer the person watches and types into (AgentTestSessions).
        if (method is "agents.test" or "agents.stopTest" or "agents.testSessions")
            return "an agent's terminal is opened in the app on the computer itself";
        // ...and its session (agent-test:<cli>) is not attached, typed into, resized or stopped from the Cloud Link,
        // host terminals allowed or not: it was started in the window, so it stays there.
        if (method.StartsWith("terminal.", StringComparison.Ordinal)
            && (parameters?["channel"] as JsonValue)?.TryGetValue<string>(out var agentChannel) == true && IsWindowOnlyChannel(agentChannel))
            return "an agent's terminal is used in the app on the computer itself";
        if (method == "runs.stop" && (parameters?["team"] as JsonValue)?.TryGetValue<string>(out var stopTeam) == true && stopTeam == Agents.AgentTestSessions.Team)
            return "an agent's terminal is stopped in the app on the computer itself";
        if (method.StartsWith("updates.", StringComparison.Ordinal) && method != "updates.status")
            return Updates.UpdateRpc.LocalOnly;
        if (Logs.LogRpc.ChangesLogs(method))
            return Logs.LogRpc.LocalOnly;
        if (method == "git.install")
            return Teams.GitRpc.InstallRefusal;
        if (method == "tools.install")
            return Integrations.ToolAskService.InstallRefusal;
        if (method is "ops.backupNow" or "ops.restore")
            return "backups can only be made or restored on the computer itself";
        if (!Allowed.Any(p => p.EndsWith('.') ? method.StartsWith(p, StringComparison.Ordinal) : method == p))
            return $"'{method}' is not available through the Cloud Link";
        if (method == "integrations.openLogin")
            return "opening a terminal happens on the computer itself: sign in there";
        // Connections hold sign-ins, and setting up an API makes this computer fetch URLs: neither is for a remote viewer, host terminals or not.
        if (method.StartsWith("integrations.connection", StringComparison.Ordinal))
            return "connections can only be seen or changed on the computer itself";
        // OAuth sign-ins (and finding a provider's addresses, which makes this computer fetch URLs) are for the person at the computer
        if (method is "integrations.websiteSignIn" or "integrations.websiteSignInDone" || method.StartsWith("integrations.oauth", StringComparison.Ordinal))
            return "sign-ins happen on the computer itself";
        if (method is "integrations.apiSave" or "integrations.apiRemove"
            || method == "integrations.discover" && (parameters?["kind"] as JsonValue)?.TryGetValue<string>(out var kind) == true && kind == "api")
            return "setting up an API happens on the computer itself";
        // Remote MCP servers: sign-ins, imports (they read config files on this computer) and secrets are for the person at the computer, host terminals or not.
        if (method is "integrations.mcpImportScan" or "integrations.mcpImport" or "integrations.mcpSignIn")
            return "sign-ins and imports happen on the computer itself";
        // (a url or transport key at all, or saving over an existing remote server, could repoint a server that keeps its token)
        if (method == "integrations.mcpSave" && parameters is not null && (parameters.ContainsKey("secrets") || parameters.ContainsKey("connectionId") || parameters.ContainsKey("auth")
            || parameters.ContainsKey("transport") || parameters.ContainsKey("url")
            || (parameters["name"] as JsonValue)?.TryGetValue<string>(out var saveName) == true && isRemoteMcp?.Invoke(saveName.Trim()) == true))
            return "sign-ins and imports happen on the computer itself: add or change a remote MCP server there";
        if (method == "integrations.mcpRemove" && parameters is not null && parameters.ContainsKey("removeConnection"))
            return "connections can only be changed on the computer itself";
        if (method == "integrations.mcpCheck" && (parameters?["name"] as JsonValue)?.TryGetValue<string>(out var checkName) == true && isRemoteMcp?.Invoke(checkName.Trim()) == true)
            return "sign-ins and imports happen on the computer itself: a remote MCP server is read there";
        if (method == "integrations.discover" && IsMcpKind(parameters?["kind"]) && (parameters?["name"] as JsonValue)?.TryGetValue<string>(out var mcpName) == true
            && isRemoteMcp?.Invoke(mcpName.Trim()) == true)
            return "sign-ins and imports happen on the computer itself: a remote MCP server is read there";
        if (!allowHostTerminals)
        {
            // Defining a program the host then runs is equivalent to a host shell.
            if (method == "integrations.mcpSave")
                return "adding an MCP server runs a program on this computer; do it on the computer, or allow host terminals for the Cloud Link";
            // Detecting, enabling or discovering runs the named program on this computer, and saving a rule adds it as an
            // integration (which later calls run): only a CLI the app knows, or one already detected here. Detect and enable
            // never read 'kind'; discover reads it as its handler does (only "mcp" is a server, anything else a CLI).
            // (No name at all: the handler refuses it, nothing runs.)
            if ((method is "integrations.detect" or "integrations.enable" or "integrations.saveRule"
                || method == "integrations.discover" && !IsMcpKind(parameters?["kind"])) && parameters?["name"] is not null)
            {
                var cli = (parameters?["name"] as JsonValue)?.TryGetValue<string>(out var n) == true ? n.Trim() : "";
                if (cli.Length == 0 || Integrations.CliCatalog.Of(cli) is null && isIntegration?.Invoke(cli) != true)
                    return $"adding '{cli}' runs a program on this computer that the app does not know; add it on the computer, or allow host terminals for the Cloud Link";
            }
            if (method == "tasks.save")
                return "defining a host task runs a program on this computer; do it on the computer, or allow host terminals for the Cloud Link";
            if (method.StartsWith("hostTerminal.", StringComparison.Ordinal))
                return "host terminals are off for the Cloud Link; allow them in the app on this computer (Cloud Link)";
            if (method.StartsWith("terminal.", StringComparison.Ordinal)
                && (parameters?["channel"] as JsonValue)?.TryGetValue<string>(out var channel) == true
                && channel.StartsWith("host:", StringComparison.Ordinal))
                return "host terminals are off for the Cloud Link; allow them in the app on this computer (Cloud Link)";
        }
        return null;
    }

    /// <summary>A terminal channel only the window may use, never the Cloud Link: an agent's Terminal / Test session (agent-test:&lt;cli&gt;).</summary>
    public static bool IsWindowOnlyChannel(string channel) => channel.StartsWith(Agents.AgentTestSessions.ChannelPrefix, StringComparison.Ordinal);

    /// <summary>Whether a checklist call's 'kind' names an MCP server, read as IntegrationService.IsMcp reads it: only the text "mcp".</summary>
    private static bool IsMcpKind(JsonNode? kind) => kind is JsonValue v && v.TryGetValue<string>(out var s) && s == "mcp";
}
