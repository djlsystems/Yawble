using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yawble.Core.Teams;

namespace Yawble.Core.Tasks;

/// <summary>The host's git on one repository, hardened for reading a checkout the person (not an agent) owns.</summary>
public sealed class RepoGit(HostGit git, string path)
{
    public string Path { get; } = path;

    // No filesystem monitor and no optional lock: a repository config must not make a read-only check run a program.
    private static readonly string[] Hardening = ["-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false", "--no-optional-locks"];

    public Task<GitResult> TryAsync(CancellationToken ct, params string[] args) => git.TryRunAsync(Path, [.. Hardening, .. args], ct);

    public async Task<string?> RevAsync(string reference, CancellationToken ct)
    {
        var r = await TryAsync(ct, "rev-parse", "--verify", "-q", reference + "^{commit}");
        return r.ExitCode == 0 && r.Stdout.Length > 0 ? r.Stdout : null;
    }
}

/// <summary>The tracked files at one commit: path to git blob id (the content identity of a file at that commit).</summary>
public sealed class TrackedTree(string commit, IReadOnlyDictionary<string, string> files)
{
    public string Commit { get; } = commit;
    public IReadOnlyDictionary<string, string> Files { get; } = files;
    public bool Has(string path) => Files.ContainsKey(path);
    public bool HasDirectory(string dir) => dir.Length == 0 || dir == "." || Files.Keys.Any(k => k.StartsWith(dir.TrimEnd('/') + "/", PathComparison));

    public static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static async Task<TrackedTree> ReadAsync(RepoGit repo, string commit, CancellationToken ct)
    {
        var r = await repo.TryAsync(ct, "ls-tree", "-r", "-z", commit);
        if (r.ExitCode != 0) throw new GitException($"git ls-tree {commit} failed: {r.Stderr}");
        var files = new Dictionary<string, string>(PathComparer);
        foreach (var entry in r.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = entry.IndexOf('\t');
            if (tab < 0) continue;
            var meta = entry[..tab].Split(' ');
            if (meta.Length < 3) continue;
            files[entry[(tab + 1)..]] = meta[2];
        }
        return new TrackedTree(commit, files);
    }
}

/// <summary>Which files "Always" will pin, and what the pin cannot cover.</summary>
public sealed record PinSet(IReadOnlyList<(string Path, string Blob)> Files, IReadOnlyList<string> Warnings)
{
    /// <summary>Nothing pinned but the command line itself (needs the person's explicit tick in files mode).</summary>
    public bool CommandLineOnly => Files.Count == 0;
}

public static class TaskPinning
{
    public const int FormatVersion = 1;

