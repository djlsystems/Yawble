using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Policy;
using MemberRef = Yawble.Core.Bus.MemberRef;

namespace Yawble.Core.Integrations;

/// <summary>A folder ask (folder_asks, 0053). <paramref name="Member"/> and <paramref name="AskedBy"/>: "Team/Name".</summary>
public sealed record FolderAskRow(long Id, string At, long TeamId, string Team, long MemberId, string Member, string AskedBy, string Path, string Access,
    string Reason, bool PathExisted, string State, string? GrantedAccess, string? DecidedBy, string? DecidedVia, string? DecidedAt, string? Note,
    long? Deliverable, long? CausationSeq)
{
    public string MemberName => Member[(Member.IndexOf('/') + 1)..];

    public string AskedByName => AskedBy[(AskedBy.IndexOf('/') + 1)..];
}

/// <summary>The access a folder ask asks for, and the person's three answers (folder.answer's choice).</summary>
public static class FolderAccess
{
    public const string Read = "read", ReadWrite = "readwrite", Deny = "deny";

    /// <summary>The access an agent named, in the words it may use ("read-only", "rw", ...); null when it is neither.</summary>
    public static string? Parse(string? access) => (access ?? "").Trim().ToLowerInvariant() switch
    {
        "read" or "readonly" or "read-only" or "ro" or "r" => Read,
        "readwrite" or "read-write" or "read/write" or "rw" or "write" or "w" => ReadWrite,
        _ => null,
    };

    /// <summary>What the person may answer: never wider than asked (a read ask is never granted read-write).</summary>
    public static IReadOnlyList<string> ChoicesFor(string asked) => asked == ReadWrite ? [ReadWrite, Read, Deny] : [Read, Deny];

    public static string Words(string access) => access == ReadWrite ? "read-write" : "read-only";
}

/// <summary>
/// Which folders an agent may ask for at all. Pure: every rule reads the member's run layout (the app's own folders,
/// the implicit denials, other teams' roots), the global policy's denied list and the other teams' folders the caller
/// names. A refusal is said to the agent at once and no ask is raised. None of this changes a policy rule: what may be
/// granted is still decided by the policy merger when the person approves (<see cref="PolicyStore.PreviewLoosen"/>).
/// </summary>
public static class FolderAskRules
{
    /// <summary>Folders under the person's home that hold secrets, refused whatever the policy says (and their contents).</summary>
    public static readonly IReadOnlyList<string> SecretFolders =
    [
        ".ssh", ".aws", ".azure", ".gnupg", ".kube", ".docker", ".config/gh", ".config/gcloud", ".password-store",
        "AppData/Roaming/Microsoft/Credentials", "AppData/Local/Microsoft/Credentials", "AppData/Roaming/Microsoft/Protect",
        "AppData/Roaming/Microsoft/Crypto", "AppData/Roaming/GitHub CLI", "Library/Keychains",
    ];

    /// <summary>Windows: folders under any drive refused for any access, and those refused for read-write only.</summary>
    public static readonly IReadOnlyList<string> WindowsSystem = ["Windows", "System Volume Information", "$Recycle.Bin", "Recovery"];

    public static readonly IReadOnlyList<string> WindowsSystemWrite = ["Program Files", "Program Files (x86)", "ProgramData"];

    /// <summary>Linux and macOS: folders refused for any access, and those refused for read-write only.</summary>
    public static readonly IReadOnlyList<string> UnixSystem = ["/proc", "/sys", "/dev", "/boot", "/etc", "/private/etc", "/System"];

    public static readonly IReadOnlyList<string> UnixSystemWrite = ["/usr", "/bin", "/sbin", "/lib", "/lib64", "/opt", "/var", "/private", "/Library", "/Applications"];

