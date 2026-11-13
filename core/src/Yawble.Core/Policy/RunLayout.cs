namespace Yawble.Core.Policy;

public enum AgentCli
{
    Claude,
    Grok,
    Codex,
    Copilot,
    Antigravity,
}

/// <summary>A member's place in the app's folder layout, used for implicit grants and denials.</summary>
public sealed record MemberRef(string Team, string Member);

/// <summary>
/// The folders one member run touches, in the target OS's path form.
///
/// App data root layout (<c>&lt;userData&gt;</c>, the folder Electron gives the app):
/// <code>
///   data/                       the core's state: yawble.db (+ -wal, -shm), credentials/ (the fallback files)
///   grok-home/                  the person's host Grok home; never granted
///   teams/&lt;team&gt;/members/&lt;member&gt;/
///       repo/                   the member's clone (cwd, read/write)
///       home/                   the member's workspace = HOME (read/write)
///       tmp/                    the member's temp folder (read/write)
/// </code>
/// The app data root itself cannot be an emitted denial, because the member's own folders live under it and
/// a deny on an ancestor would override them (Seatbelt emits denials last and they win). It is protected by
/// deny-by-default (nothing under it is granted except the member's own three folders) plus explicit
/// denials of every sensitive child: data/, the database, credentials/, grok-home/ and every other member's
/// and team's folder. The compiler's invariants refuse any grant that equals, contains or lies inside one of
/// those, and any grant that contains the app data root.
/// </summary>
public sealed record RunLayout
{
    public required TargetOs Os { get; init; }
    public required AgentCli Cli { get; init; }
    public required string Home { get; init; }
    public required string AppDataRoot { get; init; }
    public required string DataDir { get; init; }
    public required string DatabasePath { get; init; }
    public required string CredentialDir { get; init; }
    public required string HostGrokHome { get; init; }
    public required string MemberRoot { get; init; }
    public required string ClonePath { get; init; }
    public required string Workspace { get; init; }
    public required string Tmp { get; init; }

    /// <summary>The member's CLI as installed on this host (detected at launch): its program, read-only install
    /// folders and denied config folders. No pinned version, no app toolchain folder.</summary>
    public required CliInstall CliInstall { get; init; }

    /// <summary>The program the sandbox runs (the detected executable, or node for a JavaScript entry point).</summary>
    public string CliPath => CliInstall.Command[0];

    /// <summary>The full command prefix: <see cref="CliPath"/>, then the script for a JavaScript entry point.</summary>
    public IReadOnlyList<string> CliCommand => CliInstall.Command;

    /// <summary>The app's bundled helpers: yawble-bridge and yawble-grok-auth (read-only).</summary>
    public required string AppBinDir { get; init; }

    /// <summary>The built-in role texts (read-only).</summary>
    public required string RoleTextDir { get; init; }

    /// <summary>Every other member's root, in any team, and every other team's root.</summary>
    public required IReadOnlyList<string> OtherMemberRoots { get; init; }
    public required IReadOnlyList<string> OtherTeamRoots { get; init; }

    /// <summary>Always read/write: the member's clone, workspace and temp folder, and its team's documents when set.</summary>
    public IReadOnlyList<string> ImplicitReadwrite =>
        [ClonePath, Workspace, Tmp, .. (TeamDocuments is null ? Array.Empty<string>() : [TeamDocuments]), .. ExtraClonePaths];

    /// <summary>
    /// The names of the team's writable extra repositories this run has a clone of (0022, <see cref="Teams.TeamRepos"/>):
    /// each clone is &lt;member root&gt;/repos/&lt;name&gt; (read/write), and the team's shared view of it
    /// &lt;team root&gt;/shared/&lt;name&gt;.git is read-only. Set by the launcher.
    /// </summary>
    public IReadOnlyList<string> ExtraRepoNames { get; init; } = [];

    /// <summary>The names of the team's reference repositories: &lt;team root&gt;/reference/&lt;name&gt;, read-only. Set by the launcher.</summary>
    public IReadOnlyList<string> ReferenceRepoNames { get; init; } = [];

    public IReadOnlyList<string> ExtraClonePaths => ExtraRepoNames.Select(n => new PolicyPaths(Os, Home).Combine(MemberRoot, Teams.TeamRepos.ExtraClonesFolder, n)).ToList();
    public IReadOnlyList<string> ExtraSharedPaths => ExtraRepoNames.Select(n => new PolicyPaths(Os, Home).Combine(TeamRoot, Teams.TeamRepos.ExtraSharedFolder, n + ".git")).ToList();
    public IReadOnlyList<string> ReferencePaths => ReferenceRepoNames.Select(n => new PolicyPaths(Os, Home).Combine(TeamRoot, Teams.TeamRepos.ReferenceFolder, n)).ToList();

    /// <summary>
    /// The team's documents (&lt;team root&gt;/documents, <see cref="Teams.TeamDocuments"/>): what the team writes for the
    /// person and keeps between runs. Read/write for every member of the team; another team's is denied with its team
    /// root. Set by the launcher.
    /// </summary>
    public string? TeamDocuments { get; init; }

