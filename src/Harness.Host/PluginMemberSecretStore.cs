using System.Globalization;
using System.Text.Json;
using Harness.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Harness.Host;

/// <summary>Who set a member's own value for a secret field, and when. Never the value.</summary>
public sealed record MemberSecretState(string SetBy, DateTimeOffset SetAt);

/// <summary>A plugin member's own secret values, read by the runner at each run.</summary>
public interface IPluginMemberSecrets
{
    /// <summary>Each field the member has its own value for, to that value. A value that no longer
    /// decrypts (a lost key ring) is absent, as a missing one.</summary>
    Task<IReadOnlyDictionary<string, string>> ValuesAsync(ContainerId member, CancellationToken ct = default);
}

/// <summary>
/// <c>plugin_member_secrets</c> (schema step auth-028): EACH PLUGIN MEMBER OWNS ITS SECRETS. A value a
/// person types for one of a member's manifest secret fields is kept for that (team, member, field)
/// only, as Data Protection ciphertext under its own purpose and the instance's
/// <c>&lt;dataRoot&gt;/keys</c>, so two members of one plugin - on one team or several, with the same
/// account or different ones - never read, change or clash with each other's values. Every set,
/// replace and clear lands with its <c>tenant_events</c> row (<see cref="TenantActions.MemberSecretSet"/>,
/// <see cref="TenantActions.MemberSecretReplaced"/>, <see cref="TenantActions.MemberSecretCleared"/>:
/// team, member, plugin and field, never the value) in the same transaction, or not at all. The rows
/// go with the member's <c>team_members</c> row, by the foreign key.
/// </summary>
public sealed class PluginMemberSecretStore(string databasePath, IDataProtectionProvider protection) : IPluginMemberSecrets
{
    /// <summary>The Data Protection purpose: its own, so no other store's ciphertext opens here.</summary>
    public const string Purpose = "Harness.PluginMemberSecrets.v1";

    private readonly IDataProtector _protector = protection.CreateProtector(Purpose);

    private readonly string _connectionString = SqliteOpen.ConnectionString(databasePath);

    /// <summary>
    /// Why <paramref name="value"/> cannot be kept for <paramref name="field"/>, or null. The sentence
    /// names the field and never repeats the value. A masked field's value is held to a stored key's
    /// rules (<see cref="PluginSecretStore.ValueRefusal"/>: it is redacted from what the plugin writes);
    /// a field the manifest declares <c>"masked": false</c> is not secret, so only one line, not empty
    /// and not too long.
    /// </summary>
    public static string? ValueRefusal(string field, string? value, bool masked)
    {
        if (masked)
        {
            return PluginSecretStore.ValueRefusal(value) is { } refused
                ? $"`{field}`: {refused.Replace("The key's value", "the value", StringComparison.Ordinal)}"
                : null;
        }

        if (string.IsNullOrWhiteSpace(value)) return $"`{field}`: the value is empty.";
        if (value.Length > PluginSecretStore.MaxValueLength) return $"`{field}`: the value is longer than {PluginSecretStore.MaxValueLength} characters.";
        if (value.Any(c => c is '\r' or '\n' or '\0')) return $"`{field}`: the value holds a line break or a NUL; it must be one line.";

        return null;
    }

    /// <summary>
    /// Why <paramref name="changes"/> cannot be kept for a member of <paramref name="manifest"/>, or
    /// null: each field must be one the manifest declares, and each value (null clears) must pass
    /// <see cref="ValueRefusal"/>.
    /// </summary>
    public static string? ChangesRefusal(PluginManifest manifest, IReadOnlyDictionary<string, string?> changes)
    {
        foreach (var (field, value) in changes)
        {
            if (!manifest.Secrets.TryGetValue(field, out var declared))
            {
                return $"`{field}` is not a secret plugin '{manifest.Id}' names. It names: "
                    + (manifest.Secrets.Count == 0 ? "none" : string.Join(", ", manifest.Secrets.Keys)) + ".";
            }

            if (value is not null && ValueRefusal(field, value, declared.Masked) is { } refused) return refused;
        }

        return null;
    }

    public async Task<IReadOnlyDictionary<string, string>> ValuesAsync(ContainerId member, CancellationToken ct = default)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        await using var connection = Open();
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT field, value_protected FROM plugin_member_secrets WHERE team = $team AND name = $name";
        select.Parameters.AddWithValue("$team", member.Team);
        select.Parameters.AddWithValue("$name", member.Name);

