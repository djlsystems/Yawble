using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host.Auth;
using ModelContextProtocol.Server;

namespace Harness.Host;

public sealed partial class PlatformMcpTools
{
    [McpServerTool(Name = "kanban"), Description(
        "The board. action is board, show, move, comment, edit, plan, or filter. "
        + "A card's openWorkflow names the open workflow it belongs to (workflow) and that "
        + "workflow's latest row (latestSeq): pass latestSeq as causation on tell to continue it. "
        + "A card's outcome (id, name, status) is the outcome its open, else latest, workflow serves. "
        + "This is harness kanban. Do not request /api yourself.")]
    public async Task<string> Kanban(
        [Description("board, show, move, comment, edit, plan, or filter.")] string action,
        [Description("Team id. Required for a Concierge. Omit for a member of a team.")] string? team = null,
        [Description("Card id, for show, move, edit, and comment.")] string? card = null,
        [Description("Board filter: a member's identifier, or several comma-separated (any of them).")] string? member = null,
        [Description("Board filter (several comma-separated: any of them), or the new status on edit.")] string? status = null,
        [Description("Board filter: an outcome's id, or none for cards with no outcome; several comma-separated keep any of them.")] string? outcome = null,
        [Description("Lane id, for move: todo, in-progress, blocked, or done.")] string? lane = null,
        [Description("New title, for edit or plan.")] string? title = null,
        [Description("Card body, for plan.")] string? body = null,
        [Description("Note on a move or an edit.")] string? note = null,
        [Description("Comment text.")] string? text = null,
        [Description("Backlog item this planned card is a piece of, as B000H or a number.")] string? item = null,
        [Description("HARNESS_CAUSATION, for plan, so the card joins the workflow you are in.")]
        string? causation = null,
        CancellationToken cancellationToken = default)
    {
        var verb = (action ?? "").Trim().ToLowerInvariant();
        if (verb is "filter")
        {
            return "The board takes four filters: team, member, status, and outcome (an outcome's id "
                + "from the outcome tool's list, or none for cards with no outcome; a card's outcome "
                + "is the one its open workflow serves, else its latest workflow's). "
                + "Each takes several values comma-separated, and a card is kept when it matches any "
                + "of them; across filters every one must hold. "
                + "A date range is not a filter. A workflow is workflow_show. "
                + "Searching a card's text is not a flag; read the board.";
        }

        if (verb is "board" && !IsTeamBound())
        {
            return await SendAsync(
                HttpMethod.Get,
                "/api/kanban/board" + Query(
                    ("team", team), ("member", member), ("status", status), ("outcome", outcome)),
                null,
                cancellationToken);
        }

        var resolved = await TeamAsync(team, cancellationToken);
        if (resolved is null) return "Refused: name a team. A Concierge has no default team.";

        var teamPath = "/api/teams/" + Uri.EscapeDataString(resolved);
        switch (verb)
        {
            case "board":
                return await SendAsync(
                    HttpMethod.Get,
                    teamPath + "/kanban/board" + Query(("member", member), ("status", status), ("outcome", outcome)),
                    null,
                    cancellationToken);

            case "show":
                if (string.IsNullOrWhiteSpace(card)) return "Refused: name the card.";
                return await SendAsync(
                    HttpMethod.Get,
                    teamPath + "/kanban/cards/" + Uri.EscapeDataString(card.Trim()),
                    null,
                    cancellationToken);

            case "move":
                if (string.IsNullOrWhiteSpace(card)) return "Refused: name the card.";
                return await SendAsync(
                    HttpMethod.Post,
                    teamPath + "/kanban/cards/" + Uri.EscapeDataString(card.Trim()) + "/move",
                    new { laneId = lane, note },
                    cancellationToken);

            case "comment":
                if (string.IsNullOrWhiteSpace(card)) return "Refused: name the card.";
                return await SendAsync(
                    HttpMethod.Post,
                    teamPath + "/kanban/cards/" + Uri.EscapeDataString(card.Trim()) + "/comment",
                    new { text },
                    cancellationToken);

            case "edit":
                if (string.IsNullOrWhiteSpace(card)) return "Refused: name the card.";
                return await SendAsync(
                    HttpMethod.Post,
                    teamPath + "/kanban/cards/" + Uri.EscapeDataString(card.Trim()) + "/edit",
                    new { title, status, note },
                    cancellationToken);

            case "plan":
                long? plannedItem = null;
                if (!string.IsNullOrWhiteSpace(item))
                {
                    if (ItemNumber(item) is not { } parsed) return ItemRefusal();
                    plannedItem = parsed;
                }

                long? cause = null;
                if (!string.IsNullOrWhiteSpace(causation))
                {
                    if (!long.TryParse(causation.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq))
                    {
                        return "Refused: causation is the number in HARNESS_CAUSATION.";
                    }

                    cause = seq;
                }

                return await SendAsync(
                    HttpMethod.Post,
                    teamPath + "/kanban/plan",
                    new { title, body, item = plannedItem, causation = cause },
                    cancellationToken);

            default:
                return "Refused: kanban action is board, show, move, comment, edit, plan, or filter.";
        }
    }

