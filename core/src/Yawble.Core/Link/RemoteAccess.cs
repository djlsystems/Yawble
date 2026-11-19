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
        // B086: a card's or a workflow's timeline (the cloud's detail dialog).
        "timeline.get",
        "ops.backups",
        // Network asks: listed and answered like the other policy changes (policy. is a screen prefix above).
        "network.",
        // Host tool asks: listed and answered Not now like the network asks; Install is the window's alone (below).
        // Check again only looks for the program again (it runs no installer): a remote viewer may use it after installing by hand.
        "tools.catalog", "tools.asks", "tools.notNow", "tools.checkAgain",
        // Whether git is installed (the banner's text); installing it is git.install, refused below.
        "git.check",
        // The Logs dialog, read only (pruning and retention are refused below).
        "logs.",
        // Whether an update is available (installing it is the window's alone, below).
        "updates.status",
        // The read-only code browser (members' clones), unless the person turned it off (Settings > Cloud Link).
        "code.",
        // The cloud org dashboard (B048): this computer's summary, and its live changes while a viewer watches.
        "org.",
    ];

    /// <summary>A viewer who may change things: an Owner or a workgroup Member (the site's "full"; also a site that does not say).</summary>
    public const string Full = "full";

    /// <summary>A viewer who may only look: a workgroup Visitor (the site's "read").</summary>
    public const string Read = "read";

    /// <summary>What a read-only viewer reads when a call would change something.</summary>
    public const string ReadOnlyRefusal = "You have read-only access to this computer (Visitor). Ask a workgroup Member or an Owner for more.";

    /// <summary>A call's access as the site sent it (call["access"]): only "read" is read-only; missing or anything else is full (an older site).</summary>
    public static string AccessOf(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var s) && s == Read ? Read : Full;

    public enum Kind
    {
        /// <summary>Changes nothing: a read-only viewer may call it.</summary>
        Reads,
        /// <summary>Changes something: refused for a read-only viewer.</summary>
        Changes,
        /// <summary>Reads a setting, or sets it when the call carries its value: "on" (teams.push, teams.syncToBase), "days" (teams.workflowExpiry).</summary>
        ReadsUnlessOn,
    }

    /// <summary>
    /// Every method on the allow list above, read or change, for a read-only viewer (a Visitor). A method missing here
    /// is a change, so a new method is refused for read-only viewers until it is listed as a read. Terminals (attach,
    /// typing, resize, opening and stopping, host and agent) are changes: a read-only terminal view is not offered.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, Kind> Kinds = Table(
        reads:
        [
            "app.overview", "app.version", "monitor.messages", "monitor.steps", "monitor.codeEvents",
            "teams.list", "teams.agents", "teams.quiet", "teams.env", "teams.approvals", "teams.repos",
            // Settings > Teams and Settings > Concierge as saved (saving them and Generate are changes).
            "teams.defaults", "concierge.defaults",
            "docs.list", "docs.read", "docs.folder",
            "members.get",
            "bus.decisions", "bus.workflows",
            "policy.getGlobal", "policy.getMember", "policy.previewLoosen", "policy.teamHosts", "policy.checkHosts",
            "credentials.status",
            "terminal.banner",
            "skills.list",
            "triggers.list", "triggers.preview",
            "git.status", "git.check", "git.tidyPlan",
            // B075: the web address of a commit, branch, file, compare or pull request (reads only).
            "git.webLink",
            // B072, the commit view: a commit and its file list; a file's diff only while members' code is shown (below).
            "git.commitShow", "git.commitDiff",
            "core.getInfo", "sandbox.getAvailableBackends", "sandbox.getPlatformSupport", "scheduler.getState",
            "guardrail.get",
            "agents.detect", "agents.testSessions", "agents.commandLine", "agents.defaultModels", "agents.models", "agents.resolveModel",
            "agents.runRecords", "agents.versionStatus",
            "runs.list",
            // concierge.read only clears the chat's unread marker: opening a chat is a read.
            "concierge.list", "concierge.get", "concierge.read",
            "board.list", "board.card", "backlog.list", "backlog.item",
            // B086: a card's or a workflow's timeline (the detail dialog); a Visitor may view it.
            "timeline.get",
            "integrations.list", "integrations.asks", "integrations.calls", "integrations.checklist", "integrations.connectionAttention",
            "integrations.connections", "integrations.discoverStatus", "integrations.mcpList", "integrations.apiList", "integrations.watches",
            "integrations.taskRuns", "integrations.mcpParse",
            "tasks.list", "tasks.asks", "tasks.runs",
            "ops.backups",
            "network.asks",
            "tools.catalog", "tools.asks",
            "logs.query", "logs.sources", "logs.values", "logs.who", "logs.prunePreview",
            // Live logs (B048): watching new entries, and stopping, change nothing.
            "logs.tail", "logs.untail",
            // The org dashboard's summary (B048); watching and unwatching it only start and stop its pushes.
            "org.summary", "org.summary.watch", "org.summary.unwatch",
            "updates.status",
            "code.list", "code.open", "code.read", "code.close", "code.download",
        ],
        readsUnlessOn: ["teams.push", "teams.syncToBase", "teams.workflowExpiry"],
        changes:
        [
            "teams.create", "teams.pause", "teams.resume", "teams.archive", "teams.unarchive", "teams.delete", "teams.reset", "teams.setEnv",
            "teams.addApproval", "teams.removeApproval", "teams.addRepo", "teams.removeRepo", "teams.setRepo",
            "teams.setDefaults", "teams.generateNames", "concierge.setDefaults",
            "docs.write", "docs.upload", "docs.mkdir", "docs.rename", "docs.delete",
            "members.add", "members.remove", "members.update",
            "bus.tellManager", "bus.answer", "bus.closeWorkflow", "bus.stopWorkflow", "bus.nudgeWorkflow",
            "bus.workflowExtend",
            "policy.saveGlobal", "policy.saveOverride", "policy.saveLoosen", "policy.removeTeamHost",
            "credentials.setClaudeSetupToken", "credentials.setAnthropicApiKey", "credentials.setXaiApiKey", "credentials.remove", "credentials.setMode",
            "hostTerminal.start",
            "terminal.attach", "terminal.input", "terminal.resize", "terminal.openInteractive", "terminal.stop",
            "skills.save", "skills.delete",
            "triggers.save", "triggers.delete", "triggers.runNow",
            "git.fetch", "git.bringCurrent", "git.merge", "git.sync", "git.askTeam", "git.deleteBranches", "git.branchRealign", "git.install",
            "guardrail.start", "guardrail.stop",
            "agents.test", "agents.stopTest", "agents.setDefaultModel", "agents.checkForUpdates", "agents.install",
            "runs.stop",
            "concierge.send", "concierge.stop", "concierge.rename", "concierge.delete", "concierge.setTeam", "concierge.unwatch", "concierge.setWatch",
            "board.clearDone", "board.restore", "board.move", "board.edit", "board.comment",
            "backlog.add", "backlog.archive", "backlog.delete", "backlog.dispatch", "backlog.dispatchNew", "backlog.edit", "backlog.move",
            "backlog.position", "backlog.restore",
            "integrations.apiDeleteRule", "integrations.apiEnable", "integrations.apiRemove", "integrations.apiSave", "integrations.approve",
            "integrations.decline", "integrations.connectionRemove", "integrations.connectionSave", "integrations.connectionTest",
            "integrations.deleteRule", "integrations.deleteWatch", "integrations.detect", "integrations.discover", "integrations.discoverCancel",
            "integrations.enable", "integrations.mcpCheck", "integrations.mcpDeleteRule", "integrations.mcpEnable", "integrations.mcpImport",
            "integrations.mcpImportScan", "integrations.mcpRemove", "integrations.mcpSave", "integrations.mcpSaveRule", "integrations.mcpSignIn",
            "integrations.oauthDiscover", "integrations.oauthSignIn", "integrations.openLogin", "integrations.remove", "integrations.runWatch",
            "integrations.saveChecklist", "integrations.saveRule", "integrations.saveWatch", "integrations.websiteSignIn",
            "integrations.websiteSignInDone",
            // A preview evaluates the definition against the checkout: part of editing a task, which a reader cannot save.
            "tasks.preview", "tasks.approve", "tasks.cancel", "tasks.decline", "tasks.delete", "tasks.revoke", "tasks.runNow", "tasks.save",
            "network.answer",
            "tools.notNow", "tools.checkAgain",
            "logs.prune", "logs.setRetention",
        ]);

    private static Dictionary<string, Kind> Table(string[] reads, string[] readsUnlessOn, string[] changes)
    {
        var table = new Dictionary<string, Kind>(StringComparer.Ordinal);
        foreach (var m in reads) table.Add(m, Kind.Reads);
        foreach (var m in readsUnlessOn) table.Add(m, Kind.ReadsUnlessOn);
        foreach (var m in changes) table.Add(m, Kind.Changes);
        return table;
    }

    /// <summary>Whether a method's name is on the allow list (before the per-call checks of <see cref="Refusal"/>).</summary>
    internal static bool OnAllowList(string method) =>
        Allowed.Any(p => p.EndsWith('.') ? method.StartsWith(p, StringComparison.Ordinal) : method == p);

    /// <summary>Whether a call only reads, by the table: a method missing from it changes something.</summary>
    public static bool Reads(string method, JsonObject? parameters) =>
        Kinds.TryGetValue(method, out var kind) && (kind == Kind.Reads || kind == Kind.ReadsUnlessOn && parameters?[SetKey(method)] is null);

    /// <summary>The field a get-or-set call carries when it sets its setting (B085 teams.workflowExpiry: "days"; the rest "on").</summary>
    public static string SetKey(string method) => method == "teams.workflowExpiry" ? "days" : "on";

    /// <summary>What a cloud viewer reads when members' code is not shown (Settings > Cloud Link) and a call would show it.</summary>
    public const string CodeHidden = "members' code is not shown to cloud viewers; the person can turn it on in the app on the computer (Settings > Cloud Link)";

    /// <summary>
    /// A relayed call's answer as a cloud viewer may see it. B072: with members' code not shown, git.commitShow keeps the
    /// commit and its file list and says codeShown = false (its diffs, git.commitDiff, are refused above).
    /// </summary>
    public static void ShapeResult(string method, JsonNode? result, bool showCode)
    {
        if (method == "git.commitShow" && !showCode && result is JsonObject commit && commit.ContainsKey("codeShown")) commit["codeShown"] = false;
    }

    /// <summary>Null when the call is allowed for a viewer with this access; otherwise the reason, as the remote person reads it.</summary>
    /// <param name="access"><see cref="Full"/> or <see cref="Read"/> (<see cref="AccessOf"/>): a read-only viewer's change call gets
    /// <see cref="ReadOnlyRefusal"/>, whatever else would refuse it; a read goes on to the checks every viewer has.</param>
    public static string? Refusal(string method, JsonObject? parameters, bool allowHostTerminals, string access, Func<string, bool>? isIntegration = null,
        Func<string, bool>? isRemoteMcp = null, bool showCode = true) =>
        access == Read && !Reads(method, parameters)
            ? ReadOnlyRefusal
            : Refusal(method, parameters, allowHostTerminals, isIntegration, isRemoteMcp, showCode);

    /// <summary>Null when the call is allowed; otherwise the reason, as the remote person reads it.</summary>
    /// <param name="showCode">"Show members' code to cloud viewers" (Settings > Cloud Link, on by default): off refuses every code. call.</param>
    /// <param name="isIntegration">Whether a CLI name is already an integration detected on this computer (a stored path to a program): remote detect / enable / discover / saveRule of it is allowed.</param>
    public static string? Refusal(string method, JsonObject? parameters, bool allowHostTerminals, Func<string, bool>? isIntegration = null, Func<string, bool>? isRemoteMcp = null,
        bool showCode = true)
    {
        if ((method.StartsWith("code.", StringComparison.Ordinal) || method == "git.commitDiff") && !showCode)
            return CodeHidden;
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
        if (!OnAllowList(method))
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
