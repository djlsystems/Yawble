namespace Yawble.Core.Logs;

/// <summary>One column of a log source. <paramref name="Sql"/> reads it from the source's FROM clause (alias t).
/// <paramref name="Kind"/>: "time", "text" or "number". <paramref name="Filter"/>: the dialog offers its values.</summary>
public sealed record LogColumn(string Key, string Label, string Sql, string Kind = "text", bool Filter = false);

/// <summary>
/// How old entries of a source are deleted: rows of <paramref name="Table"/> whose <paramref name="TimeColumn"/> is
/// before the cutoff (and match <paramref name="Where"/>). <paramref name="FileColumn"/>: a file each row names, deleted
/// with it. <paramref name="Loss"/>: what the person loses, said before they confirm.
/// </summary>
public sealed record LogPrune(string Table, string TimeColumn, int? DefaultDays, string Loss, string? Where = null, string? FileColumn = null);

/// <summary>
/// A log in the database. <paramref name="From"/> is its FROM clause with the alias t; <paramref name="TeamWhere"/> and
/// <paramref name="MemberWhere"/> are predicates over the parameters @team, @teamId, @member ("Team/Name"), @name and
/// @memberId. <paramref name="Prune"/> null: kept, because <paramref name="KeptBecause"/>.
/// </summary>
public sealed record LogSource(
    string Id, string Title, string Group, string About, string From, string RowId, string Time,
    IReadOnlyList<LogColumn> Columns, string? TeamWhere, string? MemberWhere, LogPrune? Prune, string? KeptBecause = null);

/// <summary>Every log the app keeps in its database, and the combined view of a member's activity across them.</summary>
public static class LogSources
{
    public const string Activity = "activity";

    /// <summary>The team part and the name part of a "Team/Name" key.</summary>
    private static string TeamOf(string key) => $"substr({key}, 1, instr({key}, '/') - 1)";
    private static string NameOf(string key) => $"substr({key}, instr({key}, '/') + 1)";
    private static string IsTeamOf(string key) => $"substr({key}, 1, length(@team) + 1) = @team || '/'";

