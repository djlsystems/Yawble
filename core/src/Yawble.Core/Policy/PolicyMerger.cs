namespace Yawble.Core.Policy;

/// <summary>The policy one run gets: merge(global, override) with the loosen block applied last.</summary>
public sealed record EffectivePolicy(
    IReadOnlyList<string> Readonly,
    IReadOnlyList<string> Readwrite,
    IReadOnlyList<string> Denied,
    IReadOnlyList<string> AllowHosts,
    ComputePolicy Compute,
    string Clipboard,
    int MaxConcurrentAgents,
    IReadOnlyList<string> Loosened,
    IReadOnlyList<string> Notes)
{
    /// <summary>True when the loosen block changed anything: the card shows the "loosened" chip.</summary>
    public bool IsLoosened => Loosened.Count > 0;
}

/// <summary>
/// The outcome of a merge. <see cref="Violations"/> break a floor rule and refuse the policy everywhere.
/// <see cref="Widening"/> lists the parts of an override that tried to loosen: they are dropped (clamped) at
/// launch, and refused when the override is saved (a global change can turn a saved override into a widening
/// one; the launch then still only tightens).
/// </summary>
public sealed record MergeResult(EffectivePolicy Policy, IReadOnlyList<string> Violations, IReadOnlyList<string> Widening);

/// <summary>
/// The merge/tighten rule, the loosen block and the team's network allow list. A pure function of its inputs.
/// </summary>
public static class PolicyMerger
{
    private static readonly string[] ClipboardOrder = ["none", "read", "write", "all"];

