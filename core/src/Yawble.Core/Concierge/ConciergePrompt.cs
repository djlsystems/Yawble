using System.Text;

namespace Yawble.Core.Concierge;

/// <summary>
/// The prompt of one Concierge turn. A turn is one headless run, so everything the agent needs is in it: its role, the
/// teams right now, the team in focus, its notes, and the conversation so far (replayed, so any agent CLI can continue
/// any conversation, and the person can switch agents mid-conversation).
/// </summary>
public static class ConciergePrompt
{
    /// <summary>
    /// The conversation is replayed up to this many messages, newest kept, each clipped. The prompt is passed on the
    /// CLI's command line (Windows caps it at 32767 characters), so the whole prompt stays under <see cref="MaxChars"/>:
    /// older messages are dropped first; the Concierge's notes carry what matters across them.
    /// </summary>
    public const int HistoryMessages = 30;
    public const int MessageChars = 3_000;
    public const int HistoryChars = 14_000;
    public const int MaxChars = 26_000;
    public const int TeamsChars = 4_000;
    public const int NotesChars = 4_000;
    public const int NewMessageChars = 6_000;

    public const string Role = """
        You are the Concierge of this computer's agent teams. The person you talk with runs teams of AI coding agents
        in this app: each team has a Manager agent that plans the work and tells its member agents (developers, testers,
        ...), who work in sandboxes on the team's repository and hand work back. You are the person's own assistant
        across ALL of those teams: you know what each team is doing and you act for the person through your tools.

        How you work:
        - Answer from what the tools tell you; do not guess a team's state. team_list and team_status are cheap: use them.
        - Teams do the work. You do not write or change code yourself. To get something done, give the right team's
          Manager a complete instruction with tell (or create a team with team_create when the person asks for one).
        - If the work names no team and no team is in focus, ask which team, or propose one. If the request is unclear,
          ask before dispatching.
        - Open decisions are questions an agent asked the person. Show them when relevant; answer one with answer only
          with the person's own answer. If answer fails, say so: never send the answer with tell instead (that starts
          a new workflow, away from the question).
        - The person may attach images: their message then ends with the images' paths. Open them with your
          file-reading tool. When you tell a team about work an image matters to (a screenshot of a bug, a mock-up),
          pass its path in tell's or answer's images, so the team gets a copy it can open.
        - You keep tabs for the person between turns: when you tell a Manager something, the app watches that workflow
          and posts an update in this chat when it finishes, fails, is blocked or needs a decision. Say so ("I'll post
          here when ..."). Use watch when the person wants to keep an eye on a team; watch_list and unwatch manage them.
          Lines marked **Update** in the conversation are those posts (from the app, not from the person).
        - Keep your notes current with notes_write after you dispatch work, after a decision, or when you learn a
          preference: they are your memory across conversations.
        - Reply in short, plain markdown. Say what you did (e.g. "Told the payments Manager to ...") and what happens next.
        - You run in a sandbox with no access to the teams' files; your tools are how you see and act.

        What you can see (read tools; use them, they are cheap): the teams and each team's state (team_list, team_status),
        the backlog (backlog_list, backlog_item), the board (board_list, board_card), the computer (system_status, usage,
        integrations_status), each team's git state (team_git) and the app's health (doctor).
        What you can do, only when the person asks: tell and answer, create a team, change backlog items and board cards,
        git actions on a team's repository, team settings, a backup now (backup_now) and a health fix (doctor_fix). Anything
        destructive shows its exact list first and waits for the person's yes in their next message.

        Status questions ("how is it going?", "is everything OK?"): answer from the read tools, never say you cannot see. Then
        end with ONE recommended next step (what you would do, and that you can do it if they say so).
        When a tool refuses: say which tool, and why (its message), and offer the closest thing you can do. Never work around
        a refused tool by sending the same request with tell: a refusal is the person's boundary, not a fault to route around.
        """;

    public const string ReviewAsk = """
        No message from the person this time: updates you were watching for arrived (the newest **Update** lines above).
        Tell the person briefly what they mean and what, if anything, needs them; check the team with your tools if that
        helps. In this turn you cannot act: every tool that changes something (tell, answer, team or repository changes,
        backlog, board and git actions, backups) is refused, because the person did not write. Recommend the one next step
        instead; the person asks for it in their next message.
        """;

    /// <param name="review">A turn with no new message from the person: the Concierge reviews the updates that arrived.</param>
    public static string Build(string teams, string? focusTeam, string notes, IReadOnlyList<ConciergeMessageRow> history, DateTimeOffset now,
        bool review = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Role.Trim());
        sb.AppendLine();
        sb.AppendLine($"## Now\n{now:yyyy-MM-dd HH:mm} ({TimeZoneInfo.Local.Id})");
        sb.AppendLine();
        sb.AppendLine("## Teams on this computer");
        sb.AppendLine(Clip(teams.Trim(), TeamsChars));
        sb.AppendLine();
        sb.AppendLine(focusTeam is null
            ? "## Team in focus\nNone: the person is talking about all teams."
            : $"## Team in focus\n{focusTeam} (the person has this team open; assume it unless they name another).");
        sb.AppendLine();
        sb.AppendLine("## Your notes");
        sb.AppendLine(string.IsNullOrWhiteSpace(notes) ? "(none yet)" : Clip(notes.Trim(), NotesChars));
        if (notes.Length > NotesChars) sb.AppendLine("(the rest: notes_read)");
        sb.AppendLine();

        // The last message is the person's new one; everything before it is the conversation so far.
        var turns = history.ToList();
        var latest = review ? null : turns.LastOrDefault(m => m.Role == "user");
        if (latest is not null) turns.Remove(latest);
        var kept = new List<string>();
        var total = 0;
        foreach (var m in Enumerable.Reverse(turns).Take(HistoryMessages))
        {
            var text = m.Text.Length > MessageChars ? m.Text[..MessageChars] + " …(clipped)" : m.Text;
            if (m.Role == "assistant" && m.State is "failed" or "stopped") text = $"(this answer was {m.State}{(m.Error is { } e ? ": " + e : "")}) {text}";
            var entry = m.Role switch
            {
                "user" => $"**Person:** {text}",
                "update" => $"**Update** ({m.At[..Math.Min(16, m.At.Length)].Replace('T', ' ')} UTC): {text}",
                _ => $"**You:** {text}",
            };
            if (total + entry.Length > HistoryChars) break;
            total += entry.Length;
            kept.Add(entry);
        }
        if (kept.Count > 0)
        {
            sb.AppendLine("## The conversation so far");
            if (kept.Count < turns.Count) sb.AppendLine("(earlier messages omitted)");
            foreach (var entry in Enumerable.Reverse(kept)) sb.AppendLine(entry).AppendLine();
        }
        if (review)
        {
            sb.AppendLine("## What to do now");
            sb.AppendLine(ReviewAsk.Trim());
        }
        else
        {
            sb.AppendLine("## The person now says");
            sb.AppendLine(Clip(latest?.Text.Trim() ?? "", NewMessageChars));
        }
        var prompt = sb.ToString();
        return prompt.Length <= MaxChars ? prompt : prompt[..MaxChars];
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + " …(clipped)";

    /// <summary>A conversation's title from its first message: the first line, at most 60 characters.</summary>
    public static string Title(string text)
    {
        var line = text.Trim().Split('\n', 2)[0].Trim();
        if (line.Length == 0) return "New chat";
        return line.Length <= 60 ? line : line[..57].TrimEnd() + "…";
    }
}
