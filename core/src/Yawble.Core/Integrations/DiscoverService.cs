using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Yawble.Core.Integrations;

/// <summary>Options of one discovery; the defaults are what the app uses. Tests pass a Runner and a Version.</summary>
public sealed record DiscoverOptions
{
    public WalkOptions Walk { get; init; } = new();
    public HelpRunner? Runner { get; init; }
    public string? Version { get; init; }
}

public sealed record DiscoverOutcome(string Cli, int Commands, bool Partial, string? PartialReason, string? Error, MergeSummary? Merge, int ModelClassified);

/// <summary>One row of the checklist the UI shows. State: free (tickable), locked (credential, never allowed) or refused (destructive).</summary>
public sealed record ChecklistNode(string Path, string ParentPath, string Name, int Depth, string Summary, bool HasChildren, string Class, string Source, string Reason,
    string State, bool Ticked, string? TickedFrom, bool DefaultTick, string? Decision, bool Gone, string? Badge, string? SeenVersion,
    string? Suggested, string? DefaultDecision, string? TeamDecision, string Effective, string Origin,
    string? Tag = null, string? Method = null, string? Route = null);

public sealed record OtherRule(long Id, long? TeamId, string Pattern, string Decision);

public sealed record Checklist(string Cli, long? TeamId, string? Version, string? DiscoveredAt, int Runs, bool Partial, string? PartialReason, string? Error,
    IReadOnlyList<ChecklistNode> Nodes, IReadOnlyList<OtherRule> OtherRules, int NewCount, int ChangedCount, int GoneCount, int ChoiceCount,
    string Kind, bool OfferedAll, IReadOnlyList<long> OfferedTeams, (int Allowed, int Ask, int Off, int Undecided) Counts);

public sealed record SaveResult(int Allowed, int Asked, int Denied, int Removed);

/// <summary>What the person said to remember with an approval or a decline: this call only, always allow, keep asking, never.</summary>
public static class RememberChoice
{
    public const string Once = "once", Always = "always", Ask = "ask", Never = "never";
}

public sealed record DiscoverJob(string Cli, string State, int Found, int Walked, string Current, double Seconds, string? Error, DiscoverOutcome? Outcome);

