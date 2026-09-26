using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Harness.Contracts;

namespace Harness.Skills;

/// <summary>
/// The skill index. Built-in rows are rebuilt from the build on every start and are never written
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
            command.Parameters.AddWithValue("$kind", kind == SkillKindFilter.BuiltIn ? "builtin" : "custom");
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
            SELECT s.id, s.name, s.description, s.roles, s.kind, s.body, s.updated_at, s.updated_by
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
            SELECT id, name, description, roles, kind, body, updated_at, updated_by
            FROM skills WHERE name = $name
            """;
        command.Parameters.AddWithValue("$name", name);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<Skill> CreateCustomAsync(SkillDraft draft, string? by, CancellationToken ct = default)
    {
        var clean = Validate(draft);

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
        }

        if (!string.Equals(name, clean.Name, StringComparison.OrdinalIgnoreCase))
        {
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

    private static async Task InsertAsync(
        SqliteConnection connection, SqliteTransaction transaction, SkillDraft skill, string kind,
        string at, string? by, CancellationToken ct)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO skills (name, kind, description, roles, body, updated_at, updated_by)
            VALUES ($name, $kind, $description, $roles, $body, $at, $by)
            """;
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
            reader.GetString(4) == "builtin" ? SkillKind.BuiltIn : SkillKind.Custom,
            reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
            reader.IsDBNull(7) ? null : reader.GetString(7));

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