    public static readonly IReadOnlyList<LogSource> All =
    [
        new(Activity, "All activity", "Activity",
            "Everything a member did or was asked to do, from every log at once: runs, network, tool calls, approvals, model requests, messages, transcripts, agent changes and the audit entries about it.",
            ActivityFrom, "t.rid", "t.at",
            [
                new("at", "Time", "t.at", "time"),
                new("source", "Source", "t.source", Filter: true),
                new("team", "Team", TeamOf("t.member"), Filter: true),
                new("member", "Member", NameOf("t.member")),
                new("event", "Event", "t.event"),
                new("details", "Details", "t.details"),
            ],
            IsTeamOf("t.member"), "t.member = @member", null,
            "it is read from the other logs; prune each of them instead"),

        new("audit", "Audit", "Accountability",
            "Every change made from the window, a Cloud Link viewer, the Concierge or a paired program: who, how, which call, what it was about, and whether it worked. Never a key, a token or a message.",
            "audit_log t", "t.id", "t.at",
            [
                new("at", "Time", "t.at", "time"),
                new("via", "Via", "t.via", Filter: true),
                new("user", "Who", "t.user_name", Filter: true),
                new("client", "Program", "t.client", Filter: true),
                new("method", "Call", "t.method", Filter: true),
                new("target", "About", "t.target"),
                new("outcome", "Outcome", "t.outcome"),
            ],
            "(instr(t.target || ' ', 'team=' || @team || ' ') > 0 OR (' ' || t.target || ' ') LIKE '% teamId=' || @teamId || ' %')",
            "((' ' || t.target || ' ') LIKE '% memberId=' || @memberId || ' %' OR (instr(t.target || ' ', 'team=' || @team || ' ') > 0 AND instr(t.target || ' ', ' member=' || @name || ' ') > 0))",
            new("audit_log", "at", 365, "who changed what, and when, for those entries. The record of changes made before the cutoff is gone; nothing in the app itself changes.")),

        new("gateway", "Network", "Security",
            "Each run's gateway: every model request it forwarded, every connection it allowed or refused, and sign-in problems.",
            "gateway_events t", "t.id", "t.at",
            [
                new("at", "Time", "t.at", "time"),
                new("team", "Team", TeamOf("t.member"), Filter: true),
                new("member", "Member", NameOf("t.member")),
                new("kind", "Event", "t.kind", Filter: true),
                new("method", "Method", "t.method", Filter: true),
                new("target", "Host or path", "COALESCE(t.target, t.vhost)"),
                new("status", "Status", "t.status", "number"),
                new("detail", "Detail", "t.detail"),
            ],
            IsTeamOf("t.member"), "t.member = @member",
            new("gateway_events", "at", 365, "the record of which hosts each run reached or was refused. The guardrail check's report of its own run is kept.")),

        new("runs", "Runs", "Security",
            "One row per sandboxed run: who, which CLI and model, how it ended, its exit code and duration, whether anything outlived it, the policy in force, and the commit it started from.",
            "run_records t", "t.id", "t.started_at",
            [
                new("at", "Started", "t.started_at", "time"),
                new("team", "Team", "t.team", Filter: true),
                new("member", "Member", "t.member"),
                new("cli", "CLI", "t.cli", Filter: true),
                new("model", "Model", "t.model", Filter: true),
                new("kind", "Kind", "t.kind", Filter: true),
                new("end", "Ended", "t.end_class", Filter: true),
                new("exit", "Exit", "t.exit_code", "number"),
                new("duration", "Seconds", "ROUND(t.duration_ms / 1000.0, 1)", "number"),
                new("reason", "Reason", "t.reason"),
            ],
            "t.team = @team", "t.team = @team AND t.member = @name",
            new("run_records", "started_at", 365, "how each run before the cutoff ended. The agent's last run and the Settings > Agents run history lose those runs.")),

        new("tools", "Tool calls", "Accountability",
            "Each tool call an agent made through an integration: the program, its arguments, the rule that decided, and how it ended.",
            "integration_calls t", "t.id", "t.at",
            [
                new("at", "Time", "t.at", "time"),
                new("team", "Team", "t.team", Filter: true),
                new("member", "Member", NameOf("t.member")),
                new("integration", "Tool", "t.integration", Filter: true),
                new("args", "Arguments", "t.args"),
                new("decision", "Decision", "t.decision", Filter: true),
                new("rule", "Rule", "t.rule"),
                new("exit", "Exit", "t.exit_code", "number"),
            ],
            "t.team = @team", "t.member = @member",
            new("integration_calls", "at", 365, "the record of which tool calls agents made and which rule allowed or refused each.")),

        new("approvals", "Approvals", "Accountability",
            "Tool calls that asked first, and hosts agents were refused (network): what was asked, why, and who approved or declined it, and for whom a host was allowed.",
            ApprovalsFrom, "t.rid", "t.at",
            [
                new("at", "Asked", "t.at", "time"),
                new("team", "Team", "tm.name", Filter: true),
                new("member", "Member", NameOf("t.member")),
                new("integration", "Tool", "t.integration", Filter: true),
                new("args", "Arguments", "t.args"),
                new("state", "State", "t.state", Filter: true),
                new("by", "Decided by", "t.decided_by", Filter: true),
                new("decided", "Decided", "t.decided_at", "time"),
            ],
            "tm.name = @team", "t.member = @member",
            new("integration_asks", "at", 365, "who approved or declined those requests. Requests still waiting for an answer are never pruned.", Where: "state <> 'pending'")),

        new("usage", "Model usage", "Usage",
            "Tokens for each model request: input, output and cache, by run, member and model.",
            "usage_events t", "t.id", "t.at",
            [
                new("at", "Time", "t.at", "time"),
                new("team", "Team", TeamOf("t.member"), Filter: true),
                new("member", "Member", NameOf("t.member")),
                new("model", "Model", "t.model", Filter: true),
                new("input", "Input", "t.input_tokens", "number"),
                new("output", "Output", "t.output_tokens", "number"),
                new("cacheRead", "Cache read", "t.cache_read_tokens", "number"),
                new("cacheWrite", "Cache write", "t.cache_write_tokens", "number"),
                new("status", "Status", "t.status", "number"),
            ],
            IsTeamOf("t.member"), "t.member = @member",
            new("usage_events", "at", 365, "the token counts of those model requests. Usage totals that include that period, such as a backlog item's dispatches, count fewer tokens.")),

        new("changes", "Agent changes", "Accountability",
            "Every change of an agent's CLI or model, and of the default model, and who made it.",
            "agent_changes t", "t.id", "t.at",
            [
                new("at", "Time", "t.at", "time"),
                new("kind", "Change", "t.kind", Filter: true),
                new("subject", "Of", "t.subject"),
                new("fromAgent", "From agent", "t.from_agent"),
                new("toAgent", "To agent", "t.to_agent"),
                new("fromModel", "From model", "t.from_model"),
                new("toModel", "To model", "t.to_model"),
                new("by", "By", "t.actor_user", Filter: true),
                new("how", "How", "t.how", Filter: true),
            ],
            "(t.subject LIKE 'member:%' AND substr(t.subject, 8, length(@team) + 1) = @team || '/' OR t.subject = 'manager:' || @team)",
            "(t.subject = 'member:' || @member OR (@name = 'Manager' AND t.subject = 'manager:' || @team))",
            new("agent_changes", "at", 365, "the history of which CLI and model each agent used before the cutoff (Settings > Agents, History).")),

        new("tasks", "Host tasks", "Accountability",
            "Each run of a host task: which program, for which team, who asked, how it ended, and where its output went.",
            "host_task_runs t LEFT JOIN teams tm ON tm.id = t.team_id", "t.id", "t.started_at",
            [
                new("at", "Started", "t.started_at", "time"),
                new("team", "Team", "tm.name", Filter: true),
                new("task", "Task", "t.task_name", Filter: true),
                new("by", "Asked by", "t.requested_by", Filter: true),
                new("state", "State", "t.state", Filter: true),
                new("exit", "Exit", "t.exit_code", "number"),
                new("program", "Program", "t.program_resolved"),
                new("log", "Output", "t.log_path"),
            ],
            "tm.name = @team", "t.requested_by = @member",
            new("host_task_runs", "started_at", 365, "the record of those host task runs. Their output files in the team's documents stay.", Where: "ended_at IS NOT NULL")),

        new("transcripts", "Transcripts", "Agent output",
            "Each run's full terminal output, kept as a file only you can read.",
            "transcripts t", "t.run_id", "t.started_at",
            [
                new("at", "Started", "t.started_at", "time"),
                new("ended", "Ended", "t.ended_at", "time"),
                new("team", "Team", TeamOf("t.member"), Filter: true),
                new("member", "Member", NameOf("t.member")),
                new("run", "Run", "t.run_id", "number"),
                new("bytes", "Bytes", "t.bytes", "number"),
                new("path", "File", "t.path"),
            ],
            IsTeamOf("t.member"), "t.member = @member",
            new("transcripts", "started_at", 30, "the full terminal output of those runs: their files are deleted. The runs' records, messages and usage stay.", Where: "ended_at IS NOT NULL", FileColumn: "path")),

        new("messages", "Team messages", "Work history",
            "The teams' messages: instructions, hand-backs, questions, progress and workflow events.",
            "messages t", "t.seq", "t.occurred_at",
            [
                new("at", "Time", "t.occurred_at", "time"),
                new("type", "Type", "CASE WHEN t.type LIKE 'agent.instruction.%' THEN 'agent.instruction' ELSE t.type END", Filter: true),
                new("from", "From", "t.source", Filter: true),
                new("to", "To", "CASE WHEN t.type LIKE 'agent.instruction.%' THEN substr(t.type, 19) END"),
                new("payload", "Content", "substr(t.payload, 1, 2000)"),
                new("via", "Via", "t.via", Filter: true),
            ],
            $"({IsTeamOf("t.source")} OR substr(t.type, 1, 18 + length(@team) + 1) = 'agent.instruction.' || @team || '/' OR json_extract(t.payload, '$.team') = @team)",
            "(t.source = @member OR t.type = 'agent.instruction.' || @member)",
            null, "the team pages, workflows and the Monitor's replay are built from them"),

        new("board", "Board history", "Work history",
            "What happened to each card on the board: who moved it, comments, and what the team said about it.",
            "board_trail t JOIN board_cards c ON c.id = t.card_id", "t.id", "t.at",
            [
                new("at", "Time", "t.at", "time"),
                new("team", "Team", "c.team", Filter: true),
                new("card", "Card", "c.title"),
                new("source", "By", "t.source", Filter: true),
                new("kind", "Event", "t.kind", Filter: true),
                new("text", "Text", "t.text"),
            ],
            "c.team = @team", "c.team = @team AND t.source = @name",
            null, "each card's history on the board is built from it"),
    ];

