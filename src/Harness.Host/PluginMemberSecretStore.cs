using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Harness.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Harness.Host;

/// <summary>Who set a member's own value for a secret field, and when. Never the value.</summary>
public sealed record MemberSecretState(string SetBy, DateTimeOffset SetAt);

/// <summary>One change to a table's secret cell: the table field, the row's key, the column, and the
/// value (null clears). See <see cref="PluginMemberSecretStore.SetCellAsync"/>.</summary>
public sealed record TableCellChange(string Table, string Row, string Column, string? Value);

/// <summary>A table's secret cell that holds a value: who set it and when, and the value only for a
/// column declared <c>masked: false</c>.</summary>
public sealed record TableCellState(string SetBy, DateTimeOffset SetAt, string? Value);

/// <summary>A plugin member's own secret values, read by the runner at each run.</summary>
public interface IPluginMemberSecrets
{
    /// <summary>Each field the member has its own value for, to that value. A value that no longer
    /// decrypts (a lost key ring) is absent, as a missing one.</summary>
    Task<IReadOnlyDictionary<string, string>> ValuesAsync(ContainerId member, CancellationToken ct = default);

    /// <summary>Each named secret the member holds, by the name a person gave it, to its value. A value
    /// that no longer decrypts is absent, as a missing one. None unless the store keeps named secrets.</summary>
    Task<IReadOnlyDictionary<string, string>> NamedValuesAsync(ContainerId member, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.Ordinal));
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
public sealed class PluginMemberSecretStore(IPluginMemberSecretRows rows, IDataProtectionProvider protection) : IPluginMemberSecrets
{
    /// <summary>The SQLite store over the database at <paramref name="databasePath"/>.</summary>
    public PluginMemberSecretStore(string databasePath, IDataProtectionProvider protection)
        : this(new SqlitePluginMemberSecretRows(databasePath), protection)
    {
    }

    /// <summary>The Data Protection purpose: its own, so no other store's ciphertext opens here.</summary>
    public const string Purpose = "Harness.PluginMemberSecrets.v1";

    private readonly IDataProtector _protector = protection.CreateProtector(Purpose);

    /// <summary>The named secrets' Data Protection purpose: their own, so neither a field value's nor
    /// any other store's ciphertext opens as a named secret, nor the reverse.</summary>
    public const string NamedPurpose = "Harness.PluginMemberNamedSecrets.v1";

    private readonly IDataProtector _namedProtector = protection.CreateProtector(NamedPurpose);

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

        foreach (var row in await rows.RowsAsync(member, ct))
        {
            if (Unprotect(row.ValueProtected) is { } value) values[row.Field] = value;
        }

