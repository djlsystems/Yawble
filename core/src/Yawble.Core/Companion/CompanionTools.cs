using System.Text.Json.Nodes;
using Yawble.Core.Concierge;

namespace Yawble.Core.Companion;

/// <summary>
/// Which of the Concierge's tools a paired external client may call, by level (cumulative: read &lt; act &lt; destructive),
/// the definitions it is offered, and the plan text shown in the window for a destructive call. One rule: whatever the
/// Concierge refuses over the Cloud Link needs the person's click in the window here. The Concierge's own memory and
/// conversation tools (notes, watches, team_current) are never offered.
/// </summary>
public static class CompanionTools
{
    public static readonly IReadOnlyList<string> Read =
    [
        "team_list", "team_status", "team_activity", "team_workflows", "documents_list", "documents_read", "team_repos",
        "backlog_list", "backlog_item", "board_list", "board_card", "system_status", "team_git", "integrations_status", "usage", "doctor",
        "backups_list", "request_status",
    ];

    public static readonly IReadOnlyList<string> Act =
    [
        "tell", "answer", "team_pause", "team_resume", "workflow_nudge", "backlog_add", "backlog_edit", "backlog_ready", "backlog_dispatch",
        "backlog_archive", "backlog_restore", "board_move", "board_comment", "team_unarchive", "team_agent_change",
    ];

    public static readonly IReadOnlyList<string> Destructive =
    [
        "git_branches_delete", "git_prune", "git_push_branch", "git_sync_branch", "git_pr_open", "git_pr_merge",
        "team_archive", "team_delete", "team_env_set", "doctor_fix", "team_create", "team_repo_add", "team_repo_remove", "workflow_close",
        "backup_now", "git_merge_team", "backup_restore",
    ];

    /// <summary>The destructive tools whose own first call shows the exact list and issues a token (ConciergeActions' confirm flow).</summary>
    public static readonly IReadOnlyList<string> SelfConfirming = ["git_branches_delete", "git_prune", "git_pr_merge", "team_archive", "team_delete"];

    /// <summary>The tools the companion serves itself (not the Concierge's).</summary>
    public static readonly IReadOnlyList<string> Own = ["backups_list", "request_status", "git_merge_team", "backup_restore"];

    /// <summary>The parameters of tell and answer that only work in a Concierge chat (never offered, never passed on).</summary>
    public static readonly IReadOnlyList<string> ChatOnlyParameters = ["images", "follow_up"];

    /// <summary>The parameters of backlog_dispatch that make a new team (destructive).</summary>
    private static readonly string[] NewTeamParameters = ["new_team", "manager_cli", "repo_path", "repo_url"];

    public const string ApprovalNote =
        "needs the person's approval in the Yawble window: the call returns a request id and the exact plan, and nothing runs until " +
        "they approve it there (check with request_status).";

    /// <summary>The level a call needs; null for a tool that is not offered at all.</summary>
    public static CompanionLevel? LevelOf(string tool, JsonObject args)
    {
        if (tool == "backlog_dispatch" && NewTeam(args)) return CompanionLevel.Destructive;
        if (Read.Contains(tool)) return CompanionLevel.Read;
        if (Act.Contains(tool)) return CompanionLevel.Act;
        if (Destructive.Contains(tool)) return CompanionLevel.Destructive;
        return null;
    }

    /// <summary>As ConciergeActions reads new_team: a non-blank string.</summary>
    internal static bool NewTeam(JsonObject args) => Text(args, "new_team") is not null;

    /// <summary>The tools a client of <paramref name="level"/> is offered: no confirm parameter anywhere (the window approves).</summary>
    public static JsonArray Definitions(CompanionLevel level)
    {
        var allowed = new HashSet<string>(Read);
        if (level >= CompanionLevel.Act) allowed.UnionWith(Act);
        if (level >= CompanionLevel.Destructive) allowed.UnionWith(Destructive);
        var list = new JsonArray();
        foreach (var node in ConciergeTools.Definitions(true).Concat(OwnDefinitions()))
        {
            if (node is not JsonObject def || def["name"]?.GetValue<string>() is not { } name || !allowed.Contains(name)) continue;
            var copy = def.DeepClone().AsObject();
            var schema = copy["inputSchema"] as JsonObject;
            Drop(schema, "confirm");
            // An external client has no chat: no attachments to forward and nobody to wake for a follow-up.
            if (name is "tell" or "answer") foreach (var p in ChatOnlyParameters) Drop(schema, p);
            if (name == "backlog_dispatch")
            {
                if (level < CompanionLevel.Destructive) foreach (var p in NewTeamParameters) Drop(schema, p);
                else copy["description"] = copy["description"]!.GetValue<string>() + " With new_team, this " + ApprovalNote;
            }
            if (Destructive.Contains(name))
            {
                // The Concierge's wording about tokens and a yes in chat does not apply here (the window approves): those sentences go.
                var description = string.Join(' ', System.Text.RegularExpressions.Regex.Split(copy["description"]!.GetValue<string>(), @"(?<=\.)\s+")
                    .Where(sentence => !System.Text.RegularExpressions.Regex.IsMatch(sentence, @"(?i)token|confirm|\byes\b"))).Trim();
                copy["description"] = description + " This " + ApprovalNote;
            }
            list.Add(copy);
        }
        return list;
    }

