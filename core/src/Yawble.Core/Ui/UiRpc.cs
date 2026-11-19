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
    Monitor.StepFeed? steps = null,
    Monitor.CodeEvents? codeEvents = null)
{
    public static readonly string[] Prefixes = ["app.", "monitor.", "teams.", "members.", "bus.", "policy.", "credentials.", "hostTerminal.", "terminal.", "skills.", "triggers.", "git.", "docs.", "timeline.", "workflows."];

    /// <summary>Other things that need the person, counted in app.overview's needsYou (the doctor's failing checks).</summary>
    public Func<int>? ExtraNeedsYou { get; set; }

    /// <summary>teams.agents' agentModel per card: the model the member's pill names and where the name comes from.</summary>
    public Agents.MemberModels? Models { get; set; }

    /// <summary>The methods here, by prefix, and B085's workflowExtend under its contract name (also bus.workflowExtend).</summary>
    public static bool Handles(string method) => Prefixes.Any(p => method.StartsWith(p, StringComparison.Ordinal)) || method is "workflowExtend";

    /// <summary>B075: { teamId, team (id or name), repo? (an extra's name; absent: main), kind, commit?, branch?, file?, from?, to?, pr? } -> { url: string|null }.</summary>
    public const string WebLinkMethod = "git.webLink";

    public async Task<JsonNode?> DispatchAsync(string method, JsonObject? p, CancellationToken ct)
    {
        try
        {
            return method switch
            {
                "app.overview" => await OverviewAsync(ct),

                "monitor.messages" => await MonitorMessagesAsync(OptLong(p, "after") ?? 0, (int)(OptLong(p, "limit") ?? 200), ct),
                "monitor.steps" => MonitorSteps(OptLong(p, "after") ?? 0, (int)(OptLong(p, "sinceMinutes") ?? 30), (int)(OptLong(p, "limit") ?? 2000)),
                "monitor.codeEvents" => MonitorCodeEvents(p),

                "teams.list" => S(await bus.TeamsAsync(ct)),
                "teams.create" => await CreateTeamAsync(p, ct),
                // Settings > Teams: the default agents for new teams, the member-name list and its country.
                "teams.defaults" => S(await bus.TeamDefaultsAsync(ct)),
                "teams.setDefaults" => S(await SetTeamDefaultsAsync(p, ct)),
                "teams.generateNames" => await GenerateNamesAsync(p, ct),
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
                // B085: { teamId } reads the team's workflowExpiryDays (default 3), { teamId, days: 1-365 } sets it; both return { days }.
                "teams.workflowExpiry" => await WorkflowExpiryAsync(p, ct),
                "teams.env" => S(await bus.TeamEnvAsync(await TeamNameOf(Long(p, "teamId"), ct), ct)),
                "teams.setEnv" => S(await bus.SetTeamEnvAsync(await TeamNameOf(Long(p, "teamId"), ct), EnvEntries(p), ct)),
                "teams.approvals" => S(await bus.TeamApprovalsAsync(await TeamNameOf(Long(p, "teamId"), ct), ct)),
                "teams.addApproval" => S(await bus.AddTeamApprovalAsync(await TeamNameOf(Long(p, "teamId"), ct), Str(p, "text"),
                    Provenance.Actor.Current?.User ?? Provenance.Actor.LocalUser, ct)),
                "teams.removeApproval" => await Then(bus.RemoveTeamApprovalAsync(await TeamNameOf(Long(p, "teamId"), ct), Long(p, "id"), ct)),
                "teams.repos" => await TeamReposListAsync(Long(p, "teamId"), ct),
                "teams.addRepo" => await AddRepoAsync(Long(p, "teamId"), p, ct),
                "teams.removeRepo" => await RemoveRepoAsync(Long(p, "teamId"), Str(p, "name"), ct),
                // B075: a repository's provider override and link pattern (Team settings > Repositories).
                "teams.setRepo" => await SetRepoAsync(Long(p, "teamId"), p, ct),
                "git.webLink" => await WebLinkAsync(p, ct),

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
                // B085 Extend: resets an open workflow's idle clock. teamId is optional (the workflow names its team).
                "workflowExtend" or "bus.workflowExtend" => S(await ExtendAsync(p, ct)),
                // B104, the Workflows page: every team's workflows, and one in full (reads only; contract B104/contract.md).
                "workflows.list" => await WorkflowsListAsync(p, ct),
                "workflows.get" => await WorkflowGetAsync(Long(p, "workflow"), ct),

                "policy.getGlobal" => policies.GetGlobal().ToJson(),
                "policy.saveGlobal" => await SaveGlobalAsync(p, ct),
                "policy.getMember" => await MemberPolicyAsync(Long(p, "memberId"), ct),
                "policy.saveOverride" => await SaveOverrideAsync(p, ct),
                "policy.previewLoosen" => await PreviewLoosenAsync(p, ct),
                "policy.saveLoosen" => await SaveLoosenAsync(p, ct),
                // The team's network allow list (0041): "Allow for this team" adds to it; the team's settings list it with Remove.
                "policy.teamHosts" => await TeamHostsAsync(Long(p, "teamId"), ct),
                "policy.removeTeamHost" => await RemoveTeamHostAsync(Long(p, "teamId"), Str(p, "host"), ct),
                // The policy editors' check of each host typed: the floor's own rule, so the window never validates a second way.
                "policy.checkHosts" => CheckHosts(p),

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
                "git.deleteBranches" => await DeleteBranchesAsync(p, ct),
                "git.tidyPlan" => await TidyPlanAsync(p, ct),
                "git.branchRealign" => await BranchRealignAsync(p, ct),
                // B072, the commit view: reads only.
                "git.commitShow" => await CommitShowAsync(p, ct),
                "git.commitDiff" => await CommitDiffAsync(p, ct),
            "git.commitsResolve" => await CommitsResolveAsync(p, ct),
                // B073: the commits behind a count, and the previews of Merge to main and Delete: reads only.
                "git.commitRange" => await CommitRangeAsync(p, ct),
                "git.mergePreview" => await MergePreviewAsync(p, ct),
                "git.mergeDiff" => await MergeDiffAsync(p, ct),
                "git.deletePreview" => await DeletePreviewAsync(p, ct),
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

    /// <summary>
    /// teams.create {name, managerName?, managerCli?, members?: [{name, cli}], repoPath? | repoUrl?}. What is not given
    /// comes from Settings > Teams (the saved Manager, the member rows ticked "On new teams"); a member name that is a
    /// job word alone gets a first name from the member-name list, or the team is refused before anything is made.
    /// </summary>
    private async Task<JsonNode?> CreateTeamAsync(JsonObject? p, CancellationToken ct)
    {
        var name = Str(p, "name");
        var members = p?["members"] is JsonArray given
            ? given.Select(n => (Name: n?["name"]?.GetValue<string>() ?? "", Cli: n?["cli"]?.GetValue<string>())).ToList()
            : null;
        var plan = await bus.PlanNewTeamAsync(OptStr(p, "managerName"), OptStr(p, "managerCli"), members, ct);
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
        var teamId = await bus.CreatePlannedTeamAsync(name, plan, repoPath, ct);
        return new JsonObject { ["teamId"] = teamId };
    }

    /// <summary>teams.setDefaults {manager?: {name, cli}, members?: [{name, cli, onNewTeams}], memberNamesText?, country?} -> {saved, rejected}.</summary>
    private Task<TeamDefaultsSaved> SetTeamDefaultsAsync(JsonObject? p, CancellationToken ct)
    {
        var manager = p?["manager"] is JsonObject m
            ? new ManagerDefault(OptStr(m, "name") ?? "", OptStr(m, "cli") ?? "")
            : null;
        var members = p?["members"] is JsonArray rows
            ? rows.Select(r => new MemberDefault(OptStr(r as JsonObject, "name") ?? "", OptStr(r as JsonObject, "cli") ?? "claude",
                r?["onNewTeams"] is JsonValue v && v.TryGetValue<bool>(out var on) && on)).ToList()
            : null;
        var text = p?["memberNamesText"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : null;
        return bus.SetTeamDefaultsAsync(manager, members, text, OptStr(p, "country"), ct);
    }

    /// <summary>teams.generateNames {country} -> {names}: popular first names of the country from the Concierge's default agent, not saved.</summary>
    private async Task<JsonNode?> GenerateNamesAsync(JsonObject? p, CancellationToken ct)
    {
        var country = Teams.MemberNameList.Country(Str(p, "country")) ?? throw new RpcException("bad_country", $"'{Str(p, "country")}' is not a country of the list");
        var generate = bus.NameGenerator ?? throw new RpcException("unavailable", "generating names needs the app's agents");
        var names = await generate(country.Code, ct);
        return new JsonObject { ["names"] = new JsonArray(names.Select(n => (JsonNode?)n).ToArray()) };
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
            if (Models is { } models) node["agentModel"] = Agents.MemberModels.Json(models.For(card.Member));
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
    /// report said it is blocked), "failed" (its last run failed), "stopped" (the person stopped its last run: shown
    /// quietly, not as a failure; B097); null when nothing. The faces' rings show it.
    /// </summary>
    internal static string? Attention(AgentCard card, IReadOnlySet<string> asking, IReadOnlyDictionary<string, Message> reports)
    {
        if (asking.Contains(card.Member.Name)) return "decision";
        if (card.State == "running") return null;
        if (reports.TryGetValue(card.Member.Name, out var last) && last.Type == MessageTypes.Blocked) return "blocked";
        if (card.LastRunStopped) return "stopped";
        return card.LastRunFailed ? "failed" : null;
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

    /// <summary>
    /// monitor.codeEvents {team?, since?, until?, after?, limit?}: what really reached the team repository (B079,
    /// Monitor/CodeEvents), oldest first: [{id, kind: commit|merge-team|landed|push, team, member?, branch, sha, message,
    /// files: [{path, status, oldPath?}], at, recordedAt, workflowId?, into?, branches?}]. since/until are ISO times
    /// filtering <c>at</c>; after is an id cursor. Without a store there are none.
    /// </summary>
    private JsonArray MonitorCodeEvents(JsonObject? p)
    {
        var array = new JsonArray();
        if (codeEvents is null) return array;
        static DateTimeOffset? Time(JsonObject? p, string key)
        {
            if (OptStr(p, key) is not { } text) return null;
            return DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
                ? t : throw new RpcException("bad_params", $"{key} must be an ISO time");
        }
        foreach (var e in codeEvents.Read(OptStr(p, "team"), Time(p, "since"), Time(p, "until"), OptLong(p, "after") ?? 0, (int)(OptLong(p, "limit") ?? 2000)))
            array.Add(e.ToJson());
        return array;
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
                // A run the person stopped is recorded failed with class "stopped": the feed says "stopped" (B097).
                MessageTypes.Failed => (Field("failureClass") == FailureClasses.Stopped ? "stopped" : "failed", Field("reason")),
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
        var row = new JsonObject
        {
            ["seq"] = m.Seq, ["kind"] = kind, ["team"] = about.team, ["member"] = about.member, ["from"] = from,
            ["text"] = text, ["workflow"] = m.CorrelationId, ["at"] = m.OccurredAt.ToString("O"),
        };
        // A run's end row says how it ended (B097): failureClass is null for a completed run.
        if (m.Type is MessageTypes.Failed or MessageTypes.Completed)
            row["failureClass"] = m.Type == MessageTypes.Failed ? Field("failureClass") ?? FailureClasses.Failed : null;
        return row;
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

    /// <summary>
    /// git.deleteBranches {teamId, repo?, branches, remote, unmergedOk?, confirm?}, two-step like the Concierge's
    /// git_branches_delete. Without confirm: {plan, token, empty}, nothing changed. With confirm: the plan is made again and
    /// run only when its key is the token shown, {result}; otherwise error "stale" (show the plan again).
    /// </summary>
    private async Task<JsonNode?> DeleteBranchesAsync(JsonObject? p, CancellationToken ct)
    {
        static List<string> Names(JsonObject? p, string key) =>
            (p?[key] as JsonArray ?? []).Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "").Where(s => s.Length > 0).Distinct().ToList();
        var branches = Names(p, "branches");
        // An empty list would mean "every merged branch" to the planner: the dialog always names them.
        if (branches.Count == 0) throw new RpcException("bad_params", "name the branches to delete");
        var (repos, team) = await GitTeamAsync(Long(p, "teamId"), ct);
        var members = (await bus.AgentCardsAsync(team.Id, ct)).Select(c => c.Member.Name).ToList();
        Teams.TeamGitDelete done;
        try
        {
            done = await repos.GitDeleteBranchesAsync(team.Id, team.Name, members, OptStr(p, "repo"), branches, Flag(p, "remote"), Names(p, "unmergedOk"), OptStr(p, "confirm"), ct);
        }
        catch (Exception e) when (e is Teams.GitException or ArgumentException or InvalidOperationException)
        {
            throw new RpcException("git_failed", e.Message);
        }
        if (done.Stale) throw new RpcException("stale", "The branches changed since the list was shown; nothing was deleted. Look at the new list and confirm again.");
        return done.Result is { } result
            ? new JsonObject { ["result"] = result }
            : new JsonObject { ["plan"] = done.Plan, ["token"] = done.Token, ["empty"] = done.Empty };
    }

    /// <summary>B067: the team of a tidy call, by teamId (as every git RPC) or by its name (team).</summary>
    private async Task<(Teams.TeamRepos Repos, TeamSummary Team)> TidyTeamAsync(JsonObject? p, CancellationToken ct)
    {
        if (OptLong(p, "teamId") is { } id) return await GitTeamAsync(id, ct);
        var name = OptStr(p, "team") ?? throw new RpcException("bad_params", "teamId (or team) is required");
        var team = (await bus.TeamsAsync(ct)).FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                   ?? throw new RpcException("no_such_team", $"there is no team '{name}'");
        return await GitTeamAsync(team.Id, ct);
    }

    /// <summary>
    /// git.tidyPlan {teamId | team} -> {repos:[{repo, branches:[{name, where:[local|origin|clone:&lt;member&gt;], class, proof?,
    /// commits?[{sha,subject}], teamSha?, cloneSha?, member?}], notes?}]} (B067). Reads only.
    /// </summary>
    private async Task<JsonNode?> TidyPlanAsync(JsonObject? p, CancellationToken ct)
    {
        var (repos, team) = await TidyTeamAsync(p, ct);
        Teams.TeamGitTidyPlan plan;
        try
        {
            plan = await repos.GitTidyPlanAsync(team.Id, team.Name, ct);
        }
        catch (Exception e) when (e is Teams.GitException or ArgumentException or InvalidOperationException or IOException)
        {
            throw new RpcException("git_failed", e.Message);
        }
        return TidyPlanJson(plan);
    }

    /// <summary>The tidy plan in the window's contract (git.tidyPlan): absent optional fields are left out.</summary>
    public static JsonObject TidyPlanJson(Teams.TeamGitTidyPlan plan)
    {
        var list = new JsonArray();
        foreach (var r in plan.Repos)
        {
            var branches = new JsonArray();
            foreach (var b in r.Branches)
            {
                var o = new JsonObject { ["name"] = b.Name, ["where"] = new JsonArray(b.Where.Select(w => (JsonNode?)w).ToArray()), ["class"] = b.Class };
                if (b.Proof is not null) o["proof"] = b.Proof;
                if (b.Commits is not null) o["commits"] = new JsonArray(b.Commits.Select(c => (JsonNode?)new JsonObject { ["sha"] = c.Sha, ["subject"] = c.Subject }).ToArray());
                if (b.TeamSha is not null) o["teamSha"] = b.TeamSha;
                if (b.CloneSha is not null) o["cloneSha"] = b.CloneSha;
                if (b.Member is not null) o["member"] = b.Member;
                branches.Add(o);
            }
            var repo = new JsonObject { ["repo"] = r.Repo, ["baseBranch"] = r.BaseBranch, ["branches"] = branches };
            if (r.Notes.Count > 0) repo["notes"] = new JsonArray(r.Notes.Select(n => (JsonNode?)n).ToArray());
            list.Add(repo);
        }
        return new JsonObject { ["repos"] = list };
    }

    /// <summary>
    /// git.branchRealign {teamId | team, repo, member, expectOld?, expectNew?} -> {oldSha, newSha, ...} (B067): the window asked
    /// the person's yes. Refused with code <c>running</c> while the member runs (or has its interactive session open),
    /// <c>stale</c> when the commits are not the ones shown, and the other refusals' own codes.
    /// </summary>
    private async Task<JsonNode?> BranchRealignAsync(JsonObject? p, CancellationToken ct)
    {
        var (repos, team) = await TidyTeamAsync(p, ct);
        var memberName = OptStr(p, "member") is { Length: > 0 } m ? m : throw new RpcException("bad_params", "member is required");
        var member = (await bus.AgentCardsAsync(team.Id, ct)).Select(c => c.Member).FirstOrDefault(x => string.Equals(x.Name, memberName.Trim(), StringComparison.OrdinalIgnoreCase))
                     ?? throw new RpcException("no_such_member", $"{team.Name} has no member '{memberName}'");
        Teams.TeamGitRealign done;
        try
        {
            done = await repos.GitBranchRealignAsync(team.Id, team.Name, OptStr(p, "repo"), member.Name, async token =>
                await bus.ActiveRunOfAsync(member.Id, token) is { } run ? $"run {run}"
                : terminals.IsLive(TerminalHub.MemberChannel(member.Id)) ? "its interactive session is open" : null,
                expectOld: OptStr(p, "expectOld"), expectNew: OptStr(p, "expectNew"), ct: ct);
        }
        catch (Teams.TeamGitRefusal e)
        {
            throw new RpcException(e.Code, e.Message);
        }
        catch (Exception e) when (e is Teams.GitException or ArgumentException or InvalidOperationException or IOException)
        {
            throw new RpcException("git_failed", e.Message);
        }
        return new JsonObject
        {
            ["oldSha"] = done.OldSha, ["newSha"] = done.NewSha, ["branch"] = done.Branch, ["repo"] = done.Repo, ["member"] = done.Member,
            ["originOldSha"] = done.OriginOldSha, ["originUpdated"] = done.OriginUpdated, ["changed"] = done.Changed, ["message"] = done.Message,
        };
    }

    /// <summary>
    /// git.commitShow {team | teamId, sha, repo?} (B072): a commit of the team's repository, read with plain git: what it
    /// is, where it is, its files. {found: false, goneReason, sha} when git does not have it. codeShown is true here; the
    /// Cloud Link sets it false when members' code is not shown to cloud viewers (and refuses git.commitDiff).
    /// </summary>
    private async Task<JsonNode?> CommitShowAsync(JsonObject? p, CancellationToken ct)
    {
        var (repos, team) = await TidyTeamAsync(p, ct);
        var sha = OptStr(p, "sha") ?? throw new RpcException("bad_params", "sha is required");
        return await CommitCallAsync(async () => (await repos.GitCommitShowAsync(team.Id, team.Name, sha, ct, OptStr(p, "repo"))).ToJson());
    }

    /// <summary>
    /// git.commitDiff {team | teamId, sha, path, repo?} (B072): one file's diff in the commit (a merge: against its first
    /// parent), as hunks of lines for side-by-side and inline views; binary or too large: a summary and no hunks.
    /// Errors: gone (git no longer has the commit), no_such_file (the commit does not change that path).
    /// </summary>
    private async Task<JsonNode?> CommitDiffAsync(JsonObject? p, CancellationToken ct)
    {
        var (repos, team) = await TidyTeamAsync(p, ct);
        var sha = OptStr(p, "sha") ?? throw new RpcException("bad_params", "sha is required");
        var path = OptStr(p, "path") ?? throw new RpcException("bad_params", "path is required");
        return await CommitCallAsync(async () => (await repos.GitCommitDiffAsync(team.Id, team.Name, sha, path, ct, OptStr(p, "repo"))).ToJson());
    }

    /// <summary>
    /// git.commitsResolve {team? | teamId?, ids:[...]} (B074): which of a batch of 7-40 digit hex IDs are commits of the
    /// team's repositories (no team: every team's), {max, results:[{id, valid, matches:[{team, teamId, repo, fullId,
    /// shortId, title, pushed}]}], errors?}. At most <see cref="Teams.TeamRepos.MaxResolveIds"/> distinct IDs. Reads only.
    /// Contract: B074/contract.md in the team's documents.
    /// </summary>
    private async Task<JsonNode?> CommitsResolveAsync(JsonObject? p, CancellationToken ct)
    {
        if (p?["ids"] is not JsonArray array) throw new RpcException("bad_params", "ids is required: an array of commit IDs");
        var ids = new List<string>();
        foreach (var item in array)
        {
            if (item is not JsonValue v || !v.TryGetValue<string>(out var s)) throw new RpcException("bad_params", "ids holds strings only");
            ids.Add(s);
        }
        Teams.TeamRepos repos;
        List<(long, string)> teams;
        if (OptLong(p, "teamId") is not null || OptStr(p, "team") is not null)
        {
            var (r, team) = await TidyTeamAsync(p, ct);
            repos = r;
            teams = [(team.Id, team.Name)];
        }
        else
        {
            repos = bus.Repos ?? throw new RpcException("no_repositories", "this core has no team repositories (no app data folder)");
            teams = [.. (await bus.TeamsAsync(ct)).Select(t => (t.Id, t.Name))];
        }
        return await CommitCallAsync(async () => (await repos.ResolveCommitIdsAsync(teams, ids, ct)).ToJson());
    }

    /// <summary>git.commitRange {team | teamId, repo?, from, to, limit?} (B073): the commits on to that from lacks (the pair a count gives), newest first.</summary>
    private async Task<JsonNode?> CommitRangeAsync(JsonObject? p, CancellationToken ct)
    {
        var (repos, team) = await TidyTeamAsync(p, ct);
        var from = OptStr(p, "from") ?? throw new RpcException("bad_params", "from is required");
        var to = OptStr(p, "to") ?? throw new RpcException("bad_params", "to is required");
        var limit = OptLong(p, "limit") is { } l ? (int)Math.Clamp(l, 1, Teams.GitPreviews.MaxLimit) : (int?)null;
        return await CommitCallAsync(async () => S(await repos.GitCommitRangeAsync(team.Id, team.Name, from, to, limit, ct, OptStr(p, "repo")))!.AsObject());
    }

    /// <summary>git.mergePreview {team | teamId, repo?} (B073): what Merge to main would do, its commits and combined diff; nothing moves.</summary>
    private async Task<JsonNode?> MergePreviewAsync(JsonObject? p, CancellationToken ct)
    {
        var (repos, team) = await TidyTeamAsync(p, ct);
        return await CommitCallAsync(async () =>
        {
            var preview = await repos.GitMergePreviewAsync(team.Id, team.Name, ct, OptStr(p, "repo"));
            var o = S(preview)!.AsObject();
            o["files"] = new JsonArray([.. preview.Files.Select(f => (JsonNode)f.ToJson())]);
            return o;
        });
    }

    /// <summary>git.mergeDiff {team | teamId, repo?, from, to, path} (B073): one file of a merge preview's combined diff, shaped as git.commitDiff.</summary>
    private async Task<JsonNode?> MergeDiffAsync(JsonObject? p, CancellationToken ct)
    {
        var (repos, team) = await TidyTeamAsync(p, ct);
        var from = OptStr(p, "from") ?? throw new RpcException("bad_params", "from is required");
        var to = OptStr(p, "to") ?? throw new RpcException("bad_params", "to is required");
        var path = OptStr(p, "path") ?? throw new RpcException("bad_params", "path is required");
        return await CommitCallAsync(async () => (await repos.GitMergeDiffAsync(team.Id, team.Name, from, to, path, ct, OptStr(p, "repo"))).ToJson());
    }

    /// <summary>git.deletePreview {team | teamId, repo?, branch} (B073): what deleting the local branch loses.</summary>
    private async Task<JsonNode?> DeletePreviewAsync(JsonObject? p, CancellationToken ct)
    {
        var (repos, team) = await TidyTeamAsync(p, ct);
        var branch = OptStr(p, "branch") ?? throw new RpcException("bad_params", "branch is required");
        return await CommitCallAsync(async () => S(await repos.GitDeletePreviewAsync(team.Id, team.Name, branch, ct, OptStr(p, "repo")))!.AsObject());
    }

    private static async Task<JsonNode?> CommitCallAsync(Func<Task<JsonObject>> read)
    {
        try
        {
            return await read();
        }
        catch (Teams.TeamGitRefusal e)
        {
            throw new RpcException(e.Code, e.Message);
        }
        catch (ArgumentException e)
        {
            throw new RpcException("bad_params", e.Message);
        }
        catch (Exception e) when (e is Teams.GitException or InvalidOperationException or IOException)
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
            ["behind"] = behind is { Behind: > 0 } b ? new JsonObject { ["teamBranch"] = b.TeamBranch, ["count"] = b.Behind, ["text"] = b.Text, ["from"] = b.Range?.From, ["to"] = b.Range?.To } : null,
        };
    }

    /// <summary>The team's repositories: its main one (the run's cwd) and the others by name (0022).</summary>
    private async Task<JsonNode> TeamReposListAsync(long teamId, CancellationToken ct)
    {
        var repos = bus.Repos ?? throw new RpcException("unavailable", "team repositories need the app's data folder");
        var extras = new JsonArray();
        foreach (var e in repos.ExtrasOf(teamId)) extras.Add(await ExtraRepoJsonAsync(repos, teamId, e, ct));
        return new JsonObject { ["main"] = await MainRepoJsonAsync(repos, teamId, ct), ["extras"] = extras };
    }

    /// <summary>teams.repos' main entry, or null before the team's repository is made. Its origin (redacted) shows which repository it is.</summary>
    private static async Task<JsonObject?> MainRepoJsonAsync(Teams.TeamRepos repos, long teamId, CancellationToken ct)
    {
        if (repos.RepoOf(teamId) is not { } main) return null;
        var host = await repos.HostViewAsync(teamId, Teams.TeamRepos.MainRepoName, ct);
        var origin = host.Origin is null ? null : Teams.PersonGitPush.Redact(host.Origin);
        var o = new JsonObject
        {
            ["path"] = main.Path, ["baseBranch"] = main.BaseBranch, ["managed"] = main.Managed, ["origin"] = origin,
            ["name"] = origin is null ? null : Teams.TeamRepos.SuggestRepoName(origin),
        };
        AddHost(o, host);
        return o;
    }

    /// <summary>One entry of teams.repos' extras.</summary>
    private static async Task<JsonObject> ExtraRepoJsonAsync(Teams.TeamRepos repos, long teamId, Teams.ExtraRepo e, CancellationToken ct)
    {
        var o = new JsonObject { ["name"] = e.Name, ["path"] = e.Path, ["writable"] = e.Writable, ["baseBranch"] = e.BaseBranch, ["source"] = e.Source };
        AddHost(o, await repos.HostViewAsync(teamId, e.Name, ct));
        return o;
    }

    /// <summary>
    /// B075's fields of a repository (card 447's contract): provider = the person's OVERRIDE (null: detected),
    /// providerDetected = what the origin says, linkPattern, prSupported and prUnavailable (why not, or null).
    /// </summary>
    private static void AddHost(JsonObject o, Teams.RepoHostView h)
    {
        o["provider"] = h.Override is { } k ? Teams.RepoProviders.WireName(k) : null;
        o["providerDetected"] = Teams.RepoProviders.WireName(h.Detected);
        o["linkPattern"] = h.LinkPattern;
        o["prSupported"] = h.PrSupported;
        o["prUnavailable"] = h.PrUnavailable;
    }

    /// <summary>
    /// teams.setRepo { teamId, name (an extra's name; absent, null or "main": the main repository), provider (github,
    /// gitlab, bitbucket, azuredevops, gitea, none; null, "" or "auto": no override, detect), linkPattern (null or "":
    /// none) } -> the repository's entry as in teams.repos, or {} when the main repository is not made yet.
    /// </summary>
    private async Task<JsonNode?> SetRepoAsync(long teamId, JsonObject? p, CancellationToken ct)
    {
        var repos = bus.Repos ?? throw new RpcException("unavailable", "team repositories need the app's data folder");
        await TeamNameOf(teamId, ct);
        var wire = OptStr(p, "provider");
        Teams.RepoProviderKind? provider = null;
        if (!string.IsNullOrWhiteSpace(wire) && !string.Equals(wire.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
            provider = Teams.RepoProviders.FromWire(wire) ?? throw new RpcException("invalid", $"unknown provider '{wire}': use github, gitlab, bitbucket, azuredevops, gitea or none");
        var name = OptStr(p, "name");
        try { repos.SetHostSetting(teamId, name, provider, OptStr(p, "linkPattern")); }
        catch (ArgumentException e) { throw new RpcException("invalid", e.Message); }
        if (!string.IsNullOrWhiteSpace(name) && repos.ExtrasOf(teamId).FirstOrDefault(e => string.Equals(e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) is { } extra)
            return await ExtraRepoJsonAsync(repos, teamId, extra, ct);
        return await MainRepoJsonAsync(repos, teamId, ct) ?? new JsonObject();
    }

    /// <summary>git.webLink (B075): the web address of a commit, branch, file, compare or pull request, or { url: null }.</summary>
    private async Task<JsonNode?> WebLinkAsync(JsonObject? p, CancellationToken ct)
    {
        // team: the team's id or its name (teamId also taken).
        long? id = OptLong(p, "teamId") ?? OptLong(p, "team");
        if (id is null && OptStr(p, "team") is { } t && long.TryParse(t, out var parsed)) id = parsed;
        var (repos, team) = id is { } teamId ? await GitTeamAsync(teamId, ct) : await TidyTeamAsync(p, ct);
        var kind = OptStr(p, "kind")?.Trim().ToLowerInvariant() switch
        {
            "commit" => Teams.WebLinkKind.Commit,
            "branch" => Teams.WebLinkKind.Branch,
            "file" => Teams.WebLinkKind.File,
            "compare" => Teams.WebLinkKind.Compare,
            "pr" => Teams.WebLinkKind.Pr,
            var k => throw new RpcException("bad_params", $"kind must be commit, branch, file, compare or pr (not '{k}')"),
        };
        var req = new Teams.WebLinkRequest(kind, OptStr(p, "commit")?.Trim(), OptStr(p, "branch")?.Trim(), OptStr(p, "file")?.Trim(),
            OptStr(p, "from")?.Trim(), OptStr(p, "to")?.Trim(), OptLong(p, "pr") ?? (long.TryParse(OptStr(p, "pr"), out var n) ? n : null));
        try
        {
            return new JsonObject { ["url"] = await repos.GitWebLinkAsync(team.Id, OptStr(p, "repo"), req, ct) };
        }
        catch (ArgumentException e) { throw new RpcException("invalid", e.Message); }
        catch (Exception e) when (e is Teams.GitException or IOException) { throw new RpcException("git_failed", e.Message); }
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

    // ---------------------------------------------------------------- B104: the Workflows page

    /// <summary>How many of a workflow's messages workflows.get gives (newest first).</summary>
    public const int WorkflowMessages = 30;

    /// <summary>workflows.list {teamId?, q?, state?, limit?}: {workflows, counts, truncated, backlog, now} (contract B104/contract.md).</summary>
    private async Task<JsonNode> WorkflowsListAsync(JsonObject? p, CancellationToken ct)
    {
        if (p?["limit"] is JsonValue lv && !WholeNumber(lv, out _)) throw new RpcException("bad_params", "limit must be a number");
        long? teamId = p?["teamId"] is null ? null : Long(p, "teamId");
        if (bus.Board is { } board) await board.ProjectAsync(ct); // the cards and times include everything said so far
        var page = await bus.WorkflowsPageAsync(teamId, OptStr(p, "q"), OptStr(p, "state"), (int)(OptLong(p, "limit") ?? BusEngine.DefaultPageLimit), ct);
        return new JsonObject
        {
            ["workflows"] = new JsonArray([.. page.Rows.Select(r => (JsonNode)WorkflowRowJson(r))]),
            ["counts"] = new JsonObject([.. page.Counts.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))]),
            ["truncated"] = page.Truncated,
            // The window may always read the backlog; the Cloud Link sets this per viewer (RemoteAccess.ShapeResult).
            ["backlog"] = true,
            ["now"] = page.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    /// <summary>workflows.get {workflow}: {workflow, cards, messages, spec, backlog} (contract B104/contract.md).</summary>
    private async Task<JsonNode> WorkflowGetAsync(long workflow, CancellationToken ct)
    {
        var board = bus.Board;
        if (board is not null) await board.ProjectAsync(ct);
        var row = await bus.WorkflowRowAsync(workflow, ct) ?? throw new RpcException("no_such_workflow", $"there is no workflow {workflow}");
        var cards = new JsonArray();
        if (board is not null)
        {
            var live = await bus.LiveActivityAsync(ct);
            var now = board.Store.UtcNow();
            var lanes = Board.Lanes.All.Select(l => l.Id).ToList();
            foreach (var c in board.Store.CardsOfWorkflow(workflow).OrderBy(c => lanes.IndexOf(c.Lane) is var i && i < 0 ? lanes.Count : i).ThenBy(c => c.Id))
                cards.Add(new JsonObject
                {
                    ["id"] = c.Id, ["title"] = c.Title, ["lane"] = c.Lane, ["member"] = c.Member, ["status"] = c.Status,
                    ["time"] = board.Timeline.OfCard(c, live, now).Time.ToJson(),
                });
        }
        var thread = await bus.WorkflowThreadAsync(workflow, ct);
        var messages = new JsonArray();
        foreach (var m in thread.Reverse())
        {
            if (messages.Count >= WorkflowMessages) break;
            if (WorkflowMessageRow(m) is { } node) messages.Add(node);
        }
        return new JsonObject
        {
            ["workflow"] = WorkflowRowJson(row),
            ["cards"] = cards,
            ["messages"] = messages,
            ["spec"] = row.Item?.Spec,
            ["backlog"] = true,
        };
    }

    /// <summary>A WorkflowRow on the wire: bus.workflows' summary fields, then B104's own.</summary>
    internal static JsonObject WorkflowRowJson(WorkflowRow r)
    {
        var node = S(r.Summary)!.AsObject();
        node["ownerId"] = r.OwnerId;
        node["team"] = r.Team;
        node["teamId"] = r.TeamId;
        node["status"] = r.Status;
        node["waitingFor"] = r.WaitingFor;
        node["working"] = r.Working;
        node["item"] = r.Item is { } i ? new JsonObject { ["label"] = i.Label, ["title"] = i.Title, ["archived"] = i.Archived } : null;
        node["members"] = new JsonArray([.. r.Members.Select(m => (JsonNode)new JsonObject { ["id"] = m.Id, ["name"] = m.Name, ["state"] = m.State })]);
        return node;
    }

    /// <summary>One message of workflows.get in plain words (the Monitor's wording, plus a workflow's close and expiry); null for the rest.</summary>
    internal static JsonObject? WorkflowMessageRow(Message m)
    {
        if (MonitorRow(m) is { } row)
        {
            row.Remove("team");
            row.Remove("workflow");
            row.Remove("failureClass");
            return Ordered(row["seq"]!.GetValue<long>(), row["at"]!.GetValue<string>(), row["kind"]!.GetValue<string>(),
                row["from"]?.GetValue<string>(), row["member"]?.GetValue<string>(), row["text"]?.GetValue<string>());
        }
        if (m.Type is not (MessageTypes.WorkflowClosed or MessageTypes.WorkflowExpired)) return null;
        string? text = null;
        try
        {
            if (JsonNode.Parse(m.Payload) is JsonObject p)
                text = (m.Type == MessageTypes.WorkflowClosed ? p["reason"] : p["detail"] ?? p["reason"]) is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        }
        catch (JsonException) { }
        if (text is { Length: > 240 }) text = text[..240] + "…";
        return Ordered(m.Seq, m.OccurredAt.ToString("O"), m.Type == MessageTypes.WorkflowClosed ? "closed" : "expired", m.Source, null, text);

        static JsonObject Ordered(long seq, string at, string kind, string? from, string? member, string? text) => new()
        {
            ["seq"] = seq, ["at"] = at, ["kind"] = kind, ["from"] = from, ["member"] = member, ["text"] = text,
        };
    }

    private async Task<string> TeamNameOf(long teamId, CancellationToken ct) =>
        (await bus.TeamsAsync(ct)).SingleOrDefault(t => t.Id == teamId)?.Name ?? throw new RpcException("no_such_team", $"there is no team {teamId}");

    /// <summary>workflowExtend({ workflow, teamId? }): the workflow as the list shows it after the Extend.</summary>
    private async Task<WorkflowSummary> ExtendAsync(JsonObject? p, CancellationToken ct)
    {
        var workflow = Long(p, "workflow");
        var team = OptLong(p, "teamId") is { } teamId ? await TeamNameOf(teamId, ct)
            : await bus.WorkflowTeamAsync(workflow, ct) ?? throw new RpcException("no_such_workflow", $"there is no workflow {workflow}");
        return await bus.ExtendWorkflowAsync(team, workflow, ct);
    }

    /// <summary>teams.workflowExpiry: a get-or-set like teams.push; "days" (a whole number 1-365) sets it.</summary>
    private async Task<JsonObject> WorkflowExpiryAsync(JsonObject? p, CancellationToken ct)
    {
        var team = await TeamNameOf(Long(p, "teamId"), ct);
        var days = p?["days"] switch
        {
            null => (int?)null,
            JsonValue v when v.TryGetValue<int>(out var d) => d,
            JsonValue v when v.TryGetValue<double>(out var x) && x == Math.Floor(x) && x is >= int.MinValue and <= int.MaxValue => (int)x,
            _ => throw new RpcException("bad_request", "days must be a whole number of days from 1 to 365"),
        };
        if (days is < BusEngine.MinWorkflowExpiryDays or > BusEngine.MaxWorkflowExpiryDays)
            throw new RpcException("bad_request", $"days must be {BusEngine.MinWorkflowExpiryDays} to {BusEngine.MaxWorkflowExpiryDays}");
        return new JsonObject
        {
            ["days"] = days is { } set ? await bus.SetWorkflowExpiryDaysAsync(team, set, ct) : await bus.WorkflowExpiryDaysAsync(team, ct),
        };
    }

    private async Task<JsonNode?> DecisionsAsync(long teamId, CancellationToken ct)
    {
        var team = (await bus.TeamsAsync(ct)).SingleOrDefault(t => t.Id == teamId) ?? throw new RpcException("no_such_team", $"there is no team {teamId}");
        // B085: the question a workflow about to expire waits on carries the warning (its Needs-you line) and when it expires.
        var warnings = await bus.ExpiryWarningsAsync(team.Name, ct);
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
                ["expiresAt"] = warnings.TryGetValue(m.CorrelationId, out var warning) && warning.Question == m.Seq ? warning.ExpiresAt.ToString("O") : null,
                ["expiryWarning"] = warning is not null && warning.Question == m.Seq ? warning.Text : null,
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

    /// <summary>
    /// policy.checkHosts {hosts: [..]}: [{ host, problem }] in the order given, problem null when the entry may be on an
    /// allowHosts list (an exact host, or "*.example.com" over a registrable domain): <see cref="FloorRules.HostProblem"/>.
    /// </summary>
    internal static JsonArray CheckHosts(JsonObject? p)
    {
        var hosts = (p?["hosts"] as JsonArray ?? throw new RpcException("bad_params", "hosts is required (a list of host names)"))
            .Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").Take(500).ToList();
        return new JsonArray(hosts.Select(h => (JsonNode?)new JsonObject { ["host"] = h, ["problem"] = FloorRules.HostProblem(h) }).ToArray());
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
            ["lastRunFailureClass"] = card.LastRunFailureClass,
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
