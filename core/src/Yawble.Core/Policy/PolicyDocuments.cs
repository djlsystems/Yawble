using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yawble.Core.Policy;

/// <summary>A policy document or save was refused; every reason is listed.</summary>
public class PolicyException(IReadOnlyList<string> errors)
    : Exception("policy refused: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// <see cref="PolicyStore.AddTeamHost"/> wrote nothing because the team's members are not the ones it was given (one hired
/// or removed since the list was built): the caller builds the list again, with each member's run layout, and calls again.
/// </summary>
public sealed class TeamMembersChangedException(long teamId, IReadOnlyList<long> given, IReadOnlyList<long> current)
    : PolicyException([$"the team's members changed while the host was checked (checked {Ids(given)}, now {Ids(current)}); nothing was written"])
{
    public long TeamId { get; } = teamId;
    public IReadOnlyList<long> Given { get; } = given;
    public IReadOnlyList<long> Current { get; } = current;

    private static string Ids(IReadOnlyList<long> ids) => ids.Count == 0 ? "none" : string.Join(", ", ids);
}

public sealed record FilesystemPolicy(
    IReadOnlyList<string> GrantableRoots,
    IReadOnlyList<string> Readonly,
    IReadOnlyList<string> Readwrite,
    IReadOnlyList<string> Denied);

public sealed record NetworkPolicy(IReadOnlyList<string> AllowHosts);

/// <summary>
/// The two timeouts are enforced. The hard caps (memory, CPU, processes) are carried and merged with the same
/// tighten/loosen rules, but nothing enforces them.
/// </summary>
public sealed record ComputePolicy(int IdleTimeoutSec, int WallTimeoutSec, int? MemoryMb = null, int? CpuPercent = null, int? MaxProcesses = null);

public sealed record UiPolicy(string Clipboard);

/// <summary>app.*: the global run limit, the backend, and how many runs may hold the heavy-work lease at once (1-8, default 1).</summary>
public sealed record AppPolicy(int MaxConcurrentAgents, string Backend, int HeavyWorkAtOnce = 1);

/// <summary>policy_global: one row.</summary>
public sealed record GlobalPolicy(FilesystemPolicy Filesystem, NetworkPolicy Network, ComputePolicy Compute, UiPolicy Ui, AppPolicy App)
{
    public static GlobalPolicy Default { get; } = new(
        // The personal folders are denied too: on Windows the drive root is
        // enumerable (microsoft/mxc#1676), so their names would otherwise be listable; a deny keeps them closed.
        new FilesystemPolicy(["~/src"], [], [],
            ["~/.ssh", "~/.aws", "~/.config/gh", "~/Desktop", "~/Documents", "~/Downloads", "~/Music", "~/OneDrive", "~/Pictures", "~/Videos"]),
        new NetworkPolicy([]),
        new ComputePolicy(IdleTimeoutSec: 1800, WallTimeoutSec: 7200),
        new UiPolicy("none"),
        new AppPolicy(MaxConcurrentAgents: 4, Backend: "default"));

    public static GlobalPolicy Parse(string json)
    {
        var r = new DocReader("global");
        var root = r.Root(json);
        if (root is null) throw new PolicyException(r.Errors);
        r.Only(root, "", "filesystem", "network", "compute", "ui", "app");
        var fs = r.Obj(root, "filesystem");
        var net = r.Obj(root, "network");
        var compute = r.Obj(root, "compute");
        var ui = r.Obj(root, "ui");
        var app = r.Obj(root, "app");
        r.Only(fs, "filesystem.", "grantableRoots", "readonly", "readwrite", "denied");
        r.Only(net, "network.", "allowHosts");
        r.Only(compute, "compute.", "idleTimeoutSec", "wallTimeoutSec", "memoryMb", "cpuPercent", "maxProcesses");
        r.Only(ui, "ui.", "clipboard");
        r.Only(app, "app.", "maxConcurrentAgents", "backend", "heavyWorkAtOnce");
        var d = Default;
        var policy = new GlobalPolicy(
            new FilesystemPolicy(
                r.Strings(fs, "filesystem.grantableRoots") ?? d.Filesystem.GrantableRoots,
                r.Strings(fs, "filesystem.readonly") ?? d.Filesystem.Readonly,
                r.Strings(fs, "filesystem.readwrite") ?? d.Filesystem.Readwrite,
                r.Strings(fs, "filesystem.denied") ?? d.Filesystem.Denied),
            new NetworkPolicy(r.Strings(net, "network.allowHosts") ?? d.Network.AllowHosts),
            new ComputePolicy(
                r.Int(compute, "compute.idleTimeoutSec") ?? d.Compute.IdleTimeoutSec,
                r.Int(compute, "compute.wallTimeoutSec") ?? d.Compute.WallTimeoutSec,
                r.Int(compute, "compute.memoryMb"),
                r.Int(compute, "compute.cpuPercent"),
                r.Int(compute, "compute.maxProcesses")),
            new UiPolicy(r.String(ui, "ui.clipboard") ?? d.Ui.Clipboard),
            new AppPolicy(r.Int(app, "app.maxConcurrentAgents") ?? d.App.MaxConcurrentAgents, r.String(app, "app.backend") ?? d.App.Backend,
                r.Int(app, "app.heavyWorkAtOnce") ?? d.App.HeavyWorkAtOnce));
        if (r.Errors.Count > 0) throw new PolicyException(r.Errors);
        return policy;
    }

    public JsonObject ToJson()
    {
        var compute = new JsonObject
        {
            ["idleTimeoutSec"] = Compute.IdleTimeoutSec,
            ["wallTimeoutSec"] = Compute.WallTimeoutSec,
        };
        if (Compute.MemoryMb is { } m) compute["memoryMb"] = m;
        if (Compute.CpuPercent is { } c) compute["cpuPercent"] = c;
        if (Compute.MaxProcesses is { } p) compute["maxProcesses"] = p;
        return new JsonObject
        {
            ["filesystem"] = new JsonObject
            {
                ["grantableRoots"] = DocReader.Array(Filesystem.GrantableRoots),
                ["readonly"] = DocReader.Array(Filesystem.Readonly),
                ["readwrite"] = DocReader.Array(Filesystem.Readwrite),
                ["denied"] = DocReader.Array(Filesystem.Denied),
            },
            ["network"] = new JsonObject { ["allowHosts"] = DocReader.Array(Network.AllowHosts) },
            ["compute"] = compute,
            ["ui"] = new JsonObject { ["clipboard"] = Ui.Clipboard },
            ["app"] = new JsonObject { ["maxConcurrentAgents"] = App.MaxConcurrentAgents, ["backend"] = App.Backend, ["heavyWorkAtOnce"] = App.HeavyWorkAtOnce },
        };
    }
}

public enum PatchKind
{
    /// <summary>member_policy.override_json: may only tighten.</summary>
    Override,

    /// <summary>member_policy.loosen_json: saved only by the person, after a confirmation.</summary>
    Loosen,
}

public sealed record FilesystemPatch(IReadOnlyList<string>? Readonly = null, IReadOnlyList<string>? Readwrite = null, IReadOnlyList<string>? Denied = null);

public sealed record NetworkPatch(IReadOnlyList<string>? AllowHosts = null);

public sealed record ComputePatch(int? IdleTimeoutSec = null, int? WallTimeoutSec = null, int? MemoryMb = null, int? CpuPercent = null, int? MaxProcesses = null);

public sealed record UiPatch(string? Clipboard = null);

/// <summary>A per-agent override or loosen block: the global shape, every field optional.</summary>
public sealed record PolicyPatch(FilesystemPatch? Filesystem = null, NetworkPatch? Network = null, ComputePatch? Compute = null, UiPatch? Ui = null)
{
    public static PolicyPatch Empty { get; } = new();

    public bool IsEmpty =>
        (Filesystem is null || (Filesystem.Readonly is null && Filesystem.Readwrite is null && Filesystem.Denied is null))
        && (Network?.AllowHosts is null)
        && (Compute is null || Compute == new ComputePatch())
        && (Ui?.Clipboard is null);

    public static PolicyPatch Parse(string? json, PatchKind kind)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        var name = kind == PatchKind.Override ? "override" : "loosen";
        var r = new DocReader(name);
        var root = r.Root(json);
        if (root is null) throw new PolicyException(r.Errors);
        if (root.ContainsKey("app"))
            r.Errors.Add($"{name}: app.* is set only in the global policy and is never overridable");
        if (kind == PatchKind.Loosen && root.ContainsKey("ui"))
            r.Errors.Add("loosen: ui cannot be loosened");
        r.Only(root, "", "filesystem", "network", "compute", "ui", "app");
        var fs = r.Obj(root, "filesystem");
        if (fs is not null && fs.ContainsKey("grantableRoots"))
            r.Errors.Add($"{name}: filesystem.grantableRoots is set only in the global policy");
        if (kind == PatchKind.Loosen && fs is not null && fs.ContainsKey("denied"))
            r.Errors.Add("loosen: filesystem.denied cannot be changed by a loosen block (no deny can be removed)");
        r.Only(fs, "filesystem.", "readonly", "readwrite", "denied", "grantableRoots");
        var net = r.Obj(root, "network");
        r.Only(net, "network.", "allowHosts");
        var compute = r.Obj(root, "compute");
        r.Only(compute, "compute.", "idleTimeoutSec", "wallTimeoutSec", "memoryMb", "cpuPercent", "maxProcesses");
        var ui = kind == PatchKind.Override ? r.Obj(root, "ui") : null;
        r.Only(ui, "ui.", "clipboard");

        var patch = new PolicyPatch(
            fs is null ? null : new FilesystemPatch(
                r.Strings(fs, "filesystem.readonly"),
                r.Strings(fs, "filesystem.readwrite"),
                kind == PatchKind.Override ? r.Strings(fs, "filesystem.denied") : null),
            net is null ? null : new NetworkPatch(r.Strings(net, "network.allowHosts")),
            compute is null ? null : new ComputePatch(
                r.Int(compute, "compute.idleTimeoutSec"),
                r.Int(compute, "compute.wallTimeoutSec"),
                r.Int(compute, "compute.memoryMb"),
                r.Int(compute, "compute.cpuPercent"),
                r.Int(compute, "compute.maxProcesses")),
            ui is null ? null : new UiPatch(r.String(ui, "ui.clipboard")));
        if (r.Errors.Count > 0) throw new PolicyException(r.Errors);
        return patch;
    }

    public JsonObject ToJson()
    {
        var root = new JsonObject();
        if (Filesystem is { } fs)
        {
            var o = new JsonObject();
            if (fs.Readonly is not null) o["readonly"] = DocReader.Array(fs.Readonly);
            if (fs.Readwrite is not null) o["readwrite"] = DocReader.Array(fs.Readwrite);
            if (fs.Denied is not null) o["denied"] = DocReader.Array(fs.Denied);
            if (o.Count > 0) root["filesystem"] = o;
        }
        if (Network?.AllowHosts is { } hosts) root["network"] = new JsonObject { ["allowHosts"] = DocReader.Array(hosts) };
        if (Compute is { } c)
        {
            var o = new JsonObject();
            if (c.IdleTimeoutSec is { } i) o["idleTimeoutSec"] = i;
            if (c.WallTimeoutSec is { } w) o["wallTimeoutSec"] = w;
            if (c.MemoryMb is { } m) o["memoryMb"] = m;
            if (c.CpuPercent is { } cp) o["cpuPercent"] = cp;
            if (c.MaxProcesses is { } mp) o["maxProcesses"] = mp;
            if (o.Count > 0) root["compute"] = o;
        }
        if (Ui?.Clipboard is { } clip) root["ui"] = new JsonObject { ["clipboard"] = clip };
        return root;
    }
}