    private static void Drop(JsonObject? schema, string parameter)
    {
        if (schema is null) return;
        (schema["properties"] as JsonObject)?.Remove(parameter);
        if (schema["required"] is JsonArray required)
            foreach (var r in required.Where(r => r?.GetValue<string>() == parameter).ToList()) required.Remove(r);
    }

    private static IEnumerable<JsonObject> OwnDefinitions()
    {
        yield return Tool("backups_list", "The app's backups on this computer: name, reason, time and size.");
        yield return Tool("request_status", "Where an approval request stands (pending, done, failed, denied, expired or changed), with its result once it ran.",
            ("id", "string", "The request id a destructive call returned.", true));
        yield return Tool("git_merge_team", "Merge a team's branch into its repository's base branch (a fast-forward or a merge commit, never a rebase; a conflict changes nothing), and push it to origin when there is one.",
            ("team", "string", "The team.", true), ("repo", "string", "Repository name (default main).", false));
        yield return Tool("backup_restore", "Restore one of the app's backups (backups_list): it is staged and replaces the app's data when the app restarts.",
            ("backup", "string", "The backup's name.", true));
    }

    /// <summary>
    /// The plan the window shows for a destructive tool that has no list of its own (not <see cref="SelfConfirming"/>).
    /// Only identifying arguments are named: never team_env_set's value.
    /// </summary>
    public static string Describe(string tool, JsonObject args)
    {
        var team = Text(args, "team") ?? "?";
        var repo = Text(args, "repo") ?? "main";
        var text = tool switch
        {
            "git_push_branch" => $"Push branch {Text(args, "branch")} of {team} ({repo} repository) to origin. Never forced.",
            "git_sync_branch" => $"Bring branch {Text(args, "branch")} of {team} ({repo} repository) current with the base branch: a merge commit; a conflict changes nothing.",
            "git_pr_open" => $"Open a GitHub pull request for branch {Text(args, "branch")} of {team} ({repo} repository) into the base branch, titled \"{Text(args, "title")}\"." +
                             (Text(args, "body") is { } body ? $"\nIts description, in full:\n{body}" : ""),
            "team_env_set" => Flag(args, "remove")
                ? $"Remove environment variable {Text(args, "name")} from team {team}."
                : $"Set environment variable {Text(args, "name")} for team {team} (the value is not shown here).",
            "doctor_fix" => $"Run the health fix {Text(args, "fix")}. If it removes or archives something, its exact list is shown for another approval first.",
            "team_create" => $"Create team {Text(args, "name")} with a {Text(args, "manager_cli") ?? "claude"} Manager" +
                             (Text(args, "repo_url") is { } url ? $", cloning {ConciergeReadTools.OriginForDisplay(url)}" : Text(args, "repo_path") is { } path ? $", working on the folder {path}" : "") + ".",
            "team_repo_add" => $"Give team {team} the repository {(Text(args, "repo_url") is { } u ? ConciergeReadTools.OriginForDisplay(u) : Text(args, "repo_path"))}" +
                               (Text(args, "name") is { } n ? $" as {n}" : "") + (Flag(args, "read_only") ? " (a read-only reference)." : " (writable: every member gets a clone)."),
            "team_repo_remove" => $"Take repository {Text(args, "name")} away from team {team} (its folder on this computer is kept).",
            "workflow_close" => $"Close workflow #{Text(args, "workflow")} of team {team} without declaring it complete" + (Text(args, "reason") is { } r ? $": {r}" : "") + ".",
            "backup_now" => "Make a backup of the app's data now.",
            "git_merge_team" => $"Merge team {team}'s branch into the base branch of its {repo} repository.",
            "backup_restore" => $"Restore the backup {Text(args, "backup")}: it is staged and replaces the app's data when the app restarts.",
            "backlog_dispatch" => $"Make a new team {Text(args, "new_team")} with a {Text(args, "manager_cli") ?? "claude"} Manager" +
                                  (Text(args, "repo_url") is { } du ? $" (cloning {ConciergeReadTools.OriginForDisplay(du)})" : Text(args, "repo_path") is { } dp ? $" (on the folder {dp})" : "") +
                                  $" and dispatch {Text(args, "item")} to it. It starts paid agent work.",
            _ => $"Run {tool}.",
        };
        return ConciergeReadTools.Scrub(text);
    }

    /// <summary>A string argument (or a number, as text), trimmed; null when absent or blank.</summary>
    internal static string? Text(JsonObject args, string name)
    {
        if (args[name] is not JsonValue v) return null;
        var s = v.TryGetValue<string>(out var str) ? str : v.TryGetValue<long>(out var n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        if (string.IsNullOrWhiteSpace(s)) return null;
        // Never clipped: a plan shows in full what will run (CompanionService refuses an argument over 8 KB).
        return s.Trim();
    }

    private static bool Flag(JsonObject args, string name) =>
        args[name] is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && bool.TryParse(s, out var c) && c);

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
}
