using System.Globalization;
using System.Text.Json;
using Harness.Contracts;
using Harness.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Harness.Host;

/// <summary>What every answer says about a plugin key: whether it is set, by whom and when it was
/// stored, and which source holds it (<c>stored</c>, <c>environment</c> or null). Never the value.</summary>
public sealed record SecretStatus(bool Set, string? SetBy, DateTimeOffset? SetAt, string? Source)
{
    public const string Stored = "stored";
    public const string Environment = "environment";

    public static SecretStatus NotSet { get; } = new(false, null, null, null);
}

/// <summary>Who set or cleared a stored key: a person, named by email.</summary>
public sealed record CredentialActor(string? Id, string Email);

/// <summary>
/// <c>plugin_secrets</c> (schema step auth-025): the plugin keys a person sets from the screen, by
/// logical key. The value is Data Protection ciphertext under its own purpose and the instance's
/// <c>&lt;dataRoot&gt;/keys</c>, and every set, replace and clear lands with its <c>tenant_events</c> row in the same transaction, or not at
/// all.
///
/// RESOLVES A KEY AT EACH RUN: the stored value first, then <paramref name="environment"/> (the
/// Host's own, where the operator CLI's <c>secret set</c> values arrive), so a key set with the CLI
/// keeps working and one set from the screen reaches the next run with no restart. A key
/// <see cref="EnvironmentSecretStore.Refusal"/> refuses is never stored and never answered.
/// </summary>
public sealed class PluginSecretStore(string databasePath, IDataProtectionProvider protection, ISecretStore environment) : ISecretStore
{
    /// <summary>The longest value accepted. A key or token is a few hundred bytes.</summary>
    public const int MaxValueLength = 8 * 1024;

    private readonly IDataProtector _protector = protection.CreateProtector("Harness.PluginSecrets.v1");

    private readonly string _connectionString = SqliteOpen.ConnectionString(databasePath);

    /// <summary>
    /// Why <paramref name="value"/> cannot be stored, or null. The sentence never repeats the value
    /// or any part of it. Under <see cref="PluginMemberRunner.MinimumSecretLength"/> characters is
    /// refused, as a run refuses it: it cannot be redacted without destroying the text around it.
    /// </summary>
    public static string? ValueRefusal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "The key's value is empty.";
        if (value.Length > MaxValueLength) return $"The key's value is longer than {MaxValueLength} characters.";
        if (value.Any(c => c is '\r' or '\n' or '\0')) return "The key's value holds a line break or a NUL; it must be one line.";
        if (value != value.Trim()) return "The key's value begins or ends with white space.";
        if (value.Length < PluginMemberRunner.MinimumSecretLength)
        {
            return $"The key's value is shorter than {PluginMemberRunner.MinimumSecretLength} characters, too short to redact from a plugin's output.";
        }

        return null;
    }

    public string? TryGet(string logicalKey) =>
        EnvironmentSecretStore.Refusal(logicalKey) is not null ? null : Stored(logicalKey) ?? environment.TryGet(logicalKey);

    /// <summary>Whether <paramref name="logicalKey"/> is set and which source holds it. A stored value
    /// that no longer decrypts (a lost key ring) reads as not stored.</summary>
    public SecretStatus Status(string logicalKey)
    {
        if (EnvironmentSecretStore.Refusal(logicalKey) is not null) return SecretStatus.NotSet;

        using var connection = Open();
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT value_protected, set_by, set_at FROM plugin_secrets WHERE key = $key";
        select.Parameters.AddWithValue("$key", logicalKey);

        using (var reader = select.ExecuteReader())
        {
            if (reader.Read() && Unprotect(reader.GetString(0)) is not null)
            {
                return new SecretStatus(true, reader.GetString(1), Time(reader.GetString(2)), SecretStatus.Stored);
            }
        }

        return environment.TryGet(logicalKey) is not null
            ? new SecretStatus(true, null, null, SecretStatus.Environment)
            : SecretStatus.NotSet;
    }

    /// <summary>
    /// Stores <paramref name="value"/> for <paramref name="logicalKey"/>, with its tenant row in the
    /// same transaction: <see cref="TenantActions.SecretSet"/> when none was stored,
    /// <see cref="TenantActions.SecretReplaced"/> when one was, naming the key and never the value.
    /// Throws, storing nothing, when the row cannot be written. The caller has checked the key
    /// (<see cref="EnvironmentSecretStore.Refusal"/>) and the value (<see cref="ValueRefusal"/>).
    /// </summary>
    public async Task<SecretStatus> SetAsync(string logicalKey, string value, CredentialActor actor, CancellationToken ct = default)
    {
        var at = DateTimeOffset.UtcNow;

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        bool existed;
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT COUNT(*) FROM plugin_secrets WHERE key = $key";
            exists.Parameters.AddWithValue("$key", logicalKey);
            existed = Convert.ToInt64(await exists.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
        }

        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText =
                """
                INSERT INTO plugin_secrets (key, value_protected, set_by, set_at)
                VALUES ($key, $value, $by, $at)
                ON CONFLICT (key) DO UPDATE SET
                    value_protected = excluded.value_protected,
                    set_by = excluded.set_by,
                    set_at = excluded.set_at
                """;
            upsert.Parameters.AddWithValue("$key", logicalKey);
            upsert.Parameters.AddWithValue("$value", _protector.Protect(value));
            upsert.Parameters.AddWithValue("$by", actor.Email);
            upsert.Parameters.AddWithValue("$at", Instant.Stamp(at));
            await upsert.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, new TriggerAudit(
            actor.Id, actor.Email, existed ? TenantActions.SecretReplaced : TenantActions.SecretSet,
            logicalKey, logicalKey, JsonSerializer.Serialize(new { key = logicalKey })), ct);

        await transaction.CommitAsync(ct);

        return new SecretStatus(true, actor.Email, at, SecretStatus.Stored);
    }

    /// <summary>
    /// Clears the stored <paramref name="logicalKey"/> with its <see cref="TenantActions.SecretCleared"/>
    /// row in the same transaction; a run then reads the Host's environment for it. Nothing stored
    /// changes nothing and writes no row. Throws, clearing nothing, when the row cannot be written.
    /// </summary>
    public async Task ClearAsync(string logicalKey, CredentialActor actor, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM plugin_secrets WHERE key = $key";
            delete.Parameters.AddWithValue("$key", logicalKey);

            if (await delete.ExecuteNonQueryAsync(ct) == 0)
            {
                await transaction.RollbackAsync(ct);
                return;
            }
        }

        await TenantAuditRow.AppendAsync(connection, transaction, new TriggerAudit(
            actor.Id, actor.Email, TenantActions.SecretCleared,
            logicalKey, logicalKey, JsonSerializer.Serialize(new { key = logicalKey })), ct);

        await transaction.CommitAsync(ct);
    }

    private string? Stored(string logicalKey)
    {
        using var connection = Open();
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT value_protected FROM plugin_secrets WHERE key = $key";
        select.Parameters.AddWithValue("$key", logicalKey);

        return select.ExecuteScalar() is string protectedValue ? Unprotect(protectedValue) : null;
    }

    private string? Unprotect(string protectedValue)
    {
        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // A key ring that lost its key: the value is gone, and says so as a missing one.
            return null;
        }
    }

    private static DateTimeOffset Time(string text) => Instant.Parse(text);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqlitePragmas.Apply(connection);
        return connection;
    }
}