    public static LogSource? Find(string id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>
    /// The Approvals log: the asked tool calls (integration_asks) and the network asks (network_asks, 0039: the host, the
    /// agent's reason or what was refused, how often; allowed for whom - agent, team or everywhere - or denied, by whom and how).
    /// </summary>
    private const string ApprovalsFrom = """
        (
          SELECT 'a' || a.id AS rid, a.at, a.team_id, a.member, a.integration, a.args, a.state, a.decided_by, a.decided_at
            FROM integration_asks a
          UNION ALL
          SELECT 'n' || n.id, n.at, n.team_id, n.member, 'network',
                 n.host || COALESCE(': ' || n.reason, ' (refused ' || n.method || COALESCE(' ' || n.path_sample, '') || ')', '') || ' (' || n.count || 'x)',
                 CASE WHEN n.state = 'allowed' THEN 'allowed for ' || COALESCE(n.scope, '?') ELSE n.state END,
                 NULLIF(trim(COALESCE(n.decided_by, '') || COALESCE(' via ' || n.decided_via, '')), ''), n.decided_at
            FROM network_asks n
        ) t LEFT JOIN teams tm ON tm.id = t.team_id
        """;

    /// <summary>
    /// The combined view: one row per thing that happened to or by a member, with its member as "Team/Name". Audit
    /// entries are about a member when their target names it (memberId=…, or team=… member=…).
    /// </summary>
    private const string ActivityFrom = """
        (
          SELECT r.started_at AS at, 'Runs' AS source, r.team || '/' || r.member AS member, 'run ' || COALESCE(r.end_class, 'running') AS event,
                 trim(COALESCE(r.cli, '') || ' ' || COALESCE(r.model, '') || ' ' || COALESCE(r.kind, '') || COALESCE(' exit ' || r.exit_code, '') || COALESCE(' ' || r.reason, '')) AS details,
                 'r' || r.id AS rid
            FROM run_records r
          UNION ALL
          SELECT g.at, 'Network', g.member, g.kind, trim(COALESCE(g.method, '') || ' ' || COALESCE(g.target, g.vhost, '') || COALESCE(' ' || g.status, '') || COALESCE(' ' || g.detail, '')), 'g' || g.id
            FROM gateway_events g
          UNION ALL
          SELECT c.at, 'Tool calls', c.member, c.integration || ' ' || COALESCE(c.decision, ''), trim(COALESCE(c.args, '') || COALESCE(' exit ' || c.exit_code, '')), 'c' || c.id
            FROM integration_calls c
          UNION ALL
          SELECT a.at, 'Approvals', a.member, a.integration || ' ' || a.state, trim(COALESCE(a.args, '') || COALESCE(' by ' || a.decided_by, '')), 'a' || a.id
            FROM integration_asks a
          UNION ALL
          SELECT na.at, 'Approvals', na.member, 'network ' || na.state || COALESCE(' for ' || na.scope, ''),
                 trim(na.host || COALESCE(': ' || na.reason, '') || ' (' || na.count || 'x)' || COALESCE(' by ' || na.decided_by, '')), 'q' || na.id
            FROM network_asks na
          UNION ALL
          SELECT u.at, 'Model usage', u.member, COALESCE(u.model, 'model request'),
                 'input ' || COALESCE(u.input_tokens, 0) || ', output ' || COALESCE(u.output_tokens, 0) || ', cache ' || (COALESCE(u.cache_read_tokens, 0) + COALESCE(u.cache_write_tokens, 0)), 'u' || u.id
            FROM usage_events u
          UNION ALL
          SELECT m.occurred_at, 'Messages', m.source, m.type, substr(m.payload, 1, 1000), 'm' || m.seq
            FROM messages m WHERE m.source LIKE '%/%' AND m.type NOT LIKE 'agent.instruction.%'
          UNION ALL
          SELECT m.occurred_at, 'Messages', m.source, 'instruction to ' || substr(m.type, 19), substr(m.payload, 1, 1000), 'm' || m.seq
            FROM messages m WHERE m.source LIKE '%/%' AND m.type LIKE 'agent.instruction.%'
          UNION ALL
          SELECT m.occurred_at, 'Messages', substr(m.type, 19), 'instruction from ' || m.source, substr(m.payload, 1, 1000), 'n' || m.seq
            FROM messages m WHERE m.type LIKE 'agent.instruction.%'
          UNION ALL
          SELECT t.started_at, 'Transcripts', t.member, 'transcript', 'run ' || t.run_id || ', ' || COALESCE(t.bytes, 0) || ' bytes', 't' || t.run_id
            FROM transcripts t
          UNION ALL
          SELECT ac.at, 'Agent changes', CASE WHEN ac.subject LIKE 'member:%' THEN substr(ac.subject, 8) ELSE substr(ac.subject, 9) || '/Manager' END, ac.kind,
                 trim(COALESCE(ac.from_agent, '') || ' ' || COALESCE(ac.from_model, '') || ' -> ' || COALESCE(ac.to_agent, '') || ' ' || COALESCE(ac.to_model, '') || COALESCE(' by ' || ac.actor_user, '')), 'h' || ac.id
            FROM agent_changes ac WHERE ac.subject LIKE 'member:%' OR ac.subject LIKE 'manager:%'
          UNION ALL
          SELECT x.at, 'Audit', COALESCE(tt.name || '/' || mm.name, x.tname || '/' || x.mname), x.method,
                 trim(COALESCE(x.via, '') || ' ' || COALESCE(x.user_name, '') || ' ' || COALESCE(x.target, '') || ': ' || x.outcome), 'x' || x.id
            FROM (
              SELECT x0.*, CASE WHEN instr(x0.mrest, ' ') > 0 THEN substr(x0.mrest, 1, instr(x0.mrest, ' ') - 1) ELSE x0.mrest END AS mname
                FROM (
                  SELECT au.id, au.at, au.via, au.user_name, au.method, au.target, au.outcome,
                         CASE WHEN instr(au.target, 'memberId=') > 0 THEN CAST(substr(au.target, instr(au.target, 'memberId=') + 9) AS INTEGER) END AS mid,
                         CASE WHEN instr(au.target, 'team=') > 0 AND instr(au.target, ' member=') > 0
                              THEN substr(au.target, instr(au.target, 'team=') + 5, instr(au.target, ' member=') - instr(au.target, 'team=') - 5) END AS tname,
                         CASE WHEN instr(au.target, ' member=') > 0 THEN substr(au.target, instr(au.target, ' member=') + 8) END AS mrest
                    FROM audit_log au WHERE au.target LIKE '%memberId=%' OR au.target LIKE '% member=%'
                ) x0
            ) x
            LEFT JOIN members mm ON mm.id = x.mid
            LEFT JOIN teams tt ON tt.id = mm.team_id
        ) t
        """;
}
