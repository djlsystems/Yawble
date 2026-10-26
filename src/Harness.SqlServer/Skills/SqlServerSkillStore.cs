using Harness.Contracts;
using Harness.Skills;
using Harness.Skills.Search;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Skills;

/// <summary>
/// <see cref="ISkillStore"/> on SQL Server: SQLite's <c>SqliteSkillStore</c> write for write. Every rule
/// a write is checked against, every refusal's sentence and the reading of a row are
/// <see cref="SkillRules"/>', shared with it. A team skill's write commits with its <c>tenant_events</c>
/// row (<see cref="SqlServerTenantAuditRow"/>) or not at all.
///
/// <para>
/// A NAME AND A TEAM FOLD CASE, AS SQLITE'S <c>COLLATE NOCASE</c> COLUMNS DO, and a trailing space
/// decides every lookup as it does there (<see cref="SqlServerSql.Same"/>). A stored name is always a
/// legal one, which holds no space, but a lookup can be asked for any text: an edit names the skill it
/// changes as the caller spelled it.
/// </para>
///
/// <para>
/// SEARCH IS SQLITE'S FTS5 SEARCH, IN CODE. SQLite matches a query's words against an FTS5 index of the
/// name, the description and the body (<c>tokenize='porter unicode61'</c>); SQL Server's full-text search
/// is an optional component with its own word breakers, so this store reads the candidate rows newest
/// first, filtered in SQL by everything but the words, and keeps those <see cref="SkillSearch"/> matches
/// until the page is full: the same tokenizer, stemmer and phrase rule, ported from SQLite, so
/// <c>running</c> finds <c>run</c>, <c>cafe</c> finds it with an accent, and <c>work</c> does not find
/// <c>worktree</c>, exactly as there. The words never become SQL or parameters.
/// </para>
///
/// <para>
/// EVERY READ-THEN-WRITE HOLDS WHAT IT READ (<see cref="Held"/>). SQLite runs one writer at a time; here
/// two creates of one name would both find it free and one would fail on the unique index instead of
/// being refused in words, and a custom create and a team skill of one name could both pass the check
/// that keeps a name in one scope. Each check that a write follows reads under <c>UPDLOCK, HOLDLOCK</c>,
/// so the second writer waits for the first and then sees its row. Which lock comes first is fixed: the
/// instance-wide name, then the team names, then <c>tenant_events</c>, whose rows are inserts that read
/// nothing. No write here takes a team, outcome or backlog row, or the message append lock.
/// </para>
/// </summary>
public sealed class SqlServerSkillStore(string connectionString) : ISkillStore
{
    /// <summary>The hint every read a write follows takes, so what it read stays so until the commit.</summary>
    private const string Held = "WITH (UPDLOCK, HOLDLOCK)";

    private static readonly string NameMatch = SqlServerSql.Same("s.name", "@name");

    private static readonly string TeamMatch = SqlServerSql.Same("s.team", "@team");

    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    public Task<IReadOnlyList<string>> ReplaceBuiltInsAsync(
        IReadOnlyList<SkillDraft> builtIns, DateTimeOffset builtAt, CancellationToken ct = default) =>
        WriteAsync<IReadOnlyList<string>>(
            async (connection, transaction, token) =>
            {
                var moved = new List<string>();
                var at = Instant.Stamp(builtAt);

                await ExecuteAsync(connection, transaction, "DELETE FROM skills WHERE kind = N'builtin'", _ => { }, token);

                // Reverse name order, so newest-first paging lists the built-ins alphabetically.
                foreach (var skill in builtIns.OrderByDescending(s => s.Name, StringComparer.Ordinal))
                {
                    SkillRules.RequireLegalName(skill.Name);

                    // A custom skill that took this name before the build shipped it is kept, under
                    // another name: the built-in wins the name, and nothing the person wrote is lost.
                    if (await KindOfAsync(connection, transaction, skill.Name, token) is not null)
                    {
                        var free = await FreeNameAsync(connection, transaction, $"{skill.Name}-custom", token);
                        await RenameAsync(connection, transaction, skill.Name, free, null, token);
                        moved.Add(SkillRules.Moved(skill.Name, free));
                    }

                    // A team skill holding it is moved the same way, per team.
                    foreach (var team in await TeamsHoldingAsync(connection, transaction, skill.Name, token))
                    {
                        var free = await FreeNameAsync(connection, transaction, $"{skill.Name}-custom", token);
                        await RenameAsync(connection, transaction, skill.Name, free, team, token);
                        moved.Add(SkillRules.Moved(skill.Name, free, team));
                    }

                    await InsertAsync(connection, transaction, skill, SkillRules.BuiltIn, at, null, token);
                }

                return moved;
            },
            ct);

