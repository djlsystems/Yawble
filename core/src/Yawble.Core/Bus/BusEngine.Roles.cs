using System.Text.Json;
using System.Text.RegularExpressions;

namespace Yawble.Core.Bus;

/// <summary>A row of a team's hire list as Team settings shows it: the role's job, and the members hired as it (by id).</summary>
public sealed record HireListEntry(string Role, string Job, string Agent, string? Model, int Used, IReadOnlyList<long> Holders);

/// <summary>A row of the hiring tool: a role the Manager may hire, its description, agent and model, and whether the agent can run now.</summary>
public sealed record HiringRow(string Role, string Description, string Agent, string? Model, bool Ready, string Detail);

/// <summary>What the hiring tool offers: the team's hire list with each agent's readiness, and every agent's readiness.</summary>
public sealed record HiringOffer(IReadOnlyList<HiringRow> HireList, IReadOnlyList<AgentChoice> Agents);

/// <summary>
/// The role catalog (Settings > Teams > Roles) and each team's hire list (0059): which roles its Manager may hire, each
/// with the agent and model a hire of it runs with. An empty hire list keeps hiring open: any agent, free job text.
/// </summary>
public sealed partial class BusEngine
{
    public const string HireListKey = "teams.hireList";

    private static readonly Regex RoleWord = new("^[A-Za-z][A-Za-z0-9]{0,23}$", RegexOptions.CultureInvariant);

    public Task<IReadOnlyList<RoleRow>> RolesAsync(CancellationToken ct = default) => WithGate(() => _store.Roles(), ct);

    /// <summary>
    /// Adds a role, or edits the one named <paramref name="originalName"/> (a rename follows into every hire list and onto
    /// the members hired as it). The name is one word; the job is required; the agent must be one.
    /// </summary>
    public Task<RoleRow> SaveRoleAsync(RoleRow role, string? originalName, CancellationToken ct = default) =>
        WithGate(() => _store.InTransaction(() =>
        {
            var name = CheckRoleName(role.Name);
            var job = (role.Job ?? "").Trim();
            if (job.Length == 0) throw new BusRefusal("bad_role", $"the role {name} needs a job description");
            var saved = new RoleRow(name, job, CheckCli((role.Agent ?? "").Trim()), string.IsNullOrWhiteSpace(role.Model) ? null : role.Model.Trim());
            var original = string.IsNullOrWhiteSpace(originalName) ? null : originalName.Trim();
            if (original is not null && _store.Role(original) is null) throw new BusRefusal("no_such_role", $"there is no role {original}");
            if ((original is null || !string.Equals(original, name, StringComparison.OrdinalIgnoreCase)) && _store.Role(name) is not null)
                throw new BusRefusal("exists", $"there is already a role {name}");
            _store.SaveRole(saved, original is null ? null : _store.Role(original)!.Name);
            RenameInDefaultHireList(original, name);
            return saved;
        }), ct);

    /// <summary>Removes a role; refused while a team's hire list (or the default hire list) uses it, naming them.</summary>
    public Task RemoveRoleAsync(string name, CancellationToken ct = default) =>
        WithGate(() => _store.InTransaction(() =>
        {
            var role = _store.Role((name ?? "").Trim()) ?? throw new BusRefusal("no_such_role", $"there is no role {name}");
            var teams = _store.TeamsUsingRole(role.Name);
            if (teams.Count > 0)
                throw new BusRefusal("in_use", $"Role {role.Name} is used by the hire list of: {string.Join(", ", teams)}");
            if (DefaultHireList().Any(r => string.Equals(r.Role, role.Name, StringComparison.OrdinalIgnoreCase)))
                throw new BusRefusal("in_use", $"Role {role.Name} is used by the default hire list in Settings > Teams");
            _store.DeleteRole(role.Name);
            return true;
        }), ct);

