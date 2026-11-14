using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Policy;

namespace Yawble.Core.Ui;

/// <summary>
/// The IPC methods of the screens. Every method name starts with one of <see cref="Prefixes"/>;
/// <see cref="RpcDispatcher"/> hands those here. Refusals come back as RPC errors with the reason as the person
/// reads it: policy refusals list every reason, one per line (code "policy_refused").
/// </summary>
public sealed class UiRpc(
    BusEngine bus,
    PolicyStore policies,
    MemberLayouts layouts,
    IModelCredentials credentials,
    IHostTerminals hostTerminals,
    IInteractiveSessions interactive,
    TerminalHub terminals,
    long? machineMemoryMb = null,
    Func<string, string, bool>? stopSandbox = null,
    Skills.SkillCatalog? skills = null,
    Monitor.StepFeed? steps = null)
{
    public static readonly string[] Prefixes = ["app.", "monitor.", "teams.", "members.", "bus.", "policy.", "credentials.", "hostTerminal.", "terminal.", "skills.", "triggers.", "git.", "docs."];

    /// <summary>Other things that need the person, counted in app.overview's needsYou (the doctor's failing checks).</summary>
    public Func<int>? ExtraNeedsYou { get; set; }

    public static bool Handles(string method) => Prefixes.Any(p => method.StartsWith(p, StringComparison.Ordinal));

    public async Task<JsonNode?> DispatchAsync(string method, JsonObject? p, CancellationToken ct)
    {
        try
        {
            return method switch
            {
                "app.overview" => await OverviewAsync(ct),

                "monitor.messages" => await MonitorMessagesAsync(OptLong(p, "after") ?? 0, (int)(OptLong(p, "limit") ?? 200), ct),
                "monitor.steps" => MonitorSteps(OptLong(p, "after") ?? 0, (int)(OptLong(p, "sinceMinutes") ?? 30), (int)(OptLong(p, "limit") ?? 2000)),

                "teams.list" => S(await bus.TeamsAsync(ct)),
                "teams.create" => await CreateTeamAsync(p, ct),
                "teams.agents" => await AgentsAsync(Long(p, "teamId"), ct),
                "docs.list" => Docs().List(await TeamNameOf(Long(p, "teamId"), ct), OptStr(p, "path")),
                "docs.read" => Docs().Read(await TeamNameOf(Long(p, "teamId"), ct), Str(p, "path")),
                "docs.write" => Docs().Write(await TeamNameOf(Long(p, "teamId"), ct), Str(p, "path"), Str(p, "text")),
                "docs.upload" => Docs().Upload(await TeamNameOf(Long(p, "teamId"), ct), OptStr(p, "path"), Str(p, "name"), Str(p, "base64"), p?["replace"]?.GetValue<bool>() == true),
                "docs.mkdir" => Docs().MakeFolder(await TeamNameOf(Long(p, "teamId"), ct), OptStr(p, "path"), Str(p, "name")),
                "docs.rename" => Docs().Rename(await TeamNameOf(Long(p, "teamId"), ct), Str(p, "path"), Str(p, "name")),
                "docs.delete" => await DeleteDocAsync(Long(p, "teamId"), Str(p, "path"), ct),
                "docs.folder" => new JsonObject { ["path"] = Docs().Root(await TeamNameOf(Long(p, "teamId"), ct)) },
                "teams.pause" => await Then(bus.PauseTeamAsync(await TeamNameOf(Long(p, "teamId"), ct), ct)),
                "teams.resume" => await Then(bus.ResumeTeamAsync(await TeamNameOf(Long(p, "teamId"), ct), ct)),
                "teams.quiet" => S(await bus.TeamQuietAsync(await TeamNameOf(Long(p, "teamId"), ct), ct)),
                "teams.archive" => await Then(bus.ArchiveTeamAsync(await TeamNameOf(Long(p, "teamId"), ct), ct)),
                "teams.unarchive" => await Then(bus.UnarchiveTeamAsync(await TeamNameOf(Long(p, "teamId"), ct), ct)),
                "teams.delete" => await DeleteTeamAsync(Long(p, "teamId"), Str(p, "confirm"), ct),
                "teams.reset" => S(await bus.ResetTeamAsync(await TeamNameOf(Long(p, "teamId"), ct),
                    (p?["members"] as JsonArray ?? []).Select(n => n?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToList(),
                    new TeamResetOptions(Flag(p, "dropWaiting"), Flag(p, "clearHome"), Flag(p, "removeClone"), Flag(p, "clearDocuments")), ct)),
                "teams.syncToBase" => await SyncSettingAsync(Long(p, "teamId"), p?["on"] is JsonValue sv && sv.TryGetValue<bool>(out var son) ? son : null, ct),
                "teams.push" => PushSetting(Long(p, "teamId"), p?["on"] is JsonValue pv && pv.TryGetValue<bool>(out var pon) ? pon : null),
                "teams.env" => S(await bus.TeamEnvAsync(await TeamNameOf(Long(p, "teamId"), ct), ct)),
                "teams.setEnv" => S(await bus.SetTeamEnvAsync(await TeamNameOf(Long(p, "teamId"), ct), EnvEntries(p), ct)),
                "teams.approvals" => S(await bus.TeamApprovalsAsync(await TeamNameOf(Long(p, "teamId"), ct), ct)),
                "teams.addApproval" => S(await bus.AddTeamApprovalAsync(await TeamNameOf(Long(p, "teamId"), ct), Str(p, "text"),
                    Provenance.Actor.Current?.User ?? Provenance.Actor.LocalUser, ct)),
                "teams.removeApproval" => await Then(bus.RemoveTeamApprovalAsync(await TeamNameOf(Long(p, "teamId"), ct), Long(p, "id"), ct)),
                "teams.repos" => await TeamReposListAsync(Long(p, "teamId"), ct),
                "teams.addRepo" => await AddRepoAsync(Long(p, "teamId"), p, ct),
                "teams.removeRepo" => await RemoveRepoAsync(Long(p, "teamId"), Str(p, "name"), ct),

                "members.get" => S(await MemberAsync(Long(p, "memberId"), ct)),
                "members.add" => S(await bus.AddMemberAsync(Long(p, "teamId"), Str(p, "name"), Str(p, "cli"), ct)),
                "members.remove" => await RemoveMemberAsync(Long(p, "memberId"), ct),
                "members.update" => S(await bus.UpdateMemberAsync(Long(p, "memberId"), Str(p, "cli"), OptStr(p, "instructions") ?? "",
                    OptStr(p, "model") ?? "", OptStr(p, "credential") ?? "default", ct)),

                "bus.tellManager" => await TellManagerAsync(Long(p, "teamId"), OptStr(p, "text") ?? "", ct, Attachments.Read(p)),
                "bus.decisions" => await DecisionsAsync(Long(p, "teamId"), ct),
                "bus.answer" => await AnswerAsync(Long(p, "teamId"), Long(p, "seq"), Str(p, "text"), ct, Attachments.Read(p)),
                "bus.workflows" => S(await bus.WorkflowsAsync(await TeamNameOf(Long(p, "teamId"), ct), (int)(OptLong(p, "limit") ?? 30), ct)),
                "bus.closeWorkflow" => S(await bus.CloseWorkflowAsync(await TeamNameOf(Long(p, "teamId"), ct), Long(p, "workflow"), OptStr(p, "reason"), ct)),
                "bus.stopWorkflow" => new JsonObject { ["stopped"] = await bus.StopWorkflowAsync(await TeamNameOf(Long(p, "teamId"), ct), Long(p, "workflow"), ct) },
                "bus.nudgeWorkflow" => S(await bus.NudgeWorkflowAsync(await TeamNameOf(Long(p, "teamId"), ct), Long(p, "workflow"), OptStr(p, "note"), ct)),

                "policy.getGlobal" => policies.GetGlobal().ToJson(),
                "policy.saveGlobal" => await SaveGlobalAsync(p, ct),
                "policy.getMember" => await MemberPolicyAsync(Long(p, "memberId"), ct),
                "policy.saveOverride" => await SaveOverrideAsync(p, ct),
                "policy.previewLoosen" => await PreviewLoosenAsync(p, ct),
                "policy.saveLoosen" => await SaveLoosenAsync(p, ct),
                // The team's network allow list (0041): "Allow for this team" adds to it; the team's settings list it with Remove.
                "policy.teamHosts" => await TeamHostsAsync(Long(p, "teamId"), ct),
                "policy.removeTeamHost" => await RemoveTeamHostAsync(Long(p, "teamId"), Str(p, "host"), ct),

                "credentials.status" => S(credentials.Status()),
                "credentials.setClaudeSetupToken" => Then(() => credentials.SetClaudeSetupToken(Str(p, "token"))),
                "credentials.setAnthropicApiKey" => Then(() => credentials.SetAnthropicApiKey(Str(p, "key"))),
                "credentials.setXaiApiKey" => Then(() => credentials.SetXaiApiKey(Str(p, "key"))),
                "credentials.remove" => Then(() => credentials.Remove(Str(p, "which"))),
                "credentials.setMode" => Then(() => credentials.SetMode(Str(p, "cli"), Str(p, "mode"))),

                "hostTerminal.start" => await StartHostTerminalAsync(Str(p, "kind"), ct),

                "terminal.attach" => terminals.Attach(Channel(p)),
                "terminal.input" => new JsonObject { ["accepted"] = terminals.Input(Channel(p), Encoding.UTF8.GetBytes(Str(p, "data"))) },
                "terminal.resize" => Done(() => terminals.Resize(Channel(p), (int)Long(p, "cols"), (int)Long(p, "rows"))),
                "terminal.banner" => await BannerAsync(Long(p, "memberId"), ct),
                "terminal.openInteractive" => await OpenInteractiveAsync(Long(p, "memberId"), SizeOf(p), ct),
                "terminal.stop" => await StopAsync(Long(p, "memberId"), ct),

                "skills.list" => S(Catalog().All()),
                "skills.save" => S(Catalog().Save(Str(p, "name"), Str(p, "description"),
                    (p?["roles"] as JsonArray ?? []).Select(n => n?.GetValue<string>() ?? ""), Str(p, "body"),
                    p?["teamId"] is JsonValue t && t.TryGetValue<long>(out var team) ? team : null)),
                "skills.delete" => new JsonObject { ["deleted"] = Catalog().Delete(Long(p, "id")) },

                "triggers.list" => S(await bus.TriggersAsync(Long(p, "teamId"), ct)),
                "triggers.save" => S(await bus.SaveTriggerAsync(TriggerDraftOf(p), ct)),
                "triggers.delete" => new JsonObject { ["deleted"] = await bus.DeleteTriggerAsync(Long(p, "id"), ct) },
                "triggers.runNow" => S(await bus.RunTriggerNowAsync(Long(p, "id"), ct)),
                "triggers.preview" => TriggerPreview(TriggerDraftOf(p, preview: true).Schedule),

                // "repo": one of the team's writable extra repositories by name (0022); absent, the main one.
                "git.status" => await GitAsync(p, (r, id, name) => r.GitStatusAsync(id, name, ct, OptStr(p, "repo")), ct),
                "git.fetch" => await GitAsync(p, (r, id, name) => r.GitFetchAsync(id, name, ct, OptStr(p, "repo")), ct),
                "git.bringCurrent" => await GitAsync(p, (r, id, name) => r.GitBringBaseCurrentAsync(id, name, ct, OptStr(p, "repo")), ct),
                "git.merge" => await GitAsync(p, (r, id, name) => r.GitMergeTeamToBaseAsync(id, name, ct, OptStr(p, "repo")), ct),
                "git.sync" => await GitAsync(p, (r, id, name) => r.SyncNowAsync(id, name, ct), ct),
                "git.askTeam" => await AskTeamAsync(Long(p, "teamId"), ct, OptStr(p, "repo")),
                _ => throw new RpcException("unknown_method", $"no method named '{method}'"),
            };
        }
        catch (ArgumentException e) when (method.StartsWith("skills.", StringComparison.Ordinal))
        {
            throw new RpcException("bad_skill", e.Message);
        }
        catch (ArgumentException e) when (method.StartsWith("triggers.", StringComparison.Ordinal))
        {
            throw new RpcException("bad_trigger", e.Message);
        }
        catch (BusRefusal e)
        {
            throw new RpcException(e.Code, e.Message);
        }
        catch (Teams.DocumentsException e)
        {
            throw new RpcException(e.Code, e.Message);
        }
        catch (IOException e) when (method.StartsWith("docs.", StringComparison.Ordinal))
        {
            throw new RpcException("io", e.Message);
        }
        catch (UnauthorizedAccessException e) when (method.StartsWith("docs.", StringComparison.Ordinal))
        {
            throw new RpcException("io", e.Message);
        }
        catch (PolicyException e)
        {
            throw new RpcException("policy_refused", string.Join("\n", e.Errors));
        }
        catch (Microsoft.Data.Sqlite.SqliteException e) when (e.SqliteErrorCode == 19)
        {
            throw new RpcException("exists", "that name is already taken");
        }
    }

    // ---------------------------------------------------------------- triggers

    private static TriggerDraft TriggerDraftOf(JsonObject? p, bool preview = false)
    {
        long? OptLong(string name) => p?[name] is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;
        bool Flag(string name, bool fallback) => p?[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
        var fireAt = OptStr(p, "fireAt") is { Length: > 0 } at
            ? DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal)
            : (DateTimeOffset?)null;
        var schedule = new Triggers.TriggerSchedule(Str(p, "kind"), (int?)OptLong("intervalSec"), OptStr(p, "cron"), OptStr(p, "timezone"), fireAt);
        return new TriggerDraft(OptLong("id"), preview ? 0 : Long(p, "memberId"), preview ? "" : Str(p, "name"), schedule,
            preview ? "" : Str(p, "instruction"), Flag("onlyWhenIdle", true), Flag("enabled", true),
            OptStr(p, "wakeManager") ?? Triggers.WakeManagerPolicy.OnHandbackOrFailure,
            OptStr(p, "watchRoot") is { } root ? new Triggers.FolderWatch(root, OptStr(p, "watchPath") ?? "", OptStr(p, "watchGlob"),
                (int)(OptLong("quietSec") ?? Triggers.FolderWatch.DefaultQuietSec)) : null,
            OptStr(p, "eventName"));
    }

    private static JsonNode TriggerPreview(Triggers.TriggerSchedule s)
    {
        var now = DateTimeOffset.UtcNow;
        var error = Triggers.TriggerScheduling.Validate(s, s.Kind == Triggers.TriggerKinds.Once ? now : null);
        return new JsonObject
        {
            ["error"] = error,
            ["next"] = error is null ? S(Triggers.TriggerScheduling.Preview(s, now, 5)) : new JsonArray(),
        };
    }

    // ---------------------------------------------------------------- teams and agents

    private async Task<JsonNode?> OverviewAsync(CancellationToken ct)
    {
        var teams = await bus.TeamsAsync(ct);
        var needsYou = 0;
        foreach (var t in teams) needsYou += (await bus.OpenDecisionsAsync(t.Name, ct)).Count;
        needsYou += bus.Integrations?.PendingCount() ?? 0; // calls waiting for the person's approval
        var networkAsks = bus.NetworkAsks?.PendingCount() ?? 0; // hosts agents were refused, waiting for the person (network.asks)
        needsYou += networkAsks;
        var toolAsks = bus.ToolAsks?.PendingCount() ?? 0; // tools agents asked the person to install (tools.asks)
        needsYou += toolAsks;
        // Connections a service refused (needs reconnect): only the count here; names and reasons come from
        // integrations.connectionAttention, which the Cloud Link refuses like every integrations.connection* method.
        var reconnect = bus.Integrations?.ConnectionsNeedingReconnect().Count ?? 0;
        needsYou += reconnect;
        needsYou += bus.Repos?.SyncNotes().Count ?? 0; // a team branch that could not be brought up to its base
        // Failing health checks (Settings > Health). The doctor's Connections check reports needs_reconnect as a WARN,
        // so it is not counted twice with the line above.
        needsYou += ExtraNeedsYou?.Invoke() ?? 0;
        return new JsonObject
        {
            ["slots"] = S(await bus.RunSlotsAsync(ct)),
            // The heavy-work lease: who holds it for what since when, and who waits (the Monitor's "Heavy work" line).
            ["heavy"] = S(await bus.LeaseSnapshotAsync(ct)),
            ["teams"] = S(teams),
            // Open questions waiting on the person, across every team (the tray and the window's badge).
            ["needsYou"] = needsYou,
            ["needsReconnect"] = reconnect,
            ["networkAsks"] = networkAsks,
            ["toolAsks"] = toolAsks,
        };
    }

    private async Task<JsonNode?> CreateTeamAsync(JsonObject? p, CancellationToken ct)
    {
        var name = Str(p, "name");
        var managerCli = OptStr(p, "managerCli") ?? "claude";
        var members = (p?["members"] as JsonArray ?? []).Select(n => (Name: n?["name"]?.GetValue<string>() ?? "", Cli: n?["cli"]?.GetValue<string>() ?? "claude")).ToList();
        if (members.Select(m => m.Name.Trim()).Append(OptStr(p, "managerName") ?? "Manager").GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new RpcException("exists", "two agents of the new team have the same name");
        var repoPath = OptStr(p, "repoPath");
        if (OptStr(p, "repoUrl") is { Length: > 0 } url)
        {
            // A repository from a URL: cloned first (with the person's git), then used like a folder they picked.
            if ((await bus.TeamsAsync(ct)).Any(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new RpcException("exists", $"a team named {name.Trim()} already exists");
            var repos = bus.Repos ?? throw new RpcException("unavailable", "cloning needs the app's data folder");
            try { repoPath = await repos.CloneForTeamAsync(name.Trim(), url, ct); }
            catch (ArgumentException e) { throw new RpcException("clone_failed", e.Message); }
        }
        var teamId = await bus.CreateTeamAsync(name, OptStr(p, "managerName") ?? "Manager", managerCli, repoPath, ct);
        foreach (var (memberName, cli) in members) await bus.AddMemberAsync(teamId, memberName, cli, ct, Agents.Profiles.AgentChangeLog.HowTeamCreate);
        return new JsonObject { ["teamId"] = teamId };
    }

    private async Task<JsonNode?> AgentsAsync(long teamId, CancellationToken ct)
    {
        var cards = await bus.AgentCardsAsync(teamId, ct);
        var everyone = await bus.AllMemberRowsAsync(ct);
        var team = cards.FirstOrDefault()?.Member.Team;
        var asking = team is null ? [] : (await bus.OpenDecisionsAsync(team, ct)).Select(m => m.Source[(m.Source.IndexOf('/') + 1)..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reports = team is null ? new Dictionary<string, Message>() : await bus.LatestReportsAsync(team, ct);
        var list = new JsonArray();
        foreach (var card in cards)
        {
            var node = S(card)!.AsObject();
            node["attention"] = Attention(card, asking, reports);
            node["hasClone"] = Code.CodeHost.HasClone(bus.Repos, card.Member);
            try
            {
                var effective = Effective(card.Member, everyone);
                node["net"] = PolicyBanner.Net(effective.Policy);
                node["wallTimeout"] = PolicyBanner.Duration(effective.Policy.Compute.WallTimeoutSec);
                node["loosened"] = S(effective.Policy.Loosened);
                node["policyProblems"] = S(effective.Violations);
            }
            catch (Exception e) when (e is PolicyException or ArgumentException)
            {
                node["policyProblems"] = new JsonArray(JsonValue.Create(e.Message));
            }
            list.Add(node);
        }
        return list;
    }

    /// <summary>
    /// What the member needs from a person, loudest first: "decision" (a question of its is open), "blocked" (its last
    /// report said it is blocked), "failed" (its last run failed); null when nothing. The faces' rings show it.
    /// </summary>
    internal static string? Attention(AgentCard card, IReadOnlySet<string> asking, IReadOnlyDictionary<string, Message> reports)
    {
        if (asking.Contains(card.Member.Name)) return "decision";
        if (card.State == "running") return null;
        if (reports.TryGetValue(card.Member.Name, out var last) && last.Type == MessageTypes.Blocked) return "blocked";
        return card.LastRunState == "failed" ? "failed" : null;
    }

    /// <summary>
    /// The Monitor's feed: bus messages in plain words. Each says its kind (instruction, completed, failed, handback,
    /// progress, blocked, decision, done), the team and member it is about (an instruction's addressee, a report's
    /// sender), who sent it (person, host or a member), a short text, and its workflow.
    /// </summary>
    private async Task<JsonNode?> MonitorMessagesAsync(long after, int limit, CancellationToken ct)
    {
        var (end, messages) = await bus.MessagesPageAsync(after, limit, ct);
        var list = new JsonArray();
        foreach (var m in messages)
            if (MonitorRow(m) is { } row) list.Add(row);
        return new JsonObject { ["end"] = end, ["messages"] = list };
    }

    /// <summary>
    /// monitor.steps {after?, sinceMinutes?, limit?}: the members' steps read from their runs' transcripts
    /// (Monitor/StepFeed), {end, pending, steps: [{id, at, team, member, run, kind, tool, target}]}. Without a feed
    /// (no data folder) there are none.
    /// </summary>
    private JsonObject MonitorSteps(long after, int sinceMinutes, int limit)
    {
        if (steps is null) return new JsonObject { ["end"] = after, ["pending"] = false, ["steps"] = new JsonArray() };
        var (end, list, pending) = steps.Read(after, sinceMinutes, limit);
        var array = new JsonArray();
        foreach (var s in list) array.Add(s.ToJson());
        return new JsonObject { ["end"] = end, ["pending"] = pending, ["steps"] = array };
    }

    internal static JsonObject? MonitorRow(Message m)
    {
        static (string? Team, string? Member) Split(string qualified) =>
            qualified.IndexOf('/') is > 0 and var i ? (qualified[..i], qualified[(i + 1)..]) : (null, null);
        JsonObject? payload = null;
        try { payload = JsonNode.Parse(m.Payload) as JsonObject; } catch (JsonException) { }
        string? Field(string name) => payload?[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

        string kind;
        string? text;
        (string? team, string? member) about;
        if (m.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal))
        {
            kind = "instruction";
            about = Split(m.Type[MessageTypes.InstructionPrefix.Length..]);
            text = Field("text");
        }
        else
        {
            (kind, text) = m.Type switch
            {
                MessageTypes.Completed => ("completed", Field("reason")),
                MessageTypes.Failed => ("failed", Field("reason")),
                MessageTypes.Handback => ("handback", Field("delivered")),
                MessageTypes.Progress => ("progress", Field("status")),
                MessageTypes.Blocked => ("blocked", Field("reason")),
                MessageTypes.NeedsDecision => ("decision", Field("question")),
                MessageTypes.WorkflowCompleted => ("done", Field("delivered")),
                _ => ("other", null),
            };
            if (kind == "other") return null;
            about = Split(m.Source);
        }
        if (about.team is null) return null;
        var from = m.Source is MessageTypes.PersonSource or MessageTypes.HostSource ? m.Source : Split(m.Source).Member ?? m.Source;
        if (text is { Length: > 240 }) text = text[..240] + "…";
        return new JsonObject
        {
            ["seq"] = m.Seq, ["kind"] = kind, ["team"] = about.team, ["member"] = about.member, ["from"] = from,
            ["text"] = text, ["workflow"] = m.CorrelationId, ["at"] = m.OccurredAt.ToString("O"),
        };
    }

    private async Task<MemberRow> MemberAsync(long memberId, CancellationToken ct) =>
        await bus.MemberAsync(memberId, ct) ?? throw new RpcException("no_such_member", $"there is no member {memberId}");

    private async Task<JsonNode?> RemoveMemberAsync(long memberId, CancellationToken ct)
    {
        if (terminals.IsLive(TerminalHub.MemberChannel(memberId)))
            throw new RpcException("running", "close the agent's interactive session first");
        await bus.RemoveMemberAsync(memberId, ct);
        return new JsonObject { ["removed"] = memberId };
    }

    private async Task<JsonNode?> TellManagerAsync(long teamId, string text, CancellationToken ct,
        IReadOnlyList<(string Name, byte[] Bytes)>? images = null)
    {
        images ??= [];
        if (string.IsNullOrWhiteSpace(text) && images.Count > 0) text = images.Count == 1 ? "Look at this image." : "Look at these images.";
        if (string.IsNullOrWhiteSpace(text)) throw new RpcException("empty", "write the instruction");
        var manager = (await bus.AgentCardsAsync(teamId, ct)).Select(c => c.Member).SingleOrDefault(m => m.Role == "manager")
            ?? throw new RpcException("no_such_team", $"there is no team {teamId}");
        if (images.Count > 0)
        {
            // The team's documents: every member of the team can read them (YAWBLE_TEAM_DOCS), and the person sees them there.
            var repos = bus.Repos ?? throw new RpcException("unavailable", "images need the app's data folder");
            text = Attachments.WithImages(text, Attachments.Save(Path.Combine(repos.TeamRoot(manager.Team), Teams.TeamDocuments.Folder), images));
        }
        var message = await bus.PersonTellAsync(manager.Team, manager.Name, text.Trim(), null, ct);
        return new JsonObject { ["seq"] = message.Seq, ["workflow"] = message.CorrelationId };
    }

    // ---------------------------------------------------------------- the Git dialog (the person's buttons only)

    private async Task<(Teams.TeamRepos Repos, TeamSummary Team)> GitTeamAsync(long teamId, CancellationToken ct)
    {
        var repos = bus.Repos ?? throw new RpcException("no_repositories", "this core has no team repositories (no app data folder)");
        var team = (await bus.TeamsAsync(ct)).SingleOrDefault(t => t.Id == teamId) ?? throw new RpcException("no_such_team", $"there is no team {teamId}");
        return (repos, team);
    }

    private async Task<JsonNode?> GitAsync<T>(JsonObject? p, Func<Teams.TeamRepos, long, string, Task<T>> action, CancellationToken ct)
    {
        var (repos, team) = await GitTeamAsync(Long(p, "teamId"), ct);
        try
        {
            return S(await action(repos, team.Id, team.Name));
        }
        catch (Exception e) when (e is Teams.GitException or ArgumentException or InvalidOperationException)
        {
            throw new RpcException("git_failed", e.Message);
        }
    }

    /// <summary>Tells the Manager to bring the team branch current with the base (Yawble ask-team), as a new workflow.</summary>
    private async Task<JsonNode?> AskTeamAsync(long teamId, CancellationToken ct, string? repo = null)
    {
        var (repos, team) = await GitTeamAsync(teamId, ct);
        string text;
        try
        {
            text = await repos.GitAskTeamTextAsync(team.Id, team.Name, ct, repo);
        }
        catch (Exception e) when (e is Teams.GitException or InvalidOperationException)
        {
            throw new RpcException("git_failed", e.Message);
        }
        return await TellManagerAsync(teamId, text, ct);
    }

    private static bool Flag(JsonObject? p, string name) => p?[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>Reads, or sets, whether the team's branches are pushed to its repository's origin after runs.</summary>
    private JsonNode PushSetting(long teamId, bool? on)
    {
        var repos = bus.Repos ?? throw new RpcException("unavailable", "team repositories need the app's data folder");
        if (on is { } v) repos.SetPushesToOrigin(teamId, v);
        return new JsonObject { ["on"] = repos.PushesToOrigin(teamId), ["origin"] = repos.RepoOf(teamId)?.Path };
    }

    /// <summary>
    /// Reads, or sets, "Bring branches up to the base before new work" (on by default), with what the team page shows:
    /// the note of a sync that could not be done (with its files) and how far the team branch is behind its base (read-only
    /// git in the team repository: no fetch).
    /// </summary>
    private async Task<JsonNode> SyncSettingAsync(long teamId, bool? on, CancellationToken ct)
    {
        var repos = bus.Repos ?? throw new RpcException("unavailable", "team repositories need the app's data folder");
        if (on is { } v) repos.SetSyncsToBase(teamId, v);
        Teams.TeamBehind? behind = null;
        if ((await bus.TeamsAsync(ct)).FirstOrDefault(t => t.Id == teamId) is { } team)
        {
            try { behind = await repos.BehindBaseAsync(teamId, team.Name, DateTimeOffset.UtcNow, ct); }
            catch (Exception e) when (e is Teams.GitException or IOException or InvalidOperationException) { }
        }
        return new JsonObject
        {
            ["on"] = repos.SyncsToBase(teamId), ["note"] = repos.SyncNote(teamId),
            ["baseBranch"] = repos.RepoOf(teamId)?.BaseBranch,
            ["behind"] = behind is { Behind: > 0 } b ? new JsonObject { ["teamBranch"] = b.TeamBranch, ["count"] = b.Behind, ["text"] = b.Text } : null,
        };
    }

    /// <summary>The team's repositories: its main one (the run's cwd) and the others by name (0022).</summary>
    private async Task<JsonNode> TeamReposListAsync(long teamId, CancellationToken ct)
    {
        var repos = bus.Repos ?? throw new RpcException("unavailable", "team repositories need the app's data folder");
        var main = repos.RepoOf(teamId);
        var extras = new JsonArray();
        foreach (var e in repos.ExtrasOf(teamId))
            extras.Add(new JsonObject { ["name"] = e.Name, ["path"] = e.Path, ["writable"] = e.Writable, ["baseBranch"] = e.BaseBranch, ["source"] = e.Source });
        // The main repository's origin, so the person sees which repository it is (as the extras show where they came from).
        string? origin = null;
        if (main is not null && Directory.Exists(main.Path))
        {
            var r = await repos.Git.TryRunAsync(main.Path, ["config", "--get", "remote.origin.url"], ct);
            if (r.ExitCode == 0 && r.Stdout.Length > 0) origin = Teams.PersonGitPush.Redact(r.Stdout);
        }
        return new JsonObject
        {
            ["main"] = main is null ? null : new JsonObject
            {
                ["path"] = main.Path, ["baseBranch"] = main.BaseBranch, ["managed"] = main.Managed, ["origin"] = origin,
                ["name"] = origin is null ? null : Teams.TeamRepos.SuggestRepoName(origin),
            },
            ["extras"] = extras,
        };
    }

    private async Task<JsonNode?> AddRepoAsync(long teamId, JsonObject? p, CancellationToken ct)
    {
        var repos = bus.Repos ?? throw new RpcException("unavailable", "team repositories need the app's data folder");
        var team = await TeamNameOf(teamId, ct);
        var writable = p?["writable"] is not JsonValue w || !w.TryGetValue<bool>(out var wb) || wb; // writable unless said otherwise
        try
        {
            var added = await repos.AddExtraAsync(teamId, team, OptStr(p, "name"), OptStr(p, "path"), OptStr(p, "url"), writable, ct);
            return new JsonObject { ["name"] = added.Name, ["path"] = added.Path, ["writable"] = added.Writable, ["baseBranch"] = added.BaseBranch };
        }
        catch (ArgumentException e) { throw new RpcException("invalid", e.Message); }
        catch (Teams.GitException e) { throw new RpcException("git_failed", e.Message); }
    }

    private async Task<JsonNode?> RemoveRepoAsync(long teamId, string name, CancellationToken ct)
    {
        var repos = bus.Repos ?? throw new RpcException("unavailable", "team repositories need the app's data folder");
        var team = await TeamNameOf(teamId, ct);
        var quiet = await bus.TeamQuietAsync(team, ct);
        if (quiet.Working.Count > 0)
            throw new RpcException("not_quiet", $"{string.Join(", ", quiet.Working)} {(quiet.Working.Count == 1 ? "is" : "are")} working; remove the repository when they have finished");
        var members = (await bus.AllMemberRowsAsync(ct)).Where(m => m.TeamId == teamId).Select(m => m.Name).ToList();
        try
        {
            var left = repos.RemoveExtra(teamId, team, members, name);
            return new JsonObject { ["left"] = new JsonArray([.. left.Select(l => (JsonNode?)JsonValue.Create(l))]) };
        }
        catch (ArgumentException e) { throw new RpcException("invalid", e.Message); }
    }

    private Teams.TeamDocuments? _docs;

    private Teams.TeamDocuments Docs() =>
        _docs ??= bus.Repos is { } repos ? new Teams.TeamDocuments(repos) : throw new RpcException("unavailable", "team documents need the app's data folder");

    private async Task<JsonNode?> DeleteDocAsync(long teamId, string path, CancellationToken ct)
    {
        Docs().Delete(await TeamNameOf(teamId, ct), path);
        return new JsonObject { ["ok"] = true };
    }

    private static async Task<JsonNode?> Then(Task work)
    {
        await work;
        return new JsonObject { ["ok"] = true };
    }

    /// <summary>A team is deleted only when the person typed its name (the confirmation, checked here too).</summary>
    private async Task<JsonNode?> DeleteTeamAsync(long teamId, string confirm, CancellationToken ct)
    {
        var team = await TeamNameOf(teamId, ct);
        if (!string.Equals(confirm.Trim(), team, StringComparison.Ordinal))
            throw new RpcException("not_confirmed", $"type the team's name, {team}, to delete it");
        var left = await bus.DeleteTeamAsync(team, ct);
        return new JsonObject { ["deleted"] = team, ["left"] = new JsonArray(left.Select(l => (JsonNode?)l).ToArray()) };
    }

    /// <summary>The env editor's rows, in order: [{name, value}].</summary>
    private static List<KeyValuePair<string, string>> EnvEntries(JsonObject? p) =>
        (p?["entries"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(e => new KeyValuePair<string, string>((e["name"]?.GetValue<string>() ?? "").Trim(), e["value"]?.GetValue<string>() ?? ""))
            .Where(e => e.Key.Length > 0).ToList();

    private async Task<string> TeamNameOf(long teamId, CancellationToken ct) =>
        (await bus.TeamsAsync(ct)).SingleOrDefault(t => t.Id == teamId)?.Name ?? throw new RpcException("no_such_team", $"there is no team {teamId}");

    private async Task<JsonNode?> DecisionsAsync(long teamId, CancellationToken ct)
    {
        var team = (await bus.TeamsAsync(ct)).SingleOrDefault(t => t.Id == teamId) ?? throw new RpcException("no_such_team", $"there is no team {teamId}");
        var list = new JsonArray();
        foreach (var m in await bus.OpenDecisionsAsync(team.Name, ct))
        {
            string? question = null;
            try { question = JsonNode.Parse(m.Payload)?["question"]?.GetValue<string>(); } catch (JsonException) { }
            list.Add(new JsonObject
            {
                ["seq"] = m.Seq,
                ["from"] = m.Source[(m.Source.IndexOf('/') + 1)..],
                ["question"] = question ?? m.Payload,
                ["workflow"] = m.CorrelationId,
                ["at"] = m.OccurredAt.ToString("O"),
            });
        }
        return list;
    }

    /// <summary>The person's answer joins the asker's workflow: an instruction to the asker, caused by the question.</summary>
    private async Task<JsonNode?> AnswerAsync(long teamId, long seq, string text, CancellationToken ct, IReadOnlyList<(string Name, byte[] Bytes)>? images = null)
    {
        var team = (await bus.TeamsAsync(ct)).SingleOrDefault(t => t.Id == teamId) ?? throw new RpcException("no_such_team", $"there is no team {teamId}");
        var question = (await bus.OpenDecisionsAsync(team.Name, ct)).SingleOrDefault(m => m.Seq == seq)
            ?? throw new RpcException("no_such_decision", "that question is answered already or is not this team's");
        var asker = question.Source[(question.Source.IndexOf('/') + 1)..];
        if (images is { Count: > 0 })
        {
            var repos = bus.Repos ?? throw new RpcException("unavailable", "images need the app's data folder");
            text = Attachments.WithImages(text, Attachments.Save(Path.Combine(repos.TeamRoot(team.Name), Teams.TeamDocuments.Folder), images));
        }
        var message = await bus.PersonTellAsync(team.Name, asker, text.Trim(), seq, ct);
        return new JsonObject { ["seq"] = message.Seq };
    }

    // ---------------------------------------------------------------- policy

    private MergeResult Effective(MemberRow m, IReadOnlyList<MemberRow> everyone) =>
        policies.Effective(m.Id, layouts.For(m, everyone), machineMemoryMb);

    private async Task<JsonNode?> SaveGlobalAsync(JsonObject? p, CancellationToken ct)
    {
        var doc = p?["policy"] as JsonObject ?? throw new RpcException("bad_params", "policy is required");
        var policy = GlobalPolicy.Parse(doc.ToJsonString());
        policies.SaveGlobal(policy, layouts.Sample(await bus.AllMemberRowsAsync(ct)));
        // A raised app.maxConcurrentAgents admits the next queued entries now, not at the next admission.
        bus.Reschedule();
        return policies.GetGlobal().ToJson();
    }

    private async Task<JsonNode?> MemberPolicyAsync(long memberId, CancellationToken ct)
    {
        var member = await MemberAsync(memberId, ct);
        var everyone = await bus.AllMemberRowsAsync(ct);
        var layout = layouts.For(member, everyone);
        var stored = policies.GetMember(memberId);
        var merged = policies.Effective(memberId, layout, machineMemoryMb);
        var e = merged.Policy;
        return new JsonObject
        {
            ["memberId"] = memberId,
            ["global"] = policies.GetGlobal().ToJson(),
            ["override"] = stored.Override.ToJson(),
            ["loosen"] = stored.Loosen.ToJson(),
            ["loosenSummary"] = S(stored.LoosenSummary),
            ["loosenApprovedAt"] = stored.LoosenApprovedAt?.ToString("O"),
            // The member's team's network allow list (policy.teamHosts has who added each and when).
            ["teamHosts"] = S(policies.TeamHostsOfMember(memberId)),
            ["effective"] = new JsonObject
            {
                ["readonly"] = S(e.Readonly),
                ["readwrite"] = S(e.Readwrite),
                ["denied"] = S(e.Denied),
                ["allowHosts"] = S(e.AllowHosts),
                ["compute"] = S(e.Compute),
                ["clipboard"] = e.Clipboard,
                ["loosened"] = S(e.Loosened),
                ["notes"] = S(e.Notes),
            },
            ["implicit"] = new JsonObject
            {
                ["readwrite"] = S(layout.ImplicitReadwrite),
                ["readonly"] = S(layout.ImplicitReadonly),
                ["denied"] = S(layout.ImplicitDenied),
            },
            ["widening"] = S(merged.Widening),
            ["violations"] = S(merged.Violations),
            // The one-line network summary ("model + bus + 2 hosts").
            ["net"] = PolicyBanner.Net(e),
            ["backend"] = PolicyBanner.Backend(layouts.Os),
            ["machineMemoryMb"] = machineMemoryMb,
        };
    }

    private async Task<(MemberRow Member, RunLayout Layout, PolicyPatch Patch)> PatchArgs(JsonObject? p, string key, PatchKind kind, CancellationToken ct)
    {
        var member = await MemberAsync(Long(p, "memberId"), ct);
        var layout = layouts.For(member, await bus.AllMemberRowsAsync(ct));
        var json = p?[key] is JsonObject o ? o.ToJsonString() : null;
        return (member, layout, PolicyPatch.Parse(json, kind));
    }

    private async Task<JsonNode?> SaveOverrideAsync(JsonObject? p, CancellationToken ct)
    {
        var (member, layout, patch) = await PatchArgs(p, "override", PatchKind.Override, ct);
        policies.SaveOverride(member.Id, patch, layout);
        return await MemberPolicyAsync(member.Id, ct);
    }

    private async Task<JsonNode?> PreviewLoosenAsync(JsonObject? p, CancellationToken ct)
    {
        var (member, layout, patch) = await PatchArgs(p, "loosen", PatchKind.Loosen, ct);
        return new JsonObject { ["loosened"] = S(policies.PreviewLoosen(member.Id, patch, layout)) };
    }

    /// <summary>policy.teamHosts {teamId}: { teamId, hosts: [{ host, addedBy, addedVia, addedAt }] }, oldest first.</summary>
    private async Task<JsonNode?> TeamHostsAsync(long teamId, CancellationToken ct)
    {
        await TeamNameOf(teamId, ct);
        return new JsonObject
        {
            ["teamId"] = teamId,
            ["hosts"] = new JsonArray(policies.TeamHosts(teamId).Select(h => (JsonNode?)new JsonObject
            {
                ["host"] = h.Host, ["addedBy"] = h.AddedBy, ["addedVia"] = h.AddedVia, ["addedAt"] = h.AddedAt,
            }).ToArray()),
        };
    }

    /// <summary>policy.removeTeamHost {teamId, host}: takes it off the team's list (the dispatcher audits the call); answers as policy.teamHosts.</summary>
    private async Task<JsonNode?> RemoveTeamHostAsync(long teamId, string host, CancellationToken ct)
    {
        await TeamNameOf(teamId, ct);
        if (!policies.RemoveTeamHost(teamId, host))
            throw new RpcException("not_found", $"'{host.Trim()}' is not on this team's network allow list");
        return await TeamHostsAsync(teamId, ct);
    }

    private async Task<JsonNode?> SaveLoosenAsync(JsonObject? p, CancellationToken ct)
    {
        var (member, layout, patch) = await PatchArgs(p, "loosen", PatchKind.Loosen, ct);
        var confirmed = (p?["confirmed"] as JsonArray ?? []).Select(n => n?.GetValue<string>() ?? "").ToList();
        policies.SaveLoosen(member.Id, patch, layout, confirmed);
        return await MemberPolicyAsync(member.Id, ct);
    }

    // ---------------------------------------------------------------- terminals

    private async Task<JsonNode?> BannerAsync(long memberId, CancellationToken ct)
    {
        var member = await MemberAsync(memberId, ct);
        var merged = Effective(member, await bus.AllMemberRowsAsync(ct));
        var card = (await bus.AgentCardsAsync(member.TeamId, ct)).Single(c => c.Member.Id == memberId);
        return new JsonObject
        {
            ["memberId"] = memberId,
            ["channel"] = TerminalHub.MemberChannel(memberId),
            ["title"] = PolicyBanner.Title(member, layouts.Os, merged.Policy),
            ["details"] = S(PolicyBanner.Details(member, merged.Policy)),
            ["loosened"] = S(merged.Policy.Loosened),
            ["state"] = card.State,
            ["queuePosition"] = card.QueuePosition,
            ["queueLength"] = card.QueueLength,
            ["runId"] = card.RunId,
            ["interactive"] = card.Interactive,
            ["lastRunState"] = card.LastRunState,
            ["lastRunReason"] = card.LastRunReason,
        };
    }

    /// <summary>The optional cols/rows of a session start, clamped like terminal.resize; null when absent.</summary>
    private static Launch.PtySize? SizeOf(JsonObject? p) =>
        p?["cols"] is null || p["rows"] is null ? null
            : new Launch.PtySize((ushort)Math.Clamp(Long(p, "rows"), 2, 500), (ushort)Math.Clamp(Long(p, "cols"), 2, 1000));

    private async Task<JsonNode?> OpenInteractiveAsync(long memberId, Launch.PtySize? size, CancellationToken ct)
    {
        var member = await MemberAsync(memberId, ct);
        if (await bus.ActiveRunOfAsync(memberId, ct) is not null)
            throw new RpcException("running", $"{member.Name} is running a batch; the window shows it live");
        await interactive.StartAsync(member, TerminalHub.MemberChannel(memberId), ct, size);
        return terminals.Attach(TerminalHub.MemberChannel(memberId));
    }

    private async Task<JsonNode?> StopAsync(long memberId, CancellationToken ct)
    {
        // A real sandbox stops through the launcher, so the run ends "stopped" rather than interrupted.
        var member = await MemberAsync(memberId, ct);
        var stopped = stopSandbox?.Invoke(member.Team, member.Name) ?? false;
        if (!stopped) stopped = await bus.StopMemberRunAsync(memberId, ct);
        if (!stopped) stopped = await terminals.StopAsync(TerminalHub.MemberChannel(memberId));
        return new JsonObject { ["stopped"] = stopped };
    }

    private async Task<JsonNode?> StartHostTerminalAsync(string kind, CancellationToken ct)
    {
        if (kind is not (IHostTerminals.ClaudeSetupToken or IHostTerminals.GrokLogin))
            throw new RpcException("bad_kind", $"no host pane named '{kind}'");
        var channel = TerminalHub.HostChannel(kind);
        if (terminals.IsLive(channel)) throw new RpcException("busy", "that pane is already running");
        await hostTerminals.StartAsync(kind, channel, code =>
        {
            if (kind == IHostTerminals.GrokLogin) credentials.GrokLoginFinished(code);
        }, ct);
        return new JsonObject { ["channel"] = channel };
    }

    // ---------------------------------------------------------------- plumbing

    private static string Channel(JsonObject? p)
    {
        var channel = Str(p, "channel");
        if (!channel.StartsWith("member:", StringComparison.Ordinal) && !channel.StartsWith("host:", StringComparison.Ordinal)
            && !channel.StartsWith(Agents.AgentTestSessions.ChannelPrefix, StringComparison.Ordinal))
            throw new RpcException("bad_params", $"no terminal channel '{channel}'");
        return channel;
    }

    private JsonNode? Then(Action action)
    {
        action();
        return S(credentials.Status());
    }

    private static JsonNode? Done(Action action)
    {
        action();
        return new JsonObject { ["ok"] = true };
    }

    private Skills.SkillCatalog Catalog() => skills ?? throw new RpcException("unavailable", "skills are not available in this core");

    private static JsonNode? S<T>(T value) => JsonSerializer.SerializeToNode(value, RpcDispatcher.Json);

    private static string Str(JsonObject? p, string key) =>
        OptStr(p, key) ?? throw new RpcException("bad_params", $"{key} is required");

    private static string? OptStr(JsonObject? p, string key) =>
        p?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? OptLong(JsonObject? p, string name) => p?[name] is JsonValue v && WholeNumber(v, out var l) ? l : null;

    private static long Long(JsonObject? p, string key) =>
        p?[key] is JsonValue v && WholeNumber(v, out var l) ? l : throw new RpcException("bad_params", $"{key} must be a number");

    /// <summary>
    /// A whole number, however the value was made: parsed from JSON (the window, the cloud) or built in memory by the
    /// core itself (the Concierge's tools pass an int, which a JsonValue does not convert to long on its own).
    /// </summary>
    internal static bool WholeNumber(JsonValue v, out long l)
    {
        if (v.TryGetValue(out l)) return true;
        if (v.TryGetValue<int>(out var i)) { l = i; return true; }
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) < 9e15) { l = (long)d; return true; }
        l = 0;
        return false;
    }
}