/// <summary>Strict reader: unknown fields and wrong types are errors, with a dotted path in each message.</summary>
internal sealed class DocReader(string document)
{
    public List<string> Errors { get; } = [];

    public JsonObject? Root(string json)
    {
        try
        {
            var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            if (node is JsonObject o) return o;
            Errors.Add($"{document}: must be a JSON object");
        }
        catch (JsonException e)
        {
            Errors.Add($"{document}: not valid JSON ({e.Message})");
        }
        return null;
    }

    public void Only(JsonObject? o, string prefix, params string[] allowed)
    {
        if (o is null) return;
        foreach (var key in o.Select(p => p.Key))
            if (!allowed.Contains(key, StringComparer.Ordinal))
                Errors.Add($"{document}: unknown field {prefix}{key}");
    }

    public JsonObject? Obj(JsonObject? parent, string key)
    {
        if (parent is null || !parent.TryGetPropertyValue(key, out var node) || node is null) return null;
        if (node is JsonObject o) return o;
        Errors.Add($"{document}: {key} must be an object");
        return null;
    }

    public IReadOnlyList<string>? Strings(JsonObject? parent, string path)
    {
        var key = path[(path.LastIndexOf('.') + 1)..];
        if (parent is null || !parent.TryGetPropertyValue(key, out var node) || node is null) return null;
        if (node is not JsonArray a)
        {
            Errors.Add($"{document}: {path} must be an array of strings");
            return null;
        }
        var list = new List<string>();
        foreach (var item in a)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s)) list.Add(s);
            else Errors.Add($"{document}: {path} must contain only strings");
        }
        return list;
    }

    public int? Int(JsonObject? parent, string path)
    {
        var key = path[(path.LastIndexOf('.') + 1)..];
        if (parent is null || !parent.TryGetPropertyValue(key, out var node) || node is null) return null;
        if (node is JsonValue v && v.TryGetValue<int>(out var i)) return i;
        if (node is JsonValue d && d.TryGetValue<double>(out var dbl) && dbl == Math.Floor(dbl) && dbl is >= int.MinValue and <= int.MaxValue) return (int)dbl;
        Errors.Add($"{document}: {path} must be an integer");
        return null;
    }

    public string? String(JsonObject? parent, string path)
    {
        var key = path[(path.LastIndexOf('.') + 1)..];
        if (parent is null || !parent.TryGetPropertyValue(key, out var node) || node is null) return null;
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        Errors.Add($"{document}: {path} must be a string");
        return null;
    }

    public static JsonArray Array(IEnumerable<string> items) => new(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
}