    /// <summary>
    /// Why the member may not ask for this folder (said to the agent; no ask is raised), or null. <paramref name="path"/> is
    /// resolved (<see cref="PolicyPaths.Resolve"/>). <paramref name="otherTeamFolders"/>: every other team's repositories
    /// (the person's checkouts and the shared views) and documents folders.
    /// </summary>
    public static string? Problem(PolicyPaths paths, string path, string access, RunLayout layout, GlobalPolicy global, IReadOnlyList<string> otherTeamFolders)
    {
        var sep = layout.Os == TargetOs.Windows ? '\\' : '/';
        if (path.EndsWith(sep))
            return $"'{path}' is a drive or file system root; ask for the one folder your work needs";
        if (paths.IsWithin(paths.Home, path))
            return paths.Same(paths.Home, path)
                ? $"'{path}' is the person's user profile folder; ask for the one folder inside it your work needs"
                : $"'{path}' contains the person's user profile folder; ask for the one folder your work needs";
        foreach (var s in SecretFolders)
        {
            var secret = paths.Combine(paths.Home, s.Split('/'));
            if (paths.Overlaps(path, secret)) return $"'{path}' is a folder that keeps the person's secrets or keys ({secret}); it is never granted";
        }
        if (Hit(paths, path, [layout.DataDir, layout.CredentialDir, layout.DatabasePath, layout.HostGrokHome]) is { } data)
            return $"'{path}' is Yawble's own data, credentials or secrets ({data}); it is never granted to an agent";
        if (Hit(paths, path, [.. layout.OtherTeamRoots, .. otherTeamFolders]) is { } other)
            return $"'{path}' belongs to another team ({other}); a team works only in its own repositories and documents";
        if (Hit(paths, path, layout.OtherMemberRoots) is { } mate)
            return $"'{path}' is another member's folder ({mate}); fetch a teammate's work from the team's shared view instead";
        if (paths.Overlaps(path, layout.AppDataRoot))
            return $"'{path}' is inside Yawble's own data folder ({layout.AppDataRoot}); only a member's own folders there are ever granted";
        if (Hit(paths, path, layout.ImplicitDenied) is { } denied)
            return $"'{path}' is on the list every agent is denied ({denied}); it is never granted";
        foreach (var raw in global.Filesystem.Denied)
            if (paths.Resolve(raw) is { } d && paths.IsWithin(path, d))
                return $"'{path}' is under {d}, which the person's policy denies to every agent";
        if (SystemProblem(paths, path, access, layout.Os) is { } system) return system;
        return null;
    }

    private static string? SystemProblem(PolicyPaths paths, string path, string access, TargetOs os)
    {
        if (os == TargetOs.Windows)
        {
            var root = path.Length >= 3 && path[1] == ':' ? path[..3] : null;
            if (root is null) return null;
            foreach (var name in WindowsSystem)
                if (paths.IsWithin(path, root + name)) return $"'{path}' is a Windows system folder ({root}{name}); it is never granted";
            if (access == FolderAccess.ReadWrite)
                foreach (var name in WindowsSystemWrite)
                    if (paths.IsWithin(path, root + name))
                        return $"'{path}' is under {root}{name}, where installed programs live; it may be asked for read-only, never read-write";
            return null;
        }
        foreach (var s in UnixSystem)
            if (paths.IsWithin(path, s)) return $"'{path}' is a system folder ({s}); it is never granted";
        if (access == FolderAccess.ReadWrite)
            foreach (var s in UnixSystemWrite)
                if (paths.IsWithin(path, s)) return $"'{path}' is a system folder ({s}); it may be asked for read-only, never read-write";
        return null;
    }

    private static string? Hit(PolicyPaths paths, string path, IEnumerable<string> folders)
    {
        foreach (var f in folders)
            if (paths.Normalize(f) is { } n && paths.Overlaps(path, n)) return n;
        return null;
    }
}

/// <summary>
/// Folder asks: an agent's request_folder names a folder and read or read-write access, for itself or (a Manager)
/// for a member of its own team. Paths an agent may never have are refused at once (<see cref="FolderAskRules"/>, then
/// the policy merger's own checks), so the person is only asked about a folder that can be granted. The person answers
/// in Needs you (folder.answer): read-write (only when asked), read-only, or deny. An approval writes the path into the
/// member's loosen block through the policy store (<see cref="PolicyStore.PreviewLoosen"/> and
/// <see cref="PolicyStore.SaveLoosen"/>, the same code as policy.saveLoosen: loosen_summary and loosen_approved_at set),
/// confirmed as exactly that one path; the team's Manager is told to retry. A deny tells the asker. Either applies from
/// the member's next run. Each ask and answer is in folder_ask_events, and the answer call in the audit log.
/// </summary>
public sealed class FolderAskService
{
    /// <summary>Pushed when an ask is raised or answered: Needs you, the You node and the Monitor refresh.</summary>
    public const string ChangedEvent = "folder.asksChanged";

