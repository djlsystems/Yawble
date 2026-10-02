using System.Globalization;
using System.Text.Json;
using Harness.Contracts;
using Harness.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Harness.Host;

/// <summary>What every answer says about an issued credential: whether one is set, by whom, when. Never the value.</summary>
public sealed record CredentialStatus(bool Set, string? SetBy, DateTimeOffset? SetAt)
{
    public static CredentialStatus NotSet { get; } = new(false, null, null);
}

/// <summary>Who set or cleared a credential: a person, or the operator CLI through its request file.</summary>
public sealed record CredentialActor(string? Id, string Email)
{
    /// <summary>The operator CLI. No person is signed in at the engine; the row says who it was.</summary>
    public static CredentialActor Operator { get; } = new(null, "operator");
}

/// <summary>
/// <c>agent_credentials</c> (schema step auth-019): the credential issued to each agent CLI, by
/// command. The value is Data Protection ciphertext under its own purpose and the instance's
/// <c>&lt;dataRoot&gt;/keys</c> - the way <see cref="ConnectionStore"/> keeps a client secret - and
/// every set, replace and clear lands with its <c>tenant_events</c> row in the same transaction, or
/// not at all. No method answers the value except <see cref="RevealAsync"/>, which only the run
/// credential resolver calls.
/// </summary>
public sealed class AgentCredentialStore(string databasePath, IDataProtectionProvider protection)
{
    /// <summary>The longest value accepted. A key or token is a few hundred bytes.</summary>
    public const int MaxValueLength = 8 * 1024;

    private readonly IDataProtector _protector = protection.CreateProtector("Harness.AgentCredentials.v1");

    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false,
    }.ToString();

    /// <summary>
    /// Why <paramref name="value"/> cannot be stored for <paramref name="kind"/>, or null. The
    /// sentence never repeats the value or any part of it.
    /// </summary>
    public static string? ValueRefusal(string? value, IssuedCredentialKind kind)
    {
        if (string.IsNullOrWhiteSpace(value)) return "The credential is empty.";
        if (value.Length > MaxValueLength) return $"The credential is longer than {MaxValueLength} characters.";
        if (value.Any(c => c is '\r' or '\n' or '\0')) return "The credential holds a line break or a NUL; it must be one line.";
        if (value != value.Trim()) return "The credential begins or ends with white space.";

        if (kind.RefusedPrefixes?.FirstOrDefault(p => value.StartsWith(p, StringComparison.Ordinal)) is { } refused)
        {
            return $"This CLI refuses to start with a credential beginning `{refused}` in {kind.Variable}, so it is not stored.";
        }

        return null;
    }

    /// <summary>Whether a credential is set for <paramref name="command"/>, and by whom and when.
    /// A value that no longer decrypts (a lost key ring) reads as not set.</summary>
    public async Task<CredentialStatus> StatusAsync(string command, CancellationToken ct = default) =>
        (await AllStatusAsync(ct)).GetValueOrDefault(command) ?? CredentialStatus.NotSet;

    /// <summary>Every command with a credential row, by command (case-insensitive).</summary>
    public async Task<IReadOnlyDictionary<string, CredentialStatus>> AllStatusAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT command, value_protected, set_by, set_at FROM agent_credentials";

        var all = new Dictionary<string, CredentialStatus>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            all[reader.GetString(0)] = Unprotect(reader.GetString(1)) is null
                ? CredentialStatus.NotSet
                : new CredentialStatus(true, reader.GetString(2), Time(reader.GetString(3)));
        }

        return all;
    }

    /// <summary>The kind and value issued to <paramref name="command"/>, decrypted for a run, or null.</summary>
    internal async Task<(string Kind, string Value)?> RevealAsync(string command, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT kind, value_protected FROM agent_credentials WHERE command = $command";
        select.Parameters.AddWithValue("$command", command);

        await using var reader = await select.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return Unprotect(reader.GetString(1)) is { } value ? (reader.GetString(0), value) : null;
    }

    /// <summary>
    /// Sets or replaces <paramref name="command"/>'s credential, with its tenant row in the same
    /// transaction: <see cref="TenantActions.AgentCredentialSet"/> when none was set,
    /// <see cref="TenantActions.AgentCredentialReplaced"/> when one was. Throws, storing nothing,
    /// when the row cannot be written. The caller has checked the value (<see cref="ValueRefusal"/>).
    /// </summary>
    public async Task<CredentialStatus> SetAsync(
        string command, IssuedCredentialKind kind, string value, CredentialActor actor, CancellationToken ct = default)
    {
        var at = DateTimeOffset.UtcNow;

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        bool existed;
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT COUNT(*) FROM agent_credentials WHERE command = $command";
            exists.Parameters.AddWithValue("$command", command);
            existed = Convert.ToInt64(await exists.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
        }

        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText =
                """
                INSERT INTO agent_credentials (command, kind, value_protected, set_by, set_at)
                VALUES ($command, $kind, $value, $by, $at)
                ON CONFLICT (command) DO UPDATE SET
                    kind = excluded.kind,
                    value_protected = excluded.value_protected,
                    set_by = excluded.set_by,
                    set_at = excluded.set_at
                """;
            upsert.Parameters.AddWithValue("$command", command);
            upsert.Parameters.AddWithValue("$kind", kind.Kind);
            upsert.Parameters.AddWithValue("$value", _protector.Protect(value));
            upsert.Parameters.AddWithValue("$by", actor.Email);
            upsert.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));
            await upsert.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, new TriggerAudit(
            actor.Id, actor.Email,
            existed ? TenantActions.AgentCredentialReplaced : TenantActions.AgentCredentialSet,
            command, command,
            JsonSerializer.Serialize(new { command, kind = kind.Kind, variable = kind.Variable })), ct);

        await transaction.CommitAsync(ct);

        return new CredentialStatus(true, actor.Email, at);
    }

    /// <summary>
    /// Clears <paramref name="command"/>'s credential with its
    /// <see cref="TenantActions.AgentCredentialCleared"/> row in the same transaction. Nothing set
    /// changes nothing and writes no row. Throws, clearing nothing, when the row cannot be written.
    /// </summary>
    public async Task ClearAsync(string command, CredentialActor actor, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM agent_credentials WHERE command = $command";
            delete.Parameters.AddWithValue("$command", command);

            if (await delete.ExecuteNonQueryAsync(ct) == 0)
            {
                await transaction.RollbackAsync(ct);
                return;
            }
        }

        await TenantAuditRow.AppendAsync(connection, transaction, new TriggerAudit(
            actor.Id, actor.Email, TenantActions.AgentCredentialCleared, command, command,
            JsonSerializer.Serialize(new { command })), ct);

        await transaction.CommitAsync(ct);
    }

    private string? Unprotect(string protectedValue)
    {
        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // A key ring that lost its key: the credential is gone, and says so as a missing one.
            return null;
        }
    }

    private static DateTimeOffset Time(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqlitePragmas.Apply(connection);
        return connection;
    }
}
