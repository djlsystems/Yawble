using Yawble.Core.Agents;
using Yawble.Core.Credentials;

namespace Yawble.Core.Ops;

public static class DoctorStatus
{
    public const string Ok = "ok", Warn = "warn", Fail = "fail";

    public static string Worst(string a, string b) => Rank(a) >= Rank(b) ? a : b;

    private static int Rank(string s) => s switch { Fail => 2, Warn => 1, _ => 0 };
}

/// <summary>
/// A fix the person can run with one click (or the Concierge when asked). Kind "open" is the app's own screen (the
/// shell navigates, nothing runs in the core); "run" is ops.doctorFix. Confirm: the person must say yes first.
/// </summary>
public sealed record DoctorFix(string Id, string Label, string Kind, bool Confirm);

/// <summary>One line of the health list. Detail never contains a secret value.</summary>
public sealed record DoctorCheck(string Id, string Title, string Status, string Detail, IReadOnlyList<DoctorFix>? Fixes = null)
{
    /// <summary>The first fix (the "fix?" the check offers); <see cref="Fixes"/> has all of them.</summary>
    public DoctorFix? Fix => Fixes is { Count: > 0 } f ? f[0] : null;
}

/// <summary>
/// <paramref name="Targets"/>: what the fix acts on (the credential keys, the team), canonical order, never a value; a confirmed
/// run is bound to it. <paramref name="Changed"/>: a confirmed run whose expected targets differ from now; nothing was done.
/// </summary>
public sealed record DoctorFixResult(bool Done, bool NeedsConfirm, string Message, IReadOnlyList<string>? Targets = null, bool Changed = false);

public sealed class DoctorOptions
{
    public TimeSpan BackupWarnAge { get; init; } = TimeSpan.FromHours(48);
    public long DiskWarnBytes { get; init; } = 5L * 1024 * 1024 * 1024;
    public long DiskFailBytes { get; init; } = 1L * 1024 * 1024 * 1024;
    public long LogWarnBytes { get; init; } = 1L * 1024 * 1024 * 1024;
    public TimeSpan QuietAfter { get; init; } = TimeSpan.FromDays(30);
    /// <summary>A team branch more than this many commits, or this long, behind its base is a warn.</summary>
    public int BehindWarnCommits { get; init; } = 20;
    public TimeSpan BehindWarnAge { get; init; } = TimeSpan.FromDays(3);
}