    /// <summary>Where this member's team documents are, granted or not.</summary>
    public string TeamDocumentsCandidate => new PolicyPaths(Os, Home).Combine(TeamRoot, Teams.TeamDocuments.Folder);

    /// <summary>Always read-only: exactly the CLI's install folders, the app's own helpers and the role text, and the
    /// host's sign-in files for this CLI whose access is read-only (exact files, never their folder).</summary>
    public IReadOnlyList<string> ImplicitReadonly =>
        [.. CliInstall.ReadOnlyFolders, .. HostToolFolders, AppBinDir, RoleTextDir, .. CliInstall.SignInReadOnly, .. (SharedRepo is null ? [] : new[] { SharedRepo }), .. ExtraSharedPaths, .. ReferencePaths];

    /// <summary>The member's team folder (&lt;root&gt;/teams/&lt;team&gt;); never granted itself.</summary>
    public string TeamRoot { get; init; } = "";

    /// <summary>
    /// The team's shared view (&lt;team root&gt;/shared.git, <see cref="Teams.TeamRepos.SharedRepoPath"/>), granted
    /// read-only so the member can fetch its teammates' branches. Set by the launcher when the folder exists.
    /// </summary>
    public string? SharedRepo { get; init; }

    /// <summary>Where the shared view of this member's team is, granted or not.</summary>
    public string SharedRepoCandidate => new PolicyPaths(Os, Home).Combine(TeamRoot, Teams.TeamRepos.SharedRepoFolder);

    /// <summary>
    /// Host tools every agent may run, read-only and on PATH after the CLI's own folders (Windows: the folder of the
    /// host's git.exe; on Linux and macOS git is in the system folders already). Set by the launcher; empty otherwise.
    /// </summary>
    public IReadOnlyList<string> HostToolFolders { get; init; } = [];

    /// <summary>The host's sign-in files whose access is read-write (none while the catalog says read-only).</summary>
    public IReadOnlyList<string> SignInReadwrite => CliInstall.SignInReadWrite;

    /// <summary>Always denied, emitted into every request (implicit, not editable).</summary>
    public IReadOnlyList<string> ImplicitDenied
    {
        get
        {
            var list = new List<string> { DataDir, DatabasePath, CredentialDir, HostGrokHome };
            // The CLI's own config and credential folders on the host (~/.claude, ~/.claude.json, ~/.grok, ...).
            list.AddRange(CliInstall.ConfigDenied);
            list.AddRange(OtherTeamRoots);
            list.AddRange(OtherMemberRoots);
            if (Os == TargetOs.MacOS)
            {
                // Seatbelt allows file-read-metadata everywhere; explicit denials stop it.
                var sep = "/";
                list.Add(Home.TrimEnd('/') + sep + ".ssh");
                list.Add(Home.TrimEnd('/') + sep + "Library/Keychains");
            }
            return list;
        }
    }

    /// <summary>
    /// The standard layout. <paramref name="installDir"/> is the app's install folder (bin/ and roles/);
    /// <paramref name="cliInstall"/> is the member's CLI where the host has it installed.
    /// </summary>
    public static RunLayout Standard(
        TargetOs os, string home, string appDataRoot, string installDir,
        MemberRef member, AgentCli cli, CliInstall cliInstall, IEnumerable<MemberRef> allMembers)
    {
        var p = new PolicyPaths(os, home);
        var everyone = allMembers.ToList();
        foreach (var segment in everyone.Append(member).SelectMany(m => new[] { m.Team, m.Member }))
            if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".." || segment.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
                throw new ArgumentException($"'{segment}' cannot be a folder name in the app's layout");
        string N(string path) => p.Normalize(path) ?? throw new ArgumentException($"not an absolute {os} path: {path}");
        var root = N(appDataRoot);
        var install = N(installDir);
        var teams = p.Combine(root, "teams");
        var data = p.Combine(root, "data");
        var memberRoot = p.Combine(teams, member.Team, "members", member.Member);
        return new RunLayout
        {
            Os = os,
            Cli = cli,
            Home = p.Home,
            AppDataRoot = root,
            DataDir = data,
            DatabasePath = p.Combine(data, "yawble.db"),
            CredentialDir = p.Combine(data, "credentials"),
            HostGrokHome = p.Combine(root, "grok-home"),
            MemberRoot = memberRoot,
            TeamRoot = p.Combine(teams, member.Team),
            ClonePath = p.Combine(memberRoot, "repo"),
            Workspace = p.Combine(memberRoot, "home"),
            Tmp = p.Combine(memberRoot, "tmp"),
            CliInstall = cliInstall,
            AppBinDir = p.Combine(install, "bin"),
            RoleTextDir = p.Combine(install, "roles"),
            OtherMemberRoots = everyone
                .Where(m => string.Equals(m.Team, member.Team, p.Comparison) && !string.Equals(m.Member, member.Member, p.Comparison))
                .Select(m => p.Combine(teams, m.Team, "members", m.Member))
                .Distinct(p.Comparer).ToList(),
            OtherTeamRoots = everyone
                .Where(m => !string.Equals(m.Team, member.Team, p.Comparison))
                .Select(m => p.Combine(teams, m.Team))
                .Distinct(p.Comparer).ToList(),
        };
    }
}