    private static readonly string[] NodeLocks = ["package.json", "package-lock.json", "pnpm-lock.yaml", "yarn.lock", "bun.lockb", "bun.lock", ".npmrc"];
    private static readonly HashSet<string> NodePrograms = new(StringComparer.OrdinalIgnoreCase) { "npm", "pnpm", "yarn", "npx", "bun" };
    private static readonly string[] DotnetProps = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "nuget.config"];
    private static readonly string[] ProjectExts = [".sln", ".slnx", ".csproj", ".fsproj", ".vbproj"];

    // ------------------------------------------------------------------ the pinned file set

    /// <param name="workdir">Repo-relative, '/'-separated; "" for the root.</param>
    /// <param name="programRelative">The program's repo-relative path when it lives inside the repo, else null.</param>
    public static PinSet Compute(HostTaskDef def, string workdir, string? programRelative, TrackedTree tree)
    {
        var picked = new Dictionary<string, string>(TrackedTree.PathComparer);
        var warnings = new List<string>();
        void Add(string? p)
        {
            if (p is not null && tree.Files.TryGetValue(p, out var blob)) picked[Actual(tree, p)] = blob;
        }

        Add(programRelative);
        var resolvedArgs = def.Args.Select(a => Resolve(workdir, a)).ToList();
        foreach (var a in resolvedArgs) Add(a);

        var kind = Path.GetFileNameWithoutExtension(def.Program.Replace('\\', '/').Split('/')[^1]);
        if (NodePrograms.Contains(kind))
        {
            foreach (var dir in new[] { workdir, "" }.Distinct())
                foreach (var f in NodeLocks) Add(Join(dir, f));
        }
        else if (kind.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var projectDirs = new List<string>();
            var found = false;
            foreach (var a in resolvedArgs.Where(a => a is not null))
            {
                if (ProjectExts.Any(e => a!.EndsWith(e, StringComparison.OrdinalIgnoreCase)) && tree.Has(a!))
                {
                    Add(a);
                    projectDirs.Add(Parent(a!));
                    found = true;
                }
                else if (tree.HasDirectory(a!) && !tree.Has(a!))
                {
                    foreach (var f in ProjectsIn(tree, a!)) { Add(f); found = true; }
                    projectDirs.Add(a!);
                }
            }
            if (!found)
                foreach (var f in ProjectsIn(tree, workdir)) Add(f);
            foreach (var start in projectDirs.Append(workdir).Distinct())
                for (var dir = start; ; dir = Parent(dir))
                {
                    foreach (var f in DotnetProps) Add(Join(dir, f));
                    if (dir.Length == 0) break;
                }
        }

        foreach (var pattern in def.Watch)
        {
            var matched = false;
            var rx = GlobToRegex(pattern.Replace('\\', '/').TrimStart('/'));
            foreach (var p in tree.Files.Keys.Where(k => rx.IsMatch(k))) { Add(p); matched = true; }
            if (!matched) warnings.Add($"watch '{pattern}' matches no tracked file at {Short(tree.Commit)}: it pins nothing");
        }
        if (programRelative is not null && !tree.Has(programRelative)) warnings.Add($"the program {programRelative} is not tracked at {Short(tree.Commit)}");
        return new PinSet(picked.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)).ToList(), warnings);
    }

    private static string Actual(TrackedTree tree, string p) => tree.Files.Keys.First(k => string.Equals(k, p, TrackedTree.PathComparison));

    private static IEnumerable<string> ProjectsIn(TrackedTree tree, string dir) =>
        tree.Files.Keys.Where(k => Parent(k).Equals(dir, TrackedTree.PathComparison) && ProjectExts.Any(e => k.EndsWith(e, StringComparison.OrdinalIgnoreCase)));

    private static string Parent(string p) { var i = p.LastIndexOf('/'); return i < 0 ? "" : p[..i]; }
    private static string Join(string dir, string name) => dir.Length == 0 ? name : dir + "/" + name;

    /// <summary>An argument resolved against the working folder as a repo-relative path, or null when it leaves the repo or is not a path.</summary>
    private static string? Resolve(string workdir, string arg)
    {
        if (arg.Length == 0 || arg.StartsWith('-') || Path.IsPathRooted(arg) || arg.Contains(':')) return null;
        var parts = new List<string>(workdir.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var s in arg.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (s == ".") continue;
            if (s == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(s);
        }
        return string.Join('/', parts);
    }

    public static Regex GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var ch = glob[i];
            if (ch == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                else sb.Append(".*");
            }
            else if (ch == '*') sb.Append("[^/]*");
            else if (ch == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(ch.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);
    }

    // ------------------------------------------------------------------ hashes

    /// <summary>The canonical definition (fixed key order) and its SHA-256. The name is not in it; the resolved program path is.</summary>
    public static (string Json, string Hash) Definition(long teamId, HostTaskDef d, string resolvedProgram)
    {
        var program = OperatingSystem.IsWindows() ? resolvedProgram.ToLowerInvariant() : resolvedProgram;
        var node = new JsonObject
        {
            ["version"] = FormatVersion,
            ["team"] = teamId,
            ["repo"] = d.Repo,
            ["program"] = program,
            ["args"] = new JsonArray(d.Args.Select(a => (JsonNode?)a).ToArray()),
            ["workdir"] = d.Workdir,
            ["timeout_s"] = d.TimeoutSeconds,
            ["env_names"] = new JsonArray(d.EnvNames.OrderBy(x => x, StringComparer.Ordinal).Select(x => (JsonNode?)x).ToArray()),
            ["watch"] = new JsonArray(d.Watch.OrderBy(x => x, StringComparer.Ordinal).Select(x => (JsonNode?)x).ToArray()),
            ["pin_mode"] = d.PinMode,
        };
        // The bindings (where each value comes from), never a value: replacing an expired token asks nothing, re-pointing a binding asks
        // again. Absent when there are none, so a task saved before bindings existed keeps the hash it was approved at.
        if (d.EnvFromConnections.Count > 0)
            node["env_from_connections"] = new JsonArray(d.EnvFromConnections.OrderBy(b => b.Name, StringComparer.Ordinal).Select(b => (JsonNode?)new JsonObject
            {
                ["name"] = b.Name, ["connection_id"] = b.ConnectionId, ["field"] = b.Field, ["connection_name"] = b.ConnectionName,
            }).ToArray());
        var json = node.ToJsonString();
        return (json, Sha256(json));
    }

    /// <summary>SHA-256 over the sorted "path NUL blob" lines of the pinned files, as read from the commit.</summary>
    public static string FilesHash(IReadOnlyList<(string Path, string Blob)> files) =>
        Sha256(string.Concat(files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => f.Path + "\0" + f.Blob + "\n")));

    public static string FilesJson(IReadOnlyList<(string Path, string Blob)> files) =>
        new JsonArray(files.Select(f => (JsonNode?)new JsonObject { ["path"] = f.Path, ["blob"] = f.Blob }).ToArray()).ToJsonString();

    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static string Short(string sha) => sha[..Math.Min(8, sha.Length)];

    // ------------------------------------------------------------------ "what changed since you approved"

    public const int MaxDiffLinesPerFile = 200, MaxDiffLinesTotal = 1000;

    public static async Task<JsonObject> DescribeChangeAsync(ApprovalRow old, string newDefJson, PinSet newPins, string newCommit, string newDefHash, string newFilesHash,
        string pinMode, RepoGit repo, CancellationToken ct)
    {
        var result = new JsonObject { ["approvedCommit"] = old.CommitSha, ["currentCommit"] = newCommit };

        var definition = new JsonArray();
        var was = JsonNode.Parse(old.DefinitionJson)!.AsObject();
        var now = JsonNode.Parse(newDefJson)!.AsObject();
        foreach (var key in was.Select(kv => kv.Key).Union(now.Select(kv => kv.Key)))
        {
            var a = was[key]?.ToJsonString();
            var b = now[key]?.ToJsonString();
            if (a != b) definition.Add(new JsonObject { ["field"] = key, ["old"] = a, ["new"] = b });
        }
        result["definition"] = definition;

        var oldFiles = (JsonNode.Parse(old.FilesJson) as JsonArray ?? []).ToDictionary(n => n!["path"]!.GetValue<string>(), n => n!["blob"]!.GetValue<string>(), TrackedTree.PathComparer);
        var newFiles = newPins.Files.ToDictionary(f => f.Path, f => f.Blob, TrackedTree.PathComparer);
        var reachable = old.CommitSha == newCommit || (await repo.TryAsync(ct, "cat-file", "-e", old.CommitSha + "^{commit}")).ExitCode == 0;
        var files = new JsonArray();
        var budget = MaxDiffLinesTotal;
        foreach (var path in oldFiles.Keys.Union(newFiles.Keys).Order(StringComparer.Ordinal))
        {
            var hadOld = oldFiles.TryGetValue(path, out var oldBlob);
            var hasNew = newFiles.TryGetValue(path, out var newBlob);
            if (hadOld && hasNew && oldBlob == newBlob) continue;
            var entry = new JsonObject { ["path"] = path, ["change"] = !hadOld ? "added" : !hasNew ? "removed" : "changed", ["oldBlob"] = oldBlob, ["newBlob"] = newBlob };
            if (reachable && budget > 0)
            {
                var d = await repo.TryAsync(ct, "diff", "--no-ext-diff", "--no-textconv", "--no-color", old.CommitSha + ".." + newCommit, "--", path);
                if (d.ExitCode == 0 && d.Stdout.Length > 0)
                {
                    var lines = d.Stdout.Split('\n');
                    var take = Math.Min(Math.Min(lines.Length, MaxDiffLinesPerFile), budget);
                    budget -= take;
                    entry["diff"] = string.Join('\n', lines.Take(take));
                    if (lines.Length > take) entry["diffMoreLines"] = lines.Length - take;
                }
            }
            files.Add(entry);
        }
        result["files"] = files;
        result["diffAvailable"] = reachable;

        if (pinMode == PinModes.Commit && old.CommitSha != newCommit)
        {
            var log = reachable ? await repo.TryAsync(ct, "log", "--oneline", "--no-color", "-n", "50", old.CommitSha + ".." + newCommit) : null;
            var stat = reachable ? await repo.TryAsync(ct, "diff", "--stat", "--no-ext-diff", "--no-color", old.CommitSha + ".." + newCommit) : null;
            result["commits"] = log?.ExitCode == 0 ? log.Stdout : null;
            result["stat"] = stat?.ExitCode == 0 ? stat.Stdout : null;
        }
        result["definitionHashChanged"] = old.DefinitionHash != newDefHash;
        result["filesHashChanged"] = old.FilesHash != newFilesHash;
        return result;
    }
}
