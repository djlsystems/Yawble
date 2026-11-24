using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;

namespace Yawble.Core.Gateway;

/// <summary>
/// The MCP tools for ONE run, served at bus.yawble.internal/mcp by that run's gateway. The caller's
/// identity — member, run, causation — comes from the run the gateway was started for, never from arguments: a
/// tool argument named "member" in tell is the recipient, nothing else can be addressed. The bus enforces the
/// rules (members tell only the Manager, workflow ownership, depth limit) and refusals come back as tool errors.
/// MCP over Streamable HTTP, JSON responses only (no server-initiated stream).
/// </summary>
public sealed class McpBusServer(RunContext run) : IBusEndpoint
{
    public static readonly string[] SupportedProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    public static readonly IReadOnlyList<string> ToolNames =
        ["tell", "progress", "handback", "blocked", "needs_decision", "workflow_complete", "status", "hiring", "hire", "board", "backlog", "skills_get", "skills_list", "wake_me", "workflow_show", "workflow_digest", "lease", "repo", "request_host", "request_tool",
         "request_folder", "team_retros_write"];

    public async Task<BusReply> HandleAsync(byte[] body, CancellationToken ct)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return new BusReply(400, ErrorResponse(null, -32700, "parse error"));
        }
        if (parsed is JsonArray batch)
        {
            var replies = new JsonArray();
            foreach (var item in batch)
            {
                if (item is JsonObject o && await DispatchAsync(o, ct) is { } reply) replies.Add(reply);
            }
            return replies.Count == 0 ? new BusReply(202, null) : new BusReply(200, replies.ToJsonString());
        }
        if (parsed is not JsonObject message) return new BusReply(400, ErrorResponse(null, -32600, "invalid request"));
        var single = await DispatchAsync(message, ct);
        return single is null ? new BusReply(202, null) : new BusReply(200, single.ToJsonString());
    }

    /// <summary>One JSON-RPC message; null for a notification (no id).</summary>
    private async Task<JsonNode?> DispatchAsync(JsonObject message, CancellationToken ct)
    {
        var id = message["id"]?.DeepClone();
        var method = message["method"]?.GetValue<string>();
        if (id is null) return null; // notifications/initialized, notifications/cancelled: nothing to answer
        JsonNode result;
        try
        {
            result = method switch
            {
                "initialize" => Initialize(message["params"] as JsonObject),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = AllTools() },
                "tools/call" => await CallAsync(message["params"] as JsonObject, ct),
                _ => throw new RpcError(-32601, $"method not found: {method}"),
            };
        }
        catch (RpcError e)
        {
            return JsonNode.Parse(ErrorResponse(id, e.Code, e.Message));
        }
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
    }

    private static JsonObject Initialize(JsonObject? p)
    {
        var requested = p?["protocolVersion"]?.GetValue<string>();
        var version = requested is not null && SupportedProtocolVersions.Contains(requested) ? requested : SupportedProtocolVersions[0];
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "yawble-bus", ["version"] = CoreInfo.Version },
            ["instructions"] = "The team bus. Your identity, run and workflow are set by the platform; tools act only for you.",
        };
    }

    private async Task<JsonNode> CallAsync(JsonObject? p, CancellationToken ct)
    {
        var name = p?["name"]?.GetValue<string>() ?? throw new RpcError(-32602, "tools/call needs a name");
        var args = p["arguments"] as JsonObject ?? [];
        try
        {
            byte[]? png = null;
            var text = name switch
            {
                "tell" => Sent(await run.TellAsync(Required(args, "member"), Required(args, "instruction"), OptionalLong(args, "card"), ct)),
                "board" => await (run.Engine.Board ?? throw new BusRefusal("unavailable", "the board is not running")).BoardToolAsync(run, args, ct),
                "backlog" => (run.Engine.Board ?? throw new BusRefusal("unavailable", "the board is not running")).BacklogTool(run, args),
                "progress" => Sent(await run.ProgressAsync(Required(args, "status"), ct)),
                "handback" => Sent(await run.HandbackAsync(Required(args, "delivered"), ct)),
                "blocked" => Sent(await run.BlockedAsync(Required(args, "reason"), OptionalInt(args, "item"), ct)),
                "needs_decision" => Sent(await run.NeedsDecisionAsync(
                    DecisionAsk.Parse(Optional(args, "question"), Optional(args, "summary"), args["options"], Optional(args, "details"),
                        OptionalLong(args, "card"), Optional(args, "kind"), Optional(args, "reason")), ct)),
                "workflow_complete" => Sent(await run.WorkflowCompleteAsync(Required(args, "delivered"), ct)),
                "status" => JsonSerializer.Serialize(await run.StatusAsync(ct), RpcJson),
                "hiring" => JsonSerializer.Serialize(await run.HiringAsync(ct), RpcJson),
                "hire" => Hired(await run.HireAsync(Required(args, "name"), Optional(args, "cli"), Optional(args, "job"), ct)),
                "skills_get" => run.Skill(Required(args, "name")).Body,
                "wake_me" => Woken(await run.WakeMeAsync(OptionalInt(args, "minutes") ?? throw new RpcError(-32602, "wake_me needs minutes"), Required(args, "note"), ct)),
                "skills_list" => string.Join(Environment.NewLine, run.Skills().Select(x => $"{x.Name} - {x.Description}")),
                "lease" => await run.LeaseAsync(Optional(args, "action") ?? "status", Optional(args, "what"), ct),
                "repo" => await RepoText(run, ct),
                "request_host" => (run.Engine.NetworkAsks ?? throw new BusRefusal("unavailable", "network requests are not available here: hand back blocked on the host instead"))
                    .RequestFromAgent(run.Member, Required(args, "host"), Required(args, "reason"), run.Correlation, run.Causation),
                "request_tool" => await (run.Engine.ToolAsks ?? throw new BusRefusal("unavailable", "tool requests are not available here: hand back blocked on the tool instead"))
                    .RequestFromAgentAsync(run.Member, Required(args, "tool"), Required(args, "reason"), run.Correlation, run.Causation, ct),
                "request_folder" => await (run.Engine.FolderAsks ?? throw new BusRefusal("unavailable", "folder requests are not available here: hand back blocked on the folder instead"))
                    .RequestFromAgentAsync(run.Member, Required(args, "path"), Required(args, "access"), Required(args, "reason"), Optional(args, "member"),
                        run.Correlation, run.Causation, ct),
                "team_retros_write" => await run.TeamRetrosWriteAsync(Required(args, "text"), ct),
                "workflow_show" => WorkflowText(await run.WorkflowShowAsync(OptionalLong(args, "workflow") ?? run.Correlation, ct)),
                "workflow_digest" => await run.Engine.WorkflowDigestAsync(run, OptionalLong(args, "workflow"), ct),
                _ when run.Engine.Integrations?.IsTool(name) == true => await CallIntegrationAsync(name, args, img => png = img, ct),
                _ => throw new RpcError(-32602, $"unknown tool: {name}"),
            };
            return ToolResult(WithLeaseNotice(name, text), isError: false, png);
        }
        catch (BusRefusal refusal)
        {
            return ToolResult(WithLeaseNotice(name, $"refused ({refusal.Code}): {refusal.Message}"), isError: true);
        }
        catch (ArgumentException e)
        {
            return ToolResult(e.Message, isError: true);
        }
    }

    /// <summary>
    /// A run given the heavy-work lease while it waited in the queue is told in the reply to its next tool call (a run
    /// cannot be sent a bus message mid-run). The lease tool's own reply already says it.
    /// </summary>
    private string WithLeaseNotice(string tool, string text)
    {
        if (tool == "lease") return text;
        return run.Engine.TakeLeaseNotice(run.RunId) is { } notice ? $"[heavy-work lease] {notice}\n\n{text}" : text;
    }

    private static readonly JsonSerializerOptions RpcJson = new(JsonSerializerDefaults.Web);

    private static string Sent(Message m) => $"ok: message {m.Seq} in workflow {m.CorrelationId}";

    private static string Woken(DateTimeOffset due) =>
        $"ok: you will be woken at {due:yyyy-MM-dd HH:mm} UTC with your note. End your turn now; do not wait.";

    private static string Required(JsonObject args, string name)
    {
        var value = args[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"'{name}' is required");
        return value;
    }

    private static string? Optional(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static string Hired(MemberRow m) =>
        $"ok: hired {m.Name} ({m.Cli}) into team {m.Team}. Address it as '{m.Name}' every time (tell, its branch {Teams.TeamRepos.BranchFor(m.Team, m.Name)}). Its clone and branch are made at its first run. Give it work with tell.";

    private static async Task<string> RepoText(RunContext run, CancellationToken ct)
    {
        var repos = run.Engine.Repos ?? throw new BusRefusal("unavailable", "this team has no repository here");
        var st = await repos.GitStatusAsync(run.Member.TeamId, run.Member.Team, ct);
        var lines = new List<string>
        {
            $"Team repository ({(st.Managed ? "made by the app" : "the person's own")}), base branch {st.BaseBranch}{(st.Origin is null ? ", no origin" : $", origin {st.Origin}")}.",
            $"Team branch {st.TeamBranch}: {st.TeamAheadOfBase} commit(s) ahead of {st.BaseBranch}, {st.TeamBehindBase} behind.",
        };
        if (st.BaseAheadOfOrigin is { } a && st.BaseBehindOrigin is { } b) lines.Add($"{st.BaseBranch} against origin: {a} ahead, {b} behind.");
        foreach (var m in st.Members)
            lines.Add($"  {m.Branch} {m.Sha[..Math.Min(8, m.Sha.Length)]}: {m.AheadOfBase} ahead of {st.BaseBranch}; {(m.InTeamBranch ? "merged into the team branch" : "NOT merged into the team branch")}");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>A thread as plain lines an agent reads: "#seq time  source  type: the text".</summary>
    private static string WorkflowText(IReadOnlyList<Message> thread)
    {
        if (thread.Count == 0) return "No such workflow on your team.";
        var lines = thread.Select(m =>
        {
            string body;
            try
            {
                var o = JsonNode.Parse(m.Payload) as JsonObject;
                body = o is null ? m.Payload
                    : string.Join(" | ", new[] { "text", "status", "delivered", "reason", "question", "answer" }
                        .Select(k => o[k] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null).Where(s => s is not null))
                      is { Length: > 0 } joined ? joined : m.Payload;
            }
            catch (JsonException) { body = m.Payload; }
            if (body.Length > 1200) body = body[..1199] + "…";
            return $"#{m.Seq} {m.OccurredAt:yyyy-MM-dd HH:mm} {m.Source} {m.Type}: {body}";
        });
        return string.Join(Environment.NewLine, lines);
    }

    private static long? OptionalLong(JsonObject args, string name) =>
        args[name] is JsonValue v && (v.TryGetValue<long>(out var l) || (v.TryGetValue<string>(out var s) && long.TryParse(s, out l))) ? l : null;

    private static int? OptionalInt(JsonObject args, string name) =>
        args[name] is JsonValue v ? (v.TryGetValue<int>(out var i) ? i : v.TryGetValue<string>(out var s) && int.TryParse(s, out var j) ? j : null) : null;

    private async Task<string> CallIntegrationAsync(string name, JsonObject args, Action<byte[]?> setPng, CancellationToken ct)
    {
        var reply = await run.Engine.Integrations!.CallToolReplyAsync(run, name, args, ct);
        setPng(reply.Png);
        return reply.Text;
    }

    internal static JsonObject ToolResult(string text, bool isError, byte[]? png = null)
    {
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        if (png is { Length: > 0 }) content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" });
        return new JsonObject { ["content"] = content, ["isError"] = isError };
    }

    /// <summary>The platform's tools and the integrations enabled for the run's team.</summary>
    private JsonArray AllTools()
    {
        var tools = Tools();
        foreach (var t in run.Engine.Integrations?.ToolsFor(run.Member.TeamId) ?? []) tools.Add(t);
        return tools;
    }

    /// <summary>The bus's own tools as an agent sees them (tests read their wording).</summary>
    public static JsonArray ToolList() => Tools();

    private static JsonArray Tools() =>
    [
        Tool("tell", "Instruct a member of your team (the Manager), or, as a member, the Manager. Joins your current workflow.",
            ("member", "string", "Who to tell, by name.", true), ("instruction", "string", "The full instruction.", true),
            ("card", "integer", "The Manager: the planned board card this is for (board plan's id); the member takes it.", false)),
        Tool("board", "Your team's board: show the cards by lane, or one card in full (show with card: its description and trail - read it before working a card); the Manager plans cards (one per piece of work, unassigned, then tell a member with card), edits, moves them, or comments.",
            ("action", "string", "show (default), plan, edit, move or comment.", false),
            ("title", "string", "plan / edit: the card's title, a short line a person can read.", false),
            ("body", "string", "plan / edit: what the piece is, its files, and how it is checked.", false),
            ("item", "string", "plan: the backlog item it comes from, e.g. B007.", false),
            ("card", "integer", "show (one card in full) / move / comment / edit: the card's id.", false),
            ("lane", "string", "move: todo, in-progress, blocked or done.", false),
            ("note", "string", "move: why.", false),
            ("text", "string", "comment: what to say on the card.", false)),
        Tool("backlog", "The Manager: list your team's backlog items, read one dispatched to your team (the spec to cut into cards), or add a follow-up item for the person to review.",
            ("action", "string", "list (default), show or add.", false), ("item", "string", "show: the item, e.g. B007.", false),
            ("title", "string", "add: a short title.", false), ("body", "string", "add: what and why.", false)),
        Tool("lease", "The computer's heavy-work lease: take it (acquire) before the repository's full test suite, a full build, an image build or anything else heavy, " +
                      "and release it as soon as that command finishes. Never blocks: when busy you are queued in order and told when it is your turn.",
            ("action", "string", "acquire, release (also leaves the queue) or status (default).", false), ("what", "string", "acquire: the step, e.g. 'full test suite'.", false)),
        Tool("repo", "The team repository's branches: the team branch against the base, each member's branch (ahead of the base, merged into the team branch or not), and the origin if any."),
        Tool("progress", "Say what you are doing right now. Shown on your card; resets your idle clock.",
            ("status", "string", "One short line of current state.", true)),
        Tool("handback", "Hand finished work back to your Manager; wakes it in the same workflow.",
            ("delivered", "string", ReportText("What you finished: the summary names the branch and commit; then what changed, the tests " +
                "(what ran and passed, what could not run and why) and anything left open"), true)),
        Tool("blocked", "Stop without finishing, and say why. With item, blocks only that numbered item of your batch.",
            ("reason", "string", ReportText("Why you stopped: the summary says what is in the way; then what you tried and what would unblock it"), true), ("item", "integer", "Optional: the batch item number.", false)),
        NeedsDecisionTool(),
        Tool("workflow_complete", "Declare the workflow done (its owner, or the Manager). Refused while other work in it is running or pending, " +
            "and while a card's latest tester hand-back is a FAIL unless the person said yes to closing over it (needs_decision with card and kind).",
            ("delivered", "string", ReportText("What was delivered: the summary names the branch and commit; then each item delivered, the tests " +
                "and anything left open"), true)),
        Tool("status", "Your team: roster, who is running, who is queued for a run slot (with position), and each member's waiting instructions with why they wait (waitState: queued for a slot, waits-for-run with waitsFor, held for a paused team, pending)."),
        Tool("hiring", "The Manager: the agent CLIs you may hire, and whether each can run on this computer now."),
        Tool("hire", "The Manager: add a member to your team. It starts idle; give it work with tell.",
            ("name", "string", "The job word alone, one word: 'Developer', 'Tester'. The app adds a first name from the person's list; the answer gives " +
                "the id to address it by ('DeveloperPriya'). Only when a refusal asks for one: the job, a space, then a first name as the refusal says.", true),
            ("cli", "string", "Which agent CLI runs it (see hiring). Default: your own.", false),
            ("job", "string", "Its standing job on the team, e.g. 'Developer: writes the code' or 'Tester: builds and tests'.", false)),
        Tool("skills_get", "Load a skill: your role text (manager or member) or any skill skills_list offers you.",
            ("name", "string", "The skill's name, e.g. manager, member, team-git.", true)),
        Tool("skills_list", "The skills offered to you: name and one line each. Load one with skills_get."),
        Tool("workflow_show", "Read a workflow's whole thread, oldest first: who was told what, progress, hand-backs, questions, how it ended. Use it after a restart, a nudge or an idle reminder to see where the work stands.",
            ("workflow", "integer", "The workflow's number (default: the one you are working in now).", false)),
        Tool("workflow_digest", "The Manager: the facts of how one of your team's workflows went, by member and card: runs (time, how each ended, exit codes), " +
                                "failed and repeated commands, files edited many times, tests that failed then passed, lease waits, host and tool asks, " +
                                "blocked reports, questions to the person, work sent back. For a retrospective (skills_get retrospective).",
            ("workflow", "integer", "The workflow's number (default: the one you are working in now, or the one your retro item is for).", false)),
        Tool("wake_me", "Be woken once, later, in the work you are doing now: after this many minutes you get a run with your note. Use it to check again later instead of waiting or sleeping; then end your turn.",
            ("minutes", "integer", "How long from now (1 to 10080, a week).", true),
            ("note", "string", "What to look at when you are woken.", true)),
        Tool("request_host", "Ask the person to allow a host your work needs (a 403 \"host not on this agent's allowlist\", e.g. api.nuget.org for dotnet restore). " +
                             "The person answers in Needs you; an allowed host is reachable from your next run. Then hand back blocked on that host: " +
                             "never retry in a loop, never try another site.",
            ("host", "string", "The exact host name, e.g. api.nuget.org (no scheme, port, path or wildcard).", true),
            ("reason", "string", "One line on what you need it for, e.g. 'restore NuGet packages for the xUnit test project'.", true)),
        Tool("request_tool", "Ask the person to install a developer tool your work needs that is not on this computer: " +
                             string.Join(", ", Integrations.ToolCatalog.Names) + ". The person installs it from Needs you; it is on PATH from your next run. " +
                             "Then hand back blocked on that tool: never install it yourself, never retry in a loop.",
            ("tool", "string", "One of: " + string.Join(", ", Integrations.ToolCatalog.Names) + ".", true),
            ("reason", "string", "One line on what you need it for, e.g. 'build and test the .NET 10 console app'.", true)),
        Tool("request_folder", "Ask the person for read or read-write access to a folder on this computer outside your own folders (e.g. a data folder the work reads " +
                               "or writes). The person approves or denies in Needs you; an approved folder is reachable from your next run. Ask with request_folder; " +
                               "never ask the person to edit the policy by hand. Then hand back blocked on that folder: never retry in a loop or reach it another way. " +
                               "Refused at once: relative paths, drive roots, the user profile folder, secrets and Yawble's own folders, other teams' folders, system folders.",
            ("path", "string", @"The folder's full absolute path, e.g. Z:\smokesessions or /srv/data.", true),
            ("access", "string", "read or readwrite: only what the work needs.", true),
            ("reason", "string", "One line on what you need it for, e.g. 'read the recorded smoke sessions the tests replay'.", true),
            ("member", "string", "The Manager only: the member of your own team the access is for (default: yourself).", false)),
        Tool("team_retros_write", "The Manager: write your team's lessons, the team_retros skill every run of your team gets in its prompt (live at once). " +
                                  "Give the WHOLE new text: start from the current text (in your prompt under \"" + Skills.TeamRetros.PromptHeading + "\", or skills_get team_retros), " +
                                  "keep the person's edits, merge, reword or drop lessons, add at most a few new ones. Layout: a short list under a few headings " +
                                  "(e.g. ## Building and testing, ## Git, ## Working with the person), one lesson per line: \"- <lesson> (<date>, workflow <id>)\". " +
                                  $"At most {Skills.TeamRetros.MaxChars} characters. Refused when the text changed since you read it: read it again and merge. " +
                                  "Each write is kept as a version the person can see and restore. No secrets, tokens, personal data or command output.",
            ("text", "string", "The whole new text of team_retros (Markdown), at most " + Skills.TeamRetros.MaxChars + " characters.", true)),
    ];

    /// <summary>A long report is shown formatted on the card and in the workflow: a summary line first, then Markdown.</summary>
    private static string ReportText(string what) =>
        what + ". Write Markdown: a one-sentence summary on the first line, then the details as short bullet lists or short paragraphs, " +
        "one item per line, with real line breaks between them (never one long paragraph).";

    /// <summary>
    /// B123: needs_decision with summary (one plain sentence), options (2-4 answers as buttons, one may carry an allowlisted
    /// action the app applies when the person picks it), details (the findings, Markdown). question stays accepted.
    /// </summary>
    private static JsonObject NeedsDecisionTool()
    {
        var tool = Tool("needs_decision",
            "Ask the person to decide something. Put the decision first in plain words (summary), the answers they can pick as options " +
            "(each one a button), and your findings in details. Never number findings so they read as choices: the choices are the options. " +
            "The answer arrives as a new instruction in this workflow. Then end your turn: do not also call blocked for the same thing.",
            ("summary", "string", "The decision in one plain sentence a person reads first, e.g. 'Should the team's runs reach localhost on macOS?'.", false),
            ("details", "string", "What you found and why it matters, in Markdown (lists, tables). Shown under the summary.", false),
            ("question", "string", "Older form, still accepted: the whole decision as Markdown in one text. Prefer summary, options and details.", false),
            ("card", "integer", "Only to close the workflow over a tester's FAIL: the card the tester failed. Then give kind, and the yes option carries action " +
                                DecisionActions.CloseOverFail + " (with no options the app gives a yes and a no). workflow_complete goes through once the person picks the yes.", false),
            ("kind", "string", "With card: accept-fail (accept the FAIL as it is and close) or wont-do (the card will not be done; needs a reason).", false),
            ("reason", "string", "With card: why. Required for wont-do; for accept-fail, why the FAIL can be accepted.", false));
        tool["inputSchema"]!["properties"]!["kind"]!["enum"] = new JsonArray("accept-fail", "wont-do");
        tool["inputSchema"]!["properties"]!["options"] = new JsonObject
        {
            ["type"] = "array",
            ["description"] = $"{DecisionAsk.MinOptions} to {DecisionAsk.MaxOptions} answers the person can pick. label: the button (at most {DecisionAsk.MaxLabel} characters); " +
                              "reply: the answer you get when it is picked; action (optional, at most one option): a change the app makes for the person " +
                              $"when they pick it - only {DecisionActions.AllowLocalhostMac} (turns on the team's 'Allow localhost (macOS)') or, with card and kind, " +
                              $"{DecisionActions.CloseOverFail} (the person's yes to closing over the tester's FAIL). " +
                              "You never make that change yourself.",
            ["minItems"] = DecisionAsk.MinOptions,
            ["maxItems"] = DecisionAsk.MaxOptions,
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["label"] = new JsonObject { ["type"] = "string", ["maxLength"] = DecisionAsk.MaxLabel },
                    ["reply"] = new JsonObject { ["type"] = "string" },
                    ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(DecisionActions.All.Select(a => (JsonNode?)a).ToArray()) },
                },
                ["required"] = new JsonArray("label", "reply"),
                ["additionalProperties"] = false,
            },
        };
        return tool;
    }

    private static JsonObject Tool(string name, string description, params (string Name, string Type, string Description, bool Required)[] parameters)
    {
        var properties = new JsonObject();
        foreach (var p in parameters) properties[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(parameters.Where(p => p.Required).Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray()),
                ["additionalProperties"] = false,
            },
        };
    }

    private static string ErrorResponse(JsonNode? id, int code, string message) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    }.ToJsonString();

    private sealed class RpcError(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}

/// <summary>The role text skills_get returns. Same for every CLI; Teams/Roles/*.md.</summary>
public static class RoleText
{
    public static string For(string name) => Teams.RoleTexts.For(name);

    public static string Manager => Teams.RoleTexts.Manager;

    public static string Member => Teams.RoleTexts.Member;
}