    [McpServerTool(Name = "backlog"), Description(
        "The backlog. action is list, search, add, show, edit, archive, restore, move, delete, "
        + "or dispatch. list defaults to pending. show with a team reads the item that team was "
        + "given; show without one reads the tenant item and is refused to a member. "
        + "This is harness backlog. Do not request /api yourself. Mark an item ready only when the person tells you to, "
        + "or asks you to run the backlog: then the items in the plan you stated to them.")]
    public async Task<string> Backlog(
        [Description("list, search, add, show, edit, archive, restore, move, delete, or dispatch.")]
        string action,
        [Description("Item id, as B000H or a bare number. Not used on list, search, or add.")]
        string? id = null,
        [Description("Team id. Required for show of a dispatched item, for dispatch, and for a member. Omit on list to see every team you reach.")]
        string? team = null,
        [Description("pending, ready, declared, implemented, or all. list defaults to pending.")]
        string? state = null,
        [Description("Read the archive instead of the backlog.")] bool? archived = null,
        [Description("Title, for add or edit.")] string? title = null,
        [Description("Spec body, for add or edit.")] string? body = null,
        [Description("Search text.")] string? text = null,
        [Description("On move, the id of the item this one now sits below.")] string? after = null,
        [Description("On move, the id of the item this one now sits above.")] string? before = null,
        CancellationToken cancellationToken = default)
    {
        var verb = (action ?? "").Trim().ToLowerInvariant();
        var archive = archived == true;

        if (verb is "search" && string.IsNullOrWhiteSpace(text))
            return "Refused: search needs the text.";

        if (verb is "list" or "search")
        {
            var raw = archive
                ? await ReadArchiveAsync(cancellationToken)
                : await SendAsync(HttpMethod.Get, "/api/backlog", null, cancellationToken);

            var wanted = string.Equals(state, "all", StringComparison.OrdinalIgnoreCase) ? null
                : !string.IsNullOrWhiteSpace(state) ? state.Trim()
                : verb is "list" && !archive ? "pending"
                : null;

            return Narrow(raw, wanted, team, verb is "search" ? text : null);
        }

        if (verb is "add")
        {
            return await SendAsync(
                HttpMethod.Post,
                "/api/backlog",
                new { title, body, team },
                cancellationToken);
        }

        if (ItemNumber(id) is not { } number) return ItemRefusal();

        var itemPath = "/api/backlog/" + number.ToString(CultureInfo.InvariantCulture);
        switch (verb)
        {
            case "show":
                var shown = await TeamAsync(team, cancellationToken);
                if (shown is not null)
                {
                    return await SendAsync(
                        HttpMethod.Get,
                        "/api/teams/" + Uri.EscapeDataString(shown) + "/backlog/"
                            + number.ToString(CultureInfo.InvariantCulture),
                        null,
                        cancellationToken);
                }

                return await SendAsync(HttpMethod.Get, itemPath, null, cancellationToken);

            case "edit":
                return await SendAsync(
                    HttpMethod.Patch,
                    itemPath,
                    new { title, body, state },
                    cancellationToken);

            case "archive":
                return await SendAsync(HttpMethod.Post, itemPath + "/archive", null, cancellationToken);

            case "restore":
                return await SendAsync(HttpMethod.Post, itemPath + "/restore", null, cancellationToken);

            case "delete":
                return await SendAsync(HttpMethod.Delete, itemPath, null, cancellationToken);

            case "move":
                long? afterId = null;
                long? beforeId = null;
                if (!string.IsNullOrWhiteSpace(after))
                {
                    if (ItemNumber(after) is not { } parsedAfter) return ItemRefusal();
                    afterId = parsedAfter;
                }

                if (!string.IsNullOrWhiteSpace(before))
                {
                    if (ItemNumber(before) is not { } parsedBefore) return ItemRefusal();
                    beforeId = parsedBefore;
                }

                return await SendAsync(
                    HttpMethod.Post,
                    itemPath + "/position",
                    new { after = afterId, before = beforeId },
                    cancellationToken);

            case "dispatch":
                var destination = await TeamAsync(team, cancellationToken);
                if (destination is null) return "Refused: name a team. A Concierge has no default team.";
                return await SendAsync(
                    HttpMethod.Post,
                    "/api/teams/" + Uri.EscapeDataString(destination) + "/backlog/"
                        + number.ToString(CultureInfo.InvariantCulture) + "/dispatch",
                    null,
                    cancellationToken);

            default:
                return "Refused: backlog action is list, search, add, show, edit, archive, restore, move, delete, or dispatch.";
        }
    }

