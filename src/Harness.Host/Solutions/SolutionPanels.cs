using System.Text.Json.Nodes;
using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host.Solutions;

/// <summary>
/// WHAT THE SOLUTIONS LAUNCHER AND A SOLUTION'S CONTROL PANEL READ: one tile per installed solution,
/// and the panel the platform builds from the package's <c>solution.json</c> and the team's live
/// state. See <c>docs/solutions.md</c>.
///
/// <para>
/// READS ONLY. Every control on the panel is an existing route with its own permit marker and
/// tenant row - pause and resume, Run now, a trigger's on/off and cap, plugin settings and
/// connection bindings, the documents upload, the wizard's update - so this class adds no write.
/// </para>
///
/// <para>
/// NOTHING IS ESTIMATED. A trigger's spend today is the Triggers dialog's own view
/// (<see cref="TriggerCost.ViewAsync"/>): measured billable tokens, with the runs that reported no
/// usage counted as unmeasured, never as zero and never guessed.
/// </para>
///
/// <para>
/// PACKAGE TEXT IS TEXT. Names, descriptions, the filled status line, file names and run output are
/// answered as plain strings; the UI renders them as text, never HTML.
/// </para>
/// </summary>
public sealed class SolutionPanels(
    ITeamSolutionStore store,
    SolutionInstaller installer,
    TeamRegistry teams,
    ContainerHost host,
    ITriggerStore triggers,
    TriggerCost cost,
    IMessageLog log,
    SiteService sites,
    TeamDocuments documents,
    PluginCatalog plugins)
{
    public const string StateRunning = "running";
    public const string StateIdle = "idle";
    public const string StateBlocked = "blocked";
    public const string StatePaused = "paused";
    public const string StateCapped = "capped";

    /// <summary>
    /// EVERY CONTROL THE PANEL OFFERS, and the existing route each one is. The panel adds no write
    /// route but Uninstall: each of these carries its own permit marker and appends its own tenant
    /// row, and a test holds this list against the Host's endpoints.
    /// </summary>
    public static readonly IReadOnlyList<(string Control, string Method, string Route)> Controls =
    [
        ("Pause", "POST", "/api/teams/{team}/pause"),
        ("Resume", "POST", "/api/teams/{team}/resume"),
        ("Run now", "POST", "/api/teams/{team}/triggers/{id}/run"),
        ("A trigger's on/off and daily cap", "PATCH", "/api/teams/{team}/triggers/{id}"),
        ("Plugin settings and connection bindings", "PUT", "/api/teams/{team}/members/{member}/plugin-settings"),
        ("Upload a missing document", "POST", "/api/teams/{team}/documents/upload"),
        ("Update from a folder", "POST", "/api/solutions/update"),
        ("Uninstall", "POST", "/api/teams/{team}/solution/uninstall"),
    ];

    /// <summary>How many files one output folder lists, newest first.</summary>
    public const int OutputFiles = 50;

    /// <summary>How many recent runs the panel lists, across the package's members.</summary>
    public const int RecentRuns = 20;

    /// <summary>One tile per team installed from a package, ordered by name.</summary>
    public async Task<IReadOnlyList<SolutionTile>> TilesAsync(CancellationToken ct = default)
    {
        var tiles = new List<SolutionTile>();

        foreach (var row in await store.ListAsync(ct))
        {
            if (teams.ExistingName(row.Team) is not { } stored) continue;

            var live = await LiveAsync(stored, row, ct);
            tiles.Add(new SolutionTile(
                stored, teams.LabelFor(stored), row.PackageId, row.Name, row.Version, row.InstalledAt, row.UpdatedAt,
                row.InstalledBy, [.. row.Plugins.Keys.Order(StringComparer.Ordinal)], row.Folder,
                live.PrimarySite, live.Status, live.State, live.Paused));
        }

        return [.. tiles.OrderBy(t => t.TeamName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The control panel of <paramref name="team"/>; null when it was not installed from a
    /// package, or there is no such team.</summary>
    public async Task<object?> PanelAsync(string team, CancellationToken ct = default)
    {
        if (teams.ExistingName(team) is not { } stored || await store.FindAsync(stored, ct) is not { } row) return null;

        var manifest = ManifestOf(row);
        var live = await LiveAsync(stored, row, ct);

        return new
        {
            team = stored,
            teamName = teams.LabelFor(stored),
            id = row.PackageId,
            name = row.Name,
            version = row.Version,
            description = manifest?.Description ?? "",
            installedAt = row.InstalledAt,
            updatedAt = row.UpdatedAt,
            installedBy = row.InstalledBy,
            folder = row.Folder,
            paused = live.Paused,
            state = live.State,
            status = live.Status,
            primarySite = live.PrimarySite,
            members = live.Members,
            triggers = live.Triggers,
            blocked = live.Blocked,
            settings = SettingsOf(row, manifest),
            outputs = OutputsOf(stored, manifest),
            recentRuns = live.Runs.Take(RecentRuns).Select(r => r.Wire).ToList(),
        };
    }

    // ------------------------------------------------------------------ the live read

    private sealed record Live(
        bool Paused,
        SolutionState State,
        string Status,
        SolutionPrimarySite? PrimarySite,
        IReadOnlyList<object> Members,
        IReadOnlyList<JsonObject> Triggers,
        IReadOnlyList<SolutionBlocked> Blocked,
        IReadOnlyList<PanelRun> Runs);

    private sealed record PanelRun(long Seq, DateTimeOffset At, string Outcome, object Wire);

    private async Task<Live> LiveAsync(string team, TeamSolutionRow row, CancellationToken ct)
    {
        var manifest = ManifestOf(row);
        var now = DateTimeOffset.UtcNow;
        var paused = teams.IsPaused(team);

        // Members, each with its state and last run; every run read once for the recent-runs list too.
        var members = new List<object>();
        var runs = new List<PanelRun>();
        var anyRunning = false;

        foreach (var (packageName, name) in OrderedMembers(row, manifest))
        {
            var packageMember = manifest?.Member(packageName);
            var id = new ContainerId(team, name);
            var snapshot = host.Find(id)?.Snapshot();
            var persisted = snapshot is null ? null : await MemberOrNullAsync(team, name, ct);
            var memberRuns = persisted is null ? [] : await RunsOfAsync(id, persisted, packageName, ct);

            runs.AddRange(memberRuns);
            anyRunning |= snapshot?.State == ContainerState.Running;

            var last = memberRuns.FirstOrDefault();
            members.Add(new
            {
                packageName,
                member = snapshot?.Id ?? name,
                kind = packageMember?.Kind ?? (persisted is { } p && MemberRef.IsPlugin(p.Agent) ? MemberRef.PluginKind : MemberRef.AgentKind),
                role = packageMember?.Role ?? SolutionManifest.RoleMember,
                state = snapshot is null ? "missing" : snapshot.State == ContainerState.Running ? StateRunning : StateIdle,
                blocked = snapshot?.Blocked,
                failed = snapshot?.Failed,
                needsDecision = snapshot?.NeedsDecision,
                queueDepth = snapshot?.QueueDepth ?? 0,
                lastRun = last is null ? null : (object)new { seq = last.Seq, at = last.At, outcome = last.Outcome },
            });
        }

        runs.Sort((a, b) => b.At.CompareTo(a.At));

        // Triggers: the Triggers dialog's own view, in the package's order.
        var views = new List<JsonObject>();
        foreach (var (packageName, triggerId) in OrderedTriggers(row, manifest))
        {
            if (await triggers.FindAsync(triggerId, ct) is not { } trigger || !string.Equals(trigger.Team, team, StringComparison.OrdinalIgnoreCase)) continue;

            var view = await cost.ViewAsync(trigger, now, ct);
            var packageKind = manifest?.Triggers.FirstOrDefault(t => t.Name == packageName)?.Kind;
            view["packageName"] = packageName;
            view["packageKind"] = packageKind;
            view["runNow"] = packageKind == SolutionManifest.KindSchedule
                || (packageKind is null && trigger.Kind is "Cron" or "Every" or "Once");
            views.Add(view);
        }

        var blocked = (await installer.MissingAsync(team, row, ct)).Select(missing => BlockedOf(team, row, missing)).ToList();

        var capped = views.FirstOrDefault(v => v["capReachedToday"]?.GetValue<bool>() == true);

        var state = paused ? new SolutionState(StatePaused, null)
            : blocked.Count > 0 ? new SolutionState(StateBlocked, blocked[0].Reason)
            : anyRunning ? new SolutionState(StateRunning, null)
            : capped is not null ? new SolutionState(StateCapped, $"'{capped["packageName"]}' reached its daily cap today.")
            : new SolutionState(StateIdle, null);

        var primarySite = await PrimarySiteAsync(team, manifest, ct);
        var lastRun = runs.FirstOrDefault() is { } latest ? new SolutionLastRun(latest.At, latest.Outcome) : null;

        return new Live(paused, state, await StatusAsync(team, manifest, primarySite, lastRun, state, ct), primarySite, members, views, blocked, runs);
    }

    /// <summary>
    /// The status line: the package's <c>panel.status</c> filled, or the default - the last run and
    /// the state. A template that no longer reads (the stored manifest is the installed one, so this
    /// is only a guard) falls back to the default.
    /// </summary>
    private async Task<string> StatusAsync(
        string team, SolutionManifest? manifest, SolutionPrimarySite? primarySite, SolutionLastRun? lastRun, SolutionState state, CancellationToken ct)
    {
        if (manifest?.Panel.Status is { } text && SolutionStatusTemplate.Parse(text).Template is { } template)
        {
            var data = new Dictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal);

            foreach (var collection in template.Collections)
            {
                data[collection] = primarySite is null ? null : await CollectionAsync(team, primarySite.Name, collection, ct);
            }

            return template.Fill(collection => data.GetValueOrDefault(collection), lastRun);
        }

        return SolutionStatusTemplate.Default(lastRun, state.Kind);
    }

    private async Task<IReadOnlyList<string>?> CollectionAsync(string team, string site, string collection, CancellationToken ct)
    {
        var read = await sites.ListDocumentsAsync(team, site, collection, null, ct);
        return read.Value is { } documents ? [.. documents.Select(d => d.Json)] : null;
    }

    private async Task<SolutionPrimarySite?> PrimarySiteAsync(string team, SolutionManifest? manifest, CancellationToken ct)
    {
        if (manifest?.Panel.PrimarySite is not { } name) return null;

        var found = await sites.FindAsync(team, name, null, ct);
        if (found.Value is not { } site) return null;

        return new SolutionPrimarySite(site.Name, SiteService.EntryPath(site.Team, site.Name), site.LiveVersion is not null);
    }

    private SolutionBlocked BlockedOf(string team, TeamSolutionRow row, SolutionMissing missing)
    {
        if (missing.Kind == "document")
        {
            var folder = missing.Name.TrimEnd('/');
            return new SolutionBlocked(
                missing.Kind, missing.Name, null, null, missing.Description,
                $"Upload a file to {missing.Name}", new SolutionFix(new SolutionUploadFix(folder), null));
        }

        var member = missing.Member is { } packageMember && row.Members.TryGetValue(packageMember, out var stored) ? stored : missing.Member;
        return new SolutionBlocked(
            missing.Kind, missing.Name, member, missing.Member, missing.Description,
            $"Connect {missing.Member}'s {missing.Name}", new SolutionFix(null, new SolutionConnectionFix(member ?? "", missing.Name)));
    }

    private IReadOnlyList<object> SettingsOf(TeamSolutionRow row, SolutionManifest? manifest)
    {
        if (manifest is null) return [];

        return [.. manifest.Panel.Settings.Select(listed =>
        {
            var pluginId = manifest.Member(listed.Member)?.PluginId;
            var field = pluginId is null ? null : plugins.For(pluginId)?.Manifest.Config.GetValueOrDefault(listed.Setting);

            return (object)new
            {
                member = row.Members.GetValueOrDefault(listed.Member) ?? listed.Member,
                packageMember = listed.Member,
                setting = listed.Setting,
                personOnly = field?.PersonOnly ?? false,
            };
        })];
    }

    private IReadOnlyList<object> OutputsOf(string team, SolutionManifest? manifest)
    {
        if (manifest is null) return [];

        var outputs = new List<object>();

        foreach (var folder in manifest.Panel.Outputs)
        {
            IReadOnlyList<DocumentEntry> files;
            bool exists;

            try
            {
                exists = Directory.Exists(documents.Resolve(team, folder));
                files = exists ? documents.List(team, folder, recursive: true) : [];
            }
            catch (DocumentPathException)
            {
                exists = false;
                files = [];
            }

            var newest = files
                .Where(f => !f.IsFolder && !f.Path.Split('/').Any(part => part.StartsWith('.')))
                .OrderByDescending(f => f.ModifiedAt)
                .ToList();

            outputs.Add(new
            {
                folder,
                exists,
                files = newest.Take(OutputFiles).Select(f => new
                {
                    path = f.Path,
                    name = f.Name,
                    size = f.Size,
                    modifiedAt = f.ModifiedAt,
                    download = $"/api/teams/{Uri.EscapeDataString(team)}/documents/content?path={Uri.EscapeDataString(f.Path)}",
                }).ToList(),
                more = newest.Count > OutputFiles,
            });
        }

        return outputs;
    }

    // ------------------------------------------------------------------ helpers

    private static SolutionManifest? ManifestOf(TeamSolutionRow row) => SolutionManifest.Parse(row.Manifest).Manifest;

    /// <summary>The package's members as the row maps them, in the package's order.</summary>
    private static IEnumerable<(string PackageName, string Member)> OrderedMembers(TeamSolutionRow row, SolutionManifest? manifest)
    {
        var order = manifest?.Members.Select(m => m.Name).ToList() ?? [];
        return row.Members
            .OrderBy(pair => order.FindIndex(n => string.Equals(n, pair.Key, StringComparison.OrdinalIgnoreCase)) is var at and >= 0 ? at : int.MaxValue)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (pair.Key, pair.Value));
    }

    private static IEnumerable<(string PackageName, string TriggerId)> OrderedTriggers(TeamSolutionRow row, SolutionManifest? manifest)
    {
        var order = manifest?.Triggers.Select(t => t.Name).ToList() ?? [];
        return row.Triggers
            .OrderBy(pair => order.IndexOf(pair.Key) is var at and >= 0 ? at : int.MaxValue)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (pair.Key, pair.Value));
    }

    private async Task<PersistedMember?> MemberOrNullAsync(string team, string name, CancellationToken ct)
    {
        try
        {
            return await teams.MemberAsync(team, name, ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A member's latest finished runs, newest first, as the runs route words them.</summary>
    private async Task<IReadOnlyList<PanelRun>> RunsOfAsync(ContainerId id, PersistedMember member, string packageName, CancellationToken ct)
    {
        var plugin = MemberRef.IsPlugin(member.Agent);
        var running = host.Find(id)?.State == ContainerState.Running;

        return [.. (await log.ReadRunsAsync(id, member.FloorSeq, long.MaxValue, RecentRuns + 1, RunsWith.AnyRun, ct))
            .Where(run => !(running && run.InLatestRun))
            .Take(RecentRuns)
            .Select(run =>
            {
                var outcome = LiveViewEndpoints.Outcome(run);
                return new PanelRun(run.Terminal.Seq, run.Terminal.OccurredAt, outcome, new
                {
                    member = member.Name,
                    packageMember = packageName,
                    seq = run.Terminal.Seq,
                    startedAt = run.StartedAt,
                    endedAt = run.Terminal.OccurredAt,
                    outcome,
                    output = plugin && run.Terminal.Type != MessageTypes.Blocked ? LiveViewEndpoints.RunOutput(run.Terminal.Payload) : null,
                    reason = run.Terminal.Type == MessageTypes.Blocked ? LiveViewEndpoints.Field(run.Terminal.Payload, PayloadFields.Reason) : null,
                    transcript = !plugin,
                });
            })];
    }
}

/// <summary>A solution's state badge: running, idle, blocked (with why), paused, or capped today (with which).</summary>
public sealed record SolutionState(string Kind, string? Reason);

/// <summary>The package's primary site: the tile's Open. <paramref name="Published"/> false while it
/// is unpublished, when Open would find nothing.</summary>
public sealed record SolutionPrimarySite(string Name, string Url, bool Published);

/// <summary>One launcher tile.</summary>
public sealed record SolutionTile(
    string Team,
    string TeamName,
    string Id,
    string Name,
    string Version,
    DateTimeOffset InstalledAt,
    DateTimeOffset? UpdatedAt,
    string? InstalledBy,
    IReadOnlyList<string> Plugins,
    string Folder,
    SolutionPrimarySite? PrimarySite,
    string Status,
    SolutionState State,
    bool Paused);

/// <summary>Something the solution waits for, with the fix the panel offers inline.</summary>
public sealed record SolutionBlocked(
    string Kind, string Name, string? Member, string? PackageMember, string Description, string Reason, SolutionFix Fix);

/// <summary>Exactly one is set: an upload box for a document folder, or a connection picker for a slot.</summary>
public sealed record SolutionFix(SolutionUploadFix? Upload, SolutionConnectionFix? Connection);

public sealed record SolutionUploadFix(string Folder);

public sealed record SolutionConnectionFix(string Member, string Slot);