        return values;
    }

    /// <summary>Who set each field the member has a value for, and when. A value that no longer
    /// decrypts reads as not set.</summary>
    public async Task<IReadOnlyDictionary<string, MemberSecretState>> StatesAsync(ContainerId member, CancellationToken ct = default)
    {
        var states = new Dictionary<string, MemberSecretState>(StringComparer.Ordinal);

        foreach (var row in await rows.RowsAsync(member, ct))
        {
            if (Unprotect(row.ValueProtected) is not null) states[row.Field] = new MemberSecretState(row.SetBy, row.SetAt);
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
    public Task ChangeAsync(
        ContainerId member, string label, string plugin, IReadOnlyDictionary<string, string?> changes, CredentialActor actor, CancellationToken ct = default) =>
        rows.ChangeAsync(member, Protect(changes), actor.Email, AuditFor(member, label, plugin, actor), ct);

    /// <summary>
    /// A SETTINGS SAVE AND ITS VALUES TOGETHER: the member's settings row, every given tenant row and
    /// every value change with its own row, in ONE transaction, or nothing. What a person's Save, a
    /// person's hire and a solution's install write.
    /// </summary>
    public Task SaveWithSettingsAsync(
        ContainerId member, string label, string plugin, PluginMemberSettings settings, IReadOnlyList<TriggerAudit> audits,
        IReadOnlyDictionary<string, string?> changes, CredentialActor actor, CancellationToken ct = default) =>
        rows.SaveWithSettingsAsync(member, settings, audits, Protect(changes), actor.Email, AuditFor(member, label, plugin, actor), ct);

    /// <summary>
    /// THE ONE-TIME START STEP: for every plugin member whose secret field is bound to a key
    /// with a STORED value (<c>plugin_secrets</c>) and that has no value of its own for the field, that
    /// value is copied onto the member, and the step is marked done by its
    /// <see cref="TenantActions.MemberSecretsCopied"/> row (counts only) in the same transaction. The
    /// binding and the stored key stay, so the operator CLI and the environment still work. A key set
    /// only in the Host's environment is never copied: the Host never moves an environment value into
    /// the database. Answers false, copying nothing, once the row is there.
    /// </summary>
    public Task<bool> CopyStoredKeysOnceAsync(IDataProtectionProvider keys, CancellationToken ct = default)
    {
        var keyProtector = keys.CreateProtector(PluginSecretStore.Purpose);

        return rows.CopyStoredKeysOnceAsync(
            protectedKey =>
            {
                try
                {
                    return keyProtector.Unprotect(protectedKey);
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // A key ring that lost its key: nothing to copy for that key.
                    return null;
                }
            },
            _protector.Protect,
            (members, fields, keyCount) => new TriggerAudit(
                null, null, TenantActions.MemberSecretsCopied, null, null,
                JsonSerializer.Serialize(new { members, fields, keys = keyCount })),
            ct);
    }

    public async Task<IReadOnlyDictionary<string, string>> NamedValuesAsync(ContainerId member, CancellationToken ct = default)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in await rows.NamedRowsAsync(member, ct))
        {
            if (Unprotect(_namedProtector, row.ValueProtected) is { } value) values[row.Field] = value;
        }

        return values;
    }

    /// <summary>Each named secret the member holds, by name, with who set it and when, in name order
    /// (ignoring case). Never a value; a value that no longer decrypts reads as not held.</summary>
    public async Task<IReadOnlyList<NamedSecretState>> NamedStatesAsync(ContainerId member, CancellationToken ct = default) =>
    [
        .. (await rows.NamedRowsAsync(member, ct))
            .Where(row => Unprotect(_namedProtector, row.ValueProtected) is not null)
            .OrderBy(row => row.Field, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Field, StringComparer.Ordinal)
            .Select(row => new NamedSecretState(row.Field, row.SetBy, row.SetAt)),
    ];

    /// <summary>Every named-secret change (a value sets or replaces, null removes) with its
    /// <c>member.named-secret-*</c> row, in one transaction. The caller has checked them
    /// (<see cref="NamedSecretRules.ChangesRefusal"/>).</summary>
    public Task ChangeNamedAsync(
        ContainerId member, string label, string plugin, IReadOnlyDictionary<string, string?> changes, CredentialActor actor, CancellationToken ct = default) =>
        rows.ChangeNamedAsync(member, ProtectNamed(member, label, plugin, changes, actor), actor.Email, ct);

    /// <summary>
    /// A SETTINGS SAVE, ITS FIELD VALUES AND ITS NAMED SECRETS TOGETHER, each change with its own row, in
    /// ONE transaction, or nothing.
    /// </summary>
    public Task SaveWithSettingsAsync(
        ContainerId member, string label, string plugin, PluginMemberSettings settings, IReadOnlyList<TriggerAudit> audits,
        IReadOnlyDictionary<string, string?> changes, IReadOnlyDictionary<string, string?> namedChanges, CredentialActor actor,
        CancellationToken ct = default) =>
        rows.SaveWithSettingsAsync(
            member, settings, audits, Protect(changes), actor.Email, AuditFor(member, label, plugin, actor), ct,
            namedChanges.Count == 0 ? null : ProtectNamed(member, label, plugin, namedChanges, actor));

    // ---------------------------------------------------------------------------------------------
    // A TABLE'S SECRET CELLS. Each is one of the member's own secrets, in these same rows under this
    // same purpose, so it is stored, masked, redacted and removed with the member exactly as a field
    // value is. Its row's `field` is CellField(table, row, column), a name no manifest field can have.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The prefix of every cell's stored name: a colon, which no manifest field name holds.</summary>
    public const string CellPrefix = "cell:";

    /// <summary>
    /// The name a table's secret cell is stored under for its member: <see cref="CellPrefix"/> and 40 hex
    /// digits of SHA-256 over the table field, the row's key IN LOWER CASE (a row's key is unique
    /// ignoring case) and the column. Fixed length, so a long row key fits every provider's column.
    /// </summary>
    public static string CellField(string table, string row, string column) =>
        CellPrefix + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{table}\n{row.ToLowerInvariant()}\n{column}")))[..40];

    /// <summary>
    /// Why <paramref name="value"/> (null clears) cannot be kept for the secret cell
    /// (<paramref name="table"/>, <paramref name="row"/>, <paramref name="column"/>) of a member whose
    /// settings are <paramref name="stored"/>, or null: the table and the column must be declared, the
    /// column secret, the row one the member has (ignoring case), and a value held to a masked field's
    /// rules (an unmasked column's when the manifest says <c>masked: false</c>) with
    /// <paramref name="maxLength"/> in place of the ordinary limit. A person's save refuses a read-only
    /// column (<paramref name="byPerson"/>): only the platform writes one. Never repeats the value.
    /// </summary>
    public static string? CellRefusal(
        PluginManifest manifest, PluginMemberSettings stored, string table, string row, string column, string? value,
        bool byPerson = true, int maxLength = PluginSecretStore.MaxValueLength)
    {
        if (!manifest.Config.TryGetValue(table, out var field) || field.Table is not { } declared)
        {
            return $"`{table}` is not a table of plugin '{manifest.Id}'.";
        }

        if (declared.Column(column) is not { IsSecret: true } secret)
        {
            return $"`{table}.{column}` is not a secret column of the table. Its secret columns: "
                + (declared.SecretColumns.Any() ? string.Join(", ", declared.SecretColumns.Select(c => c.Name)) : "none") + ".";
        }

        if (declared.Row(stored.Config.TryGetValue(table, out var rows) ? rows : null, row) is null)
        {
            return $"`{table}` has no row '{row}'.";
        }

        if (byPerson && secret.ReadOnly && value is not null)
        {
            return $"`{table}[{row}].{column}` is written only by the platform; a person may clear it, not set it.";
        }

        if (value is null) return null;

        var at = $"{table}[{row}].{column}";
        if (string.IsNullOrWhiteSpace(value)) return $"`{at}`: the value is empty.";
        if (value.Length > maxLength) return $"`{at}`: the value is longer than {maxLength} characters.";
        if (value.Any(c => c is '\r' or '\n' or '\0')) return $"`{at}`: the value holds a line break or a NUL; it must be one line.";

        if (secret.Masked)
        {
            if (value != value.Trim()) return $"`{at}`: the value begins or ends with white space.";
            if (value.Length < PluginMemberRunner.MinimumSecretLength)
            {
                return $"`{at}`: the value is shorter than {PluginMemberRunner.MinimumSecretLength} characters, too short to redact from a plugin's output.";
            }
        }

        return null;
    }

    /// <summary>The value of one secret cell, or null when it has none (or no longer decrypts).</summary>
    public async Task<string?> CellValueAsync(ContainerId member, string table, string row, string column, CancellationToken ct = default)
    {
        var name = CellField(table, row, column);
        var found = (await rows.RowsAsync(member, ct)).FirstOrDefault(r => r.Field == name);
        return found is null ? null : Unprotect(found.ValueProtected);
    }

    /// <summary>Each secret cell of <paramref name="table"/> that holds a value, by row (as
    /// <paramref name="rowNames"/> spells it) and column: who set it and when, and its value ONLY for a
    /// column the manifest declares <c>masked: false</c>. Never a masked value.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, TableCellState>>> CellStatesAsync(
        ContainerId member, string table, PluginTable declared, IEnumerable<string> rowNames, CancellationToken ct = default)
    {
        var held = (await rows.RowsAsync(member, ct)).ToDictionary(r => r.Field, StringComparer.Ordinal);
        var states = new Dictionary<string, IReadOnlyDictionary<string, TableCellState>>(StringComparer.Ordinal);

        foreach (var row in rowNames)
        {
            var cells = new Dictionary<string, TableCellState>(StringComparer.Ordinal);
            foreach (var column in declared.SecretColumns)
            {
                if (held.TryGetValue(CellField(table, row, column.Name), out var found) && Unprotect(found.ValueProtected) is { } value)
                {
                    cells[column.Name] = new TableCellState(found.SetBy, found.SetAt, column.Masked ? null : value);
                }
            }

            if (cells.Count > 0) states[row] = cells;
        }

        return states;
    }

    /// <summary>
    /// SETS OR REPLACES ONE SECRET CELL with its <c>member.secret-set</c> or <c>-replaced</c> row (team,
    /// member, plugin, table, row, column - never the value) in one transaction. The caller has checked
    /// it with <see cref="CellRefusal"/>. What the interactive sign-in page keeps a row's session with.
    /// </summary>
    public Task SetCellAsync(
        ContainerId member, string label, string plugin, string table, string row, string column, string value,
        CredentialActor actor, CancellationToken ct = default) =>
        ChangeCellsAsync(member, label, plugin, [new TableCellChange(table, row, column, value)], actor, ct);

    /// <summary>Clears one secret cell with its <c>member.secret-cleared</c> row; nothing stored writes no row.</summary>
    public Task ClearCellAsync(
        ContainerId member, string label, string plugin, string table, string row, string column,
        CredentialActor actor, CancellationToken ct = default) =>
        ChangeCellsAsync(member, label, plugin, [new TableCellChange(table, row, column, null)], actor, ct);

    /// <summary>Every cell change (a value sets or replaces, null clears) with its row, in one transaction.</summary>
    public Task ChangeCellsAsync(
        ContainerId member, string label, string plugin, IReadOnlyList<TableCellChange> cells, CredentialActor actor, CancellationToken ct = default)
    {
        var (protectedCells, audit) = ProtectCells(member, label, plugin, cells, actor, AuditFor(member, label, plugin, actor));
        return rows.ChangeAsync(member, protectedCells, actor.Email, audit, ct);
    }

    /// <summary>
    /// A SETTINGS SAVE WITH ITS FIELD VALUES, NAMED SECRETS AND TABLE CELLS, each change with its own
    /// row, in ONE transaction, or nothing: what a person's Save of a table writes.
    /// </summary>
    public Task SaveWithSettingsAsync(
        ContainerId member, string label, string plugin, PluginMemberSettings settings, IReadOnlyList<TriggerAudit> audits,
        IReadOnlyDictionary<string, string?> changes, IReadOnlyDictionary<string, string?> namedChanges,
        IReadOnlyList<TableCellChange> cells, CredentialActor actor, CancellationToken ct = default)
    {
        var fieldAudit = AuditFor(member, label, plugin, actor);
        var (protectedCells, audit) = ProtectCells(member, label, plugin, cells, actor, fieldAudit);
        foreach (var (field, value) in Protect(changes)) protectedCells[field] = value;

        return rows.SaveWithSettingsAsync(
            member, settings, audits, protectedCells, actor.Email, audit, ct,
            namedChanges.Count == 0 ? null : ProtectNamed(member, label, plugin, namedChanges, actor));
    }

    /// <summary>Seals each cell's value under the member purpose by its stored name, with an audit that
    /// names a cell's table, row and column (and falls back to <paramref name="fieldAudit"/> for a field).</summary>
    private (Dictionary<string, string?> Protected, Func<string, string, TriggerAudit> Audit) ProtectCells(
        ContainerId member, string label, string plugin, IReadOnlyList<TableCellChange> cells, CredentialActor actor,
        Func<string, string, TriggerAudit> fieldAudit)
    {
        var named = new Dictionary<string, TableCellChange>(StringComparer.Ordinal);
        var sealedCells = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var cell in cells)
        {
            var name = CellField(cell.Table, cell.Row, cell.Column);
            named[name] = cell;
            sealedCells[name] = cell.Value is null ? null : _protector.Protect(cell.Value);
        }

        return (sealedCells, (field, action) => named.TryGetValue(field, out var cell)
            ? new TriggerAudit(
                actor.Id, actor.Email, action, $"{member.Team}/{member.Name}", label,
                JsonSerializer.Serialize(new { team = member.Team, member = member.Name, plugin, field = cell.Table, row = cell.Row, column = cell.Column }))
            : fieldAudit(field, action));
    }

    /// <summary>Seals each named value under its own purpose, with the tenant row of each change: team,
    /// member, plugin and name, never the value.</summary>
    private ProtectedNamedChanges ProtectNamed(
        ContainerId member, string label, string plugin, IReadOnlyDictionary<string, string?> changes, CredentialActor actor) =>
        new(
            changes.ToDictionary(c => c.Key, c => c.Value is null ? null : _namedProtector.Protect(c.Value), StringComparer.Ordinal),
            (name, action) => new TriggerAudit(
                actor.Id, actor.Email, action, $"{member.Team}/{member.Name}", label,
                JsonSerializer.Serialize(new { team = member.Team, member = member.Name, plugin, name })));

    /// <summary>Seals each value (null stays null, a clear) before it reaches the rows.</summary>
    private Dictionary<string, string?> Protect(IReadOnlyDictionary<string, string?> changes) =>
        changes.ToDictionary(c => c.Key, c => c.Value is null ? null : _protector.Protect(c.Value), StringComparer.Ordinal);

    /// <summary>The tenant row of one field's change: team, member, plugin and field, never the value.</summary>
    private static Func<string, string, TriggerAudit> AuditFor(ContainerId member, string label, string plugin, CredentialActor actor) =>
        (field, action) => new TriggerAudit(
            actor.Id, actor.Email, action, $"{member.Team}/{member.Name}", label,
            JsonSerializer.Serialize(new { team = member.Team, member = member.Name, plugin, field }));

    private string? Unprotect(string protectedValue) => Unprotect(_protector, protectedValue);

    private static string? Unprotect(IDataProtector protector, string protectedValue)
    {
        try
        {
            return protector.Unprotect(protectedValue);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
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
        // ANY PROVIDER'S DATABASE FAULT, not only SQLite's: `SqliteException` and SQL Server's `SqlException`
        // are both a `DbException`, and either leaves the copy to the next start as before.
        catch (DbException exception)
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

    /// <summary>Each named secret the member holds, who set it and when; never a value.</summary>
    public Task<IReadOnlyList<NamedSecretState>> NamedStatesAsync(ContainerId id, CancellationToken ct) => members.NamedStatesAsync(id, ct);

    /// <summary>Each table field to its rows' secret cells that hold a value (<see cref="PluginMemberSecretStore.CellStatesAsync"/>):
    /// <c>{ set, setBy, setAt }</c>, and <c>value</c> only for an unmasked column. <c>{}</c> for a plugin with no table.</summary>
    public async Task<Dictionary<string, object>> TableStatesAsync(ContainerId id, PluginManifest manifest, PluginMemberSettings stored, CancellationToken ct)
    {
        var tables = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var (name, field) in manifest.Config)
        {
            if (field.Table is not { } table) continue;

            var states = await members.CellStatesAsync(id, name, table, table.RowNames(stored.Config.TryGetValue(name, out var rows) ? rows : null), ct);
            tables[name] = states.ToDictionary(
                r => r.Key,
                r => r.Value.ToDictionary(
                    c => c.Key,
                    c => c.Value.Value is null
                        ? (object)new { set = true, setBy = c.Value.SetBy, setAt = c.Value.SetAt }
                        : new { set = true, setBy = c.Value.SetBy, setAt = c.Value.SetAt, value = c.Value.Value },
                    StringComparer.Ordinal),
                StringComparer.Ordinal);
        }

        return tables;
    }

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

/// <summary>
/// WHAT THE ONE-TIME COPY COUNTS, for every provider's <see cref="IPluginMemberSecretRows.CopyStoredKeysOnceAsync"/>:
/// the members (a team and member in any case is one), the fields and the distinct keys a copy wrote,
/// answered as the step's tenant row.
/// </summary>
public sealed class MemberSecretCopyTally
{
    private readonly HashSet<string> _members = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private int _fields;

    /// <summary>One field written for (<paramref name="team"/>, <paramref name="name"/>) from <paramref name="key"/>.</summary>
    public void Copied(string team, string name, string key)
    {
        _fields++;
        _members.Add($"{team}/{name}");
        _keys.Add(key);
    }

    /// <summary>The step's tenant row, given the members, fields and keys counted.</summary>
    public TriggerAudit Done(Func<int, int, int, TriggerAudit> done) => done(_members.Count, _fields, _keys.Count);
}

/// <summary>
/// One of a member's two secret tables, as both providers' rows write it: its name, the column naming a
/// row within the member, and the tenant action of a set, a replace and a clear or removal.
/// </summary>
public sealed record MemberSecretTable(string Name, string Column, string Set, string Replaced, string Cleared)
{
    /// <summary><c>plugin_member_secrets</c> (auth-028): a member's own value for a manifest field.</summary>
    public static readonly MemberSecretTable Fields = new(
        "plugin_member_secrets", "field",
        TenantActions.MemberSecretSet, TenantActions.MemberSecretReplaced, TenantActions.MemberSecretCleared);

    /// <summary><c>plugin_member_named_secrets</c> (auth-030): a secret a person named, matched ignoring case.</summary>
    public static readonly MemberSecretTable Named = new(
        "plugin_member_named_secrets", "secret",
        TenantActions.MemberNamedSecretSet, TenantActions.MemberNamedSecretReplaced, TenantActions.MemberNamedSecretRemoved);
}

/// <summary>
/// The SQLite version of <see cref="IPluginMemberSecretRows"/>: <c>plugin_member_secrets</c> in the
/// log's own file, every change with its tenant rows in the same transaction.
/// </summary>
public sealed class SqlitePluginMemberSecretRows(string databasePath) : IPluginMemberSecretRows
{
    private readonly string _connectionString = SqliteOpen.ConnectionString(databasePath);

    public async Task<IReadOnlyList<MemberSecretRow>> RowsAsync(ContainerId member, CancellationToken ct = default)
    {
        var rows = new List<MemberSecretRow>();

        await using var connection = Open();
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT field, value_protected, set_by, set_at FROM plugin_member_secrets WHERE team = $team AND name = $name";
        select.Parameters.AddWithValue("$team", member.Team);
        select.Parameters.AddWithValue("$name", member.Name);

        await using var reader = await select.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new MemberSecretRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), Instant.Parse(reader.GetString(3))));
        }

        return rows;
    }

    public async Task ChangeAsync(
        ContainerId member, IReadOnlyDictionary<string, string?> protectedChanges, string setBy,
        Func<string, string, TriggerAudit> audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await ApplyAsync(connection, transaction, member, MemberSecretTable.Fields, protectedChanges, setBy, audit, ct);

        await transaction.CommitAsync(ct);
    }

    public async Task SaveWithSettingsAsync(
        ContainerId member, PluginMemberSettings settings, IReadOnlyList<TriggerAudit> audits,
        IReadOnlyDictionary<string, string?> protectedChanges, string setBy,
        Func<string, string, TriggerAudit> audit, CancellationToken ct = default, ProtectedNamedChanges? named = null)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await SqlitePluginMemberSettings.UpsertAsync(connection, transaction, member, settings, ct);
        foreach (var row in audits) await TenantAuditRow.AppendAsync(connection, transaction, row, ct);
        await ApplyAsync(connection, transaction, member, MemberSecretTable.Fields, protectedChanges, setBy, audit, ct);
        if (named is not null) await ApplyAsync(connection, transaction, member, MemberSecretTable.Named, named.Changes, setBy, named.Audit, ct);

        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<MemberSecretRow>> NamedRowsAsync(ContainerId member, CancellationToken ct = default)
    {
        var rows = new List<MemberSecretRow>();

        await using var connection = Open();
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT secret, value_protected, set_by, set_at FROM plugin_member_named_secrets WHERE team = $team AND name = $name";
        select.Parameters.AddWithValue("$team", member.Team);
        select.Parameters.AddWithValue("$name", member.Name);

        await using var reader = await select.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new MemberSecretRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), Instant.Parse(reader.GetString(3))));
        }

        return rows;
    }

    public async Task ChangeNamedAsync(ContainerId member, ProtectedNamedChanges named, string setBy, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await ApplyAsync(connection, transaction, member, MemberSecretTable.Named, named.Changes, setBy, named.Audit, ct);

        await transaction.CommitAsync(ct);
    }

    public async Task<bool> CopyStoredKeysOnceAsync(
        Func<string, string?> unprotectKey, Func<string, string> protectForMember,
        Func<int, int, int, TriggerAudit> done, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var marked = connection.CreateCommand())
        {
            marked.Transaction = transaction;
            marked.CommandText = "SELECT COUNT(*) FROM tenant_events WHERE action = $action";
            marked.Parameters.AddWithValue("$action", TenantActions.MemberSecretsCopied);
            if (Convert.ToInt64(await marked.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0)
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
                if (unprotectKey(reader.GetString(1)) is { } value) stored[reader.GetString(0)] = (value, reader.GetString(2));
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
                foreach (var (field, key) in PluginMemberSettingsRows.SecretBindings(reader.GetString(2)))
                {
                    bindings.Add((reader.GetString(0), reader.GetString(1), field, key));
                }
            }
        }

        var tally = new MemberSecretCopyTally();
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
            insert.Parameters.AddWithValue("$value", protectForMember(value.Value));
            insert.Parameters.AddWithValue("$by", value.SetBy);
            insert.Parameters.AddWithValue("$at", at);

            if (await insert.ExecuteNonQueryAsync(ct) > 0) tally.Copied(team, name, key);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, tally.Done(done), ct);

        await transaction.CommitAsync(ct);
        return true;
    }

    private static async Task ApplyAsync(
        SqliteConnection connection, SqliteTransaction transaction, ContainerId member, MemberSecretTable table,
        IReadOnlyDictionary<string, string?> protectedChanges, string setBy, Func<string, string, TriggerAudit> audit,
        CancellationToken ct)
    {
        var at = Instant.Stamp(DateTimeOffset.UtcNow);
        var match = $"team = $team AND name = $name AND {table.Column} = $field";

        foreach (var (field, value) in protectedChanges.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            string action;

            if (value is null)
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM {table.Name} WHERE {match}";
                Bind(delete, member, field);

                // Nothing stored: nothing changes and no row.
                if (await delete.ExecuteNonQueryAsync(ct) == 0) continue;
                action = table.Cleared;
            }
            else
            {
                bool existed;
                await using (var exists = connection.CreateCommand())
                {
                    exists.Transaction = transaction;
                    exists.CommandText = $"SELECT COUNT(*) FROM {table.Name} WHERE {match}";
                    Bind(exists, member, field);
                    existed = Convert.ToInt64(await exists.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
                }

                await using var upsert = connection.CreateCommand();
                upsert.Transaction = transaction;
                upsert.CommandText =
                    $"""
                    INSERT INTO {table.Name} (team, name, {table.Column}, value_protected, set_by, set_at)
                    VALUES ($team, $name, $field, $value, $by, $at)
                    ON CONFLICT (team, name, {table.Column}) DO UPDATE SET
                        value_protected = excluded.value_protected,
                        set_by = excluded.set_by,
                        set_at = excluded.set_at
                    """;
                Bind(upsert, member, field);
                upsert.Parameters.AddWithValue("$value", value);
                upsert.Parameters.AddWithValue("$by", setBy);
                upsert.Parameters.AddWithValue("$at", at);
                await upsert.ExecuteNonQueryAsync(ct);

                action = existed ? table.Replaced : table.Set;
            }

            await TenantAuditRow.AppendAsync(connection, transaction, audit(field, action), ct);
        }
    }

    private static void Bind(SqliteCommand command, ContainerId member, string field)
    {
        command.Parameters.AddWithValue("$team", member.Team);
        command.Parameters.AddWithValue("$name", member.Name);
        command.Parameters.AddWithValue("$field", field);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqlitePragmas.Apply(connection);
        return connection;
    }
}
