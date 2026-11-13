using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Companion;

public sealed record CompanionClient(long Id, string Name, CompanionLevel Level, DateTimeOffset PairedAt, string? PairedBy, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);

/// <summary>The paired external clients (0036_companion_clients.sql). Keys are kept only as SHA-256 hex.</summary>
public sealed partial class CompanionClientStore(string connectionString, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private const string Columns = "id, name, level, paired_at, paired_by, last_used_at, revoked_at";

    // \z, not $: a trailing newline must not pass.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,31}\z")]
    private static partial Regex NamePattern();

    public static bool ValidName(string name) => name is not null && NamePattern().IsMatch(name);

    private static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private string Now() => _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Pairs a client; the key comes back once in <paramref name="plainKey"/>. A revoked row of the same name is replaced, by a
    /// row with a higher id than any there was (the old row's id included): a re-paired name is never the same id, so nothing
    /// kept by id (a request, its result, a follow-up) passes from the old client to the new one.
    /// </summary>
    public CompanionClient Add(string name, CompanionLevel level, string pairedBy, out string plainKey)
    {
        if (!ValidName(name)) throw new ArgumentException("a client name is lowercase letters, digits and '-', 1 to 32 characters", nameof(name));
        plainKey = "ywb_" + Base64Url(RandomNumberGenerator.GetBytes(32));
        using var c = Open();
        using var tx = c.BeginTransaction();
        long nextId;
        using (var max = c.CreateCommand())
        {
            // Read before the revoked row goes: a plain INTEGER PRIMARY KEY would otherwise hand its id out again.
            max.Transaction = tx;
            max.CommandText = "SELECT COALESCE(MAX(id), 0) + 1 FROM companion_clients;";
            nextId = Convert.ToInt64(max.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM companion_clients WHERE name = $name AND revoked_at IS NOT NULL;";
            del.Parameters.AddWithValue("$name", name);
            del.ExecuteNonQuery();
        }
        try
        {
            using var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO companion_clients (id, name, key_hash, level, paired_at, paired_by) VALUES ($id, $name, $hash, $level, $at, $by) RETURNING " + Columns + ";";
            ins.Parameters.AddWithValue("$id", nextId);
            ins.Parameters.AddWithValue("$name", name);
            ins.Parameters.AddWithValue("$hash", Hash(plainKey));
            ins.Parameters.AddWithValue("$level", CompanionLevels.Name(level));
            ins.Parameters.AddWithValue("$at", Now());
            ins.Parameters.AddWithValue("$by", (object?)pairedBy ?? DBNull.Value);
            CompanionClient row;
            using (var r = ins.ExecuteReader())
            {
                r.Read();
                row = Read(r);
            }
            tx.Commit();
            return row;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"a client named '{name}' is already paired");
        }
    }

    /// <summary>The client when <paramref name="presentedKey"/> is its key and it is not revoked; otherwise null.</summary>
    public CompanionClient? Authenticate(string name, string presentedKey)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(presentedKey)) return null;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT key_hash, " + Columns + " FROM companion_clients WHERE name = $name AND revoked_at IS NULL;";
        cmd.Parameters.AddWithValue("$name", name);
        string stored;
        CompanionClient client;
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return null;
            stored = r.GetString(0);
            client = Read(r, 1);
        }
        var a = Encoding.UTF8.GetBytes(stored);
        var b = Encoding.UTF8.GetBytes(Hash(presentedKey));
        if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b)) return null;

        var now = _clock.GetUtcNow();
        if (client.LastUsedAt is { } last && now - last < TimeSpan.FromMinutes(1)) return client;
        using var touch = c.CreateCommand();
        touch.CommandText = "UPDATE companion_clients SET last_used_at = $at WHERE id = $id;";
        touch.Parameters.AddWithValue("$at", now.ToString("O", CultureInfo.InvariantCulture));
        touch.Parameters.AddWithValue("$id", client.Id);
        touch.ExecuteNonQuery();
        return client with { LastUsedAt = now };
    }

    public IReadOnlyList<CompanionClient> List()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT " + Columns + " FROM companion_clients ORDER BY id;";
        using var r = cmd.ExecuteReader();
        var rows = new List<CompanionClient>();
        while (r.Read()) rows.Add(Read(r));
        return rows;
    }

    /// <summary>Revokes an active client; false when none by that name is active.</summary>
    public bool Revoke(string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE companion_clients SET revoked_at = $at WHERE name = $name AND revoked_at IS NULL;";
        cmd.Parameters.AddWithValue("$at", Now());
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Revokes exactly the client row <paramref name="id"/> if it is still active (not a later client of the same name).</summary>
    public bool RevokeId(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE companion_clients SET revoked_at = $at WHERE id = $id AND revoked_at IS NULL;";
        cmd.Parameters.AddWithValue("$at", Now());
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        return c;
    }

    private static CompanionClient Read(SqliteDataReader r, int o = 0)
    {
        CompanionLevels.TryParse(r.GetString(o + 2), out var level);
        return new CompanionClient(
            r.GetInt64(o), r.GetString(o + 1), level, Time(r.GetString(o + 3)),
            r.IsDBNull(o + 4) ? null : r.GetString(o + 4),
            r.IsDBNull(o + 5) ? null : Time(r.GetString(o + 5)),
            r.IsDBNull(o + 6) ? null : Time(r.GetString(o + 6)));
    }

    private static DateTimeOffset Time(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