    private static string CheckRoleName(string? name)
    {
        var n = (name ?? "").Trim();
        if (!RoleWord.IsMatch(n)) throw new BusRefusal("bad_name", $"a role's name is one word of letters and digits, starting with a letter: '{n}' is not");
        try { Teams.MemberNames.IdFor(n); }
        catch (ArgumentException e) { throw new BusRefusal("bad_name", e.Message); }
        if (!string.Equals(Teams.MemberNames.IdFor(n), n, StringComparison.Ordinal))
            throw new BusRefusal("bad_name", $"'{n}' cannot be a member's name word on this computer");
        return n;
    }

    private void RenameInDefaultHireList(string? original, string name)
    {
        if (original is null || string.Equals(original, name, StringComparison.Ordinal)) return;
        var rows = DefaultHireList();
        if (!rows.Any(r => string.Equals(r.Role, original, StringComparison.OrdinalIgnoreCase))) return;
        _store.SetAppDefault(HireListKey, JsonSerializer.Serialize(
            rows.Select(r => string.Equals(r.Role, original, StringComparison.OrdinalIgnoreCase) ? r with { Role = name } : r).ToList(), DefaultsJson));
    }

    // ---------------------------------------------------------------- the hire list

    /// <summary>The team's hire list, each row with its role's job and the members hired as it.</summary>
    public Task<IReadOnlyList<HireListEntry>> HireListAsync(long teamId, CancellationToken ct = default) =>
        WithGate(() =>
        {
            if (_store.Teams().All(t => t.Id != teamId)) throw new BusRefusal("no_such_team", $"there is no team {teamId}");
            var members = _store.MemberRows(teamId);
            return (IReadOnlyList<HireListEntry>)_store.HireList(teamId).Select(r =>
            {
                var holders = members.Where(m => string.Equals(m.HireRole, r.Role, StringComparison.OrdinalIgnoreCase)).Select(m => m.Id).ToList();
                return new HireListEntry(r.Role, _store.Role(r.Role)?.Job ?? "", r.Agent, r.Model, holders.Count, holders);
            }).ToList();
        }, ct);

    /// <summary>Replaces the team's hire list (Team settings > Hiring); an empty list opens hiring to any agent again.</summary>
    public async Task<IReadOnlyList<HireListEntry>> SetHireListAsync(long teamId, IReadOnlyList<HireListRow> rows, CancellationToken ct = default)
    {
        await WithGate(() => _store.InTransaction(() =>
        {
            if (_store.Teams().All(t => t.Id != teamId)) throw new BusRefusal("no_such_team", $"there is no team {teamId}");
            _store.SetHireList(teamId, CheckHireList(rows));
            return true;
        }), ct);
        return await HireListAsync(teamId, ct);
    }

