using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Harness.Contracts;

namespace Harness.Skills;

/// <summary>
/// The skill index. Built-in rows are rebuilt from the build on every start and plugin rows from
/// each installed plugin whenever the plugins are loaded or rescanned, and neither is written
/// otherwise; custom rows are the person's and exist only here.
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
                rename.CommandText = "UPDATE skills SET name = $to WHERE name = $from";
                rename.Parameters.AddWithValue("$from", skill.Name);
                rename.Parameters.AddWithValue("$to", free);
                await rename.ExecuteNonQueryAsync(ct);

                moved.Add($"{skill.Name} -> {free}");
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
                        rename.CommandText = "UPDATE skills SET name = $to WHERE name = $from";
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

        command.CommandText =
            $"""
            SELECT s.id, s.name, s.description, s.roles, s.kind, s.body, s.updated_at, s.updated_by, s.source
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

    public async Task<Skill?> GetAsync(string name, CancellationToken ct = default)
    {
        if (!IsLegalName(name)) return null;

        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, name, description, roles, kind, body, updated_at, updated_by, source
            FROM skills WHERE name = $name
            """;
        command.Parameters.AddWithValue("$name", name);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
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
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE skills
                SET name = $to, description = $description, roles = $roles, body = $body,
                    updated_at = $at, updated_by = $by
                WHERE name = $name AND kind = 'custom'
                """;
            update.Parameters.AddWithValue("$name", name);
            update.Parameters.AddWithValue("$to", clean.Name);
            update.Parameters.AddWithValue("$description", clean.Description);
            update.Parameters.AddWithValue("$roles", string.Join(' ', clean.Roles));
            update.Parameters.AddWithValue("$body", clean.Body);
            update.Parameters.AddWithValue("$at", Stamp(DateTimeOffset.UtcNow));
            update.Parameters.AddWithValue("$by", (object?)by ?? DBNull.Value);
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
            delete.CommandText = "DELETE FROM skills WHERE name = $name AND kind = 'custom'";
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

    private static async Task InsertAsync(
        SqliteConnection connection, SqliteTransaction transaction, SkillDraft skill, string kind,
        string at, string? by, CancellationToken ct, string? source = null)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO skills (name, kind, description, roles, body, updated_at, updated_by, source)
            VALUES ($name, $kind, $description, $roles, $body, $at, $by, $source)
            """;
        insert.Parameters.AddWithValue("$source", (object?)source ?? DBNull.Value);
        insert.Parameters.AddWithValue("$name", skill.Name);
        insert.Parameters.AddWithValue("$kind", kind);
        insert.Parameters.AddWithValue("$description", skill.Description);
        insert.Parameters.AddWithValue("$roles", string.Join(' ', SkillRoles.Normalise(skill.Roles)));
        insert.Parameters.AddWithValue("$body", skill.Body);
        insert.Parameters.AddWithValue("$at", at);
        insert.Parameters.AddWithValue("$by", (object?)by ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string?> KindOfAsync(
        SqliteConnection connection, SqliteTransaction transaction, string name, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT kind FROM skills WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<string> FreeNameAsync(
        SqliteConnection connection, SqliteTransaction transaction, string preferred, CancellationToken ct)
    {
        var candidate = preferred.Length > 48 ? preferred[..48] : preferred;

        for (var suffix = 2; await KindOfAsync(connection, transaction, candidate, ct) is not null; suffix++)
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
            reader.IsDBNull(8) ? null : reader.GetString(8));

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