    public Task<IReadOnlyList<string>> ReplacePluginSkillsAsync(
        string source, IReadOnlyList<SkillDraft> drafts, DateTimeOffset at, CancellationToken ct = default) =>
        WriteAsync<IReadOnlyList<string>>(
            async (connection, transaction, token) =>
            {
                var said = new List<string>();
                var stamp = Instant.Stamp(at);

                await ExecuteAsync(
                    connection, transaction,
                    $"DELETE FROM skills WHERE kind = N'plugin' AND {SqlServerSql.Same("source", "@source")}",
                    command => command.Parameters.AddWithValue("@source", source),
                    token);

                foreach (var draft in drafts.OrderByDescending(s => s.Name, StringComparer.Ordinal))
                {
                    var skill = SkillRules.Validate(draft);
                    SkillRules.RequireBelongs(skill.Name, source);

                    switch (await KindOfAsync(connection, transaction, skill.Name, token))
                    {
                        case SkillRules.Custom:
                            // The plugin wins its own namespace, and nothing the person wrote is lost.
                            var free = await FreeNameAsync(connection, transaction, $"{skill.Name}-custom", token);
                            await RenameAsync(connection, transaction, skill.Name, free, null, token);
                            said.Add(SkillRules.PluginRelabelled(source, skill.Name, free));
                            break;

                        case SkillRules.BuiltIn or SkillRules.Plugin:
                            // `plugin-a-b` is plugin `a`'s skill `b` AND plugin `a-b`'s own: the first to
                            // hold it keeps it.
                            said.Add(SkillRules.PluginNotIndexed(source, skill.Name));
                            continue;
                    }

                    await InsertAsync(connection, transaction, skill, SkillRules.Plugin, stamp, null, token, source);
                }

                return said;
            },
            ct);