    /// <summary>
    /// A hire list as saved: each role a catalog role (its catalog spelling), once; each agent an agent; a blank model the
    /// agent's default (null). Under the gate.
    /// </summary>
    private List<HireListRow> CheckHireList(IReadOnlyList<HireListRow> rows)
    {
        var roles = _store.Roles();
        var saved = new List<HireListRow>();
        foreach (var r in rows)
        {
            var role = roles.FirstOrDefault(x => string.Equals(x.Name, (r.Role ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new BusRefusal("no_such_role", $"'{r.Role}' is not a role ({string.Join(", ", roles.Select(x => x.Name))})");
            if (saved.Any(s => string.Equals(s.Role, role.Name, StringComparison.OrdinalIgnoreCase)))
                throw new BusRefusal("exists", $"the role {role.Name} is on the hire list twice");
            saved.Add(new HireListRow(role.Name, CheckCli((r.Agent ?? "").Trim()), string.IsNullOrWhiteSpace(r.Model) ? null : r.Model.Trim()));
        }
        return saved;
    }

    /// <summary>The default hire list new teams start from (Settings > Teams; a malformed value reads as empty).</summary>
    private IReadOnlyList<HireListRow> DefaultHireList() =>
        Read<List<HireListRow>>(HireListKey)?.Where(r => r is { Role.Length: > 0, Agent.Length: > 0 }).ToList() ?? [];

    // ---------------------------------------------------------------- hiring

    /// <summary>What the Manager may hire: the team's hire list with each row's agent readiness, and every agent's readiness.</summary>
    internal async Task<HiringOffer> HiringOfferAsync(RunContext run, CancellationToken ct)
    {
        var agents = await HiringAsync(ct);
        var rows = await WithGate(() => _store.HireList(run.Member.TeamId).Select(r => (Row: r, Job: _store.Role(r.Role)?.Job ?? "")).ToList(), ct);
        return new HiringOffer(rows.Select(x =>
        {
            var ready = agents.FirstOrDefault(a => string.Equals(a.Cli, x.Row.Agent, StringComparison.OrdinalIgnoreCase));
            return new HiringRow(x.Row.Role, x.Job, x.Row.Agent, x.Row.Model, ready?.Ready ?? false, ready?.Detail ?? "not an agent on this computer");
        }).ToList(), agents);
    }

    /// <summary>
    /// What a hire takes from the team's hire list (under the gate): with a list, the role is required and must be on it,
    /// and the agent, model and job are the row's (a <paramref name="cli"/> or <paramref name="job"/> that differs is
    /// refused). Without a list a role is optional (a catalog role; its job when none is given) and the rest is as given.
    /// The name defaults to the role word, and a name given must start with it.
    /// </summary>
    private (string Name, string? Cli, string? Job, string Model, string? Role) HireTerms(long teamId, string? name, string? cli, string? job, string? role)
    {
        var list = _store.HireList(teamId);
        var asked = string.IsNullOrWhiteSpace(role) ? null : role.Trim();
        HireListRow? row = null;
        RoleRow? catalog = null;
        if (list.Count > 0)
        {
            var allowed = string.Join(", ", list.Select(r => r.Role));
            if (asked is null)
                throw new BusRefusal("role_required", $"this team's hire list names the roles you may hire: {allowed}; call hire with role (see hiring)");
            row = list.FirstOrDefault(r => string.Equals(r.Role, asked, StringComparison.OrdinalIgnoreCase))
                ?? throw new BusRefusal("not_on_list", $"{asked} is not on this team's hire list; the roles you may hire: {allowed}");
            catalog = _store.Role(row.Role);
        }
        else if (asked is not null)
        {
            catalog = _store.Role(asked)
                ?? throw new BusRefusal("no_such_role", $"{asked} is not a role; the roles: {string.Join(", ", _store.Roles().Select(r => r.Name))}");
        }
        var word = row?.Role ?? catalog?.Name;
        var label = string.Join(' ', (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (word is null)
        {
            if (label.Length == 0) throw new ArgumentException("'name' is required");
            return (label, cli, job, "", null);
        }
        if (label.Length == 0) label = word;
        else if (!string.Equals(label.Split(' ')[0], word, StringComparison.OrdinalIgnoreCase))
            throw new BusRefusal("bad_name", $"a {word} is named '{word}' (the app adds the first name), or '{word}' and a first name; '{label}' is not");
        else label = word + label[label.Split(' ')[0].Length..];
        if (row is null)
            return (label, cli, string.IsNullOrWhiteSpace(job) ? catalog!.Job : job, "", catalog!.Name);

        var roleJob = catalog?.Job ?? "";
        if (!string.IsNullOrWhiteSpace(cli) && !string.Equals(CheckCli(cli.Trim()), row.Agent, StringComparison.OrdinalIgnoreCase))
            throw new BusRefusal("bad_cli", $"a {row.Role} on this team runs on {row.Agent} (the team's hire list); leave cli out");
        if (!string.IsNullOrWhiteSpace(job) && !string.Equals(job.Trim(), roleJob, StringComparison.OrdinalIgnoreCase))
            throw new BusRefusal("bad_job", $"a {row.Role}'s job is the role's own: \"{roleJob}\"; leave job out and put the work in the card you tell it");
        return (label, row.Agent, roleJob, row.Model ?? "", row.Role);
    }
}
