using System.Text.Json;
using Yawble.Core.Teams;

namespace Yawble.Core.Bus;

/// <summary>The Manager a new team starts with (Settings > Teams).</summary>
public sealed record ManagerDefault(string Name, string Cli);

/// <summary>An optional member for new teams: ticked (<paramref name="OnNewTeams"/>) rows start in the New Team list.</summary>
public sealed record MemberDefault(string Name, string Cli, bool OnNewTeams);

/// <summary>Settings > Teams as saved: the default agents, the member-name list and its country, and how many list names a member uses.</summary>
public sealed record TeamDefaults(ManagerDefault Manager, IReadOnlyList<MemberDefault> Members, IReadOnlyList<string> MemberNames,
    string Country, IReadOnlyList<NameCountry> Countries, int UsedCount)
{
    /// <summary>The hire list new teams start from (role, agent, model); empty: new teams hire any agent.</summary>
    public IReadOnlyList<HireListRow> HireList { get; init; } = [];
}

/// <summary>What teams.setDefaults saved, and the member-name lines it left out.</summary>
public sealed record TeamDefaultsSaved(TeamDefaults Saved, IReadOnlyList<RejectedName> Rejected);

/// <summary>A new team's agents after the defaults and the member-name list: the Manager, then each member's full name and agent.</summary>
/// <see cref="MemberRoles"/> is each member's role (null: none), in the order of <see cref="Members"/>; <see cref="HireList"/> the team's hire list.
public sealed record NewTeamPlan(string ManagerName, string ManagerCli, IReadOnlyList<(string Name, string Cli)> Members)
{
    public IReadOnlyList<string?> MemberRoles { get; init; } = [];
    public IReadOnlyList<HireListRow> HireList { get; init; } = [];
}

/// <summary>
/// The person's saved defaults (0046 app_defaults): the Concierge's default agent, and for teams the default Manager,
/// optional members and the member-name list new members' first names are picked from. The defaults apply to every
/// way a team is made; a caller that gives its own Manager, agent or members uses those.
/// </summary>
public sealed partial class BusEngine
{
    public const string DefaultAgentKey = "concierge.defaultAgent", ManagerKey = "teams.manager", MembersKey = "teams.members",
        MemberNamesKey = "teams.memberNames", CountryKey = "teams.country";

    private static readonly JsonSerializerOptions DefaultsJson = new(JsonSerializerDefaults.Web);

    /// <summary>The picker's random numbers (a test sets a seeded one).</summary>
    public Random NameRandom { get; set; } = Random.Shared;

    /// <summary>Generate's run (Settings > Teams): the Concierge's default agent asked once for popular first names of a country (set by the app).</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<string>>>? NameGenerator { get; set; }

    // ---------------------------------------------------------------- the Concierge

    /// <summary>The saved default agent for new Concierge conversations; null = the first available on this computer.</summary>
    public Task<string?> ConciergeDefaultAgentAsync(CancellationToken ct = default) =>
        WithGate(() => Read<string>(DefaultAgentKey), ct);