    private readonly string _cs;
    private readonly PolicyStore _policies;
    private readonly Func<long, CancellationToken, Task<RunLayout>> _layoutFor;
    private readonly Func<long, CancellationToken, Task<IReadOnlyList<string>>> _otherTeamFolders;
    private readonly NetworkAskTell _tell;
    private readonly Func<string, bool> _exists;
    private readonly Func<string, string>? _canonicalize;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly HashSet<long> _answering = [];

    /// <param name="layoutFor">The member's run layout (by member id), as its next run gets it.</param>
    /// <param name="otherTeamFolders">By team id: every OTHER team's repositories and documents folders.</param>
    /// <param name="exists">Whether a folder exists on this computer (production: Directory.Exists).</param>
    /// <param name="canonicalize">The policy store's canonicaliser (production: HostPathCanonicalizer.Canonicalize).</param>
    public FolderAskService(string connectionString, PolicyStore policies, Func<long, CancellationToken, Task<RunLayout>> layoutFor,
        Func<long, CancellationToken, Task<IReadOnlyList<string>>> otherTeamFolders, NetworkAskTell tell, Func<string, bool>? exists = null,
        Func<string, string>? canonicalize = null, TimeProvider? clock = null)
    {
        _cs = connectionString;
        _policies = policies;
        _layoutFor = layoutFor;
        _otherTeamFolders = otherTeamFolders;
        _tell = tell;
        _exists = exists ?? Directory.Exists;
        _canonicalize = canonicalize;
        _clock = clock ?? TimeProvider.System;
    }

    public Action? Changed { get; set; }

    public static bool Handles(string method) => method.StartsWith("folder.", StringComparison.Ordinal);

    // ------------------------------------------------------------------ request_folder

