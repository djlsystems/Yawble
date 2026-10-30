using System.Globalization;
using Harness.Contracts;
using Harness.Host.Solutions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Harness.Host;

/// <summary>
/// ONE SLOT'S MOVE FOR ONE MEMBER, sealed, as <see cref="IPluginSecretMoveRows"/> writes it in one
/// transaction: the API-key connection made from the member's own values (its masked values already
/// ciphertext, <paramref name="SealedSecrets"/>), the slot it binds, and the member's own fields it
/// removes, each with the ciphertext read before the transaction (<paramref name="Fields"/>), so a value
/// a person changed meanwhile is never removed unmoved. <paramref name="Audits"/> makes the tenant rows
/// from the binding key chosen inside the transaction: they name fields, never a value.
/// </summary>
public sealed record SecretMove(
    ContainerId Member,
    string Slot,
    bool Multiple,
    ConnectionRecord Connection,
    object SealedSecrets,
    string? ConnectedBy,
    IReadOnlyDictionary<string, string> Fields,
    Func<string, IReadOnlyList<TriggerAudit>> Audits);

/// <summary>A member's own value changed, or went, between the read and the move's transaction: the
/// move is rolled back and nothing of it lands.</summary>
public sealed class SecretMoveChangedException(string field)
    : InvalidOperationException($"The member's own value for `{field}` changed while it was being moved, so nothing was moved.");

/// <summary>
/// THE ROWS A SECRET MOVE WRITES (CON 7), and nothing else: in ONE transaction, the connection and its
/// sealed values, the member's settings row with the new binding (which assigns the connection to the
/// member's team), the removal of the member's own values for the moved fields, and every tenant row.
/// Anything failing rolls back all of it.
/// </summary>
public interface IPluginSecretMoveRows
{
    /// <summary>The move, committed, answering the binding key it bound (<c>&lt;slot&gt;</c> or, for a
    /// multiple slot, its first free <c>&lt;slot&gt;#n</c>); null, with nothing written, when the slot
    /// takes one connection and already has it. Throws <see cref="SecretMoveChangedException"/>,
    /// <see cref="ConnectionNameTakenException"/> or a database fault, writing nothing.</summary>
    Task<string?> MoveAsync(SecretMove move, CancellationToken ct = default);
}

/// <summary>What one slot's move came to for one member.</summary>
/// <param name="Outcome"><see cref="PluginSecretMoves.Moved"/>, <see cref="PluginSecretMoves.Nothing"/>
/// (no own value: no connection, the slot unbound), <see cref="PluginSecretMoves.Kept"/> (the slot was
/// already bound: the own values stay) or <see cref="PluginSecretMoves.Failed"/> (rolled back).</param>
public sealed record SecretMoveResult(
    ContainerId Member, string Slot, string Outcome, IReadOnlyList<string> Fields, string? ConnectionId = null, string? Key = null,
    string? Reason = null);