    public static MergeResult Merge(
        GlobalPolicy global, PolicyPatch? overridePatch, PolicyPatch? loosen, RunLayout layout,
        Func<string, string>? canonicalize = null, long? machineMemoryMb = null, IReadOnlyList<string>? teamHosts = null)
    {
        overridePatch ??= PolicyPatch.Empty;
        loosen ??= PolicyPatch.Empty;
        var paths = new PolicyPaths(layout.Os, layout.Home, canonicalize);
        var violations = new List<string>();
        var widening = new List<string>();
        var loosened = new List<string>();
        var notes = new List<string>();

        violations.AddRange(CheckGlobal(global));

        // ---- network.allowHosts: intersection with global; loosen may add; vendor hosts never.
        var hosts = new List<string>(global.Network.AllowHosts);
        if (overridePatch.Network?.AllowHosts is { } oHosts)
        {
            foreach (var h in oHosts.Where(h => !global.Network.AllowHosts.Contains(h, StringComparer.Ordinal)))
                widening.Add($"override network.allowHosts: '{h}' is not in the global list (only the loosen block can add a host)");
            hosts = hosts.Where(h => oHosts.Contains(h, StringComparer.Ordinal)).ToList();
        }
        foreach (var h in (overridePatch.Network?.AllowHosts ?? []).Concat(loosen.Network?.AllowHosts ?? []))
            if (FloorRules.HostProblem(h) is { } problem) violations.Add($"network.allowHosts: {problem}");
        foreach (var h in loosen.Network?.AllowHosts ?? [])
        {
            if (FloorRules.HostProblem(h) is not null || hosts.Contains(h, StringComparer.Ordinal)) continue;
            hosts.Add(h);
            loosened.Add($"network.allowHosts + {h}");
        }
        // The team's network allow list (team_network_allows, 0041): added as the loosen block adds, after the override's
        // narrowing, under the same floor rules. A host the floor refuses is dropped with a note, never a violation: the list
        // is checked when it is written, and a later floor change must not stop the whole team from launching.
        foreach (var h in teamHosts ?? [])
        {
            if (FloorRules.HostProblem(h) is { } problem) { notes.Add($"network.allowHosts: the team's host is dropped: {problem}"); continue; }
            if (hosts.Contains(h, StringComparer.Ordinal)) continue;
            hosts.Add(h);
            loosened.Add($"network.allowHosts + {h} (team)");
        }
        hosts = hosts.Where(h => FloorRules.HostProblem(h) is null).Distinct(StringComparer.Ordinal).ToList();

        // ---- compute: min(global, override); loosen may raise up to the ceiling.
        var memCeiling = machineMemoryMb is { } mm ? (int)Math.Min(int.MaxValue, mm * FloorRules.MemoryCeilingPercentOfMachine / 100) : (int?)null;
        var oc = overridePatch.Compute ?? new ComputePatch();
        var lc = loosen.Compute ?? new ComputePatch();
        int idle = Tighten("idleTimeoutSec", global.Compute.IdleTimeoutSec, oc.IdleTimeoutSec, widening, violations, FloorRules.TimeoutCeilingSec);
        int wall = Tighten("wallTimeoutSec", global.Compute.WallTimeoutSec, oc.WallTimeoutSec, widening, violations, FloorRules.TimeoutCeilingSec);
        idle = Raise("idleTimeoutSec", idle, lc.IdleTimeoutSec, FloorRules.TimeoutCeilingSec, loosened, violations);
        wall = Raise("wallTimeoutSec", wall, lc.WallTimeoutSec, FloorRules.TimeoutCeilingSec, loosened, violations);
        int? mem = TightenCap("memoryMb", global.Compute.MemoryMb, oc.MemoryMb, widening, violations);
        int? cpu = TightenCap("cpuPercent", global.Compute.CpuPercent, oc.CpuPercent, widening, violations);
        int? procs = TightenCap("maxProcesses", global.Compute.MaxProcesses, oc.MaxProcesses, widening, violations);
        mem = RaiseCap("memoryMb", mem, lc.MemoryMb, memCeiling, loosened, violations);
        cpu = RaiseCap("cpuPercent", cpu, lc.CpuPercent, FloorRules.CpuPercentCeiling, loosened, violations);
        procs = RaiseCap("maxProcesses", procs, lc.MaxProcesses, FloorRules.MaxProcessesCeiling, loosened, violations);

        // ---- ui.clipboard: most restrictive.
        var clipboard = global.Ui.Clipboard;
        if (overridePatch.Ui?.Clipboard is { } oClip)
        {
            if (!ClipboardOrder.Contains(oClip)) violations.Add($"override ui.clipboard: unknown value '{oClip}'");
            else
            {
                var meet = Meet(clipboard, oClip);
                if (meet != oClip) widening.Add($"override ui.clipboard: '{oClip}' is wider than the global '{clipboard}'");
                clipboard = meet;
            }
        }

        // ---- filesystem.
        var implicitDenied = layout.ImplicitDenied.Select(p => Must(paths, p)).ToList();
        var implicitGrants = layout.ImplicitReadwrite.Concat(layout.ImplicitReadonly).Select(p => Must(paths, p)).ToList();
        var appRoot = Must(paths, layout.AppDataRoot);

        var grantable = Resolve(paths, global.Filesystem.GrantableRoots, "global filesystem.grantableRoots", violations);
        var denied = Resolve(paths, global.Filesystem.Denied, "global filesystem.denied", violations)
            .Concat(Resolve(paths, overridePatch.Filesystem?.Denied ?? [], "override filesystem.denied", violations))
            .ToList();
        var ro = Resolve(paths, global.Filesystem.Readonly, "global filesystem.readonly", violations);
        var rw = Resolve(paths, global.Filesystem.Readwrite, "global filesystem.readwrite", violations);

        foreach (var p in Resolve(paths, overridePatch.Filesystem?.Readwrite ?? [], "override filesystem.readwrite", violations))
        {
            if (grantable.Any(g => paths.IsWithin(p, g))) rw.Add(p);
            else widening.Add($"override filesystem.readwrite: '{p}' is outside the grantable roots (only the loosen block can grant it)");
        }
        foreach (var p in Resolve(paths, overridePatch.Filesystem?.Readonly ?? [], "override filesystem.readonly", violations))
        {
            var narrows = rw.Any(g => paths.IsWithin(p, g));
            if (narrows || grantable.Any(g => paths.IsWithin(p, g)))
            {
                ro.Add(p);
                if (rw.RemoveAll(g => paths.Same(g, p)) > 0) notes.Add($"filesystem: '{p}' narrowed from read/write to read-only");
            }
            else
            {
                widening.Add($"override filesystem.readonly: '{p}' is outside the grantable roots (only the loosen block can grant it)");
            }
        }
        foreach (var p in Resolve(paths, loosen.Filesystem?.Readonly ?? [], "loosen filesystem.readonly", violations))
        {
            if (ro.Concat(rw).Concat(implicitGrants).Any(g => paths.IsWithin(p, g))) continue;
            ro.Add(p);
            loosened.Add($"filesystem.readonly + {p}");
        }
        foreach (var p in Resolve(paths, loosen.Filesystem?.Readwrite ?? [], "loosen filesystem.readwrite", violations))
        {
            if (rw.Concat(layout.ImplicitReadwrite.Select(i => Must(paths, i))).Any(g => paths.IsWithin(p, g))) continue;
            rw.Add(p);
            loosened.Add($"filesystem.readwrite + {p}");
        }

        // Floor: no grant may touch an implicit denial or contain the app data root.
        foreach (var g in ro.Concat(rw))
        {
            if (implicitDenied.FirstOrDefault(d => paths.Overlaps(g, d)) is { } hit)
                violations.Add($"filesystem: '{g}' overlaps the implicitly denied '{hit}' and can never be granted");
            else if (paths.Overlaps(g, appRoot))
                violations.Add($"filesystem: '{g}' overlaps the app data root '{appRoot}'; only the member's own folders there are ever granted");
        }

        // A path that is both denied and granted is denied (the sandbox: deny > read-only > read/write).
        int Drop(List<string> list, string reason)
        {
            var dropped = list.Where(g => denied.Any(d => paths.Same(d, g))).ToList();
            foreach (var g in dropped) notes.Add($"filesystem: '{g}' is denied; the {reason} grant is dropped");
            return list.RemoveAll(g => denied.Any(d => paths.Same(d, g)));
        }
        Drop(ro, "read-only");
        Drop(rw, "read/write");
        rw.RemoveAll(g => ro.Any(r => paths.Same(r, g)));

        var effective = new EffectivePolicy(
            Distinct(paths, ro), Distinct(paths, rw), Distinct(paths, denied), hosts,
            new ComputePolicy(idle, wall, mem, cpu, procs), clipboard, global.App.MaxConcurrentAgents,
            loosened, notes);
        return new MergeResult(effective, violations.Distinct().ToList(), widening);
    }

