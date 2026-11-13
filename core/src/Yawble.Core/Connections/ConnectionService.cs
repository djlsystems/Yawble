using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Credentials;

namespace Yawble.Core.Connections;

/// <summary>One field of a Connection as the person defines it. <see cref="Value"/> is set only for non-secret fields.</summary>
public sealed record ConnectionField(string Key, string Label, bool Secret, string? Value);

/// <summary><see cref="Shown"/> is the masked value for a secret, the full value otherwise; <see cref="Set"/> says a value is stored.</summary>
public sealed record ConnectionFieldView(string Key, string Label, bool Secret, string? Shown, bool Set);

public sealed record ConnectionView(long Id, string Name, string Kind, IReadOnlyList<ConnectionFieldView> Fields, string Status,
    string? StatusReason, string? TestedAt, IReadOnlyList<string> UsedBy);

/// <summary>One line of <see cref="ConnectionService.Index"/>. The status is "untested", "works" or "needs_reconnect".</summary>
public sealed record ConnectionIndexEntry(long Id, string Name, string Kind, string Status, string StoreKey);

/// <summary>A Connection's name and status only, read without touching the credential store.</summary>
/// <summary>A Connection's secret field keys: <paramref name="Set"/> exist in the store; for <paramref name="Unknown"/> the store could not say without reading.</summary>
public sealed record ConnectionSecretKeys(long Id, string Name, string Kind, IReadOnlyList<string> Set, IReadOnlyList<string> Unknown);

public sealed record ConnectionBrief(long Id, string Name, string Status, string? StatusReason);

/// <summary>
/// Shared sign-ins (API keys and tokens). Rows are in SQLite; secret values live only in the credential store under
/// "connection/{storeKey}/{fieldKey}", where storeKey is the row's random GUID: ids restart at 1 in every data dir, and the OS
/// store is shared by all of them on one computer. Views are masked; <see cref="Secrets"/> is for the host and never reaches RPC.
/// </summary>
public sealed class ConnectionService
{
    private readonly ICredentialStore _store;
    private readonly IIssuedKeyLedger? _ledger;
    private readonly string _cs;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    /// <summary>Connections that could not be loaded at start (a store error, unreadable fields): never sent until saved again.</summary>
    private readonly HashSet<long> _broken = [];

    private const string BrokenReason = "its key could not be read on this computer; edit it and enter the key again";

    /// <summary>Called after a Connection is saved or removed, or its status is set (the host publishes an event so open screens refresh).</summary>
    public Action? Changed { get; set; }

    public const string ChangedEvent = "connections.changed";

    /// <param name="log">Where a connection that fails to load is reported, never with a value. Default: stderr.</param>
    /// <param name="ledger">Records every store key this service creates, so cleanup can tell this data folder's secrets from another's.</param>
    public ConnectionService(ICredentialStore store, string connectionString, TimeProvider? clock = null, Action<string>? log = null,
        IIssuedKeyLedger? ledger = null)
    {
        _store = store;
        _ledger = ledger;
        _cs = connectionString;
        _clock = clock ?? TimeProvider.System;
        log ??= line => Console.Error.WriteLine(line);
        foreach (var r in Rows())
        {
            // One connection that cannot be loaded is skipped (it has no scrubber entry, so it is never sent): the core still starts.
            try
            {
                var fields = Fields(r.FieldsJson);
                SecretScrubber.Set(this, r.Id, ScrubValues(fields, fields.Where(f => f.Secret).Select(f => _store.Get(Key(r.StoreKey, f.Key))).OfType<string>().ToList()));
            }
            catch (Exception e) when (IsLoadFailure(e))
            {
                lock (_gate) _broken.Add(r.Id);
                log($"connections: connection {r.Id} could not be loaded ({e.GetType().Name}); it is not used until it is saved again");
            }
        }
    }

    private static bool IsLoadFailure(Exception e) => e is CredentialStoreException or JsonException or InvalidOperationException or KeyNotFoundException;

