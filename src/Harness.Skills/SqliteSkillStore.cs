using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Harness.Contracts;
using Harness.Identity;

namespace Harness.Skills;

/// <summary>
/// The skill index. Built-in rows are rebuilt from the build on every start and plugin rows from
/// each installed plugin whenever the plugins are loaded or rescanned, and neither is written
/// otherwise; custom rows are the person's and exist only here. A custom row may belong to one team
/// (a TEAM SKILL, <see cref="Skill.Team"/>); a name is unique among the instance-wide rows and within
/// a team, and never held by both, so a team's member never sees two skills of one name.
/// </summary>
public sealed class SqliteSkillStore : ISkillStore
{
    private readonly string _connectionString;

    /// <summary>Absence means the safe value. Only the test fixture asks for anything else.</summary>
    private readonly SqliteDurability _durability;

    private static readonly Regex LegalName = new("^[a-z0-9][a-z0-9-]{0,47}$", RegexOptions.Compiled);

    public SqliteSkillStore(string databasePath, SqliteDurability durability = SqliteDurability.SurvivesPowerLoss)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();

        _durability = durability;
    }

    public static bool IsLegalName(string value) => LegalName.IsMatch(value);

    public async Task<IReadOnlyList<string>> ReplaceBuiltInsAsync(
        IReadOnlyList<SkillDraft> builtIns, DateTimeOffset builtAt, CancellationToken ct = default)
    {
        var moved = new List<string>();
        var at = Stamp(builtAt);

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM skills WHERE kind = 'builtin'";
            await clear.ExecuteNonQueryAsync(ct);
        }

        // Reverse name order, so newest-first paging lists the built-ins alphabetically.
        foreach (var skill in builtIns.OrderByDescending(s => s.Name, StringComparer.Ordinal))
        {
            RequireLegalName(skill.Name);

            // A custom skill that took this name before the build shipped it is kept, under
            // another name: the built-in wins the name, and nothing the person wrote is lost.
            if (await KindOfAsync(connection, transaction, skill.Name, ct) is not null)
            {
                var free = await FreeNameAsync(connection, transaction, $"{skill.Name}-custom", ct);

                await using var rename = connection.CreateCommand();
                rename.Transaction = transaction;
                rename.CommandText = "UPDATE skills SET name = $to WHERE name = $from AND team IS NULL";
                rename.Parameters.AddWithValue("$from", skill.Name);
                rename.Parameters.AddWithValue("$to", free);
                await rename.ExecuteNonQueryAsync(ct);

                moved.Add($"{skill.Name} -> {free}");
            }

            // A team skill holding it is moved the same way, per team.
            foreach (var team in await TeamsHoldingAsync(connection, transaction, skill.Name, ct))
            {
                var free = await FreeNameAsync(connection, transaction, $"{skill.Name}-custom", ct);

                await using var rename = connection.CreateCommand();
                rename.Transaction = transaction;
                rename.CommandText = "UPDATE skills SET name = $to WHERE name = $from AND team = $team";
                rename.Parameters.AddWithValue("$from", skill.Name);
                rename.Parameters.AddWithValue("$to", free);
                rename.Parameters.AddWithValue("$team", team);
                await rename.ExecuteNonQueryAsync(ct);

                moved.Add($"{skill.Name} -> {free} (team {team})");
            }

            await InsertAsync(connection, transaction, skill, "builtin", at, null, ct);
        }

        await transaction.CommitAsync(ct);
        return moved;
    }

    public async Task<IReadOnlyList<string>> ReplacePluginSkillsAsync(
        string source, IReadOnlyList<SkillDraft> drafts, DateTimeOffset at, CancellationToken ct = default)
    {
        var said = new List<string>();
        var stamp = Stamp(at);

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM skills WHERE kind = 'plugin' AND source = $source";
            clear.Parameters.AddWithValue("$source", source);
            await clear.ExecuteNonQueryAsync(ct);
        }

        foreach (var draft in drafts.OrderByDescending(s => s.Name, StringComparer.Ordinal))
        {
            var skill = Validate(draft);

            if (!PluginSkillNames.Belongs(skill.Name, source))
            {
                throw new ArgumentException(
                    $"'{skill.Name}' is not a name plugin '{source}' may ship: it must be {PluginSkillNames.Main(source)} or begin {PluginSkillNames.Main(source)}-.");
            }

            switch (await KindOfAsync(connection, transaction, skill.Name, ct))
            {
                case "custom":
                    // The plugin wins its own namespace, and nothing the person wrote is lost.
                    var free = await FreeNameAsync(connection, transaction, $"{skill.Name}-custom", ct);

                    await using (var rename = connection.CreateCommand())
                    {
                        rename.Transaction = transaction;
                        rename.CommandText = "UPDATE skills SET name = $to WHERE name = $from AND team IS NULL";
                        rename.Parameters.AddWithValue("$from", skill.Name);
                        rename.Parameters.AddWithValue("$to", free);
                        await rename.ExecuteNonQueryAsync(ct);
                    }

                    said.Add($"a custom skill held plugin '{source}''s skill name and was relabelled: {skill.Name} -> {free}.");
                    break;

                case "builtin" or "plugin":
                    // `plugin-a-b` is plugin `a`'s skill `b` AND plugin `a-b`'s own: the first to
                    // hold it keeps it.
                    said.Add($"plugin '{source}''s skill {skill.Name} was not indexed: another skill already holds that name.");
                    continue;
            }

            await InsertAsync(connection, transaction, skill, "plugin", stamp, null, ct, source);
        }

        await transaction.CommitAsync(ct);
        return said;
    }

    public async Task<IReadOnlyList<string>> PluginSourcesAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT source FROM skills WHERE kind = 'plugin' ORDER BY source";

        var sources = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) sources.Add(reader.GetString(0));
        return sources;
    }

    public async Task<IReadOnlyList<Skill>> ListAsync(
        SkillKindFilter kind,
        string? query,
        string? role,
        long? before,
        int take,
        CancellationToken ct = default)
    {
        var fts = string.IsNullOrWhiteSpace(query) ? null : ToInertFtsQuery(query);
        take = Math.Clamp(take, 1, 200);

        await using var connection = Open();
        await using var command = connection.CreateCommand();

        var where = new List<string>();

        if (kind != SkillKindFilter.All)
        {
            where.Add("s.kind = $kind");
            command.Parameters.AddWithValue("$kind", kind switch
            {
                SkillKindFilter.BuiltIn => "builtin",
                SkillKindFilter.Plugin => "plugin",
                _ => "custom",
            });
        }

        if (fts is not null)
        {
            where.Add("s.id IN (SELECT rowid FROM skills_fts WHERE skills_fts MATCH $query)");
            command.Parameters.AddWithValue("$query", fts);
        }

        if (before is { } cursor)
        {
            where.Add("s.id < $before");
            command.Parameters.AddWithValue("$before", cursor);
        }

        // What a role is offered instance-wide never includes a team's own skill.
        if (role is not null) where.Add("s.team IS NULL");

        command.CommandText =
            $"""
            SELECT {Columns}
            FROM skills s
            {(where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where))}
            ORDER BY s.id DESC
            """;

        // Roles are filtered here rather than in SQL: they are a short space-separated list, and a
        // LIKE over it would match `manager` inside a name nobody wrote. The page is read until it
        // is full, so a role filter never returns a short page while rows remain.
        var list = new List<Skill>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (list.Count < take && await reader.ReadAsync(ct))
        {
            var skill = Read(reader);
            if (role is not null && !SkillRoles.Offers(skill.Roles, role)) continue;
            list.Add(skill);
        }

        return list;
    }

    public async Task<IReadOnlyList<Skill>> ListOfferedAsync(
        string role, string? team, string? query, int take, CancellationToken ct = default)
    {
        var fts = string.IsNullOrWhiteSpace(query) ? null : ToInertFtsQuery(query);
        take = Math.Clamp(take, 1, 200);

        await using var connection = Open();
        await using var command = connection.CreateCommand();

        var where = new List<string> { team is null ? "s.team IS NULL" : "(s.team IS NULL OR s.team = $team)" };
        if (team is not null) command.Parameters.AddWithValue("$team", team);

        if (fts is not null)
        {
            where.Add("s.id IN (SELECT rowid FROM skills_fts WHERE skills_fts MATCH $query)");
            command.Parameters.AddWithValue("$query", fts);
        }

        command.CommandText = $"SELECT {Columns} FROM skills s WHERE {string.Join(" AND ", where)} ORDER BY s.id DESC";

        var list = new List<Skill>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (list.Count < take && await reader.ReadAsync(ct))
        {
            var skill = Read(reader);
            if (SkillRoles.Offers(skill.Roles, role)) list.Add(skill);
        }

        return list;
    }

    public async Task<Skill?> GetAsync(string name, CancellationToken ct = default)
    {
        if (!IsLegalName(name)) return null;

        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM skills s WHERE s.name = $name AND s.team IS NULL";
        command.Parameters.AddWithValue("$name", name);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<Skill>> FindAllAsync(string name, CancellationToken ct = default)
    {
        if (!IsLegalName(name)) return [];

        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM skills s WHERE s.name = $name ORDER BY s.team IS NOT NULL, s.team";
        command.Parameters.AddWithValue("$name", name);

        var list = new List<Skill>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(Read(reader));
        return list;
    }

    public async Task<IReadOnlyList<Skill>> ListTeamAsync(string team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM skills s WHERE s.team = $team ORDER BY s.name";
        command.Parameters.AddWithValue("$team", team);

        var list = new List<Skill>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(Read(reader));
        return list;
    }

    public async Task<Skill> PutTeamSkillAsync(
        string team, SkillDraft draft, bool replace, string? by, TriggerAudit audit, CancellationToken ct = default)
    {
        var clean = Validate(draft);
        RefuseReserved(clean.Name);

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await RefuseInstanceNameAsync(connection, transaction, clean.Name, ct);

        var exists = await TeamHoldsAsync(connection, transaction, team, clean.Name, ct);
        if (exists && !replace)
        {
            throw new SkillRefusedException($"Team {team} already has a skill named '{clean.Name}'.");
        }

        if (exists)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE skills
                SET name = $to, description = $description, roles = $roles, body = $body,
                    updated_at = $at, updated_by = $by
                WHERE name = $to AND team = $team
                """;
            BindDraft(update, clean, by);
            update.Parameters.AddWithValue("$team", team);
            await update.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await InsertAsync(connection, transaction, clean, "custom", Stamp(DateTimeOffset.UtcNow), by, ct, team: team);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);

        return (await TeamSkillAsync(team, clean.Name, ct))!;
    }

    public async Task<Skill?> UpdateTeamSkillAsync(
        string team, string name, SkillDraft draft, string? by, TriggerAudit audit, CancellationToken ct = default)
    {
        var clean = Validate(draft);

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        if (!await TeamHoldsAsync(connection, transaction, team, name, ct)) return null;

        if (!string.Equals(name, clean.Name, StringComparison.OrdinalIgnoreCase))
        {
            RefuseReserved(clean.Name);
            await RefuseInstanceNameAsync(connection, transaction, clean.Name, ct);

            if (await TeamHoldsAsync(connection, transaction, team, clean.Name, ct))
            {
                throw new SkillRefusedException($"Team {team} already has a skill named '{clean.Name}'.");
            }
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE skills
                SET name = $to, description = $description, roles = $roles, body = $body,
                    updated_at = $at, updated_by = $by
                WHERE name = $name AND team = $team
                """;
            BindDraft(update, clean, by);
            update.Parameters.AddWithValue("$name", name);
            update.Parameters.AddWithValue("$team", team);
            await update.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);

        return await TeamSkillAsync(team, clean.Name, ct);
    }

    public async Task<bool> DeleteTeamSkillAsync(string team, string name, TriggerAudit audit, CancellationToken ct = default)
    {
        if (!IsLegalName(name)) return false;

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM skills WHERE name = $name AND team = $team";
            delete.Parameters.AddWithValue("$name", name);
            delete.Parameters.AddWithValue("$team", team);
            if (await delete.ExecuteNonQueryAsync(ct) == 0) return false;
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<string>> DeleteTeamSkillsAsync(
        string team, Func<string, TriggerAudit> audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var names = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT name FROM skills WHERE team = $team ORDER BY name";
            select.Parameters.AddWithValue("$team", team);
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM skills WHERE team = $team";
            delete.Parameters.AddWithValue("$team", team);
            await delete.ExecuteNonQueryAsync(ct);
        }

        foreach (var name in names) await TenantAuditRow.AppendAsync(connection, transaction, audit(name), ct);

        await transaction.CommitAsync(ct);
        return names;
    }

    public async Task<Skill> CreateCustomAsync(SkillDraft draft, string? by, CancellationToken ct = default)
    {
        var clean = Validate(draft);
        RefuseReserved(clean.Name);

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        switch (await KindOfAsync(connection, transaction, clean.Name, ct))
        {
            case "builtin":
                throw new SkillRefusedException(
                    $"'{clean.Name}' is a built-in skill's name. A custom skill needs a name of its own.");
            case "custom":
                throw new SkillRefusedException($"A custom skill named '{clean.Name}' already exists.");
        }

        await RefuseTeamNameAsync(connection, transaction, clean.Name, ct);

        await InsertAsync(connection, transaction, clean, "custom", Stamp(DateTimeOffset.UtcNow), by, ct);
        await transaction.CommitAsync(ct);

        return (await GetAsync(clean.Name, ct))!;
    }

    public async Task<Skill?> UpdateCustomAsync(
        string name, SkillDraft draft, string? by, CancellationToken ct = default)
    {
        var clean = Validate(draft);

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        switch (await KindOfAsync(connection, transaction, name, ct))
        {
            case null:
                return null;
            case "builtin":
                throw new SkillRefusedException(
                    $"'{name}' is a built-in skill. Built-in skills change only with the product, "
                    + "so it cannot be edited or relabelled here.");
            case "plugin":
                throw new SkillRefusedException(PluginSkillNames.Locked(name, "edited or relabelled"));
        }

        if (!string.Equals(name, clean.Name, StringComparison.OrdinalIgnoreCase))
        {
            RefuseReserved(clean.Name);

            switch (await KindOfAsync(connection, transaction, clean.Name, ct))
            {
                case "builtin":
                    throw new SkillRefusedException(
                        $"'{clean.Name}' is a built-in skill's name. A custom skill needs a name of its own.");
                case "custom":
                    throw new SkillRefusedException($"A custom skill named '{clean.Name}' already exists.");
            }

            await RefuseTeamNameAsync(connection, transaction, clean.Name, ct);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE skills
                SET name = $to, description = $description, roles = $roles, body = $body,
                    updated_at = $at, updated_by = $by
                WHERE name = $name AND kind = 'custom' AND team IS NULL
                """;
            update.Parameters.AddWithValue("$name", name);
            BindDraft(update, clean, by);
            await update.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return await GetAsync(clean.Name, ct);
    }

    public async Task<bool> DeleteCustomAsync(string name, CancellationToken ct = default)
    {
        if (!IsLegalName(name)) return false;

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        switch (await KindOfAsync(connection, transaction, name, ct))
        {
            case null:
                return false;
            case "builtin":
                throw new SkillRefusedException(
                    $"'{name}' is a built-in skill. Built-in skills change only with the product, "
                    + "so it cannot be deleted.");
            case "plugin":
                throw new SkillRefusedException(PluginSkillNames.Locked(name, "deleted"));
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM skills WHERE name = $name AND kind = 'custom' AND team IS NULL";
            delete.Parameters.AddWithValue("$name", name);
            await delete.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return true;
    }

    private static SkillDraft Validate(SkillDraft draft)
    {
        var name = (draft.Name ?? "").Trim();
        RequireLegalName(name);

        var description = (draft.Description ?? "").Trim();
        if (description.Length == 0)
        {
            throw new ArgumentException("A skill needs a one-line description.");
        }

        if (description.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("A skill's description is one line.");
        }

        var body = draft.Body ?? "";
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("A skill needs a body.");
        }

        return new SkillDraft(name, description, SkillRoles.Normalise(draft.Roles), body);
    }

    /// <summary>A custom skill may not take a name in the plugins' namespace, installed or not.</summary>
    private static void RefuseReserved(string name)
    {
        if (PluginSkillNames.IsReserved(name))
        {
            throw new SkillRefusedException(
                $"'{name}' begins '{PluginSkillNames.Prefix}', which is reserved for the skills installed plugins ship. "
                + "A custom skill needs a name of its own.");
        }
    }

    /// <summary>A team skill may not take an instance-wide skill's name, of any kind.</summary>
    private static async Task RefuseInstanceNameAsync(
        SqliteConnection connection, SqliteTransaction transaction, string name, CancellationToken ct)
    {
        switch (await KindOfAsync(connection, transaction, name, ct))
        {
            case "builtin":
                throw new SkillRefusedException(
                    $"'{name}' is a built-in skill's name. A team skill needs a name of its own.");
            case "custom":
                throw new SkillRefusedException(
                    $"'{name}' is an instance-wide custom skill's name. A team skill needs a name of its own.");
            case "plugin":
                throw new SkillRefusedException(
                    $"'{name}' is a plugin's skill name. A team skill needs a name of its own.");
        }
    }

    /// <summary>An instance-wide custom skill may not take a name a team's skill holds.</summary>
    private static async Task RefuseTeamNameAsync(
        SqliteConnection connection, SqliteTransaction transaction, string name, CancellationToken ct)
    {
        if ((await TeamsHoldingAsync(connection, transaction, name, ct)) is [var team, ..])
        {
            throw new SkillRefusedException(
                $"'{name}' is team {team}'s skill name. An instance-wide skill needs a name no team's skill holds.");
        }
    }

    private static void BindDraft(SqliteCommand command, SkillDraft clean, string? by)
    {
        command.Parameters.AddWithValue("$to", clean.Name);
        command.Parameters.AddWithValue("$description", clean.Description);
        command.Parameters.AddWithValue("$roles", string.Join(' ', clean.Roles));
        command.Parameters.AddWithValue("$body", clean.Body);
        command.Parameters.AddWithValue("$at", Stamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$by", (object?)by ?? DBNull.Value);
    }

    private async Task<Skill?> TeamSkillAsync(string team, string name, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM skills s WHERE s.name = $name AND s.team = $team";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$team", team);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    private static async Task InsertAsync(
        SqliteConnection connection, SqliteTransaction transaction, SkillDraft skill, string kind,
        string at, string? by, CancellationToken ct, string? source = null, string? team = null)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO skills (name, kind, description, roles, body, updated_at, updated_by, source, team)
            VALUES ($name, $kind, $description, $roles, $body, $at, $by, $source, $team)
            """;
        insert.Parameters.AddWithValue("$source", (object?)source ?? DBNull.Value);
        insert.Parameters.AddWithValue("$team", (object?)team ?? DBNull.Value);
        insert.Parameters.AddWithValue("$name", skill.Name);
        insert.Parameters.AddWithValue("$kind", kind);
        insert.Parameters.AddWithValue("$description", skill.Description);
        insert.Parameters.AddWithValue("$roles", string.Join(' ', SkillRoles.Normalise(skill.Roles)));
        insert.Parameters.AddWithValue("$body", skill.Body);
        insert.Parameters.AddWithValue("$at", at);
        insert.Parameters.AddWithValue("$by", (object?)by ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The kind of the INSTANCE-WIDE row named <paramref name="name"/>, or null.</summary>
    private static async Task<string?> KindOfAsync(
        SqliteConnection connection, SqliteTransaction transaction, string name, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT kind FROM skills WHERE name = $name AND team IS NULL";
        command.Parameters.AddWithValue("$name", name);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<IReadOnlyList<string>> TeamsHoldingAsync(
        SqliteConnection connection, SqliteTransaction transaction, string name, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT team FROM skills WHERE name = $name AND team IS NOT NULL ORDER BY team";
        command.Parameters.AddWithValue("$name", name);

        var teams = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) teams.Add(reader.GetString(0));
        return teams;
    }

    private static async Task<bool> TeamHoldsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string team, string name, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM skills WHERE name = $name AND team = $team";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$team", team);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>Whether any row, of any scope, holds <paramref name="name"/>.</summary>
    private static async Task<bool> AnyHoldsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string name, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM skills WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<string> FreeNameAsync(
        SqliteConnection connection, SqliteTransaction transaction, string preferred, CancellationToken ct)
    {
        var candidate = preferred.Length > 48 ? preferred[..48] : preferred;

        for (var suffix = 2; await AnyHoldsAsync(connection, transaction, candidate, ct); suffix++)
        {
            var tail = $"-{suffix}";
            candidate = (preferred.Length + tail.Length > 48 ? preferred[..(48 - tail.Length)] : preferred) + tail;
        }

        return candidate;
    }

    private static Skill Read(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3).Split(' ', StringSplitOptions.RemoveEmptyEntries),
            reader.GetString(4) switch
            {
                "builtin" => SkillKind.BuiltIn,
                "plugin" => SkillKind.Plugin,
                _ => SkillKind.Custom,
            },
            reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));

    private const string Columns =
        "s.id, s.name, s.description, s.roles, s.kind, s.body, s.updated_at, s.updated_by, s.source, s.team";

    private static string Stamp(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    private static string ToInertFtsQuery(string query)
    {
        var tokens = query.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return "";
        return string.Join(" OR ", tokens.Select(token => $"\"{token.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
    }

    private static void RequireLegalName(string value)
    {
        if (!IsLegalName(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a legal skill name. Use lowercase kebab-case (letters, digits, '-') and 48 characters or fewer.");
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        SqlitePragmas.Apply(connection, durability: _durability);

        return connection;
    }
}