    /// <summary>
    /// The request_folder bus tool: the agent's answer, as text. <paramref name="forMember"/>: null or the caller's own name
    /// asks for the caller; a Manager may name a member of its own team ("Name" or "Team/Name").
    /// </summary>
    public async Task<string> RequestFromAgentAsync(MemberRef caller, string path, string access, string reason, string? forMember,
        long deliverable, long causation, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("give a reason: one line on what you need the folder for");
        var wanted = FolderAccess.Parse(access) ?? throw new ArgumentException($"access is read or readwrite, not '{access.Trim()}'");
        if (Target(caller, forMember) is not { } target)
            return Refused(forMember is null || caller.Role == MemberRole.Manager
                ? $"there is no member named '{forMember}' on your team {caller.Team}; a Manager may ask only for a member of its own team"
                : "only your Manager may ask for a folder for another member; ask for yourself (leave member out)");
        var layout = await _layoutFor(target.Id, ct).ConfigureAwait(false);
        var paths = new PolicyPaths(layout.Os, layout.Home, _canonicalize);
        var raw = path.Trim();
        if (!IsAbsolute(raw, layout.Os) || paths.Resolve(raw) is not { } resolved)
            return Refused($"'{raw}' is not an absolute path on this computer; give the full path, e.g. {(layout.Os == TargetOs.Windows ? @"Z:\data\sessions" : "/srv/data/sessions")}");
        var whom = target.Id == caller.Id ? "you" : target.Name;

        // Already granted (the member's own folders, its team's documents, or an earlier answer): nothing to ask.
        var effective = _policies.Effective(target.Id, layout).Policy;
        if (Granted(paths, effective, layout, resolved) is { } has && (has == FolderAccess.ReadWrite || wanted == FolderAccess.Read))
            return $"{resolved} is already {(has == FolderAccess.ReadWrite ? "read-write" : "readable")} for {whom} (from the next run that starts; a run that started before it was granted does not have it). " +
                   "Nothing is asked. If this run cannot reach it, hand back blocked on it so the work is retried in a new run.";

        var others = await _otherTeamFolders(target.TeamId, ct).ConfigureAwait(false);
        if (FolderAskRules.Problem(paths, resolved, wanted, layout, _policies.GetGlobal(), others) is { } problem) return Refused(problem);
        try
        {
            var current = _policies.GetMember(target.Id).Loosen;
            _policies.PreviewLoosen(target.Id, WithPath(current, paths, resolved, wanted), layout);
        }
        catch (PolicyException e)
        {
            return Refused(string.Join("; ", e.Errors));
        }

        var clean = Clip(reason.Trim(), 500);
        var exists = SafeExists(resolved);
        long id;
        bool first;
        lock (_lock)
        {
            using var c = Open();
            var open = Asks(c, openOnly: true, teamId: target.TeamId)
                .FirstOrDefault(a => a.MemberId == target.Id && a.Access == wanted && paths.Same(a.Path, resolved));
            using var tx = c.BeginTransaction();
            if (open is not null)
            {
                id = open.Id;
                first = false;
                Event(c, tx, id, "asked again", null, Provenance.Actor.Agent, $"{caller.Qualified}: {clean}");
            }
            else
            {
                using (var cmd = Command(c,
                           """
                           INSERT INTO folder_asks (at, team_id, member_id, member, asked_by, path, access, reason, path_exists, deliverable, causation_seq)
                           VALUES ($now, $t, $mid, $m, $by, $p, $a, $r, $e, $w, $c) RETURNING id;
                           """,
                           ("$now", Stamp(_clock.GetUtcNow())), ("$t", target.TeamId), ("$mid", target.Id), ("$m", target.Qualified), ("$by", caller.Qualified),
                           ("$p", resolved), ("$a", wanted), ("$r", clean), ("$e", exists ? 1 : 0), ("$w", deliverable), ("$c", causation)))
                {
                    cmd.Transaction = tx;
                    id = (long)cmd.ExecuteScalar()!;
                }
                first = true;
                Event(c, tx, id, "asked", null, Provenance.Actor.Agent, $"{caller.Qualified} for {target.Qualified}: {FolderAccess.Words(wanted)} {resolved}: {clean}");
            }
            tx.Commit();
        }
        if (first) Changed?.Invoke();
        return $"asked (ask {id}); the person decides; it applies from your next run. " +
               $"The person is asked in Needs you to allow {FolderAccess.Words(wanted)} access to {resolved} for {whom}{(first ? "" : " (it was already waiting)")}" +
               $"{(exists ? "" : "; that folder does not exist on this computer yet")}. " +
               "Hand back now as blocked on that folder (say what it is for); the Manager is told when it is allowed. " +
               "Never ask the person to edit the policy by hand, and do not retry in a loop or reach the folder another way.";
    }

    private static string Refused(string why) => $"refused: {why}. The person is not asked; do not try to reach it another way.";