/// <summary>
/// A PLUGIN UPDATE MOVES A MEMBER'S OWN SECRET INTO A CONNECTION (CON 7). Run once a version whose
/// manifest declares <c>secrets.&lt;field&gt;.movedTo</c> is active - a plugin install or Replace (the
/// route and the operator CLI's request), and a solution install or update once its run has succeeded -
/// for every member hired on the plugin, on every team:
///
/// <list type="bullet">
/// <item>For each slot fields move to, the member's own values for those fields (several may move into
/// one slot, as Namecheap's <c>apiUser</c> and <c>apiKey</c>) become ONE API-key connection, named after
/// the team and the member, its fields named as the plugin's, assigned to the team, bound to the slot
/// (a multiple slot's first free key), and the own values removed - in one transaction
/// (<see cref="IPluginSecretMoveRows"/>), or nothing.</item>
/// <item>A field with no own value makes nothing: the slot stays unbound and, required, reads Needs you.</item>
/// <item>A slot that takes one connection and ALREADY HAS ONE is not overwritten, and the member KEEPS
/// its own values for the fields: nothing is lost, and a person clears them when the bound connection
/// is the one meant. A <see cref="TenantActions.MemberSecretMoveKept"/> row names the fields.</item>
/// </list>
///
/// Running it again moves nothing twice: a moved value is gone, and a kept one meets the same binding.
/// A failed move is said in a <see cref="TenantActions.MemberSecretMoveFailed"/> row and does not undo
/// the install. No row, answer or log line holds a value.
/// </summary>
public sealed class PluginSecretMoves(
    PluginCatalog plugins, TeamRegistry teams, IPluginMemberSettingsStore memberSettings, IPluginMemberSecretRows secretRows,
    IPluginSecretMoveRows rows, Connections connections, IDataProtectionProvider protection, PluginSetup? setup = null)
{
    public const string Moved = "moved";

    public const string Nothing = "nothing";

    public const string Kept = "kept";

    public const string Failed = "failed";

    private readonly IDataProtector _memberProtector = protection.CreateProtector(PluginMemberSecretStore.Purpose);

    private readonly ConnectionCipher _cipher = new(protection);

    /// <summary>Every move the active version of <paramref name="pluginId"/> declares, for every member
    /// hired on it. None when it declares none.</summary>
    public async Task<IReadOnlyList<SecretMoveResult>> MoveAsync(string pluginId, SolutionActor actor, CancellationToken ct = default)
    {
        if (plugins.For(pluginId)?.Manifest is not { } manifest) return [];

        var targets = manifest.Secrets
            .Where(s => s.Value.MovedTo is not null && manifest.Connections.ContainsKey(s.Value.MovedTo))
            .GroupBy(s => s.Value.MovedTo!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        if (targets.Count == 0) return [];

        var results = new List<SecretMoveResult>();

        foreach (var (team, member) in teams.All()
            .SelectMany(team => team.Containers.Select(member => (team, member)))
            .Where(pair => MemberRef.IsPlugin(pair.member.Agent, out var hired) && hired == pluginId))
        {
            var id = new ContainerId(team.Id, member.Id);
            var own = (await secretRows.RowsAsync(id, ct)).ToDictionary(r => r.Field, StringComparer.Ordinal);

            foreach (var target in targets)
            {
                var fields = target.Select(s => s.Key).Order(StringComparer.Ordinal).ToList();
                results.Add(await MoveSlotAsync(
                    manifest, id, team.Name, member.Name, target.Key, fields, own, targets.Count > 1, actor, ct));
            }

            // NEEDS YOU, read again: a slot left unbound by a field with no value reads it at once.
            if (setup is not null) await setup.RefreshAsync(id, ct);
        }

        return results;
    }

    private async Task<SecretMoveResult> MoveSlotAsync(
        PluginManifest manifest, ContainerId member, string teamName, string memberName, string slot, IReadOnlyList<string> fields,
        IReadOnlyDictionary<string, MemberSecretRow> own, bool severalSlots, SolutionActor actor, CancellationToken ct)
    {
        var subject = $"{member.Team}/{member.Name}";

        // ONLY A VALUE THAT OPENS MOVES: one that no longer decrypts reads as not set, as everywhere.
        var values = new Dictionary<string, (string Value, string Sealed)>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (own.TryGetValue(field, out var row) && Open(row.ValueProtected) is { } value) values[field] = (value, row.ValueProtected);
        }

        if (values.Count == 0) return new SecretMoveResult(member, slot, Nothing, fields);

        var moved = values.Keys.Order(StringComparer.Ordinal).ToList();
        var declared = manifest.Connections[slot];

        try
        {
            // ALREADY BOUND, a slot that takes one: not overwritten, and the own values stay. The
            // transaction reads the binding again, for a person's save in between.
            if (PluginSecretMoves.FreeKey((await memberSettings.ForAsync(member, ct)).Connections, slot, declared.Multiple) is null)
            {
                await connections.Store.RecordAsync(actor.Row(TenantActions.MemberSecretMoveKept, subject, memberName,
                    new { team = member.Team, member = member.Name, plugin = manifest.Id, slot, fields = moved }), ct);
                return new SecretMoveResult(member, slot, Kept, moved);
            }

            // THE CONNECTION'S FORM: a preset holding every moved field, masked as the manifest says -
            // exactly these first, else the one with the fewest fields over (Namecheap's apiUser and apiKey,
            // without its clientIp) - else Other with the plugin's own field names. A preset with fields over
            // is taken only for two or more of its fields: one shared name such as apiKey says nothing of
            // which service it is.
            var preset = CredentialPresets.All
                    .Where(p => p.Kind == ConnectionKinds.ApiKey && !p.NamedFields
                        && (p.Fields.Count == moved.Count || moved.Count > 1)
                        && moved.All(m => p.Fields.Any(f => f.Name == m && f.Masked == manifest.Secrets[m].Masked)))
                    .MinBy(p => p.Fields.Count)
                ?? CredentialPresets.For(ConnectionKinds.ApiKey, CredentialPresets.Other)!;

            // A REQUIRED FIELD THE MEMBER DOES NOT HOLD is left empty, never made up: the connection reads
            // Needs you, as a refused test does, until a person fills it in and saves it.
            var unheld = preset.Fields.Where(f => f.Required && !values.ContainsKey(f.Name)).ToList();
            var form = unheld.Count == 0 ? preset
                : preset with { Fields = [.. preset.Fields.Select(f => unheld.Contains(f) ? f with { Required = false } : f)] };
            var needsYou = unheld.Count == 0 ? null : string.Join(" ", unheld.Select(f => $"`{f.Name}` ({f.Label}) is required."));

            var (settings, secrets, _, refusal) = CredentialForm.Read(
                form, [.. moved.Select(f => new CredentialFieldValue(f, values[f].Value, manifest.Secrets[f].Masked))]);
            if (refusal is not null) return await FailAsync(member, memberName, manifest, slot, moved, refusal, actor, ct);

            var name = await connections.UniqueNameAsync(severalSlots ? $"{teamName} - {memberName} - {slot}" : $"{teamName} - {memberName}", ct);
            var account = settings!.Fields.FirstOrDefault(f => !f.Masked && f.Value is { Length: > 0 })?.Value ?? manifest.Id;
            var record = new ConnectionRecord(
                "conn-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
                name, ConnectionKinds.ApiKey, account, [], DateTimeOffset.UtcNow, null,
                needsYou is null ? ConnectionRecord.Ok : ConnectionRecord.NeedsReconnect, needsYou, Credential: settings);

            IReadOnlyList<TriggerAudit> Audits(string key) =>
            [
                actor.Row(TenantActions.ConnectionConnected, record.Id, record.Name, new
                {
                    connection = record.Id, provider = ConnectionKinds.ApiKey, preset = preset.Id, account, fields = moved,
                    movedFrom = new { team = member.Team, member = member.Name, plugin = manifest.Id },
                }),
                actor.Row(TenantActions.TeamConnectionAssigned, member.Team, teamName,
                    new { team = member.Team, connection = record.Id, name = record.Name, kind = ConnectionKinds.ApiKey }),
                actor.Row(TenantActions.MemberConnectionsChanged, subject, memberName,
                    new { plugin = manifest.Id, slots = new[] { key }, connection = record.Id }),
                .. moved.Select(field => actor.Row(TenantActions.MemberSecretMoved, subject, memberName,
                    new { team = member.Team, member = member.Name, plugin = manifest.Id, field, slot, key, connection = record.Id })),
            ];

            var bound = await rows.MoveAsync(new SecretMove(
                member, slot, declared.Multiple, record, ConnectionRows.SealSecrets(_cipher, secrets!), actor.UserId,
                moved.ToDictionary(f => f, f => values[f].Sealed, StringComparer.Ordinal), Audits), ct);

            if (bound is null)
            {
                await connections.Store.RecordAsync(actor.Row(TenantActions.MemberSecretMoveKept, subject, memberName,
                    new { team = member.Team, member = member.Name, plugin = manifest.Id, slot, fields = moved }), ct);
                return new SecretMoveResult(member, slot, Kept, moved);
            }

            return new SecretMoveResult(member, slot, Moved, moved, record.Id, bound);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // THE REASON NAMES FIELDS AND A FAULT CODE, never a value: an exception's own text may quote one.
            var reason = exception switch
            {
                SecretMoveChangedException or ConnectionNameTakenException => exception.Message,
                System.Data.Common.DbException db => $"The database refused the move ({DatabaseFault.Code(db)}).",
                _ => $"The move failed ({exception.GetType().Name}).",
            };

            return await FailAsync(member, memberName, manifest, slot, moved, reason, actor, ct);
        }
    }

    private async Task<SecretMoveResult> FailAsync(
        ContainerId member, string memberName, PluginManifest manifest, string slot, IReadOnlyList<string> fields, string reason,
        SolutionActor actor, CancellationToken ct)
    {
        Console.WriteLine($"WARNING: {member}'s own values for {string.Join(", ", fields)} were not moved into slot `{slot}`: {reason}");

        try
        {
            await connections.Store.RecordAsync(actor.Row(TenantActions.MemberSecretMoveFailed, $"{member.Team}/{member.Name}", memberName,
                new { team = member.Team, member = member.Name, plugin = manifest.Id, slot, fields, reason }), ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The warning above stands; the member keeps its values either way.
        }

        return new SecretMoveResult(member, slot, Failed, fields, Reason: reason);
    }

    private string? Open(string sealedValue)
    {
        try
        {
            return _memberProtector.Unprotect(sealedValue);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The key a move binds in <paramref name="bound"/>: the slot itself when free; for a
    /// multiple slot, the first of <c>&lt;slot&gt;#2</c>, <c>&lt;slot&gt;#3</c>... not bound; null when the
    /// slot takes one connection and has it, or a multiple slot has no free key left.</summary>
    public static string? FreeKey(IReadOnlyDictionary<string, string> bound, string slot, bool multiple)
    {
        if (!bound.ContainsKey(slot)) return slot;
        if (!multiple) return null;

        for (var n = 2; n <= 999; n++)
        {
            var key = $"{slot}{PluginConnectionSlot.More}{n.ToString(CultureInfo.InvariantCulture)}";
            if (!bound.ContainsKey(key)) return key;
        }

        return null;
    }
}

/// <summary>
/// The SQLite version of <see cref="IPluginSecretMoveRows"/>: every row in the log's own file, in one
/// transaction. The settings row is read inside it, so a binding a person made meanwhile is kept.
/// </summary>
public sealed class SqlitePluginSecretMoveRows(string databasePath) : IPluginSecretMoveRows
{
    private readonly string _connectionString = SqliteOpen.ConnectionString(databasePath);

    public async Task<string?> MoveAsync(SecretMove move, CancellationToken ct = default)
    {
        var settings = move.Connection.Credential ?? throw new ArgumentException("A moved secret makes a credential connection.", nameof(move));

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await SqlitePragmas.ApplyAsync(connection, ct: ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var stored = PluginMemberSettings.None;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT config_json, secrets_json, connections_json FROM team_member_config WHERE team = $team AND name = $name";
            read.Parameters.AddWithValue("$team", move.Member.Team);
            read.Parameters.AddWithValue("$name", move.Member.Name);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) stored = PluginMemberSettingsRows.Read(reader.GetString(0), reader.GetString(1), reader.GetString(2));
        }

        // ALREADY BOUND, a slot that takes one: nothing is written (disposed uncommitted).
        if (PluginSecretMoves.FreeKey(stored.Connections, move.Slot, move.Multiple) is not { } key) return null;

        await using (var names = connection.CreateCommand())
        {
            names.Transaction = transaction;
            names.CommandText = "SELECT name FROM connections";
            var taken = ConnectionRows.NameKey(move.Connection.Name);
            await using var reader = await names.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (ConnectionRows.NameKey(reader.GetString(0)) == taken) throw new ConnectionNameTakenException(move.Connection.Name);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO connections (id, name, provider, account, scopes, connected_at, refreshed_at, status, status_reason, connected_by)
                VALUES ($id, $name, $provider, $account, '[]', $at, NULL, $status, $reason, $by)
                """;
            command.Parameters.AddWithValue("$id", move.Connection.Id);
            command.Parameters.AddWithValue("$status", move.Connection.Status);
            command.Parameters.AddWithValue("$reason", (object?)move.Connection.StatusReason ?? DBNull.Value);
            command.Parameters.AddWithValue("$name", move.Connection.Name);
            command.Parameters.AddWithValue("$provider", move.Connection.Provider);
            command.Parameters.AddWithValue("$account", move.Connection.Account);
            command.Parameters.AddWithValue("$at", Instant.Stamp(move.Connection.ConnectedAt));
            command.Parameters.AddWithValue("$by", (object?)move.ConnectedBy ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO credential_connections (id, preset, fields, secrets_protected) VALUES ($id, $preset, $fields, $secrets)";
            command.Parameters.AddWithValue("$id", move.Connection.Id);
            command.Parameters.AddWithValue("$preset", (object?)settings.Preset ?? DBNull.Value);
            command.Parameters.AddWithValue("$fields", settings.FieldsJson());
            command.Parameters.AddWithValue("$secrets", move.SealedSecrets);
            await command.ExecuteNonQueryAsync(ct);
        }

        // THE BINDING, which assigns the connection to the member's team in the same write.
        var connections = new Dictionary<string, string>(stored.Connections, StringComparer.Ordinal) { [key] = move.Connection.Id };
        await SqlitePluginMemberSettings.UpsertAsync(connection, transaction, move.Member, stored with { Connections = connections }, ct);

        foreach (var (field, sealedValue) in move.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM plugin_member_secrets WHERE team = $team AND name = $name AND field = $field AND value_protected = $value";
            delete.Parameters.AddWithValue("$team", move.Member.Team);
            delete.Parameters.AddWithValue("$name", move.Member.Name);
            delete.Parameters.AddWithValue("$field", field);
            delete.Parameters.AddWithValue("$value", sealedValue);
            if (await delete.ExecuteNonQueryAsync(ct) != 1) throw new SecretMoveChangedException(field);
        }

        foreach (var row in move.Audits(key)) await TenantAuditRow.AppendAsync(connection, transaction, row, ct);

        await transaction.CommitAsync(ct);
        return key;
    }
}