/// <summary>
/// Discover: walk a CLI's help, classify, store, show as a checklist, turn the person's ticks into ordinary rules and
/// merge a re-discover keeping their choices. There is no second decision engine: CliRules.Decide stays the only gate (with the
/// universal credential floor in front of it); the checklist only writes integration_rules rows.
/// </summary>
public sealed partial class DiscoverService(string connectionString, Func<ICommandModel?>? model = null, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly DiscoverStore _store = new(connectionString, clock ?? TimeProvider.System);
    private readonly ConcurrentDictionary<string, JobState> _jobs = new(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ discover

    /// <summary>Walks, classifies and merges. A walk that finds nothing leaves the stored tree as it was and says why.</summary>
    public async Task<DiscoverOutcome> DiscoverAsync(string cliName, DiscoverOptions? options = null, IProgress<WalkProgress>? progress = null, CancellationToken ct = default)
    {
        options ??= new DiscoverOptions();
        var name = CheckAddable(cliName);
        var entry = CliCatalog.Of(name);
        var id = EnsureIntegration(name);
        EnsureBuiltins(id, name);
        HelpRunner run;
        string? version = options.Version;
        string? tempDir = null;
        try
        {
            if (options.Runner is { } r) run = r;
            else
            {
                var path = StoredPath(id) ?? CliRunner.Find(name) ?? throw new ArgumentException($"{name} is not installed on this computer (not on PATH)");
                tempDir = Directory.CreateTempSubdirectory("yawble-discover-").FullName; // a CLI must not act on a repository
                run = CliWalker.ForExecutable(path, entry, tempDir, options.Walk.CallTimeout);
                version ??= await VersionAsync(path, entry, tempDir, ct);
                await RefreshStatusAsync(id, path, entry ?? CliCatalog.Generic(name), tempDir, ct); // a sign-in made since Detect shows now
            }
            var walk = await CliWalker.WalkAsync(name, entry, run, options.Walk, progress, ct);
            if (walk.Error is not null)
            {
                _store.RecordFailure(id, version, walk.Error);
                return new DiscoverOutcome(name, 0, false, null, walk.Error, null, 0);
            }
            var classes = new Dictionary<string, Classification>(StringComparer.Ordinal);
            foreach (var c in walk.Commands) classes[c.Path] = CommandClassifier.Classify(name, entry, c.Path, c.HasChildren);
            // model fallback: only unknown leaves, never those it already answered for (same help)
            var known = _store.Commands(id).ToDictionary(x => x.Path, StringComparer.Ordinal);
            var ask = walk.Commands.Where(c => !c.HasChildren && classes[c.Path].Class == CommandClass.Unknown
                    && !(known.TryGetValue(c.Path, out var k) && k.Source == ClassSource.Model && (c.HelpHash is null || c.HelpHash == k.HelpHash)))
                .Select(c => (c.Path, c.Summary)).ToList();
            var answers = await ModelClassifier.ClassifyAsync(name, ask, model?.Invoke(), ct);
            foreach (var (path, cls) in answers) classes[path] = cls;
            var merge = _store.Merge(id, version, walk, classes);
            return new DiscoverOutcome(name, walk.Commands.Count, walk.Partial, walk.PartialReason, null, merge, answers.Count);
        }
        finally
        {
            if (tempDir is not null) try { Directory.Delete(tempDir, true); } catch (IOException) { }
        }
    }

    /// <summary>The path Detect stored for an integration (the one its calls run), or null.</summary>
    private string? StoredPath(long integrationId)
    {
        using var c = _store.Open();
        return DiscoverStore.Scalar(c, null, "SELECT path FROM integrations WHERE id = $i;", ("$i", integrationId)) is string { Length: > 0 } p && File.Exists(p) ? p : null;
    }

    /// <summary>Runs the CLI's own status command (as Detect does) and stores what it says: "signed in: ..." or "not signed in: ...".</summary>
    private async Task RefreshStatusAsync(long integrationId, string path, CliEntry entry, string cwd, CancellationToken ct)
    {
        string text;
        try { text = IntegrationService.StatusText(await CliRunner.RunAsync(path, entry.StatusArgs, cwd, entry.Env, TimeSpan.FromSeconds(30), ct)); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or IOException) { return; }
        using var c = _store.Open();
        DiscoverStore.Run(c, null, "UPDATE integrations SET status = $s, updated_at = $now WHERE id = $i;",
            ("$s", text), ("$now", _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)), ("$i", integrationId));
    }

    private static async Task<string?> VersionAsync(string path, CliEntry? entry, string cwd, CancellationToken ct)
    {
        try
        {
            var o = await CliRunner.RunAsync(path, ["--version"], cwd, entry?.Env, TimeSpan.FromSeconds(20), ct);
            return (o.Stdout + o.Stderr).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) { return null; }
    }

    // ------------------------------------------------------------------ the walk as a background job (progress line and Cancel in the UI)

    private sealed class JobState
    {
        public volatile string State = "running";
        public int Found, Walked;
        public string Current = "";
        public double Seconds;
        public string? Error;
        public DiscoverOutcome? Outcome;
        public CancellationTokenSource Cts = new();
    }

    /// <summary>Starts a walk in the background; false if one is already running for that CLI.</summary>
    public bool Start(string cliName, DiscoverOptions? options = null)
    {
        var name = CheckAddable(cliName);
        return StartJob(name, (job, ct) =>
        {
            var progress = new Progress<WalkProgress>(p => { job.Found = p.Found; job.Walked = p.Walked; job.Current = p.Current; job.Seconds = p.Elapsed.TotalSeconds; });
            return DiscoverAsync(name, options, progress, ct);
        });
    }

    /// <summary>Runs one discovery as a background job under a key ("gh", "mcp:server"); false if that key is already running.</summary>
    private bool StartJob(string key, Func<JobState, CancellationToken, Task<DiscoverOutcome>> work)
    {
        var job = new JobState();
        lock (_jobs)
        {
            if (_jobs.TryGetValue(key, out var cur) && cur.State == "running") return false;
            _jobs[key] = job;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                job.Outcome = await work(job, job.Cts.Token);
                job.Error = job.Outcome.Error;
                job.State = job.Outcome.Error is null ? "done" : "failed";
            }
            catch (OperationCanceledException) { job.State = "cancelled"; }
            catch (Exception e)
            {
                job.Error = e.Message;
                job.State = "failed";
            }
        });
        return true;
    }

    public DiscoverJob? Status(string cliName) => JobStatus(CheckName(cliName), CheckName(cliName));

    public bool Cancel(string cliName) => CancelJob(CheckName(cliName));

    private DiscoverJob? JobStatus(string key, string name) => _jobs.TryGetValue(key, out var j)
        ? new DiscoverJob(name, j.State, j.Found, j.Walked, j.Current, j.Seconds, j.Error, j.Outcome) : null;

    private bool CancelJob(string key)
    {
        if (!_jobs.TryGetValue(key, out var j) || j.State != "running") return false;
        j.Cts.Cancel();
        return true;
    }

    // ------------------------------------------------------------------ the checklist

    public Checklist GetChecklist(string cliName, long? teamId)
    {
        var name = CheckName(cliName);
        var id = IntegrationId(name) ?? throw new ArgumentException($"{name} is not set up yet: detect or enable it first");
        EnsureBuiltins(id, name);
        AdoptPlainRules(id, name, CliCatalog.Of(name) ?? CliCatalog.Generic(name));
        var nodes = _store.Commands(id);
        var choices = _store.Choices(id);
        var rules = _store.Rules(id);
        var disc = _store.Discovery(id);
        var first = disc is { Runs: 1 };
        var (offeredAll, offered) = Offered(id);

        (string? Decision, string? From) Saved(string path, long? scope)
        {
            var ch = choices.FirstOrDefault(x => x.TeamId == scope && x.Path == path);
            if (ch is not null) return (ch.Decision, "choice");
            foreach (var rule in rules)
                if (rule.TeamId == scope && DiscoverStore.IsPatternOf(rule.Pattern, path))
                    return (rule.Decision, "rule");
            return (null, null);
        }

        var entry = CliCatalog.Of(name) ?? CliCatalog.Generic(name);
        var teamRules = rules.Where(r => r.TeamId == teamId && teamId is not null).Select(r => new CliRule(r.Pattern, r.Decision, "team", r.Id)).ToList();
        var globalRules = rules.Where(r => r.TeamId is null).Select(r => new CliRule(r.Pattern, r.Decision, "global", r.Id)).ToList();
        int cAllow = 0, cAsk = 0, cOff = 0, cUndecided = 0;

        var view = new List<ChecklistNode>();
        foreach (var n in nodes)
        {
            var state = n.Class == CommandClass.Credential ? "locked" : n.Class == CommandClass.Destructive ? "refused" : "free";
            var (decision, from) = Saved(n.Path, teamId);
            string? tickedFrom = from;
            if (decision is null && teamId is not null)
            {
                (decision, from) = Saved(n.Path, null);
                if (decision is not null) tickedFrom = "all teams";
            }
            var preTick = n.Class == CommandClass.ReadOnly && n.Source is ClassSource.Verb or ClassSource.Preset && (n.Badge != "new" || first) && !n.Gone;
            var ticked = state == "free" && (decision is not null ? decision == Decisions.Allow : preTick);
            if (state == "free" && decision is null && preTick) tickedFrom = "default";
            var defaultDecision = Saved(n.Path, null).Decision;
            var teamDecision = teamId is null ? null : Saved(n.Path, teamId).Decision;
            var (eff, effRule) = CliRules.Decide(n.Path.Split(' '), entry, teamRules, globalRules);
            var effective = effRule is { Layer: "floor" } ? RowStates.Never : eff;
            if (!n.HasChildren && !n.Gone)
            {
                if (effective == Decisions.Allow) cAllow++;
                else if (effective == Decisions.Ask && defaultDecision is null && teamDecision is null) cUndecided++;
                else if (effective == Decisions.Ask) cAsk++;
                else cOff++;
            }
            view.Add(new ChecklistNode(n.Path, n.ParentPath, n.Path.Split(' ')[^1], n.Path.Split(' ').Length, n.Summary, n.HasChildren, n.Class, n.Source, n.Reason, state,
                ticked, ticked ? tickedFrom : null, state == "free" && decision is null && preTick, decision, n.Gone, n.Badge, n.SeenVersion,
                Suggested(n.Class, n.Source), defaultDecision, teamDecision, effective, n.Origin));
        }
        // Other rules: every rule of these scopes that no row owns (its choice) or shows (a leaf's own pattern, when the row has no choice
        // there): a group's prefix ("pr*", "pr *") and a second rule for a row are listed, so they can be seen and removed.
        var shownOnRows = choices.Where(ch => ch.RuleId is not null).Select(ch => ch.RuleId!.Value).ToHashSet();
        foreach (var n in nodes.Where(n => !n.HasChildren))
            foreach (var sc in teamId is null ? new long?[] { null } : [null, teamId])
                if (!choices.Any(ch => ch.TeamId == sc && ch.Path == n.Path)
                    && rules.FirstOrDefault(r => r.TeamId == sc && DiscoverStore.IsPatternOf(r.Pattern, n.Path)) is { Pattern: not null } shownRule)
                    shownOnRows.Add(shownRule.Id);
        var other = rules.Where(r => !shownOnRows.Contains(r.Id) && (r.TeamId == teamId || r.TeamId is null))
            .Select(r => new OtherRule(r.Id, r.TeamId, r.Pattern, r.Decision)).ToList();
        return new Checklist(name, teamId, disc?.Version, disc?.DiscoveredAt, disc?.Runs ?? 0, disc?.Partial ?? false, disc?.PartialReason, disc?.Error, view, other,
            nodes.Count(n => n.Badge == "new" && !first), nodes.Count(n => n.Badge == "changed"), nodes.Count(n => n.Gone), choices.Count(c => c.TeamId == teamId),
            "cli", offeredAll, offered, (cAllow, cAsk, cOff, cUndecided));
    }

    // ------------------------------------------------------------------ save: ticks -> rules

    /// <summary>
    /// Saves the ticked commands of a scope (null team = every team) as choices and rules, in one transaction. A ticked group
    /// ticks its read-only leaves, never "group*". A credential (locked) or destructive (refused) path is rejected, whatever the UI
    /// sent. Destructive nodes are saved as deny rules; a preset-allowed node the person unticked is saved as an explicit ask.
    /// </summary>
    public SaveResult SaveChecklist(string cliName, long? teamId, IReadOnlyCollection<string> tickedPaths)
    {
        var name = CheckName(cliName);
        var id = IntegrationId(name) ?? throw new ArgumentException($"{name} is not set up yet");
        if (teamId is { } t && !_store.TeamExists(t)) throw new ArgumentException($"no team {t}");
        var entry = CliCatalog.Of(name) ?? CliCatalog.Generic(name);
        var nodes = _store.Commands(id).ToDictionary(n => n.Path, StringComparer.Ordinal);
        var allow = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in tickedPaths)
        {
            var path = (raw ?? "").Trim();
            if (!nodes.TryGetValue(path, out var node)) throw new ArgumentException($"'{path}' is not a command in the list");
            if (node.HasChildren)
            {
                foreach (var leaf in nodes.Values.Where(n => !n.HasChildren && !n.Gone && n.Class == CommandClass.ReadOnly && n.Path.StartsWith(path + " ", StringComparison.Ordinal)))
                    allow.Add(leaf.Path);
                continue;
            }
            Refuse(entry, node);
            allow.Add(path);
        }
        foreach (var p in allow) Refuse(entry, nodes[p]); // group-expanded leaves are checked too

        var globalRules = _store.Rules(id).Where(r => r.TeamId is null && teamId is not null).Select(r => new CliRule(r.Pattern, r.Decision, "global", r.Id)).ToList();
        int allowed = 0, asked = 0, denied = 0, removed = 0;
        using var c = _store.Open();
        using var tx = c.BeginTransaction();
        var now = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        var choices = _store.Choices(id);
        var rules = _store.Rules(id);
        var firstRun = _store.Discovery(id) is { Runs: 1 };
        foreach (var n in nodes.Values.Where(n => !n.HasChildren))
        {
            var mine = choices.FirstOrDefault(ch => ch.TeamId == teamId && ch.Path == n.Path);
            if (n.Class == CommandClass.Credential)
            {
                if (mine is not null) { _store.RemoveChoice(c, tx, mine); removed++; }
                continue;
            }
            if (n.Class == CommandClass.Destructive)
            {
                if (mine?.Decision != Decisions.Deny) { _store.SetChoice(c, tx, id, teamId, n.Path, Decisions.Deny, now, "destructive: refused"); denied++; }
                continue;
            }
            if (n.Gone && !allow.Contains(n.Path)) continue; // a gone node's choice is kept as it is
            if (allow.Contains(n.Path))
            {
                if (mine?.Decision != Decisions.Allow || mine.RuleId is null) { _store.SetChoice(c, tx, id, teamId, n.Path, Decisions.Allow, now); allowed++; }
                continue;
            }
            // unticked: an explicit ask if something else (the preset, a rule for every team) would allow it; else nothing
            var inherited = CliRules.Decide(n.Path.Split(' '), entry, [], globalRules).Decision == Decisions.Allow;
            var ownRule = rules.FirstOrDefault(r => r.TeamId == teamId && DiscoverStore.IsPatternOf(r.Pattern, n.Path));
            // a node that shows ticked by default must say so when unticked, or it would show ticked again
            var byDefault = n.Class == CommandClass.ReadOnly && n.Source is ClassSource.Verb or ClassSource.Preset && (n.Badge != "new" || firstRun);
            if (inherited || byDefault)
            {
                if (mine?.Decision != Decisions.Ask) { _store.SetChoice(c, tx, id, teamId, n.Path, Decisions.Ask, now); asked++; }
            }
            else if (mine is not null) { _store.RemoveChoice(c, tx, mine); removed++; }
            else if (ownRule.Pattern is not null && ownRule.Decision == Decisions.Allow)
            {
                DiscoverStore.Run(c, tx, "DELETE FROM integration_rules WHERE id = $id;", ("$id", ownRule.Id));
                removed++;
            }
        }
        tx.Commit();
        return new SaveResult(allowed, asked, denied, removed);
    }

    /// <summary>Server-side refusal: the UI cannot get a credential or a destructive command ticked.</summary>
    private static void Refuse(CliEntry entry, CommandRow node)
    {
        var words = node.Path.Split(' ');
        const string locked = "is locked: it reveals or changes a sign-in and is never allowed";
        if (node.Class == CommandClass.Credential) throw new ArgumentException($"'{node.Path}' {locked}");
        if (node.Class == CommandClass.Destructive)
            throw new ArgumentException($"'{node.Path}' is refused: it is destructive (write a rule by hand if you really need it)");
        if (CliRules.Decide(words, entry, [], []) is { Decision: Decisions.Deny, Rule.Layer: "floor" }) throw new ArgumentException($"'{node.Path}' {locked}");
    }

    // ------------------------------------------------------------------ found in use: an answer the person remembered becomes a row

    private static readonly System.Text.RegularExpressions.Regex PlainWord = new("^[A-Za-z][A-Za-z0-9-]*$");

    /// <summary>A rule pattern of plain words and one trailing *, no inner * and no flag: "pr lock*" (not "pr *": that needs more words).</summary>
    private static readonly System.Text.RegularExpressions.Regex PlainPattern = new("^([A-Za-z][A-Za-z0-9-]*(?: [A-Za-z][A-Za-z0-9-]*)*)\\*$");

    /// <summary>
    /// The row a call is remembered under: the longest known leaf that is a word prefix of the arguments (case-insensitive); else
    /// all its leading plain words (stopping at the first that is not plain; Ruling 12: no cap, so "storage account update" never becomes
    /// "storage account"); null when the first argument is not plain (a flag).
    /// </summary>
    public static string? FoundPath(IReadOnlyList<string> args, IReadOnlyCollection<string> knownLeaves)
    {
        var words = args.Select(a => (a ?? "").Trim()).Where(a => a.Length > 0).ToList();
        if (words.Count == 0 || !PlainWord.IsMatch(words[0])) return null;
        string? best = null;
        var bestLength = 0;
        foreach (var leaf in knownLeaves)
        {
            var parts = leaf.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Length > words.Count || parts.Length <= bestLength) continue;
            if (parts.Select((p, i) => string.Equals(p, words[i], StringComparison.OrdinalIgnoreCase)).All(x => x)) (best, bestLength) = (leaf, parts.Length);
        }
        return best ?? string.Join(' ', PlainRun(words).Select(w => w.ToLowerInvariant()));
    }

    private static IEnumerable<string> PlainRun(IEnumerable<string> words) =>
        words.Select(a => (a ?? "").Trim()).Where(a => a.Length > 0).TakeWhile(w => PlainWord.IsMatch(w));

    /// <summary>
    /// The row an ask of this CLI is remembered under (<see cref="FoundPath"/> over its leaves). Null (the exact line is remembered instead)
    /// for a flag first, a group, a passthrough command (gh api, az rest, any api / rest / graphql: Ruling 12), a guess shorter than the
    /// call's plain words, or a one-word guess that is not a known row when the CLI was never discovered or its built-in rules go deeper
    /// under that word ("pr"): one approval must not cover a whole command family the person never saw.
    /// </summary>
    public string? FoundPathOf(string cliName, IReadOnlyList<string> args)
    {
        var name = CheckName(cliName);
        var nodes = IntegrationId(name) is { } id ? _store.Commands(id) : [];
        return Holds(name, nodes, args, FoundPath(args, nodes.Where(n => !n.HasChildren).Select(n => n.Path).ToList()));
    }

    /// <summary>
    /// The row a stored ask is remembered under: its action re-checked with <see cref="FoundPathOf"/>'s rules (an ask saved before
    /// them may name a group or a short guess; then null, the exact line), else FoundPathOf now.
    /// </summary>
    public string? ActionOf(string cliName, IReadOnlyList<string> args, string? action)
    {
        if (action is null) return FoundPathOf(cliName, args);
        var name = CheckName(cliName);
        var path = action.Trim();
        // a built-in tool's ask (gh: pr_open / pr_status) is remembered under its own row; its args are the gh line it runs
        if (CliCatalog.BuiltinRows(name).FirstOrDefault(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase)) is { Path: { } builtin }) return builtin;
        var plain = PlainRun(args).ToList();
        var words = path.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // it must still be a word prefix of the call's plain words
        if (words.Length == 0 || words.Length > plain.Count || words.Where((w, i) => !string.Equals(w, plain[i], StringComparison.OrdinalIgnoreCase)).Any()) return null;
        return Holds(name, IntegrationId(name) is { } id ? _store.Commands(id) : [], args, path);
    }

    /// <summary>FoundPathOf's rules for a candidate row: the path, or null when it would cover more than the person saw.</summary>
    private static string? Holds(string name, IReadOnlyList<CommandRow> nodes, IReadOnlyList<string> args, string? path)
    {
        var leaves = nodes.Where(n => !n.HasChildren).Select(n => n.Path).ToList();
        if (path is null || nodes.Any(n => n.HasChildren && n.Path == path) || CliCatalog.IsPassthrough(path)) return null;
        if (leaves.Contains(path)) return path;
        if (path.Split(' ').Length < PlainRun(args).Count()) return null; // a guess never covers fewer words than the call had
        if (path.Contains(' ')) return path;
        if (!nodes.Any(n => n.Origin == RowOrigin.Discovered)) return null;
        var entry = CliCatalog.Of(name) ?? CliCatalog.Generic(name);
        var deeper = entry.Preset.Select(p => p.Pattern).Concat(entry.Floors).Any(p => p.Trim().StartsWith(path + " ", StringComparison.OrdinalIgnoreCase));
        return deeper ? null : path;
    }

    /// <summary>The class a remembered path has: its row's, a built-in's, or the classifier's; and whether a floor hits it.</summary>
    private (Classification Class, bool Locked, bool Group) ClassOf(long integrationId, string name, CliEntry entry, string path)
    {
        var node = _store.Commands(integrationId).FirstOrDefault(n => n.Path == path);
        var builtin = CliCatalog.BuiltinRows(name).FirstOrDefault(b => b.Path == path);
        var cls = node is not null ? new Classification(node.Class, node.Source, node.Reason)
            : builtin.Path is not null ? BuiltinClass(builtin.Class)
            : CommandClassifier.Classify(name, entry, path, false);
        var floor = CliRules.Decide(path.Split(' '), entry, [], []) is { Decision: Decisions.Deny, Rule.Layer: "floor" };
        return (cls, cls.Class == CommandClass.Credential || floor, node?.HasChildren == true);
    }

    private static Classification BuiltinClass(string cls) => new(cls, ClassSource.Preset, "built into the app");

    /// <summary>
    /// Whether an ask can be remembered as an "always" row: false when it has no row (a flag first, or its stored action no longer
    /// holds: <see cref="ActionOf"/>, as <see cref="Remember"/> reads it) or the row reveals a sign-in.
    /// </summary>
    public bool CanAlways(string cliName, IReadOnlyList<string> args, string? action)
    {
        var name = CheckName(cliName);
        if (IntegrationId(name) is not { } id) return false;
        if (ActionOf(name, args, action) is not { } path) return false;
        var (_, locked, group) = ClassOf(id, name, CliCatalog.Of(name) ?? CliCatalog.Generic(name), path);
        return !locked && !group;
    }

    /// <summary>
    /// Remembers the person's answer to an ask (allow = always, ask = keep asking, deny = never) for a team or every team (null).
    /// The row is the ask's action, else <see cref="FoundPath"/>; it is added as "found" if missing, and its choice written, in one
    /// transaction. Returns the path, or null when there is no row (a flag first): then today's exact-line rule is written. A row that
    /// reveals a sign-in is refused for allow and ask, and writes nothing for deny (the floor refuses it anyway).
    /// </summary>
    public string? Remember(string cliName, long? teamId, IReadOnlyList<string> args, string? action, string decision)
    {
        var name = CheckName(cliName);
        if (!Decisions.All.Contains(decision)) throw new ArgumentException("decision is allow, ask or deny");
        var id = IntegrationId(name) ?? throw new ArgumentException($"{name} is not set up yet");
        if (teamId is { } t && !_store.TeamExists(t)) throw new ArgumentException($"no team {t}");
        var entry = CliCatalog.Of(name) ?? CliCatalog.Generic(name);
        // an ask stored before Ruling 12 or the guess rules (a passthrough, a group, a short or one-word guess): the exact line
        var path = ActionOf(name, args, action);
        var now = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        if (path is not null)
        {
            var (cls, locked, group) = ClassOf(id, name, entry, path);
            if (!group)
            {
                if (locked)
                {
                    if (decision == Decisions.Deny) return null;
                    throw new ArgumentException($"'{path}' is locked: it reveals or changes a sign-in and is never allowed");
                }
                var builtin = CliCatalog.BuiltinRows(name).Any(b => b.Path == path);
                var words = path.Split(' ');
                using var c = _store.Open();
                using var tx = c.BeginTransaction();
                _store.UpsertRow(c, tx, id, path, string.Join(' ', words[..^1]), "", cls, builtin ? RowOrigin.Builtin : RowOrigin.Found);
                _store.SetChoice(c, tx, id, teamId, path, decision, now, "remembered from an approval");
                tx.Commit();
                return path;
            }
        }
        // no row to remember it under: the exact line
        if (CliRules.Decide(args, entry, [], []) is { Decision: Decisions.Deny, Rule.Layer: "floor" })
        {
            if (decision == Decisions.Deny) return null;
            throw new ArgumentException("this call reveals or changes a sign-in and is never allowed");
        }
        var line = CliRules.Join(args).Replace("*", "");
        if (CliRules.Problem(line, decision) is { } why) throw new ArgumentException(why);
        using (var c = _store.Open())
        {
            using var tx = c.BeginTransaction();
            DiscoverStore.Run(c, tx, "DELETE FROM integration_rules WHERE integration_id = $i AND team_id IS $t AND pattern = $p COLLATE NOCASE;", ("$i", id), ("$t", teamId), ("$p", line));
            DiscoverStore.Run(c, tx, "INSERT INTO integration_rules (integration_id, team_id, pattern, decision, created_at) VALUES ($i, $t, $p, $d, $now);",
                ("$i", id), ("$t", teamId), ("$p", line), ("$d", decision), ("$now", now));
            tx.Commit();
        }
        return null;
    }

    /// <summary>The MCP twin of <see cref="Remember"/>: the row is the tool, classified from its annotations if the server listed it.</summary>
    public string? RememberMcp(string server, long? teamId, string tool, string decision)
    {
        if (!Decisions.All.Contains(decision)) throw new ArgumentException("decision is allow, ask or deny");
        var s = McpServerOf(server);
        if (teamId is { } t && !_store.TeamExists(t)) throw new ArgumentException($"no team {t}");
        var (cls, summary, locked) = McpClassOf(s.Id, tool);
        if (locked)
        {
            if (decision == Decisions.Deny) return null;
            throw new ArgumentException($"'{tool}' is locked: it reveals or changes a sign-in and is never allowed");
        }
        var now = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        using var c = _store.Open();
        using var tx = c.BeginTransaction();
        _store.UpsertMcpRow(c, tx, s.Id, tool, summary, cls, RowOrigin.Found);
        _store.SetMcpChoice(c, tx, s.Id, teamId, tool, decision, now, "remembered from an approval");
        tx.Commit();
        return tool;
    }

    public bool CanAlwaysMcp(string server, string tool)
    {
        if (_store.McpServer(server.Trim()) is not { } s) return false;
        return !McpClassOf(s.Id, tool).Locked;
    }

    private (Classification Class, string Summary, bool Locked) McpClassOf(long serverId, string tool)
    {
        var node = _store.McpCommands(serverId).FirstOrDefault(n => n.Path == tool);
        var listed = _store.McpTool(serverId, tool);
        var cls = node is not null ? new Classification(node.Class, node.Source, node.Reason) : McpClassifier.Classify(listed ?? new JsonObject { ["name"] = tool });
        var summary = node?.Summary ?? McpClassifier.StringOf(listed?["description"]) ?? "";
        if (summary.Length > 200) summary = summary[..200];
        var floor = CliRules.Decide([tool], null, [], []) is { Decision: Decisions.Deny, Rule.Layer: "floor" };
        return (cls, summary, cls.Class == CommandClass.Credential || floor);
    }

    /// <summary>The built-in rows of a CLI (gh: pr_open, pr_status), added if missing; a saved choice on them is never touched.</summary>
    public void EnsureBuiltins(long integrationId, string cli)
    {
        foreach (var (path, summary, cls) in CliCatalog.BuiltinRows(cli))
            _store.UpsertRow(integrationId, path, "", summary, BuiltinClass(cls), RowOrigin.Builtin);
    }

    /// <summary>
    /// A rule typed by hand that names plain words ("pr lock*") but no row becomes a "found" row whose choice adopts that rule (it stays
    /// the same rule, owned by the choice now). Patterns with an inner * or a flag, a group's prefix, or a sign-in stay "other rules".
    /// </summary>
    private void AdoptPlainRules(long integrationId, string name, CliEntry entry)
    {
        var nodes = _store.Commands(integrationId);
        var paths = nodes.Select(n => n.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choices = _store.Choices(integrationId);
        var adopt = new List<(long Id, long? TeamId, string Path, string Decision)>();
        foreach (var rule in _store.Rules(integrationId))
        {
            var m = PlainPattern.Match(rule.Pattern.Trim());
            if (!m.Success) continue;
            var path = m.Groups[1].Value.ToLowerInvariant();
            if (paths.Contains(path) || nodes.Any(n => n.Path.StartsWith(path + " ", StringComparison.OrdinalIgnoreCase))) continue;
            if (choices.Any(ch => ch.TeamId == rule.TeamId && ch.Path == path) || adopt.Any(a => a.TeamId == rule.TeamId && a.Path == path)) continue;
            adopt.Add((rule.Id, rule.TeamId, path, rule.Decision));
        }
        if (adopt.Count == 0) return;
        var now = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        using var c = _store.Open();
        using var tx = c.BeginTransaction();
        foreach (var a in adopt)
        {
            // another checklist read may have adopted it meanwhile: the inserts below do nothing then
            var cls = CommandClassifier.Classify(name, entry, a.Path, false);
            if (cls.Class == CommandClass.Credential || CliRules.Decide(a.Path.Split(' '), entry, [], []) is { Decision: Decisions.Deny, Rule.Layer: "floor" }) continue;
            var words = a.Path.Split(' ');
            _store.UpsertRow(c, tx, integrationId, a.Path, string.Join(' ', words[..^1]), "", cls, RowOrigin.Found);
            _store.AdoptRule(c, tx, integrationId, a.TeamId, a.Path, a.Decision, a.Id, now);
        }
        tx.Commit();
    }

    // ------------------------------------------------------------------ the store of integrations

    private long? IntegrationId(string name)
    {
        using var c = _store.Open();
        var v = DiscoverStore.Scalar(c, null, "SELECT id FROM integrations WHERE name = $n;", ("$n", name));
        return v is null ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    private long EnsureIntegration(string name)
    {
        using var c = _store.Open();
        var now = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        DiscoverStore.Run(c, null, "INSERT OR IGNORE INTO integrations (kind, name, created_at, updated_at) VALUES ('cli', $n, $now, $now);", ("$n", name), ("$now", now));
        return Convert.ToInt64(DiscoverStore.Scalar(c, null, "SELECT id FROM integrations WHERE name = $n;", ("$n", name)), CultureInfo.InvariantCulture);
    }

    /// <summary>A name that may become a CLI integration: a command name, and not a system program (Ruling 11).</summary>
    internal static string CheckAddable(string name)
    {
        var n = CheckName(name);
        return CliCatalog.Refused(n) is { } why ? throw new ArgumentException(why) : n;
    }

    private static string CheckName(string name)
    {
        var n = (name ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(n, "^[A-Za-z0-9][A-Za-z0-9._-]{0,40}$"))
            throw new ArgumentException($"'{name}' is not a command name (letters, digits, . _ -)");
        return n;
    }

    // ------------------------------------------------------------------ JSON for the UI

    public static JsonNode ToJson(Checklist c) => new JsonObject
    {
        ["cli"] = c.Cli, ["teamId"] = c.TeamId, ["version"] = c.Version, ["discoveredAt"] = c.DiscoveredAt, ["runs"] = c.Runs, ["partial"] = c.Partial,
        ["partialReason"] = c.PartialReason, ["error"] = c.Error, ["new"] = c.NewCount, ["changed"] = c.ChangedCount, ["gone"] = c.GoneCount, ["choices"] = c.ChoiceCount,
        ["kind"] = c.Kind, ["offeredAll"] = c.OfferedAll, ["offeredTeams"] = new JsonArray(c.OfferedTeams.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
        ["counts"] = new JsonObject { ["allowed"] = c.Counts.Allowed, ["ask"] = c.Counts.Ask, ["off"] = c.Counts.Off, ["undecided"] = c.Counts.Undecided },
        ["nodes"] = new JsonArray(c.Nodes.Select(n => (JsonNode?)new JsonObject
        {
            ["path"] = n.Path, ["parent"] = n.ParentPath, ["name"] = n.Name, ["depth"] = n.Depth, ["summary"] = n.Summary, ["hasChildren"] = n.HasChildren,
            ["class"] = n.Class, ["source"] = n.Source, ["reason"] = n.Reason, ["state"] = n.State, ["ticked"] = n.Ticked, ["tickedFrom"] = n.TickedFrom,
            ["defaultTick"] = n.DefaultTick, ["decision"] = n.Decision, ["gone"] = n.Gone, ["badge"] = n.Badge, ["seenVersion"] = n.SeenVersion,
            ["suggested"] = n.Suggested, ["defaultDecision"] = n.DefaultDecision, ["teamDecision"] = n.TeamDecision, ["effective"] = n.Effective, ["origin"] = n.Origin,
            ["tag"] = n.Tag, ["method"] = n.Method, ["route"] = n.Route,
        }).ToArray()),
        ["otherRules"] = new JsonArray(c.OtherRules.Select(r => (JsonNode?)new JsonObject { ["id"] = r.Id, ["teamId"] = r.TeamId, ["pattern"] = r.Pattern, ["decision"] = r.Decision }).ToArray()),
    };

    public static JsonNode ToJson(DiscoverJob? j) => j is null ? new JsonObject { ["state"] = "idle" } : new JsonObject
    {
        ["cli"] = j.Cli, ["state"] = j.State, ["found"] = j.Found, ["walked"] = j.Walked, ["current"] = j.Current, ["seconds"] = j.Seconds, ["error"] = j.Error,
        ["needsSignIn"] = j.Error?.Contains(IntegrationService.NeedsSignInHint, StringComparison.Ordinal) == true ? true : null,
        ["outcome"] = j.Outcome is null ? null : new JsonObject
        {
            ["commands"] = j.Outcome.Commands, ["partial"] = j.Outcome.Partial, ["partialReason"] = j.Outcome.PartialReason, ["error"] = j.Outcome.Error,
            ["new"] = j.Outcome.Merge?.New, ["changed"] = j.Outcome.Merge?.Changed, ["gone"] = j.Outcome.Merge?.Gone, ["unchanged"] = j.Outcome.Merge?.Unchanged,
            ["dropped"] = j.Outcome.Merge is null ? null : new JsonArray(j.Outcome.Merge.Dropped.Select(d => (JsonNode?)d).ToArray()), ["modelClassified"] = j.Outcome.ModelClassified,
        },
    };
}