    /// <summary>
    /// What the scrubber removes for a Connection: each secret, and the basic sign-in it makes with each non-secret field (a user name),
    /// base64("user:secret"), as an Authorization: Basic header carries it.
    /// </summary>
    private static IEnumerable<string> ScrubValues(IReadOnlyList<ConnectionField> fields, IEnumerable<string> secrets)
    {
        var users = fields.Where(f => !f.Secret).Select(f => f.Value ?? "").Append("").Distinct(StringComparer.Ordinal).ToList();
        foreach (var s in secrets)
        {
            yield return s;
            foreach (var u in users) yield return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(u + ":" + s));
        }
    }

    public static string Key(string storeKey, string fieldKey) => $"connection/{storeKey}/{fieldKey}";

    public static string Mask(string value) =>
        value.Length >= 16 ? value[..4] + "••••••" + value[^4..]
        : value.Length >= 8 ? "••••••" + value[^2..]
        : "••••••";

    /// <param name="kind">'secret' (the default for a new one), 'oauth' (a browser sign-in) or 'website' (a login kept in the app window's own session; no secret here); null on an edit keeps the kind it has.</param>
    /// <param name="keepStatus">New secret values normally reset the status to untested; a refreshed sign-in keeps it.</param>
    public ConnectionView Save(long? id, string name, IReadOnlyList<ConnectionField> fields, IReadOnlyDictionary<string, string> secrets,
        string? kind = null, bool keepStatus = false)
    {
        if (kind is not (null or "secret" or "oauth" or "website")) throw new ArgumentException($"unknown connection kind '{kind}'");
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("a connection needs a name");
        if (fields.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count() != fields.Count)
            throw new ArgumentException("field keys must be unique");
        long cid;
        lock (_gate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            var now = _clock.GetUtcNow().ToString("O");
            if (Scalar(c, tx, "SELECT id FROM connections WHERE name = $n AND id IS NOT $i;", ("$n", name), ("$i", id)) is not null)
                throw new ArgumentException($"a connection named '{name}' already exists");

            // Non-secret values are kept in the row; a secret field carries no value there.
            var stored = new JsonArray();
            foreach (var f in fields)
            {
                var o = new JsonObject { ["key"] = f.Key, ["label"] = f.Label, ["secret"] = f.Secret };
                if (!f.Secret) o["value"] = f.Value ?? "";
                stored.Add(o);
            }
            var storedJson = stored.ToJsonString();

            var newSecrets = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in fields.Where(f => f.Secret))
                if (secrets.TryGetValue(f.Key, out var v) && v.Length > 0) newSecrets[f.Key] = v;

            var oldSecretKeys = new List<string>();
            if (id is null)
            {
                foreach (var f in fields.Where(f => f.Secret))
                    if (!newSecrets.ContainsKey(f.Key)) throw new ArgumentException($"'{f.Label}' is required");
                Exec(c, tx, "INSERT INTO connections (name, store_key, kind, fields, status, created_at, updated_at) VALUES ($n, $k, $kind, $f, 'untested', $t, $t);",
                    ("$n", name), ("$kind", kind ?? "secret"), ("$k", Guid.NewGuid().ToString("N")), ("$f", storedJson), ("$t", now));
                id = Convert.ToInt64(Scalar(c, tx, "SELECT last_insert_rowid();"));
            }
            else
            {
                var old = Scalar(c, tx, "SELECT fields FROM connections WHERE id = $i;", ("$i", id)) as string
                    ?? throw new ArgumentException("no such connection");
                // A connection that could not be loaded is saved as a new one: every secret comes with its value again.
                if (!_broken.Contains(id.Value)) oldSecretKeys = Fields(old).Where(f => f.Secret).Select(f => f.Key).ToList();
                // A secret field added by an edit has nothing stored yet, so it must come with a value.
                foreach (var f in fields.Where(f => f.Secret && !oldSecretKeys.Contains(f.Key)))
                    if (!newSecrets.ContainsKey(f.Key)) throw new ArgumentException($"'{f.Label}' is required");
                Exec(c, tx, "UPDATE connections SET name = $n, fields = $f, kind = coalesce($kind, kind), updated_at = $t WHERE id = $i;",
                    ("$n", name), ("$f", storedJson), ("$kind", kind), ("$t", now), ("$i", id));
                if (newSecrets.Count > 0 && !keepStatus)
                    Exec(c, tx, "UPDATE connections SET status = 'untested', status_reason = NULL, tested_at = NULL WHERE id = $i;", ("$i", id));
            }

            // Store first, then commit: a store failure rolls the row back, so no row exists without its secrets.
            cid = id.Value;
            var storeKey = (string)Scalar(c, tx, "SELECT store_key FROM connections WHERE id = $i;", ("$i", cid))!;
            foreach (var (k, v) in newSecrets)
            {
                _ledger?.Record(Key(storeKey, k)); // before the store write: a crash between leaves a record for nothing, never a secret without one
                _store.Set(Key(storeKey, k), v);
            }
            tx.Commit();

            var keep = fields.Where(f => f.Secret).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var k in oldSecretKeys.Where(k => !keep.Contains(k)))
            {
                _store.Delete(Key(storeKey, k));
                _ledger?.Forget(Key(storeKey, k));
            }
            SecretScrubber.Set(this, cid, ScrubValues(fields, keep.Select(k => _store.Get(Key(storeKey, k))).OfType<string>().ToList()));
            _broken.Remove(cid);
        }
        Changed?.Invoke();
        return Get(cid)!;
    }

    public IReadOnlyList<ConnectionView> List() => Rows().Select(View).ToList();

    public ConnectionView? Get(long id) => Rows(id).Select(View).FirstOrDefault();

    /// <summary>One Connection's name and status, without reading its secrets (null: no such connection).</summary>
    public ConnectionBrief? Brief(long id) => Rows(id).Select(BriefOf).FirstOrDefault();

    /// <summary>The Connections that need the person: marked "needs reconnect", or not loadable at start. No secret is read.</summary>
    public IReadOnlyList<ConnectionBrief> NeedingReconnect() => Rows().Select(BriefOf).Where(b => b.Status == "needs_reconnect").ToList();

    /// <summary>
    /// Every Connection as the doctor needs it: id, name, kind, status and store key. Read-only: it never touches the credential
    /// store. It waits for a save in progress, so a Connection whose secrets are being written is not missed.
    /// </summary>
    public IReadOnlyList<ConnectionIndexEntry> Index()
    {
        lock (_gate)
            return Rows().Select(r => new ConnectionIndexEntry(r.Id, r.Name, r.Kind, BriefOf(r).Status, r.StoreKey)).ToList();
    }

    /// <summary>
    /// Every Connection with the keys of its secret fields, for a backup's sidecar. It never reads a value: whether a secret is
    /// set comes from the store's existence check (<see cref="Ops.ICredentialIndex.Has"/>). A store without one, or one that
    /// fails the check, gets those keys in <see cref="ConnectionSecretKeys.Unknown"/> instead.
    /// </summary>
    public IReadOnlyList<ConnectionSecretKeys> SecretKeys()
    {
        var index = _store as Ops.ICredentialIndex;
        lock (_gate)
            return Rows().Select(r =>
            {
                List<ConnectionField> fields;
                try { fields = Fields(r.FieldsJson); }
                catch (Exception e) when (IsLoadFailure(e)) { fields = []; }
                var set = new List<string>();
                var unknown = new List<string>();
                foreach (var f in fields.Where(f => f.Secret))
                {
                    bool? has;
                    try { has = index?.Has(Key(r.StoreKey, f.Key)); }
                    catch (Exception e) when (e is CredentialStoreException or NotSupportedException) { has = null; }
                    if (has is null) unknown.Add(f.Key);
                    else if (has.Value) set.Add(f.Key);
                }
                return new ConnectionSecretKeys(r.Id, r.Name, r.Kind, set, unknown);
            }).ToList();
    }

    private bool IsBroken(long id)
    {
        lock (_gate) return _broken.Contains(id);
    }

    private ConnectionBrief BriefOf(Row r) =>
        IsBroken(r.Id) ? new ConnectionBrief(r.Id, r.Name, "needs_reconnect", BrokenReason) : new ConnectionBrief(r.Id, r.Name, r.Status, r.StatusReason);

    /// <summary>
    /// The row's random store key (null: no such connection). A website login's browser session is kept under the partition
    /// "persist:website-" + this key, for the same reason secrets are: ids restart at 1 in every data dir.
    /// </summary>
    internal string? StoreKeyOf(long id) => Rows(id).Cast<Row?>().FirstOrDefault()?.StoreKey;

    public bool Remove(long id)
    {
        lock (_gate)
        {
            var row = Rows(id).Cast<Row?>().FirstOrDefault();
            if (row is null) return false;
            using (var c = Open())
            using (var tx = c.BeginTransaction())
            {
                Exec(c, tx, "DELETE FROM connections WHERE id = $i;", ("$i", id));
                tx.Commit();
            }
            List<ConnectionField> fields;
            try { fields = Fields(row.Value.FieldsJson); }
            catch (Exception e) when (IsLoadFailure(e)) { fields = []; } // unreadable fields: nothing to find in the store by them
            foreach (var f in fields.Where(f => f.Secret))
            {
                _store.Delete(Key(row.Value.StoreKey, f.Key));
                _ledger?.Forget(Key(row.Value.StoreKey, f.Key));
            }
            SecretScrubber.Forget(this, id);
            _broken.Remove(id);
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// The secret values by field key. Host only: never put this in an RPC result, a log or an agent's output. A connection that could
    /// not be loaded at start has none (its values are not in the scrubber, so they are never sent).
    /// </summary>
    public IReadOnlyDictionary<string, string> Secrets(long id)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (IsBroken(id)) return map;
        foreach (var r in Rows(id))
            foreach (var f in Fields(r.FieldsJson).Where(f => f.Secret))
                if (_store.Get(Key(r.StoreKey, f.Key)) is { Length: > 0 } v) map[f.Key] = v;
        return map;
    }

    public void MarkStatus(long id, string status, string? reason)
    {
        if (status is not ("untested" or "works" or "needs_reconnect")) throw new ArgumentException($"unknown status '{status}'");
        using (var c = Open())
        {
            var now = _clock.GetUtcNow().ToString("O");
            Exec(c, null, "UPDATE connections SET status = $s, status_reason = $r, tested_at = $t, updated_at = $t WHERE id = $i;",
                ("$s", status), ("$r", reason is null ? null : SecretScrubber.Scrub(reason)), ("$t", now), ("$i", id));
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// The id of an existing connection holding exactly the same secret values, compared as a multiset whatever their field keys (a key
    /// pasted for an API's "X-API-Key" matches a connection that keeps it as "token"). Never one with no secrets. When
    /// <paramref name="plain"/> is given, the non-secret values (a user name) must match too, so bob's password never reuses alice's sign-in.
    /// </summary>
    public long? FindIdentical(IReadOnlyDictionary<string, string> secrets, IEnumerable<string?>? plain = null)
    {
        if (secrets.Count == 0) return null;
        var wanted = secrets.Values.Order(StringComparer.Ordinal).ToList();
        var wantedPlain = plain?.Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).Order(StringComparer.Ordinal).ToList();
        foreach (var r in Rows())
        {
            if (wantedPlain is not null)
            {
                List<ConnectionField> stored;
                try { stored = Fields(r.FieldsJson); }
                catch (Exception e) when (IsLoadFailure(e)) { continue; }
                var mine0 = stored.Where(f => !f.Secret && !string.IsNullOrEmpty(f.Value)).Select(f => f.Value!).Order(StringComparer.Ordinal);
                if (!mine0.SequenceEqual(wantedPlain, StringComparer.Ordinal)) continue;
            }
            IReadOnlyDictionary<string, string> mine;
            try { mine = Secrets(r.Id); }
            catch (Exception e) when (IsLoadFailure(e)) { continue; }
            if (mine.Count == wanted.Count && mine.Values.Order(StringComparer.Ordinal).SequenceEqual(wanted, StringComparer.Ordinal)) return r.Id;
        }
        return null;
    }

    private ConnectionView View(Row r)
    {
        var broken = IsBroken(r.Id);
        List<ConnectionField> stored;
        try { stored = Fields(r.FieldsJson); }
        catch (Exception e) when (IsLoadFailure(e)) { stored = []; broken = true; }
        IReadOnlyDictionary<string, string> secrets = new Dictionary<string, string>();
        if (!broken)
        {
            try { secrets = Secrets(r.Id); }
            catch (CredentialStoreException) { /* shown as not set */ }
        }
        if (broken)
            return new ConnectionView(r.Id, r.Name, r.Kind,
                stored.Select(f => new ConnectionFieldView(f.Key, f.Label, f.Secret, f.Secret ? null : f.Value, !f.Secret && f.Value is { Length: > 0 })).ToList(),
                "needs_reconnect", BrokenReason, r.TestedAt, UsedBy(r.Id));
        var fields = stored.Select(f => f.Secret
            ? new ConnectionFieldView(f.Key, f.Label, true, secrets.TryGetValue(f.Key, out var s) ? Mask(s) : null, secrets.ContainsKey(f.Key))
            : new ConnectionFieldView(f.Key, f.Label, false, f.Value, f.Value is { Length: > 0 })).ToList();
        return new ConnectionView(r.Id, r.Name, r.Kind, fields, r.Status, r.StatusReason, r.TestedAt, UsedBy(r.Id));
    }

    private List<string> UsedBy(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM apis WHERE connection_id = $i UNION ALL SELECT 'MCP: ' || name FROM mcp_servers WHERE connection_id = $i ORDER BY 1 COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$i", id);
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    private readonly record struct Row(long Id, string Name, string StoreKey, string Kind, string FieldsJson, string Status, string? StatusReason, string? TestedAt);

    private List<Row> Rows(long? id = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, store_key, kind, fields, status, status_reason, tested_at FROM connections WHERE ($i IS NULL OR id = $i) ORDER BY name COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$i", (object?)id ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<Row>();
        while (r.Read())
            list.Add(new Row(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
        return list;
    }

    private static List<ConnectionField> Fields(string json)
    {
        var list = new List<ConnectionField>();
        foreach (var e in JsonDocument.Parse(json).RootElement.EnumerateArray())
            list.Add(new ConnectionField(e.GetProperty("key").GetString()!, e.GetProperty("label").GetString()!, e.GetProperty("secret").GetBoolean(),
                e.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null));
        return list;
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        using var fk = c.CreateCommand();
        fk.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        fk.ExecuteNonQuery();
        return c;
    }

    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var o = cmd.ExecuteScalar();
        return o is DBNull ? null : o;
    }
}