    public Task<IReadOnlyList<string>> PluginSourcesAsync(CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<string>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT DISTINCT source FROM skills WHERE kind = N'plugin' ORDER BY source";

                var sources = new List<string>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) sources.Add(reader.GetString(0));
                return sources;
            },
            ct);

    public Task<IReadOnlyList<Skill>> ListAsync(
        SkillKindFilter kind,
        string? query,
        string? role,
        long? before,
        int take,
        CancellationToken ct = default)
    {
        take = SkillRules.Take(take);

        return RunAsync<IReadOnlyList<Skill>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                var where = new List<string>();

                if (SkillRules.KindOf(kind) is { } stored)
                {
                    where.Add("s.kind = @kind");
                    command.Parameters.AddWithValue("@kind", stored);
                }

                var search = SkillSearch.For(query);

                if (before is { } cursor)
                {
                    where.Add("s.id < @before");
                    command.Parameters.AddWithValue("@before", cursor);
                }

                // What a role is offered instance-wide never includes a team's own skill.
                if (role is not null) where.Add("s.team IS NULL");

                command.CommandText =
                    $"SELECT {SkillRules.Columns} FROM skills s"
                    + (where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where))
                    + " ORDER BY s.id DESC";

                // Roles are filtered here rather than in SQL, as on SQLite; the page is read until it
                // is full, so a role filter never returns a short page while rows remain.
                var list = new List<Skill>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (list.Count < take && await reader.ReadAsync(token))
                {
                    var skill = SkillRules.Read(reader);
                    if (role is not null && !SkillRoles.Offers(skill.Roles, role)) continue;
                    if (search is not null && !search.Matches(skill)) continue;
                    list.Add(skill);
                }

                return list;
            },
            ct);
    }

    public Task<IReadOnlyList<Skill>> ListOfferedAsync(
        string role, string? team, string? query, int take, CancellationToken ct = default)
    {
        take = SkillRules.Take(take);

        return RunAsync<IReadOnlyList<Skill>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();

                var where = new List<string> { team is null ? "s.team IS NULL" : $"(s.team IS NULL OR {TeamMatch})" };
                if (team is not null) command.Parameters.AddWithValue("@team", team);

                var search = SkillSearch.For(query);

                command.CommandText = $"SELECT {SkillRules.Columns} FROM skills s WHERE {string.Join(" AND ", where)} ORDER BY s.id DESC";

                var list = new List<Skill>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (list.Count < take && await reader.ReadAsync(token))
                {
                    var skill = SkillRules.Read(reader);
                    if (SkillRoles.Offers(skill.Roles, role) && (search is null || search.Matches(skill))) list.Add(skill);
                }

                return list;
            },
            ct);
    }

    public async Task<Skill?> GetAsync(string name, CancellationToken ct = default)
    {
        if (!SkillRules.IsLegalName(name)) return null;

        return (await ReadAsync($"SELECT {SkillRules.Columns} FROM skills s WHERE {NameMatch} AND s.team IS NULL", name, null, ct))
            .SingleOrDefault();
    }

    public async Task<IReadOnlyList<Skill>> FindAllAsync(string name, CancellationToken ct = default)
    {
        if (!SkillRules.IsLegalName(name)) return [];

        return await ReadAsync(
            $"SELECT {SkillRules.Columns} FROM skills s WHERE {NameMatch}"
            + $" ORDER BY CASE WHEN s.team IS NULL THEN 0 ELSE 1 END, {SqlServerSql.NoCaseOrder("s.team")}",
            name, null, ct);
    }

    public Task<IReadOnlyList<Skill>> ListTeamAsync(string team, CancellationToken ct = default) =>
        ReadAsync($"SELECT {SkillRules.Columns} FROM skills s WHERE {TeamMatch} ORDER BY {SqlServerSql.NoCaseOrder("s.name")}", null, team, ct);

    public async Task<Skill> PutTeamSkillAsync(
        string team, SkillDraft draft, bool replace, string? by, TriggerAudit audit, CancellationToken ct = default)
    {
        var clean = SkillRules.Validate(draft);
        SkillRules.RefuseReserved(clean.Name);

        await WriteAsync(
            async (connection, transaction, token) =>
            {
                await RefuseInstanceNameAsync(connection, transaction, clean.Name, token);

                var exists = await TeamHoldsAsync(connection, transaction, team, clean.Name, token);
                if (exists && !replace) throw SkillRules.TeamHolds(team, clean.Name);

                if (exists)
                {
                    await ExecuteAsync(
                        connection, transaction,
                        $"""
                         UPDATE s
                         SET name = @to, description = @description, roles = @roles, body = @body,
                             updated_at = @at, updated_by = @by
                         FROM skills s
                         WHERE {SqlServerSql.Same("s.name", "@to")} AND {TeamMatch}
                         """,
                        command =>
                        {
                            BindDraft(command, clean, by);
                            command.Parameters.AddWithValue("@team", team);
                        },
                        token);
                }
                else
                {
                    await InsertAsync(connection, transaction, clean, SkillRules.Custom, Instant.Stamp(DateTimeOffset.UtcNow), by, token, team: team);
                }

                await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);
                return 0;
            },
            ct);

        return (await TeamSkillAsync(team, clean.Name, ct))!;
    }

    public async Task<Skill?> UpdateTeamSkillAsync(
        string team, string name, SkillDraft draft, string? by, TriggerAudit audit, CancellationToken ct = default)
    {
        var clean = SkillRules.Validate(draft);

        var held = await WriteAsync(
            async (connection, transaction, token) =>
            {
                if (!await TeamHoldsAsync(connection, transaction, team, name, token)) return false;

                if (!string.Equals(name, clean.Name, StringComparison.OrdinalIgnoreCase))
                {
                    SkillRules.RefuseReserved(clean.Name);
                    await RefuseInstanceNameAsync(connection, transaction, clean.Name, token);

                    if (await TeamHoldsAsync(connection, transaction, team, clean.Name, token))
                    {
                        throw SkillRules.TeamHolds(team, clean.Name);
                    }
                }

                await ExecuteAsync(
                    connection, transaction,
                    $"""
                     UPDATE s
                     SET name = @to, description = @description, roles = @roles, body = @body,
                         updated_at = @at, updated_by = @by
                     FROM skills s
                     WHERE {NameMatch} AND {TeamMatch}
                     """,
                    command =>
                    {
                        BindDraft(command, clean, by);
                        command.Parameters.AddWithValue("@name", name);
                        command.Parameters.AddWithValue("@team", team);
                    },
                    token);

                await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);
                return true;
            },
            ct);

        return held ? await TeamSkillAsync(team, clean.Name, ct) : null;
    }

    public async Task<bool> DeleteTeamSkillAsync(string team, string name, TriggerAudit audit, CancellationToken ct = default)
    {
        if (!SkillRules.IsLegalName(name)) return false;

        return await WriteAsync(
            async (connection, transaction, token) =>
            {
                var deleted = await ExecuteAsync(
                    connection, transaction,
                    $"DELETE s FROM skills s WHERE {NameMatch} AND {TeamMatch}",
                    command =>
                    {
                        command.Parameters.AddWithValue("@name", name);
                        command.Parameters.AddWithValue("@team", team);
                    },
                    token);

                if (deleted == 0) return false;

                await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);
                return true;
            },
            ct);
    }

    public Task<IReadOnlyList<string>> DeleteTeamSkillsAsync(
        string team, Func<string, TriggerAudit> audit, CancellationToken ct = default) =>
        WriteAsync<IReadOnlyList<string>>(
            async (connection, transaction, token) =>
            {
                var names = new List<string>();

                await using (var select = Command(
                    connection, transaction,
                    $"SELECT s.name FROM skills s {Held} WHERE {TeamMatch} ORDER BY {SqlServerSql.NoCaseOrder("s.name")}"))
                {
                    select.Parameters.AddWithValue("@team", team);
                    await using var reader = await select.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token)) names.Add(reader.GetString(0));
                }

                await ExecuteAsync(
                    connection, transaction,
                    $"DELETE s FROM skills s WHERE {TeamMatch}",
                    command => command.Parameters.AddWithValue("@team", team),
                    token);

                foreach (var name in names) await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit(name), token);

                return names;
            },
            ct);

    public async Task<Skill> CreateCustomAsync(SkillDraft draft, string? by, CancellationToken ct = default)
    {
        var clean = SkillRules.Validate(draft);
        SkillRules.RefuseReserved(clean.Name);

        await WriteAsync(
            async (connection, transaction, token) =>
            {
                if (SkillRules.CustomNameRefusal(await KindOfAsync(connection, transaction, clean.Name, token), clean.Name) is { } taken)
                {
                    throw taken;
                }

                await RefuseTeamNameAsync(connection, transaction, clean.Name, token);

                await InsertAsync(connection, transaction, clean, SkillRules.Custom, Instant.Stamp(DateTimeOffset.UtcNow), by, token);
                return 0;
            },
            ct);

        return (await GetAsync(clean.Name, ct))!;
    }

    public async Task<Skill?> UpdateCustomAsync(
        string name, SkillDraft draft, string? by, CancellationToken ct = default)
    {
        var clean = SkillRules.Validate(draft);

        var held = await WriteAsync(
            async (connection, transaction, token) =>
            {
                switch (await KindOfAsync(connection, transaction, name, token))
                {
                    case null:
                        return false;
                    case { } kind when SkillRules.LockedRefusal(kind, name, "edited or relabelled", "edited or relabelled here") is { } locked:
                        throw locked;
                }

                if (!string.Equals(name, clean.Name, StringComparison.OrdinalIgnoreCase))
                {
                    SkillRules.RefuseReserved(clean.Name);

                    if (SkillRules.CustomNameRefusal(await KindOfAsync(connection, transaction, clean.Name, token), clean.Name) is { } taken)
                    {
                        throw taken;
                    }

                    await RefuseTeamNameAsync(connection, transaction, clean.Name, token);
                }

                await ExecuteAsync(
                    connection, transaction,
                    $"""
                     UPDATE s
                     SET name = @to, description = @description, roles = @roles, body = @body,
                         updated_at = @at, updated_by = @by
                     FROM skills s
                     WHERE {NameMatch} AND s.kind = N'custom' AND s.team IS NULL
                     """,
                    command =>
                    {
                        command.Parameters.AddWithValue("@name", name);
                        BindDraft(command, clean, by);
                    },
                    token);

                return true;
            },
            ct);

        return held ? await GetAsync(clean.Name, ct) : null;
    }

    public async Task<bool> DeleteCustomAsync(string name, CancellationToken ct = default)
    {
        if (!SkillRules.IsLegalName(name)) return false;

        return await WriteAsync(
            async (connection, transaction, token) =>
            {
                switch (await KindOfAsync(connection, transaction, name, token))
                {
                    case null:
                        return false;
                    case { } kind when SkillRules.LockedRefusal(kind, name, "deleted", "deleted") is { } locked:
                        throw locked;
                }

                await ExecuteAsync(
                    connection, transaction,
                    $"DELETE s FROM skills s WHERE {NameMatch} AND s.kind = N'custom' AND s.team IS NULL",
                    command => command.Parameters.AddWithValue("@name", name),
                    token);

                return true;
            },
            ct);
    }

    /// <summary>A team skill may not take an instance-wide skill's name, of any kind.</summary>
    private static async Task RefuseInstanceNameAsync(
        SqlConnection connection, SqlTransaction transaction, string name, CancellationToken ct)
    {
        if (SkillRules.TeamNameRefusal(await KindOfAsync(connection, transaction, name, ct), name) is { } taken)
        {
            throw taken;
        }
    }

    /// <summary>An instance-wide custom skill may not take a name a team's skill holds.</summary>
    private static async Task RefuseTeamNameAsync(
        SqlConnection connection, SqlTransaction transaction, string name, CancellationToken ct)
    {
        if ((await TeamsHoldingAsync(connection, transaction, name, ct)) is [var team, ..])
        {
            throw SkillRules.TeamHoldsInstanceName(team, name);
        }
    }

    private static void BindDraft(SqlCommand command, SkillDraft clean, string? by)
    {
        command.Parameters.AddWithValue("@to", clean.Name);
        command.Parameters.AddWithValue("@description", clean.Description);
        command.Parameters.AddWithValue("@roles", SkillRules.StoredRoles(clean.Roles));
        command.Parameters.AddWithValue("@body", clean.Body);
        command.Parameters.AddWithValue("@at", Instant.Stamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("@by", (object?)by ?? DBNull.Value);
    }

    private async Task<Skill?> TeamSkillAsync(string team, string name, CancellationToken ct) =>
        (await ReadAsync($"SELECT {SkillRules.Columns} FROM skills s WHERE {NameMatch} AND {TeamMatch}", name, team, ct))
            .SingleOrDefault();

    private static Task RenameAsync(
        SqlConnection connection, SqlTransaction transaction, string from, string to, string? team, CancellationToken ct) =>
        ExecuteAsync(
            connection, transaction,
            $"UPDATE s SET name = @to FROM skills s WHERE {NameMatch} AND "
            + (team is null ? "s.team IS NULL" : TeamMatch),
            command =>
            {
                command.Parameters.AddWithValue("@name", from);
                command.Parameters.AddWithValue("@to", to);
                if (team is not null) command.Parameters.AddWithValue("@team", team);
            },
            ct);

    private static Task InsertAsync(
        SqlConnection connection, SqlTransaction transaction, SkillDraft skill, string kind,
        string at, string? by, CancellationToken ct, string? source = null, string? team = null) =>
        ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO skills (name, kind, description, roles, body, updated_at, updated_by, source, team)
            VALUES (@name, @kind, @description, @roles, @body, @at, @by, @source, @team)
            """,
            insert =>
            {
                insert.Parameters.AddWithValue("@source", (object?)source ?? DBNull.Value);
                insert.Parameters.AddWithValue("@team", (object?)team ?? DBNull.Value);
                insert.Parameters.AddWithValue("@name", skill.Name);
                insert.Parameters.AddWithValue("@kind", kind);
                insert.Parameters.AddWithValue("@description", skill.Description);
                insert.Parameters.AddWithValue("@roles", SkillRules.StoredRoles(SkillRoles.Normalise(skill.Roles)));
                insert.Parameters.AddWithValue("@body", skill.Body);
                insert.Parameters.AddWithValue("@at", at);
                insert.Parameters.AddWithValue("@by", (object?)by ?? DBNull.Value);
            },
            ct);

    /// <summary>The kind of the INSTANCE-WIDE row named <paramref name="name"/>, or null; held.</summary>
    private static async Task<string?> KindOfAsync(
        SqlConnection connection, SqlTransaction transaction, string name, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, $"SELECT s.kind FROM skills s {Held} WHERE {NameMatch} AND s.team IS NULL");
        command.Parameters.AddWithValue("@name", name);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>The teams whose skill is named <paramref name="name"/>, in team order; held.</summary>
    private static async Task<IReadOnlyList<string>> TeamsHoldingAsync(
        SqlConnection connection, SqlTransaction transaction, string name, CancellationToken ct)
    {
        await using var command = Command(
            connection, transaction,
            $"SELECT s.team FROM skills s {Held} WHERE {NameMatch} AND s.team IS NOT NULL ORDER BY {SqlServerSql.NoCaseOrder("s.team")}");
        command.Parameters.AddWithValue("@name", name);

        var teams = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) teams.Add(reader.GetString(0));
        return teams;
    }

    /// <summary>Whether team <paramref name="team"/> has a skill named <paramref name="name"/>; held.</summary>
    private static async Task<bool> TeamHoldsAsync(
        SqlConnection connection, SqlTransaction transaction, string team, string name, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, $"SELECT 1 FROM skills s {Held} WHERE {NameMatch} AND {TeamMatch}");
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@team", team);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>Whether any row, of any scope, holds <paramref name="name"/>; held.</summary>
    private static async Task<bool> AnyHoldsAsync(
        SqlConnection connection, SqlTransaction transaction, string name, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, $"SELECT 1 FROM skills s {Held} WHERE {NameMatch}");
        command.Parameters.AddWithValue("@name", name);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<string> FreeNameAsync(
        SqlConnection connection, SqlTransaction transaction, string preferred, CancellationToken ct)
    {
        foreach (var candidate in SkillRules.FreeNames(preferred))
        {
            if (!await AnyHoldsAsync(connection, transaction, candidate, ct)) return candidate;
        }

        throw new InvalidOperationException("Unreachable: FreeNames does not end.");
    }

    private Task<IReadOnlyList<Skill>> ReadAsync(string sql, string? name, string? team, CancellationToken ct) =>
        RunAsync<IReadOnlyList<Skill>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                if (name is not null) command.Parameters.AddWithValue("@name", name);
                if (team is not null) command.Parameters.AddWithValue("@team", team);

                var list = new List<Skill>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) list.Add(SkillRules.Read(reader));
                return list;
            },
            ct);

    private static async Task<int> ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, Action<SqlCommand> bind, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, sql);
        bind(command);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    /// <summary>
    /// ONE TRANSACTION, committed when <paramref name="work"/> returns and rolled back when it throws,
    /// run whole again if SQL Server picks it as a deadlock victim. A refusal thrown inside it leaves
    /// nothing written, as SQLite's disposed transaction does.
    /// </summary>
    private Task<T> WriteAsync<T>(
        Func<SqlConnection, SqlTransaction, CancellationToken, Task<T>> work, CancellationToken ct) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);
                var result = await work(connection, transaction, token);
                await SqlServerOpen.CommitAsync(transaction, token);
                return result;
            },
            ct);

    private Task<T> RunAsync<T>(Func<SqlConnection, CancellationToken, Task<T>> work, CancellationToken ct) =>
        SqlServerOpen.RunAsync(_connectionString, work, ct);
}