        await using var reader = await select.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (Unprotect(reader.GetString(1)) is { } value) values[reader.GetString(0)] = value;
        }

        return values;
    }

    /// <summary>Who set each field the member has a value for, and when. A value that no longer
    /// decrypts reads as not set.</summary>
    public async Task<IReadOnlyDictionary<string, MemberSecretState>> StatesAsync(ContainerId member, CancellationToken ct = default)
    {
        var states = new Dictionary<string, MemberSecretState>(StringComparer.Ordinal);

        await using var connection = Open();
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT field, value_protected, set_by, set_at FROM plugin_member_secrets WHERE team = $team AND name = $name";
        select.Parameters.AddWithValue("$team", member.Team);
        select.Parameters.AddWithValue("$name", member.Name);

        await using var reader = await select.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (Unprotect(reader.GetString(1)) is not null)
            {
                states[reader.GetString(0)] = new MemberSecretState(reader.GetString(2), Instant.Parse(reader.GetString(3)));
            }
        }

        return states;
    }

    /// <summary>Sets or replaces one field's value with its row, in one transaction. Throws, changing
    /// nothing, when the row cannot be written. The caller has checked the value.</summary>
    public Task SetAsync(ContainerId member, string label, string plugin, string field, string value, CredentialActor actor, CancellationToken ct = default) =>
        ChangeAsync(member, label, plugin, new Dictionary<string, string?> { [field] = value }, actor, ct);

    /// <summary>Clears one field's value with its row, in one transaction. Nothing stored changes
    /// nothing and writes no row.</summary>
    public Task ClearAsync(ContainerId member, string label, string plugin, string field, CredentialActor actor, CancellationToken ct = default) =>
        ChangeAsync(member, label, plugin, new Dictionary<string, string?> { [field] = null }, actor, ct);

    /// <summary>Every change (a value sets or replaces, null clears) and its rows, in one transaction.</summary>
    public async Task ChangeAsync(
        ContainerId member, string label, string plugin, IReadOnlyDictionary<string, string?> changes, CredentialActor actor, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await ApplyAsync(connection, transaction, member, label, plugin, changes, actor, ct);

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// A SETTINGS SAVE AND ITS VALUES TOGETHER: the member's settings row, every given tenant row and
    /// every value change with its own row, in ONE transaction, or nothing. What a person's Save, a
    /// person's hire and a solution's install write.
    /// </summary>
    public async Task SaveWithSettingsAsync(
        ContainerId member, string label, string plugin, PluginMemberSettings settings, IReadOnlyList<TriggerAudit> audits,
        IReadOnlyDictionary<string, string?> changes, CredentialActor actor, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await SqlitePluginMemberSettings.UpsertAsync(connection, transaction, member, settings, ct);
        foreach (var audit in audits) await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await ApplyAsync(connection, transaction, member, label, plugin, changes, actor, ct);

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// THE ONE-TIME START STEP (B006R): for every plugin member whose secret field is bound to a key
    /// with a STORED value (<c>plugin_secrets</c>) and that has no value of its own for the field, that
    /// value is copied onto the member, and the step is marked done by its
    /// <see cref="TenantActions.MemberSecretsCopied"/> row (counts only) in the same transaction. The
    /// binding and the stored key stay, so the operator CLI and the environment still work. A key set
    /// only in the Host's environment is never copied: the Host never moves an environment value into
    /// the database. Answers false, copying nothing, once the row is there.
    /// </summary>
    public async Task<bool> CopyStoredKeysOnceAsync(IDataProtectionProvider keys, CancellationToken ct = default)
    {
        var keyProtector = keys.CreateProtector(PluginSecretStore.Purpose);

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var done = connection.CreateCommand())
        {
            done.Transaction = transaction;
            done.CommandText = "SELECT COUNT(*) FROM tenant_events WHERE action = $action";
            done.Parameters.AddWithValue("$action", TenantActions.MemberSecretsCopied);
            if (Convert.ToInt64(await done.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0)
            {
                await transaction.RollbackAsync(ct);
                return false;
            }
        }

        var stored = new Dictionary<string, (string Value, string SetBy)>(StringComparer.Ordinal);
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT key, value_protected, set_by FROM plugin_secrets";
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                try
                {
                    stored[reader.GetString(0)] = (keyProtector.Unprotect(reader.GetString(1)), reader.GetString(2));
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // A key ring that lost its key: nothing to copy for that key.
                }
            }
        }

        var bindings = new List<(string Team, string Name, string Field, string Key)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT team, name, secrets_json FROM team_member_config";
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                foreach (var (field, key) in JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(2)) ?? [])
                {
                    bindings.Add((reader.GetString(0), reader.GetString(1), field, key));
                }
            }
        }

        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keysCopied = new HashSet<string>(StringComparer.Ordinal);
        var fields = 0;
        var at = Instant.Stamp(DateTimeOffset.UtcNow);

        foreach (var (team, name, field, key) in bindings)
        {
            if (!stored.TryGetValue(key, out var value)) continue;

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO plugin_member_secrets (team, name, field, value_protected, set_by, set_at)
                VALUES ($team, $name, $field, $value, $by, $at)
                ON CONFLICT (team, name, field) DO NOTHING
                """;
            insert.Parameters.AddWithValue("$team", team);
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$field", field);
            insert.Parameters.AddWithValue("$value", _protector.Protect(value.Value));
            insert.Parameters.AddWithValue("$by", value.SetBy);
            insert.Parameters.AddWithValue("$at", at);

            if (await insert.ExecuteNonQueryAsync(ct) > 0)
            {
                fields++;
                members.Add($"{team}/{name}");
                keysCopied.Add(key);
            }
        }

        await TenantAuditRow.AppendAsync(connection, transaction, new TriggerAudit(
            null, null, TenantActions.MemberSecretsCopied, null, null,
            JsonSerializer.Serialize(new { members = members.Count, fields, keys = keysCopied.Count })), ct);

        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task ApplyAsync(
        SqliteConnection connection, SqliteTransaction transaction, ContainerId member, string label, string plugin,
        IReadOnlyDictionary<string, string?> changes, CredentialActor actor, CancellationToken ct)
    {
        var at = Instant.Stamp(DateTimeOffset.UtcNow);

        foreach (var (field, value) in changes.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            string action;

            if (value is null)
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM plugin_member_secrets WHERE team = $team AND name = $name AND field = $field";
                Bind(delete, member, field);

                // Nothing stored: nothing changes and no row.
                if (await delete.ExecuteNonQueryAsync(ct) == 0) continue;
                action = TenantActions.MemberSecretCleared;
            }
            else
            {
                bool existed;
                await using (var exists = connection.CreateCommand())
                {
                    exists.Transaction = transaction;
                    exists.CommandText = "SELECT COUNT(*) FROM plugin_member_secrets WHERE team = $team AND name = $name AND field = $field";
                    Bind(exists, member, field);
                    existed = Convert.ToInt64(await exists.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
                }

                await using var upsert = connection.CreateCommand();
                upsert.Transaction = transaction;
                upsert.CommandText =
                    """
                    INSERT INTO plugin_member_secrets (team, name, field, value_protected, set_by, set_at)
                    VALUES ($team, $name, $field, $value, $by, $at)
                    ON CONFLICT (team, name, field) DO UPDATE SET
                        value_protected = excluded.value_protected,
                        set_by = excluded.set_by,
                        set_at = excluded.set_at
                    """;
                Bind(upsert, member, field);
                upsert.Parameters.AddWithValue("$value", _protector.Protect(value));
                upsert.Parameters.AddWithValue("$by", actor.Email);
                upsert.Parameters.AddWithValue("$at", at);
                await upsert.ExecuteNonQueryAsync(ct);

                action = existed ? TenantActions.MemberSecretReplaced : TenantActions.MemberSecretSet;
            }

            await TenantAuditRow.AppendAsync(connection, transaction, new TriggerAudit(
                actor.Id, actor.Email, action, $"{member.Team}/{member.Name}", label,
                JsonSerializer.Serialize(new { team = member.Team, member = member.Name, plugin, field })), ct);
        }
    }

    private static void Bind(SqliteCommand command, ContainerId member, string field)
    {
        command.Parameters.AddWithValue("$team", member.Team);
        command.Parameters.AddWithValue("$name", member.Name);
        command.Parameters.AddWithValue("$field", field);
    }

    private string? Unprotect(string protectedValue)
    {
        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
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
/// The one-time start step of <see cref="PluginMemberSecretStore.CopyStoredKeysOnceAsync"/>, run as the
/// Host starts. A copy that fails is logged and tried again at the next start: until then every member
/// still resolves its bound key exactly as before.
/// </summary>
public sealed class MemberSecretsCopyAtStart(
    PluginMemberSecretStore store, IDataProtectionProvider keys, ILogger<MemberSecretsCopyAtStart> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await store.CopyStoredKeysOnceAsync(keys, cancellationToken);
        }
        catch (SqliteException exception)
        {
            logger.LogWarning(exception, "The stored plugin keys were not copied onto their members; the next start tries again.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// WHAT EVERY ANSWER SAYS OF A MEMBER'S SECRET FIELDS, one entry per manifest field in the manifest's
/// order: <c>field</c>, <c>label</c>, <c>masked</c>, <c>required</c>, <c>needed</c>, <c>set</c>,
/// <c>setBy</c>, <c>setAt</c>, <c>source</c> (<see cref="Member"/>, <see cref="Key"/>,
/// <see cref="Environment"/> or null) and <c>key</c>, the bound key's NAME or null - and <c>value</c>
/// only for a field the manifest declares <c>"masked": false</c>. Never a masked value.
/// </summary>
public sealed class MemberSecretReads(PluginMemberSecretStore members, PluginSecretStore keys, Marketplace? marketplace = null)
{
    public const string Member = "member";
    public const string Key = "key";
    public const string Environment = SecretStatus.Environment;

    public string? MarketplacePage(string pluginId) => marketplace?.PluginPage(pluginId);

    public async Task<IReadOnlyList<Dictionary<string, object?>>> StatesAsync(
        ContainerId id, PluginManifest manifest, PluginMemberSettings stored, CancellationToken ct)
    {
        var own = await members.StatesAsync(id, ct);
        var values = manifest.Secrets.Values.Any(s => !s.Masked) ? await members.ValuesAsync(id, ct) : null;

        return [.. manifest.Secrets.Select(s => Entry(s.Key, s.Value, manifest, stored, own, values))];
    }

    public async Task<Dictionary<string, object?>> StateAsync(
        ContainerId id, PluginManifest manifest, PluginMemberSettings stored, string field, CancellationToken ct) =>
        (await StatesAsync(id, manifest, stored, ct)).Single(e => (string)e["field"]! == field);

    private Dictionary<string, object?> Entry(
        string field, PluginSecret secret, PluginManifest manifest, PluginMemberSettings stored,
        IReadOnlyDictionary<string, MemberSecretState> own, IReadOnlyDictionary<string, string>? values)
    {
        var key = stored.Secrets.GetValueOrDefault(field);
        bool set;
        string? setBy = null, source = null, value = null;
        DateTimeOffset? setAt = null;

        if (own.TryGetValue(field, out var mine))
        {
            (set, setBy, setAt, source) = (true, mine.SetBy, mine.SetAt, Member);
            if (!secret.Masked) value = values?.GetValueOrDefault(field);
        }
        else if (key is not null && keys.Status(key) is { Set: true } status)
        {
            (set, setBy, setAt) = (true, status.SetBy, status.SetAt);
            source = status.Source == SecretStatus.Stored ? Key : Environment;
            if (!secret.Masked) value = keys.TryGet(key);
        }
        else
        {
            set = false;
        }

        var entry = new Dictionary<string, object?>
        {
            ["field"] = field,
            ["label"] = secret.Label,
            ["masked"] = secret.Masked,
            ["required"] = secret.Required,
            ["needed"] = Needed(secret, manifest, stored),
            ["set"] = set,
            ["setBy"] = setBy,
            ["setAt"] = setAt,
            ["source"] = source,
            ["key"] = key,
        };

        // A VALUE ONLY WHERE THE PLUGIN DECLARED IT NOT SECRET.
        if (!secret.Masked && value is not null) entry["value"] = value;

        return entry;
    }

    private static bool Needed(PluginSecret secret, PluginManifest manifest, PluginMemberSettings stored)
    {
        if (secret.When is not { } when) return true;

        JsonElement? current = stored.Config.TryGetValue(when.Setting, out var chosen)
            ? chosen
            : manifest.Config.GetValueOrDefault(when.Setting)?.Default;

        return when.HoldsIn(current);
    }
}