    /// <summary>An absolute path in the target OS's form: a drive or UNC path on Windows, "/..." elsewhere; "~/..." is the person's home.</summary>
    private static bool IsAbsolute(string p, TargetOs os) =>
        p.StartsWith("~/", StringComparison.Ordinal) || p.StartsWith("~\\", StringComparison.Ordinal) || (os == TargetOs.Windows
            ? (p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] is '\\' or '/') || p.StartsWith(@"\\", StringComparison.Ordinal)
            : p.StartsWith('/'));

    /// <summary>What the member's effective policy grants on the path: readwrite, read, or null.</summary>
    private static string? Granted(PolicyPaths paths, EffectivePolicy e, RunLayout layout, string path)
    {
        if (e.Denied.Any(d => paths.IsWithin(path, d))) return null;
        if (e.Readwrite.Concat(layout.ImplicitReadwrite).Any(g => paths.IsWithin(path, g))) return FolderAccess.ReadWrite;
        if (e.Readonly.Concat(layout.ImplicitReadonly).Any(g => paths.IsWithin(path, g))) return FolderAccess.Read;
        return null;
    }

    /// <summary>The loosen block with the path added at that access (read-write also takes it off the read-only list, where it would win).</summary>
    private static PolicyPatch WithPath(PolicyPatch loosen, PolicyPaths paths, string path, string access)
    {
        var fs = loosen.Filesystem ?? new FilesystemPatch();
        var ro = (fs.Readonly ?? []).ToList();
        var rw = (fs.Readwrite ?? []).ToList();
        if (access == FolderAccess.ReadWrite)
        {
            ro.RemoveAll(p => paths.Resolve(p) is { } r && paths.Same(r, path));
            if (!rw.Any(p => paths.Resolve(p) is { } r && paths.Same(r, path))) rw.Add(path);
        }
        else if (!ro.Any(p => paths.Resolve(p) is { } r && paths.Same(r, path)))
        {
            ro.Add(path);
        }
        return loosen with { Filesystem = new FilesystemPatch(ro.Count == 0 ? null : ro, rw.Count == 0 ? null : rw, null) };
    }

    private MemberRef? Target(MemberRef caller, string? forMember)
    {
        var name = forMember?.Trim();
        if (string.IsNullOrEmpty(name)) return caller;
        var slash = name.IndexOf('/');
        if (slash >= 0)
        {
            if (!string.Equals(name[..slash].Trim(), caller.Team, StringComparison.OrdinalIgnoreCase)) return null;
            name = name[(slash + 1)..].Trim();
        }
        if (string.Equals(name, caller.Name, StringComparison.OrdinalIgnoreCase)) return caller;
        if (caller.Role != MemberRole.Manager) return null;
        using var c = Open();
        using var cmd = Command(c, "SELECT m.id, m.name, m.role FROM members m WHERE m.team_id = $t AND m.name = $n COLLATE NOCASE;", ("$t", caller.TeamId), ("$n", name));
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new MemberRef(r.GetInt64(0), caller.TeamId, caller.Team, r.GetString(1), r.GetString(2) == "manager" ? MemberRole.Manager : MemberRole.Member)
            : null;
    }

    private bool SafeExists(string path)
    {
        try { return _exists(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    // ------------------------------------------------------------------ reading

    public int PendingCount() => (int)(long)(Scalar("SELECT count(*) FROM folder_asks WHERE state = 'pending';") ?? 0L);

    public IReadOnlyList<FolderAskRow> Asks(bool openOnly = true, long? teamId = null)
    {
        using var c = Open();
        return Asks(c, openOnly, teamId);
    }

    private static List<FolderAskRow> Asks(SqliteConnection c, bool openOnly, long? teamId)
    {
        using var cmd = Command(c,
            "SELECT a.id, a.at, a.team_id, COALESCE(t.name, ''), a.member_id, a.member, a.asked_by, a.path, a.access, a.reason, a.path_exists, a.state, " +
            "a.granted_access, a.decided_by, a.decided_via, a.decided_at, a.note, a.deliverable, a.causation_seq FROM folder_asks a LEFT JOIN teams t ON t.id = a.team_id WHERE 1 = 1" +
            (openOnly ? " AND a.state = 'pending'" : "") + (teamId is null ? "" : " AND a.team_id = $t") + " ORDER BY a.id;",
            ("$t", teamId));
        using var r = cmd.ExecuteReader();
        var list = new List<FolderAskRow>();
        while (r.Read())
            list.Add(new FolderAskRow(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetInt64(4), r.GetString(5), r.GetString(6), r.GetString(7),
                r.GetString(8), r.GetString(9), r.GetInt64(10) != 0, r.GetString(11), S(r, 12), S(r, 13), S(r, 14), S(r, 15), S(r, 16),
                r.IsDBNull(17) ? null : r.GetInt64(17), r.IsDBNull(18) ? null : r.GetInt64(18)));
        return list;
    }

    /// <summary>One ask as Needs you reads it (folder.asks); docs/folder-asks.md is the contract.</summary>
    public JsonObject ToJson(FolderAskRow a) => new()
    {
        ["id"] = a.Id, ["at"] = a.At, ["teamId"] = a.TeamId, ["team"] = a.Team, ["memberId"] = a.MemberId, ["member"] = a.Member, ["memberName"] = a.MemberName,
        ["askedBy"] = a.AskedBy, ["path"] = a.Path, ["access"] = a.Access, ["reason"] = a.Reason,
        // Whether the folder exists now (checked as the list is read); existedWhenAsked: when the agent asked.
        ["exists"] = a.State == "pending" ? SafeExists(a.Path) : a.PathExisted, ["existedWhenAsked"] = a.PathExisted,
        ["state"] = a.State, ["grantedAccess"] = a.GrantedAccess, ["decidedBy"] = a.DecidedBy, ["decidedVia"] = a.DecidedVia, ["decidedAt"] = a.DecidedAt,
        ["note"] = a.Note, ["deliverable"] = a.Deliverable,
        ["choices"] = new JsonArray((a.State == "pending" ? FolderAccess.ChoicesFor(a.Access) : []).Select(ch => (JsonNode?)ch).ToArray()),
    };

    /// <summary>The waiting asks as plain lines (the Concierge's and the companion's status reads).</summary>
    public IReadOnlyList<string> PendingLines() => Asks().Select(a =>
        $"- {a.Member} asks for {FolderAccess.Words(a.Access)} access to {a.Path}" + (a.AskedBy == a.Member ? "" : $" (asked by {a.AskedByName})") +
        $": \"{Clip(a.Reason, 200)}\" since {a.At} (folder request {a.Id})").ToList();

    // ------------------------------------------------------------------ the person's answer

    public async Task<JsonNode?> DispatchAsync(string method, JsonObject? p, CancellationToken ct)
    {
        try
        {
            return method switch
            {
                // [FolderAsk]: the waiting asks (all: true for answered ones too; teamId: one team's)
                "folder.asks" => new JsonArray(Asks(p?["all"] is JsonValue all && all.TryGetValue<bool>(out var b) && b ? false : true, OptLong(p, "teamId"))
                    .Select(a => (JsonNode?)ToJson(a)).ToArray()),
                // { id, choice: readwrite | read | deny, note? }
                "folder.answer" => await AnswerAsync(OptLong(p, "id") ?? throw new ArgumentException("id is required"),
                    OptStr(p, "choice") ?? throw new ArgumentException("choice is required (readwrite, read or deny)"), OptStr(p, "note"), ct),
                _ => throw new RpcException("unknown_method", $"no method named '{method}'"),
            };
        }
        catch (ArgumentException e)
        {
            throw new RpcException("bad_params", e.Message);
        }
        catch (PolicyException e)
        {
            throw new RpcException("policy_refused", string.Join("; ", e.Errors));
        }
    }

    /// <summary>
    /// The person's answer: readwrite (only for a read-write ask), read, or deny. An approval writes the member's loosen
    /// block first (a policy refusal leaves the ask waiting), checks the member's effective policy now has the path with
    /// that access, then closes the ask and tells the team's Manager to retry; a deny closes it and tells the asker.
    /// </summary>
    public async Task<JsonObject> AnswerAsync(long id, string choice, string? note, CancellationToken ct)
    {
        var who = Provenance.Actor.Current ?? Provenance.Actor.Window;
        // Never without the person's click: an agent, the Concierge, a trigger or a paired program cannot answer.
        if (who.Via is not (Provenance.Actor.App or Provenance.Actor.Cloud))
            throw new ArgumentException("a folder request is answered by the person, in Needs you");
        var c0 = (choice ?? "").Trim().ToLowerInvariant();
        if (c0 is not (FolderAccess.ReadWrite or FolderAccess.Read or FolderAccess.Deny)) throw new ArgumentException($"'{choice}' is not a choice (readwrite, read or deny)");
        lock (_lock)
            if (!_answering.Add(id)) throw new ArgumentException($"folder request {id} is being answered already");
        try
        {
            return await AnswerClaimedAsync(id, c0, note, who, ct);
        }
        finally
        {
            lock (_lock) _answering.Remove(id);
        }
    }

    private async Task<JsonObject> AnswerClaimedAsync(long id, string choice, string? note, Provenance.Actor who, CancellationToken ct)
    {
        var ask = Asks().FirstOrDefault(a => a.Id == id) ?? throw new ArgumentException($"folder request {id} is not waiting");
        if (!FolderAccess.ChoicesFor(ask.Access).Contains(choice))
            throw new ArgumentException($"folder request {id} asks for read-only access; it cannot be granted read-write");
        var cleanNote = string.IsNullOrWhiteSpace(note) ? null : Clip(note.Trim(), 500);
        var approve = choice != FolderAccess.Deny;
        if (approve)
        {
            if (!MemberExists(ask.MemberId, ask.TeamId)) throw new ArgumentException($"{ask.Member} is no longer on the team");
            await GrantAsync(ask, choice, ct);
        }

        lock (_lock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            // exactly one answer wins: a second one (the window and a cloud viewer at once) finds nothing waiting
            if (Exec(c, tx, "UPDATE folder_asks SET state = $s, granted_access = $g, decided_by = $by, decided_via = $via, decided_at = $now, note = $note WHERE id = $id AND state = 'pending';",
                    ("$s", approve ? "approved" : "denied"), ("$g", approve ? choice : null), ("$by", who.User), ("$via", who.Via),
                    ("$now", Stamp(_clock.GetUtcNow())), ("$note", cleanNote), ("$id", id)) == 0)
                throw new ArgumentException($"folder request {id} is not waiting");
            Event(c, tx, id, approve ? "approved" : "denied", who.User, who.Via,
                (approve ? $"{FolderAccess.Words(choice)} {ask.Path} for {ask.Member}" : $"{FolderAccess.Words(ask.Access)} {ask.Path} for {ask.Member}") +
                (cleanNote is null ? "" : $": {cleanNote}"));
            tx.Commit();
        }
        Changed?.Invoke();

        string? told = null;
        if (approve)
        {
            if (ManagerOf(ask.TeamId) is { } manager)
            {
                var narrowed = choice != ask.Access ? " (asked for read-write; the person granted read-only)" : "";
                var text = $"{ask.MemberName} can now {(choice == FolderAccess.ReadWrite ? "read and write" : "read")} {ask.Path}{narrowed} (folder request {id}); " +
                           "retry what was blocked. It applies to runs that start from now on.";
                if (await TellAsync(ask.Team, manager, text, ask.CausationSeq, ct)) told = $"{ask.Team}/{manager}";
            }
        }
        else
        {
            var text = $"The person denied {FolderAccess.Words(ask.Access)} access to {ask.Path} for {(ask.AskedBy == ask.Member ? "you" : ask.MemberName)} (folder request {id})" +
                       $"{(cleanNote is null ? "" : $": {cleanNote}")}. Do not reach it another way or ask the person to edit the policy: carry on without it, or hand back blocked on it.";
            if (await TellAsync(ask.Team, ask.AskedByName, text, ask.CausationSeq, ct)) told = ask.AskedBy;
        }
        return new JsonObject
        {
            ["ok"] = true, ["id"] = id, ["choice"] = choice, ["path"] = ask.Path, ["member"] = ask.Member,
            ["grantedAccess"] = approve ? choice : null, ["told"] = told,
        };
    }

    /// <summary>
    /// Writes the path into the member's loosen block, as policy.saveLoosen does: PreviewLoosen before and after, the
    /// person's click confirming exactly the one added line, then SaveLoosen (its checks and the sandbox dry run). Refused
    /// when the rules now refuse the path, or when the change would loosen anything else.
    /// </summary>
    private async Task GrantAsync(FolderAskRow ask, string access, CancellationToken ct)
    {
        var layout = await _layoutFor(ask.MemberId, ct).ConfigureAwait(false);
        var paths = new PolicyPaths(layout.Os, layout.Home, _canonicalize);
        var others = await _otherTeamFolders(ask.TeamId, ct).ConfigureAwait(false);
        if (FolderAskRules.Problem(paths, ask.Path, access, layout, _policies.GetGlobal(), others) is { } problem) throw new PolicyException([problem]);
        var current = _policies.GetMember(ask.MemberId).Loosen;
        var loosen = WithPath(current, paths, ask.Path, access);
        var before = _policies.PreviewLoosen(ask.MemberId, current, layout);
        var after = _policies.PreviewLoosen(ask.MemberId, loosen, layout);
        var added = after.Except(before, StringComparer.Ordinal).ToList();
        var expected = $"filesystem.{(access == FolderAccess.ReadWrite ? "readwrite" : "readonly")} + ";
        if (added.Any(a => !(a.StartsWith(expected, StringComparison.Ordinal) && paths.Same(a[expected.Length..], ask.Path))))
            throw new PolicyException([$"granting {ask.Path} would loosen more than that folder: {string.Join(", ", added)}"]);
        _policies.SaveLoosen(ask.MemberId, loosen, layout, after);
        // Never tell the Manager a folder is granted unless the member's next run has it.
        var granted = Granted(paths, _policies.Effective(ask.MemberId, layout).Policy, layout, ask.Path);
        if (granted is null || (access == FolderAccess.ReadWrite && granted != FolderAccess.ReadWrite))
            throw new PolicyException([$"{ask.Path} is still not {FolderAccess.Words(access)} for {ask.Member} after the change; the request stays waiting"]);
    }

    /// <summary>
    /// Closes the member's waiting asks when it is removed from the team (within the removal's transaction), as network
    /// asks do. Returns how many closed.
    /// </summary>
    public static int CloseForRemovedMember(SqliteConnection c, SqliteTransaction? tx, long memberId, string now)
    {
        using var cmd = Command(c,
            "UPDATE folder_asks SET state = 'closed', decided_by = $by, decided_via = $via, decided_at = $now, note = 'closed: the member was removed from the team' " +
            "WHERE state = 'pending' AND member_id = $id;",
            ("$by", Provenance.Actor.Current?.User ?? Provenance.Actor.LocalUser), ("$via", Provenance.Actor.Current?.Via ?? Provenance.Actor.Host), ("$now", now), ("$id", memberId));
        cmd.Transaction = tx;
        return cmd.ExecuteNonQuery();
    }

    private async Task<bool> TellAsync(string team, string member, string text, long? causation, CancellationToken ct)
    {
        try
        {
            await _tell(team, member, text, causation, ct);
            return true;
        }
        catch (BusRefusal) when (causation is not null)
        {
            try
            {
                await _tell(team, member, text, null, ct);
                return true;
            }
            catch (BusRefusal) { return false; }
        }
        catch (BusRefusal) { return false; }
    }

    // ------------------------------------------------------------------ the store

    /// <summary>What happened to one ask, oldest first: (kind, at, by, via, detail).</summary>
    public IReadOnlyList<(string Kind, string At, string? By, string? Via, string? Detail)> Events(long askId)
    {
        using var c = Open();
        using var cmd = Command(c, "SELECT kind, at, by_user, via, detail FROM folder_ask_events WHERE ask_id = $a ORDER BY id;", ("$a", askId));
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string, string?, string?, string?)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), S(r, 2), S(r, 3), S(r, 4)));
        return list;
    }

    private void Event(SqliteConnection c, SqliteTransaction tx, long askId, string kind, string? by, string? via, string? detail) =>
        Exec(c, tx, "INSERT INTO folder_ask_events (ask_id, at, kind, by_user, via, detail) VALUES ($a, $at, $k, $by, $via, $d);",
            ("$a", askId), ("$at", Stamp(_clock.GetUtcNow())), ("$k", kind), ("$by", by), ("$via", via), ("$d", detail is null ? null : Clip(detail, 1000)));

    private bool MemberExists(long memberId, long teamId) =>
        Scalar("SELECT count(*) FROM members WHERE id = $id AND team_id = $t;", ("$id", memberId), ("$t", teamId)) is > 0L;

    private string? ManagerOf(long teamId) =>
        Scalar("SELECT name FROM members WHERE team_id = $t AND role = 'manager' LIMIT 1;", ("$t", teamId)) as string;

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    private static SqliteCommand Command(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        using var c = Open();
        using var cmd = Command(c, sql, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    private static int Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(c, sql, args);
        cmd.Transaction = tx;
        return cmd.ExecuteNonQuery();
    }

    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static string Stamp(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static long? OptLong(JsonObject? p, string name) =>
        p?[name] is JsonValue v && (v.TryGetValue<long>(out var l) || (v.TryGetValue<string>(out var s) && long.TryParse(s, CultureInfo.InvariantCulture, out l))) ? l : null;

    private static string? OptStr(JsonObject? p, string name) =>
        p?[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