/// <summary>ops.doctor and the fixes it offers.</summary>
public sealed class DoctorService(
    IDoctorSystem system, IBackupLister? backups, ICliDetector? agents, IConnectionSource? connections,
    ICredentialIndex? credentials, IDoctorTeams? teams, TimeProvider? clock = null, DoctorOptions? options = null,
    IIssuedKeyLedger? ledger = null)
{
    public const string ConnectionPrefix = "connection/";
    public const string RemoveOrphansFix = "remove-orphan-credentials";
    public const string ArchiveFixPrefix = "archive-team:";
    public const string SyncFixPrefix = "sync-team:";
    public const string OpenConnectionFixPrefix = "open-connection:";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly DoctorOptions _o = options ?? new DoctorOptions();

    public async Task<IReadOnlyList<DoctorCheck>> RunAsync(CancellationToken ct)
    {
        var list = new List<DoctorCheck>
        {
            Guard("database", "Database", Database),
            Guard("disk", "Disk space", Disk),
            Guard("sandbox", "Sandbox", Sandbox),
            await GuardAsync("agents", "Agent CLIs", Agents, ct),
            Guard("cloudLink", "Cloud Link", Link),
            Guard("connections", "Connections", Connections),
            await GuardAsync("teams", "Teams", Teams, ct),
            Guard("logs", "Logs", Logs),
        };
        Volatile.Write(ref _lastFails, list.Count(c => c.Status == DoctorStatus.Fail));
        return list;
    }

    private int _lastFails;

    /// <summary>How many checks failed the last time the list was run (0 before the first run): the app's "Needs You" count.</summary>
    public int LastFailCount => Volatile.Read(ref _lastFails);

    private static DoctorCheck Guard(string id, string title, Func<DoctorCheck> check)
    {
        try { return check(); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new DoctorCheck(id, title, DoctorStatus.Fail, $"the check itself failed: {e.GetType().Name}");
        }
    }

    private static async Task<DoctorCheck> GuardAsync(string id, string title, Func<CancellationToken, Task<DoctorCheck>> check, CancellationToken ct)
    {
        try { return await check(ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new DoctorCheck(id, title, DoctorStatus.Fail, $"the check itself failed: {e.GetType().Name}");
        }
    }

    // ------------------------------------------------------------------ database

    private DoctorCheck Database()
    {
        var status = DoctorStatus.Ok;
        var parts = new List<string>();
        var quick = system.QuickCheck();
        if (quick == "ok") parts.Add("integrity ok");
        else { status = DoctorStatus.Fail; parts.Add($"integrity check found: {quick}"); }

        var applied = system.AppliedMigrations();
        var pending = system.ShippedMigrations().Where(m => !applied.Contains(m)).ToList();
        if (pending.Count == 0) parts.Add($"{applied.Count} migrations applied");
        else { status = DoctorStatus.Warn; parts.Add($"{pending.Count} migration(s) not applied: {string.Join(", ", pending)}"); }

        if (backups is not null)
        {
            var newest = backups.List().OrderByDescending(b => b.Time).FirstOrDefault();
            if (newest is null) { status = DoctorStatus.Worst(status, DoctorStatus.Warn); parts.Add("no backup yet"); }
            else
            {
                var age = _clock.GetUtcNow() - newest.Time;
                if (age > _o.BackupWarnAge) status = DoctorStatus.Worst(status, DoctorStatus.Warn);
                parts.Add($"last backup {Age(age)} ago" + (age > _o.BackupWarnAge ? " (over 48 h)" : ""));
            }
        }
        return new DoctorCheck("database", "Database", status, string.Join("; ", parts));
    }

    // ------------------------------------------------------------------ disk

    private DoctorCheck Disk()
    {
        var free = system.FreeBytesOnDataDrive();
        var status = free < _o.DiskFailBytes ? DoctorStatus.Fail : free < _o.DiskWarnBytes ? DoctorStatus.Warn : DoctorStatus.Ok;
        var sizes = system.TeamFolderSizes();
        var detail = $"{Size(free)} free on the data drive";
        if (sizes.Count > 0)
            detail += "; teams/: " + string.Join(", ", sizes.OrderByDescending(s => s.Value).Select(s => $"{s.Key} {Size(s.Value)}"));
        return new DoctorCheck("disk", "Disk space", status, detail);
    }

    // ------------------------------------------------------------------ sandbox

    private DoctorCheck Sandbox()
    {
        var s = system.Sandbox();
        if (s.LauncherProblem is not null)
            return new DoctorCheck("sandbox", "Sandbox", DoctorStatus.Fail, $"the sandbox launcher is not available: {s.LauncherProblem}");
        var status = DoctorStatus.Ok;
        var parts = new List<string> { "sandbox launcher available" };
        switch (s.GuardrailState)
        {
            case "done": parts.Add("last guardrail check passed" + (s.GuardrailDetail is null ? "" : $" ({s.GuardrailDetail})")); break;
            case "failed": status = DoctorStatus.Warn; parts.Add($"last guardrail check failed: {s.GuardrailDetail}"); break;
            default: parts.Add("guardrail check not run yet"); break;
        }
        if (s.GatewayProblem is null) parts.Add("gateway answered a test request");
        else { status = DoctorStatus.Fail; parts.Add($"the gateway did not answer: {s.GatewayProblem}"); }
        return new DoctorCheck("sandbox", "Sandbox", status, string.Join("; ", parts));
    }

    // ------------------------------------------------------------------ agents

    private async Task<DoctorCheck> Agents(CancellationToken ct)
    {
        if (agents is null) return new DoctorCheck("agents", "Agent CLIs", DoctorStatus.Warn, "agent detection is not wired in this core");
        var found = await agents.DetectAllAsync(ct).ConfigureAwait(false);
        var ready = found.Where(c => c.Found && c.SignIn?.Status == SignInStatus.SignedIn).Select(c => c.CliId).ToList();
        var notSignedIn = found.Where(c => c.Found && c.SignIn?.Status != SignInStatus.SignedIn).Select(c => c.CliId).ToList();
        var missing = found.Where(c => !c.Found).Select(c => c.CliId).ToList();
        var parts = new List<string>();
        if (ready.Count > 0) parts.Add($"ready: {string.Join(", ", ready)}");
        if (notSignedIn.Count > 0) parts.Add($"installed, not signed in: {string.Join(", ", notSignedIn)}");
        if (missing.Count > 0) parts.Add($"not installed: {string.Join(", ", missing)}");
        var status = ready.Count == 0 ? DoctorStatus.Fail : notSignedIn.Count > 0 ? DoctorStatus.Warn : DoctorStatus.Ok;
        return new DoctorCheck("agents", "Agent CLIs", status, parts.Count == 0 ? "no agent CLIs known" : string.Join("; ", parts));
    }

    // ------------------------------------------------------------------ Cloud Link

    private DoctorCheck Link()
    {
        var l = system.Link();
        return l.State switch
        {
            "off" => new DoctorCheck("cloudLink", "Cloud Link", DoctorStatus.Ok, "off"),
            "offline" when l.Error is not null => new DoctorCheck("cloudLink", "Cloud Link", DoctorStatus.Fail, $"failing: {l.Error}"),
            "offline" => new DoctorCheck("cloudLink", "Cloud Link", DoctorStatus.Warn, "offline"),
            _ => new DoctorCheck("cloudLink", "Cloud Link", DoctorStatus.Ok, l.State),
        };
    }

    // ------------------------------------------------------------------ Connections

    /// <summary>
    /// The credential keys this data folder issued (the ledger), that still exist in the store, and that no Connection row owns.
    /// Key names only, never values. The OS stores are shared by every data folder on the computer, so a key the ledger never
    /// recorded is never listed here, whatever the store holds. Without a ledger nothing is ever an orphan.
    /// </summary>
    public IReadOnlyList<string> OrphanCredentialKeys()
    {
        if (connections is null || credentials is null || ledger is null) return [];
        // Order matters: look at the ledger and the store first and the Connections after. ConnectionService.Index waits for a
        // save in progress, so a Connection being created now is either not in the ledger snapshot or already in the index.
        var issued = ledger.Keys().Where(k => k.StartsWith(ConnectionPrefix, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        if (issued.Count == 0) return [];
        IEnumerable<string> present;
        try { present = credentials.KeysWithPrefix(ConnectionPrefix).Where(issued.Contains).ToList(); }
        catch (NotSupportedException) { present = issued.Where(credentials.Has).ToList(); } // a store that cannot list: ask for each issued key
        var owners = connections.List().Select(c => ConnectionPrefix + c.StoreKey + "/").ToList();
        return present.Where(k => !owners.Any(o => k.StartsWith(o, StringComparison.Ordinal))).Order(StringComparer.Ordinal).ToList();
    }

    private DoctorCheck Connections()
    {
        if (connections is null) return new DoctorCheck("connections", "Connections", DoctorStatus.Ok, "no Connections");
        var list = connections.List();
        var reconnect = list.Where(c => c.NeedsReconnect).ToList();
        var orphans = OrphanCredentialKeys();
        var fixes = new List<DoctorFix>();
        var parts = new List<string>();
        foreach (var c in reconnect)
        {
            parts.Add($"{c.Name} needs signing in again");
            fixes.Add(new DoctorFix(OpenConnectionFixPrefix + c.Id, $"Open {c.Name} in Settings", "open", false));
        }
        if (orphans.Count > 0)
        {
            parts.Add($"{orphans.Count} saved credential(s) belong to no Connection");
            fixes.Add(new DoctorFix(RemoveOrphansFix, $"Remove {orphans.Count} unused credential(s)", "run", true));
        }
        if (parts.Count == 0) parts.Add($"{list.Count} Connection(s), all signed in");
        return new DoctorCheck("connections", "Connections", fixes.Count > 0 ? DoctorStatus.Warn : DoctorStatus.Ok, string.Join("; ", parts), fixes);
    }

    // ------------------------------------------------------------------ teams

    private async Task<DoctorCheck> Teams(CancellationToken ct)
    {
        if (teams is null) return new DoctorCheck("teams", "Teams", DoctorStatus.Ok, "no teams");
        var all = await teams.ListAsync(ct).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        var status = DoctorStatus.Ok;
        var parts = new List<string>();
        var fixes = new List<DoctorFix>();
        foreach (var t in all.Where(t => !t.Archived))
        {
            foreach (var m in t.Stuck)
            {
                status = DoctorStatus.Fail;
                parts.Add($"{t.Name}/{m.Name} has been in one run for {Age(m.Running)}, longer than the watchdog allows");
            }
            if (t.UnattendedWorkflows > 0)
            {
                status = DoctorStatus.Worst(status, DoctorStatus.Warn);
                parts.Add($"{t.Name}: {t.UnattendedWorkflows} open workflow(s) nobody is working");
            }
            if (t.BehindBase > 0 && (t.BehindBase > _o.BehindWarnCommits || t.BehindAge > _o.BehindWarnAge))
            {
                status = DoctorStatus.Worst(status, DoctorStatus.Warn);
                var branch = t.TeamBranch.Length > 0 ? t.TeamBranch : "the team branch";
                var of = t.BaseBranch.Length > 0 ? t.BaseBranch : "its base";
                parts.Add($"{t.Name}: {branch} is {t.BehindBase} commit(s) behind {of}" + (t.BehindAge is { } a ? $", the oldest {Age(a)} old" : ""));
                fixes.Add(new DoctorFix(SyncFixPrefix + t.Name, $"Sync {t.Name} now", "run", false));
            }
            if (t.Paused)
            {
                status = DoctorStatus.Worst(status, DoctorStatus.Warn);
                parts.Add($"{t.Name} is paused");
            }
            if (now - t.LastActivityAt >= _o.QuietAfter)
            {
                status = DoctorStatus.Worst(status, DoctorStatus.Warn);
                parts.Add($"{t.Name} is quiet: no messages for {(int)(now - t.LastActivityAt).TotalDays} days");
                fixes.Add(new DoctorFix(ArchiveFixPrefix + t.Name, $"Archive {t.Name}", "run", true));
            }
        }
        if (parts.Count == 0) parts.Add($"{all.Count(t => !t.Archived)} team(s), all in order");
        return new DoctorCheck("teams", "Teams", status, string.Join("; ", parts), fixes);
    }

    // ------------------------------------------------------------------ logs

    private DoctorCheck Logs()
    {
        var bytes = system.LogBytes();
        return new DoctorCheck("logs", "Logs", bytes > _o.LogWarnBytes ? DoctorStatus.Warn : DoctorStatus.Ok, $"{Size(bytes)} of logs");
    }

    // ------------------------------------------------------------------ fixes

    /// <summary>
    /// Runs a fix by its id. Fixes that remove or archive do nothing until <paramref name="confirmed"/>; without it the
    /// result says what would happen. What is removed is recomputed now, never taken from the caller; <paramref name="expected"/>
    /// (the targets the person said yes to) can only stop a run whose targets moved, never widen one.
    /// </summary>
    public async Task<DoctorFixResult> RunFixAsync(string fix, bool confirmed, CancellationToken ct, IReadOnlyList<string>? expected = null)
    {
        if (fix == RemoveOrphansFix)
        {
            var orphans = OrphanCredentialKeys();
            if (orphans.Count == 0) return new DoctorFixResult(true, false, "no unused credentials", []);
            var ask = $"Remove {orphans.Count} saved credential(s) that no Connection uses ({string.Join(", ", orphans)})? Their values are not shown and cannot be brought back.";
            if (!confirmed) return new DoctorFixResult(false, true, ask, orphans);
            // The yes was given to a list. If the list now differs, that yes does not cover it: remove nothing and show the new one.
            if (expected is not null && !SameTargets(expected, orphans))
                return new DoctorFixResult(false, true, "The list changed since it was confirmed. " + ask, orphans, Changed: true);
            var removed = 0;
            foreach (var k in orphans)
            {
                if (credentials!.Delete(k)) removed++;
                ledger!.Forget(k);
            }
            return new DoctorFixResult(true, false, $"removed {removed} unused credential(s)");
        }
        if (fix.StartsWith(SyncFixPrefix, StringComparison.Ordinal) && teams is not null)
        {
            var name = fix[SyncFixPrefix.Length..];
            var team = (await teams.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !t.Archived)
                ?? throw new Ipc.RpcException("bad_params", $"no active team '{name}'");
            var (done, message) = await teams.SyncAsync(team.Name, ct).ConfigureAwait(false);
            return new DoctorFixResult(done, false, message, [team.Name]);
        }
        if (fix.StartsWith(ArchiveFixPrefix, StringComparison.Ordinal) && teams is not null)
        {
            var name = fix[ArchiveFixPrefix.Length..];
            var team = (await teams.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !t.Archived)
                ?? throw new Ipc.RpcException("bad_params", $"no active team '{name}'");
            if (_clock.GetUtcNow() - team.LastActivityAt < _o.QuietAfter)
                return new DoctorFixResult(true, false, $"{team.Name} is no longer quiet; nothing archived", []);
            var ask = $"Archive {team.Name}? It stops taking work and leaves the lists; unarchive brings it back.";
            if (!confirmed) return new DoctorFixResult(false, true, ask, [team.Name]);
            if (expected is not null && !SameTargets(expected, [team.Name]))
                return new DoctorFixResult(false, true, "The team changed since it was confirmed. " + ask, [team.Name], Changed: true);
            await teams.ArchiveAsync(team.Name, ct).ConfigureAwait(false);
            return new DoctorFixResult(true, false, $"archived {team.Name}");
        }
        throw new Ipc.RpcException("bad_params", $"'{fix}' is not a fix the core runs");
    }

    private static bool SameTargets(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Order(StringComparer.Ordinal).SequenceEqual(b.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static string Age(TimeSpan t) =>
        t.TotalDays >= 2 ? $"{(int)t.TotalDays} days" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h" : $"{Math.Max(0, (int)t.TotalMinutes)} min";

    private static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MB" : $"{bytes / 1024.0:0.#} KB";
}