    [McpServerTool(Name = "repo"), Description(
        "Repository and worktree status for one team: paths, and whether work is pushed or merged. "
        + "action is status (the default) or merge. "
        + "status answers conciergeMayMerge, whether a person has turned on concierge.mayMerge now: read it "
        + "before saying whether you may merge, never guess and never try a merge to find out. "
        + "For the Concierge, status with no team answers that setting alone. "
        + "merge is the Concierge's, and only when a person has turned on the setting concierge.mayMerge: "
        + "it runs the platform's Merge to main for one repository (bringCurrent true: Bring current and "
        + "merge), which lands the team branch by fast-forward or a merge commit, never forced, refuses a "
        + "conflict changing nothing, and records landed on the backlog item. It is refused to a Manager "
        + "and a member. This is harness repo. It does not fetch, rebase, push, or delete a branch. "
        + "Those are a person's buttons. Do not request /api yourself, and do not guess a path.")]
    public async Task<string> Repo(
        [Description("Team id. Required for a Concierge, except status with no team, which answers whether it may merge now. Omit for a member of a team.")] string? team = null,
        [Description("Ask origin again. Omit to read the status already recorded.")] bool? refresh = null,
        [Description("status (the default) or merge.")] string? action = null,
        [Description("The repository's name, for merge, as the status names it.")] string? repo = null,
        [Description("For merge: true runs Bring current and merge, which first merges the default branch into the team branch.")]
        bool? bringCurrent = null,
        CancellationToken cancellationToken = default)
    {
        var verb = string.IsNullOrWhiteSpace(action) ? "status" : action.Trim().ToLowerInvariant();
        if (verb is not ("status" or "merge"))
        {
            return "Refused: repo action is status or merge.";
        }

        var resolved = await TeamAsync(team, cancellationToken);
        if (resolved is null && verb == "status" && IsConciergeCaller())
        {
            return await MayMergeAsync(cancellationToken);
        }

        if (resolved is null)
        {
            return verb == "merge"
                ? MergeRefusal("name a team. A Concierge has no default team.")
                : "Refused: name a team. A Concierge has no default team.";
        }

        var teamPath = "/api/teams/" + Uri.EscapeDataString(resolved);
        if (verb == "status")
        {
            return await SendAsync(
                HttpMethod.Get,
                teamPath + "/repo-status" + Query(("refresh", refresh == true ? "true" : null)),
                null,
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(repo))
        {
            return MergeRefusal("name the repository, as the status names it.");
        }

        // RELAYED WITH THE CALLER'S OWN KEY: who may merge, and whether the setting is on, are the
        // routes' to decide, never this tool's.
        var route = bringCurrent == true ? "bring-current-and-merge" : "merge-to-main";
        var reply = await SendAsync(
            HttpMethod.Post,
            teamPath + "/repos/" + Uri.EscapeDataString(repo.Trim()) + "/" + route,
            null,
            cancellationToken);

        if (reply.StartsWith("HTTP 2", StringComparison.Ordinal)) return reply;
        return MergeRefusal(reply.StartsWith("Refused: ", StringComparison.Ordinal) ? reply["Refused: ".Length..] : Explain(reply));
    }

    /// <summary>
    /// Whether the Concierge may merge now, with no team named: the tenant setting, read through the
    /// settings route with the caller's own key at every call, so turning it off shows at once.
    /// </summary>
    private async Task<string> MayMergeAsync(CancellationToken ct)
    {
        var reply = await SendAsync(HttpMethod.Get, "/api/tenant/settings", null, ct);
        var split = reply.Split(Environment.NewLine, 2);
        if (!reply.StartsWith("HTTP 2", StringComparison.Ordinal) || split.Length < 2) return Explain(reply);

        using var document = JsonDocument.Parse(split[1]);
        var value = document.RootElement.GetProperty("settings").EnumerateArray()
            .Where(setting => setting.GetProperty("name").GetString() == TenantSettings.ConciergeMayMergeName)
            .Select(setting => setting.GetProperty("value").GetString())
            .SingleOrDefault();

        return split[0] + Environment.NewLine
            + JsonSerializer.Serialize(new { conciergeMayMerge = value == "on" });
    }

    private bool IsConciergeCaller() =>
        http.HttpContext is not null
        && PrincipalClaims.From(http.HttpContext.User) is { Kind: PrincipalKind.TenantConcierge or PrincipalKind.Concierge };

    /// <summary>
    /// A merge refusal: it starts <c>Refused:</c>, names the tool and the setting, and carries no URL
    /// - a fetch's stderr names origin's, and an agent reading one goes to fetch it.
    /// </summary>
    private static string MergeRefusal(string reason) =>
        "Refused: the `repo` tool's merge did not run or did not land: " + WithoutUrls(reason).Trim().TrimEnd('.')
        + ". The Concierge merges only while a person has turned on " + TenantSettings.ConciergeMayMergeName
        + ", and never a Manager or a member.";

    /// <summary>The route's status, error and detail, from the relayed reply.</summary>
    private static string Explain(string reply)
    {
        var split = reply.Split(Environment.NewLine, 2);
        var status = split[0];
        if (split.Length < 2 || string.IsNullOrWhiteSpace(split[1])) return $"{status}.";

        try
        {
            using var document = JsonDocument.Parse(split[1]);
            string Field(string name) =>
                document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out var value)
                    ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString()
                    : "";

            var error = Field("error");
            var detail = Field("detail");
            return string.Join(" ", new[] { $"{status}:", error, detail }.Where(p => p.Length > 0));
        }
        catch (JsonException)
        {
            return $"{status}.";
        }
    }