    /// <summary>Validation of the global document on its own (not layout-dependent).</summary>
    public static IReadOnlyList<string> CheckGlobal(GlobalPolicy g)
    {
        var errors = new List<string>();
        foreach (var h in g.Network.AllowHosts)
            if (FloorRules.HostProblem(h) is { } p) errors.Add($"global network.allowHosts: {p}");
        if (g.Compute.IdleTimeoutSec is < 60 or > FloorRules.TimeoutCeilingSec)
            errors.Add($"global compute.idleTimeoutSec must be 60..{FloorRules.TimeoutCeilingSec}");
        if (g.Compute.WallTimeoutSec is < 60 or > FloorRules.TimeoutCeilingSec)
            errors.Add($"global compute.wallTimeoutSec must be 60..{FloorRules.TimeoutCeilingSec}");
        if (g.Compute.MemoryMb is <= 0) errors.Add("global compute.memoryMb must be positive");
        if (g.Compute.CpuPercent is <= 0 or > FloorRules.CpuPercentCeiling) errors.Add("global compute.cpuPercent must be 1..100");
        if (g.Compute.MaxProcesses is <= 0 or > FloorRules.MaxProcessesCeiling) errors.Add($"global compute.maxProcesses must be 1..{FloorRules.MaxProcessesCeiling}");
        if (g.Ui.Clipboard != "none") errors.Add("global ui.clipboard: only 'none' is offered (GUI and clipboard stay disabled)");
        if (g.App.MaxConcurrentAgents is < 1 or > FloorRules.MaxConcurrentAgentsCeiling)
            errors.Add($"global app.maxConcurrentAgents must be 1..{FloorRules.MaxConcurrentAgentsCeiling}");
        if (g.App.HeavyWorkAtOnce is < 1 or > Bus.BusEngine.MaxHeavyAtOnce)
            errors.Add($"global app.heavyWorkAtOnce must be 1..{Bus.BusEngine.MaxHeavyAtOnce}");
        if (g.App.Backend != "default") errors.Add("global app.backend: only 'default' is supported (the OS's default sandbox backend)");
        return errors;
    }