    /// <summary>Saves the Concierge's default agent (null or empty: the first available). Refused for a name that is no agent.</summary>
    public Task<string?> SetConciergeDefaultAgentAsync(string? agent, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var stored = string.IsNullOrWhiteSpace(agent) ? null : CheckCli(agent.Trim());
            _store.SetAppDefault(DefaultAgentKey, stored is null ? null : JsonSerializer.Serialize(stored, DefaultsJson));
            return stored;
        }, ct);

    // ---------------------------------------------------------------- teams

    public Task<TeamDefaults> TeamDefaultsAsync(CancellationToken ct = default) => WithGate(ReadTeamDefaults, ct);

    /// <summary>
    /// Saves Settings > Teams. A part left null keeps what is saved. The name text is cleaned
    /// (<see cref="MemberNameList.Clean(string?)"/>): the lines it rejects are returned and not saved.
    /// </summary>
    public Task<TeamDefaultsSaved> SetTeamDefaultsAsync(ManagerDefault? manager, IReadOnlyList<MemberDefault>? members, string? memberNamesText,
        string? country, CancellationToken ct = default, IReadOnlyList<HireListRow>? hireList = null) =>
        WithGate(() => _store.InTransaction(() =>
        {
            if (hireList is not null) _store.SetAppDefault(HireListKey, JsonSerializer.Serialize(CheckHireList(hireList), DefaultsJson));
            IReadOnlyList<RejectedName> rejected = [];
            if (manager is not null)
            {
                var name = CheckDefaultName(manager.Name, "the Manager");
                _store.SetAppDefault(ManagerKey, JsonSerializer.Serialize(new ManagerDefault(name, CheckCli(manager.Cli)), DefaultsJson));
            }
            if (members is not null)
            {
                var rows = members.Select(m => new MemberDefault(CheckDefaultName(m.Name, "a member"), CheckCli(m.Cli), m.OnNewTeams)).ToList();
                // Rows with a job word alone each get their own first name at team creation; full names must differ.
                var repeat = rows.Where(r => !MemberNameList.IsJobWordOnly(r.Name))
                    .GroupBy(r => MemberNames.IdFor(r.Name), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
                if (repeat is not null) throw new BusRefusal("exists", $"two default members have the same name '{repeat.First().Name}'");
                _store.SetAppDefault(MembersKey, JsonSerializer.Serialize(rows, DefaultsJson));
            }
            if (memberNamesText is not null)
            {
                var (names, bad) = MemberNameList.Clean(memberNamesText);
                rejected = bad;
                _store.SetAppDefault(MemberNamesKey, JsonSerializer.Serialize(names, DefaultsJson));
            }
            if (country is not null)
            {
                var known = MemberNameList.Country(country) ?? throw new BusRefusal("bad_country", $"'{country}' is not a country of the list");
                _store.SetAppDefault(CountryKey, JsonSerializer.Serialize(known.Code, DefaultsJson));
            }
            return new TeamDefaultsSaved(ReadTeamDefaults(), rejected);
        }), ct);

    private static string CheckDefaultName(string? name, string who)
    {
        var label = string.Join(' ', (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        try { MemberNames.IdFor(label); }
        catch (ArgumentException e) { throw new BusRefusal("bad_name", $"{who}: {e.Message}"); }
        return label;
    }

    private TeamDefaults ReadTeamDefaults()
    {
        var manager = Read<ManagerDefault>(ManagerKey) is { Name.Length: > 0, Cli.Length: > 0 } m ? m : new ManagerDefault("Manager", "claude");
        var members = Read<List<MemberDefault>>(MembersKey)?.Where(r => r is { Name.Length: > 0, Cli.Length: > 0 }).ToList() ?? [];
        var names = NameList();
        var used = _store.GivenNames();
        return new TeamDefaults(manager, members, names, SavedCountry().Code, MemberNameList.Countries, names.Count(n => IsGivenNameUsed(n, used)))
        {
            HireList = DefaultHireList(),
        };
    }

    /// <summary>The saved member-name list (a malformed value reads as empty: a hire never fails on it).</summary>
    private IReadOnlyList<string> NameList() =>
        Read<List<string?>>(MemberNamesKey) is { } saved ? MemberNameList.Clean(saved).Names : [];

    private NameCountry SavedCountry() => MemberNameList.Country(Read<string>(CountryKey)) ?? MemberNameList.Country(MemberNameList.DefaultCountry)!;

    private T? Read<T>(string key)
    {
        if (_store.AppDefault(key) is not { } json) return default;
        try { return JsonSerializer.Deserialize<T>(json, DefaultsJson); }
        catch (JsonException) { return default; }
    }

    // ---------------------------------------------------------------- first names

    /// <summary>
    /// Whether a first name is used by a member of any team (archived too; Managers do not count), compared
    /// case-insensitively. A member whose first name is not known (from before first names were kept, and not
    /// splittable from its id) counts by its id's end, only where a first name starts there: the id ends with the
    /// name capitalized, after a lowercase letter ("DeveloperEster" uses Ester; "Tester" does not), or is the name.
    /// </summary>
    public static bool IsGivenNameUsed(string name, IReadOnlyList<GivenNameRow> members) =>
        members.Any(m => m.GivenName is { } given
            ? string.Equals(given, name, StringComparison.OrdinalIgnoreCase)
            : EndsWithFirstName(m.Name, name));

    private static bool EndsWithFirstName(string id, string name)
    {
        if (name.Length == 0 || id.Length < name.Length) return false;
        if (id.Length == name.Length) return string.Equals(id, name, StringComparison.OrdinalIgnoreCase);
        var tail = id[^name.Length..];
        return char.IsUpper(tail[0]) && string.Equals(tail, name, StringComparison.OrdinalIgnoreCase) && char.IsLower(id[^(name.Length + 1)]);
    }

    /// <summary>A free first name from the list (<see cref="MemberNameList.Pick"/>), also free of <paramref name="alsoTaken"/>; null when none.</summary>
    private string? PickGivenName(IReadOnlyCollection<string>? alsoTaken = null)
    {
        var used = _store.GivenNames();
        return MemberNameList.Pick(NameList(), n => IsGivenNameUsed(n, used) || alsoTaken?.Contains(n, StringComparer.OrdinalIgnoreCase) == true, NameRandom);
    }

    /// <summary>
    /// A new team's agents: the caller's own Manager name, agent and members where given, the saved defaults where not
    /// (the Manager, and the member rows ticked "On new teams"). A member name that is a job word alone gets a first name
    /// from the list; when the list has none free the team is refused here, before anything is made.
    /// </summary>
    /// <remarks>
    /// <paramref name="roles"/> gives each of <paramref name="members"/> its role (by index; null or blank: none): a row with a
    /// role and no name is named for the role, and with no agent runs on the role's suggested one. <paramref name="hireList"/>
    /// is the team's hire list; null: the default hire list (Settings > Teams).
    /// </remarks>
    public Task<NewTeamPlan> PlanNewTeamAsync(string? managerName, string? managerCli, IReadOnlyList<(string Name, string? Cli)>? members,
        CancellationToken ct = default, IReadOnlyList<string?>? roles = null, IReadOnlyList<HireListRow>? hireList = null) =>
        WithGate(() =>
        {
            var defaults = ReadTeamDefaults();
            var manager = string.IsNullOrWhiteSpace(managerName) ? defaults.Manager.Name : managerName.Trim();
            var cli = string.IsNullOrWhiteSpace(managerCli) ? defaults.Manager.Cli : managerCli.Trim();
            var rows = members ?? defaults.Members.Where(m => m.OnNewTeams).Select(m => (m.Name, (string?)m.Cli)).ToList();
            var picked = new List<string>();
            var planned = new List<(string Name, string Cli)>();
            var plannedRoles = new List<string?>();
            foreach (var (rawName, rawCli) in rows)
            {
                var asked = members is not null && roles is not null && plannedRoles.Count < roles.Count ? roles[plannedRoles.Count] : null;
                var role = string.IsNullOrWhiteSpace(asked) ? null
                    : _store.Role(asked.Trim()) ?? throw new BusRefusal("no_such_role", $"'{asked.Trim()}' is not a role ({string.Join(", ", _store.Roles().Select(r => r.Name))})");
                plannedRoles.Add(role?.Name);
                var name = (rawName ?? "").Trim();
                if (name.Length == 0 && role is not null) name = role.Name;
                var memberCli = !string.IsNullOrWhiteSpace(rawCli) ? rawCli.Trim() : role?.Agent ?? "claude";
                if (name.Length > 0 && MemberNameList.IsJobWordOnly(name))
                {
                    try { MemberNames.IdFor(name); }
                    catch (ArgumentException e) { throw new BusRefusal("bad_name", e.Message); }
                    var given = PickGivenName(picked) ?? throw new BusRefusal("no_free_name", MemberNameList.CreateNoFreeName(name));
                    picked.Add(given);
                    name = $"{name} {given}";
                }
                planned.Add((name, memberCli));
            }
            var ids = planned.Select(m => m.Name).Prepend(manager).Select(n => { try { return MemberNames.IdFor(n); } catch (ArgumentException) { return n; } });
            if (ids.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                throw new BusRefusal("exists", "two agents of the new team have the same name");
            return new NewTeamPlan(manager, cli, planned)
            {
                MemberRoles = plannedRoles,
                // A default row whose role or agent has gone since it was saved is left out rather than refusing the team.
                HireList = hireList is not null ? CheckHireList(hireList)
                    : defaults.HireList.Where(r => { try { CheckHireList([r]); return true; } catch (BusRefusal) { return false; } }).ToList(),
            };
        }, ct);

    /// <summary>Makes the planned team: its Manager, its hire list, then its members (team_create in the change log).</summary>
    public async Task<long> CreatePlannedTeamAsync(string name, NewTeamPlan plan, string? repoPath, CancellationToken ct = default)
    {
        var teamId = await CreateTeamAsync(name, plan.ManagerName, plan.ManagerCli, repoPath, ct);
        if (plan.HireList.Count > 0) await WithGate(() => _store.InTransaction(() => { _store.SetHireList(teamId, plan.HireList); return true; }), ct);
        for (var i = 0; i < plan.Members.Count; i++)
        {
            var (memberName, cli) = plan.Members[i];
            var role = i < plan.MemberRoles.Count ? plan.MemberRoles[i] : null;
            if (role is null) await AddMemberAsync(teamId, memberName, cli, ct, Agents.Profiles.AgentChangeLog.HowTeamCreate);
            else await AddMemberAsRoleAsync(teamId, memberName, cli, role, ct);
        }
        return teamId;
    }

    /// <summary>A New team row with a role: the member gets the role and its job as its standing job.</summary>
    private async Task AddMemberAsRoleAsync(long teamId, string name, string cli, string role, CancellationToken ct)
    {
        var added = await AddMemberCoreAsync(teamId, name, cli, ct);
        var row = await WithGate(() => _store.InTransaction(() =>
        {
            _store.SetHireRole(added.Id, role);
            if (_store.Role(role)?.Job is { Length: > 0 } job) _store.UpdateMember(added.Id, added.Cli, job, added.Model, added.Credential);
            return _store.MemberRows(memberId: added.Id).Single();
        }), ct);
        LogFirstAgent(row, Agents.Profiles.AgentChangeLog.HowTeamCreate);
    }

    /// <summary>The hire name after the list: a job word alone gets a picked first name, a full name is checked unused. The full name and first name.</summary>
    private (string Label, string? Given) HireName(string? name)
    {
        var label = string.Join(' ', (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (label.Length > 0 && MemberNameList.IsJobWordOnly(label))
        {
            try { MemberNames.IdFor(label); }
            catch (ArgumentException e) { throw new BusRefusal("bad_name", e.Message + "; name them for the job alone, e.g. 'Developer': the app adds the first name"); }
            var given = PickGivenName() ?? throw new BusRefusal("bad_name", MemberNameList.HireNoFreeName(label, SavedCountry()));
            return ($"{label} {given}", given);
        }
        var first = MemberNameList.GivenNameOf(label);
        if (first is not null && _store.GivenNames().FirstOrDefault(m => IsGivenNameUsed(first, [m])) is { } taken)
            throw new BusRefusal("exists",
                $"the first name '{first}' is already used by a member ({taken.Name}); call hire again with another first name common in {SavedCountry().DisplayName()}, one not used by any member of any team");
        return (label, first);
    }
}