    private static string WithoutUrls(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(text, @"[a-zA-Z][a-zA-Z0-9+.-]*://\S+", "(an address)"),
            @"/api/\S*", "(a route)");

    private bool IsTeamBound()
    {
        if (http.HttpContext is null) return false;
        return PrincipalClaims.From(http.HttpContext.User) is
            { Kind: PrincipalKind.Container or PrincipalKind.Concierge };
    }

    private static string Query(params (string Key, string? Value)[] pairs)
    {
        var parts = pairs
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value!))
            .ToArray();

        return parts.Length == 0 ? "" : "?" + string.Join("&", parts);
    }

    private static long? ItemNumber(string? raw) =>
        PlatformBacklogId.TryParse(raw, out var id) ? id : null;

    private static string ItemRefusal() =>
        "Refused: name the item as B000H or a bare number.";

    /// <summary>
    /// The WHOLE archive, walked page by page along the route's id cursor.
    ///
    /// The route answers one page; this tool's list and search narrow what they are handed, so
    /// handing them the newest page alone would make a search over the archive miss every older
    /// item and say so with a confident empty answer. Bounded by <see cref="ArchivePages"/> so a
    /// cursor that failed to advance cannot spin forever.
    /// </summary>
    private async Task<string> ReadArchiveAsync(CancellationToken ct)
    {
        const int take = 200;
        var rows = new List<string>();
        long? before = null;

        for (var page = 0; page < ArchivePages; page++)
        {
            var raw = await SendAsync(
                HttpMethod.Get,
                "/api/backlog" + Query(
                    ("archived", "true"),
                    ("before", before?.ToString(CultureInfo.InvariantCulture)),
                    ("take", take.ToString(CultureInfo.InvariantCulture))),
                null,
                ct);

            var split = raw.Split(Environment.NewLine, 2);
            if (split.Length != 2 || split[0] != "HTTP 200") return raw;

            long? last = null;
            var count = 0;

            try
            {
                using var document = JsonDocument.Parse(split[1]);
                if (document.RootElement.ValueKind != JsonValueKind.Array) return raw;

                foreach (var item in document.RootElement.EnumerateArray())
                {
                    rows.Add(item.GetRawText());
                    count++;
                    if (item.TryGetProperty("id", out var id) && id.TryGetInt64(out var value)) last = value;
                }
            }
            catch (JsonException)
            {
                return raw;
            }

            if (count < take || last is null || last == before) break;
            before = last;
        }

        return $"HTTP 200{Environment.NewLine}[{string.Join(",", rows)}]";
    }

    private const int ArchivePages = 100;

    /// <summary>
    /// The route returns every visible item, body and all. list and search are rows, and a bare
    /// list is pending. This does not ask the store again.
    /// </summary>
    private static string Narrow(string raw, string? state, string? team, string? search)
    {
        var split = raw.Split(Environment.NewLine, 2);
        if (split.Length != 2 || split[0] != "HTTP 200") return raw;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(split[1]);
        }
        catch (JsonException)
        {
            return raw;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) return raw;

            var needle = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
            var rows = new List<object>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var itemState = TextOf(item, "state");
                if (state is not null
                    && !string.Equals(itemState, state, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var itemTeam = TextOf(item, "team");
                if (!string.IsNullOrWhiteSpace(team)
                    && !string.Equals(itemTeam, team.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var title = TextOf(item, "title") ?? "";
                var spec = TextOf(item, "body") ?? "";
                string? context = null;
                if (needle is not null)
                {
                    var inTitle = title.Contains(needle, StringComparison.OrdinalIgnoreCase);
                    var at = spec.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                    if (!inTitle && at < 0) continue;
                    if (at >= 0)
                    {
                        var start = Math.Max(0, at - 40);
                        var length = Math.Min(spec.Length - start, 120);
                        context = spec.Substring(start, length).Replace('\n', ' ').Replace('\r', ' ');
                    }
                }

                var number = item.TryGetProperty("id", out var idValue) && idValue.TryGetInt64(out var id)
                    ? id
                    : 0;

                rows.Add(new
                {
                    id = number,
                    cited = number > 0 ? PlatformBacklogId.Format(number) : null,
                    title,
                    state = itemState,
                    team = itemTeam,
                    teamName = TextOf(item, "teamName"),

                    // THE OPEN WORKFLOW OF THE ITEM'S DISPATCH, so a reader continues it by number
                    // rather than working it out from the log. Null when nothing is in flight.
                    workflow = NumberOf(item, "inFlight", "correlation"),

                    // AND, WHEN THAT WORKFLOW WAS LEFT BEHIND, WHERE ITS WORK CONTINUED - with the
                    // sentence the row says. Closing it is a person's; nothing here offers a verb.
                    continuedIn = NumberOf(item, "stranded", "continuedIn"),
                    notice = item.TryGetProperty("stranded", out var stranded)
                        && stranded.ValueKind == JsonValueKind.Object
                            ? TextOf(stranded, "notice")
                            : null,
                    context,
                });
            }

            return "HTTP 200" + Environment.NewLine + JsonSerializer.Serialize(rows);
        }
    }

    private static long? NumberOf(JsonElement item, string outer, string name) =>
        item.TryGetProperty(outer, out var value)
        && value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var number)
        && number.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static string? TextOf(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