    private static int Tighten(string field, int global, int? value, List<string> widening, List<string> violations, int ceiling)
    {
        if (value is not { } v) return global;
        if (v < 1) { violations.Add($"override compute.{field} must be positive"); return global; }
        if (v > global) widening.Add($"override compute.{field}: {v} is above the global {global} (only the loosen block can raise it)");
        return Math.Min(global, Math.Min(v, ceiling));
    }

    private static int Raise(string field, int current, int? value, int ceiling, List<string> loosened, List<string> violations)
    {
        if (value is not { } v) return current;
        if (v < 1) { violations.Add($"loosen compute.{field} must be positive"); return current; }
        if (v > ceiling) violations.Add($"loosen compute.{field}: {v} is above the hard ceiling {ceiling}");
        var raised = Math.Min(v, ceiling);
        if (raised <= current) return current;
        loosened.Add($"compute.{field} {current} -> {raised}");
        return raised;
    }

    private static int? TightenCap(string field, int? global, int? value, List<string> widening, List<string> violations)
    {
        if (value is not { } v) return global;
        if (v < 1) { violations.Add($"override compute.{field} must be positive"); return global; }
        if (global is { } g && v > g)
        {
            widening.Add($"override compute.{field}: {v} is above the global {g} (only the loosen block can raise it)");
            return g;
        }
        return v;
    }

    private static int? RaiseCap(string field, int? current, int? value, int? ceiling, List<string> loosened, List<string> violations)
    {
        if (value is not { } v) return current;
        if (v < 1) { violations.Add($"loosen compute.{field} must be positive"); return current; }
        if (ceiling is { } c && v > c) violations.Add($"loosen compute.{field}: {v} is above the hard ceiling {c}");
        if (current is not { } cur) return current; // no cap at all is already the loosest
        var raised = ceiling is { } c2 ? Math.Min(v, c2) : v;
        if (raised <= cur) return current;
        loosened.Add($"compute.{field} {cur} -> {raised}");
        return raised;
    }

    private static string Meet(string a, string b)
    {
        if (a == b) return a;
        if (a == "all") return b;
        if (b == "all") return a;
        return "none"; // read vs write, or either with none
    }

    private static string Must(PolicyPaths paths, string p) =>
        paths.Resolve(p) ?? throw new ArgumentException($"layout path is not absolute for {paths.Os}: {p}");

    private static List<string> Resolve(PolicyPaths paths, IEnumerable<string> items, string field, List<string> violations)
    {
        var list = new List<string>();
        foreach (var raw in items)
        {
            var r = paths.Resolve(raw);
            if (r is null) violations.Add($"{field}: '{raw}' is not an absolute {paths.Os} path (or ~/...)");
            else list.Add(r);
        }
        return list;
    }

    private static IReadOnlyList<string> Distinct(PolicyPaths paths, IEnumerable<string> items) =>
        items.Distinct(paths.Comparer).ToList();
}
