using System.Text.Json;
using Harness.Contracts;
using Microsoft.Data.Sqlite;

namespace Harness.Host;

/// <summary>Reads and writes a plugin member's settings: <see cref="IPluginMemberSettings"/> is
/// the runner's read side.</summary>
public interface IPluginMemberSettingsStore : IPluginMemberSettings
{
    Task SaveAsync(ContainerId member, PluginMemberSettings settings, CancellationToken ct = default);

    /// <summary>Saves a change a person made after hire and its <c>tenant_events</c> row IN ONE
    /// TRANSACTION: a change with no record of who made it does not land.</summary>
    Task SaveAsync(ContainerId member, PluginMemberSettings settings, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>Every logical key bound by a member currently on <paramref name="team"/> - the keys a
    /// person has already bound there, which is all a Manager may bind (see the hire route).</summary>
    Task<IReadOnlySet<string>> KeysBoundOnAsync(string team, CancellationToken ct = default);
}

/// <summary>
/// <c>team_member_config</c> (schema step auth-008): one row per plugin member, removed with its
/// <c>team_members</c> row by the foreign key. Secrets are stored as LOGICAL KEYS only.
/// </summary>
public sealed class SqlitePluginMemberSettings(string databasePath) : IPluginMemberSettingsStore
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false,
    }.ToString();

    public async Task<PluginMemberSettings> ForAsync(ContainerId member, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT config_json, secrets_json FROM team_member_config WHERE team = $team AND name = $name";
        command.Parameters.AddWithValue("$team", member.Team);
        command.Parameters.AddWithValue("$name", member.Name);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return PluginMemberSettings.None;

        return new PluginMemberSettings(
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(reader.GetString(0)) ?? [],
            JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(1)) ?? []);
    }

    public async Task SaveAsync(ContainerId member, PluginMemberSettings settings, CancellationToken ct = default)
    {
        await using var connection = Open();
        await UpsertAsync(connection, null, member, settings, ct);
    }

    public async Task SaveAsync(ContainerId member, PluginMemberSettings settings, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await UpsertAsync(connection, transaction, member, settings, ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO tenant_events
                    (occurred_at, actor_id, actor_email, action, subject, subject_name, detail)
                VALUES ($at, $actorId, $actorEmail, $action, $subject, $subjectName, $detail)
                """;
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$actorId", (object?)audit.ActorId ?? DBNull.Value);
            command.Parameters.AddWithValue("$actorEmail", (object?)audit.ActorEmail ?? DBNull.Value);
            command.Parameters.AddWithValue("$action", audit.Action);
            command.Parameters.AddWithValue("$subject", audit.Subject);
            command.Parameters.AddWithValue("$subjectName", (object?)audit.SubjectName ?? DBNull.Value);
            command.Parameters.AddWithValue("$detail", (object?)audit.Detail ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    private static async Task UpsertAsync(
        SqliteConnection connection, SqliteTransaction? transaction, ContainerId member, PluginMemberSettings settings, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO team_member_config (team, name, config_json, secrets_json)
            VALUES ($team, $name, $config, $secrets)
            ON CONFLICT (team, name) DO UPDATE SET
                config_json = excluded.config_json,
                secrets_json = excluded.secrets_json
            """;
        command.Parameters.AddWithValue("$team", member.Team);
        command.Parameters.AddWithValue("$name", member.Name);
        command.Parameters.AddWithValue("$config", JsonSerializer.Serialize(settings.Config));
        command.Parameters.AddWithValue("$secrets", JsonSerializer.Serialize(settings.Secrets));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlySet<string>> KeysBoundOnAsync(string team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT secrets_json FROM team_member_config WHERE team = $team";
        command.Parameters.AddWithValue("$team", team);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            keys.UnionWith((JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(0)) ?? []).Values);
        }

        return keys;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqlitePragmas.Apply(connection);
        return connection;
    }
}

/// <summary>
/// SECRETS FROM THE HOST'S OWN ENVIRONMENT - where every `secret set NAME` value already arrives
/// (the operator's 0600 env file, handed to the container at start). Nothing new for an operator:
/// `secret set ACME_STORAGE_KEY`, restart, and bind `ACME_STORAGE_KEY` on the member.
///
/// REFUSES WHAT A PLUGIN MUST NEVER BE POINTED AT: the platform's own `HARNESS_*` variables and
/// every model provider's credential. A later encrypted store replaces this class and nothing else.
/// </summary>
public sealed class EnvironmentSecretStore : ISecretStore
{
    public string? TryGet(string logicalKey) =>
        Refusal(logicalKey) is null && Environment.GetEnvironmentVariable(logicalKey) is { Length: > 0 } value
            ? value
            : null;

    /// <summary>Why <paramref name="logicalKey"/> cannot be bound, or null.</summary>
    public static string? Refusal(string logicalKey)
    {
        if (logicalKey.Length is 0 or > 128
            || !(char.IsAsciiLetterUpper(logicalKey[0]) || logicalKey[0] == '_')
            || !logicalKey.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_'))
        {
            return $"'{logicalKey}' is not a secret key: use upper-case letters, digits and underscores, as in ACME_STORAGE_KEY.";
        }

        if (logicalKey.StartsWith("HARNESS_", StringComparison.Ordinal))
        {
            return $"'{logicalKey}' is the platform's own and cannot be handed to a plugin.";
        }

        if (AgentEnvironment.ProviderVariables.Contains(logicalKey))
        {
            return $"'{logicalKey}' is a model provider's credential and cannot be handed to a plugin.";
        }

        return null;
    }
}
