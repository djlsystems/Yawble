using System.Security.Cryptography;
using System.Text;
using Yawble.Core.Credentials;

namespace Yawble.Core.Teams;

/// <summary>
/// A team's stored variables as this computer reads them: the values it could read, and the names whose value it
/// could not (the key that protects them is not in this computer's credential store).
/// </summary>
/// <param name="Values">What a run gets: the readable variables only.</param>
/// <param name="Unreadable">The names whose value could not be read.</param>
/// <param name="Shown">Every name in its stored order: a readable one with its value, an unreadable one with <see cref="TeamEnv.UnreadableValue"/>.</param>
public sealed record TeamEnvRead(IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Unreadable, IReadOnlyDictionary<string, string> Shown);

/// <summary>
/// Keeps the values of the teams' environment variables encrypted in the database (teams.env), so the database file
/// and every backup of it hold no readable value. The names stay readable. Each value is sealed with AES-256-GCM
/// under one key for this computer, and the key is kept in the credential store (<see cref="KeyName"/>), never in the
/// database or a backup. A sealed value is tied to its variable's name: moved under another name it does not open.
/// <para>
/// A database brought to a computer whose credential store has no key, or another key, still lists every name; the
/// values read as unreadable (<see cref="TeamEnvRead.Unreadable"/>), no run gets them, and the person enters them again.
/// </para>
/// </summary>
public sealed class TeamEnvVault(ICredentialStore store, Action<string>? log = null)
{
    /// <summary>The key's name in the credential store, under the app's own service name.</summary>
    public const string KeyName = "team-env.key";

    /// <summary>What a sealed value starts with in teams.env; the rest is base64 of nonce, tag and ciphertext.</summary>
    public const string SealedPrefix = "enc:v1:";

    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly Lock _gate = new();
    private byte[]? _key;

    public static bool IsSealed(string stored) => stored.StartsWith(SealedPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Reads a team's stored variables. Never throws: a sealed value that does not open (no key here, another key, a
    /// credential store that does not answer, damaged text) is unreadable. Without a vault every sealed value is
    /// unreadable. A value stored unsealed is read as it is.
    /// </summary>
    public static TeamEnvRead Read(TeamEnvVault? vault, string? json)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        var shown = new Dictionary<string, string>(StringComparer.Ordinal);
        byte[]? key = null;
        var asked = false;
        foreach (var (name, stored) in TeamEnv.Parse(json))
        {
            if (IsSealed(stored))
            {
                if (!asked) { key = vault?.KeyOrNull(); asked = true; }
                if (key is null || Open(key, name, stored) is not { } value) { unreadable.Add(name); shown[name] = TeamEnv.UnreadableValue; continue; }
                values[name] = value;
            }
            else values[name] = stored;
            shown[name] = values[name];
        }
        return new TeamEnvRead(values, unreadable, shown);
    }

    /// <summary>
    /// The text to store for a save of a team's variables (whole), or the problems that stop it. Every value is sealed.
    /// An entry whose value is <see cref="TeamEnv.UnreadableValue"/> keeps the sealed value stored under its name: the
    /// person left a value this computer cannot read as it is. Without a vault the values are stored as given.
    /// </summary>
    public static (string? Json, IReadOnlyList<string> Problems) Write(
        TeamEnvVault? vault, IReadOnlyList<KeyValuePair<string, string>> entries, string? storedJson)
    {
        var stored = TeamEnv.Parse(storedJson);
        var result = new List<KeyValuePair<string, string>>();
        var problems = new List<string>();
        byte[]? key = null;
        foreach (var (name, value) in entries)
        {
            if (value == TeamEnv.UnreadableValue)
            {
                if (stored.TryGetValue(name, out var kept) && IsSealed(kept)) result.Add(new(name, kept));
                else problems.Add($"'{name}' has no value yet: give it one");
                continue;
            }
            if (vault is null) { result.Add(new(name, value)); continue; }
            if (key is null)
            {
                try { key = vault.KeyOrNew(); }
                catch (Exception e) when (StoreFailure(e))
                {
                    return (null, [$"this computer's credential store did not give or keep the key that protects the values ({e.Message})"]);
                }
            }
            result.Add(new(name, Seal(key, name, value)));
        }
        return problems.Count != 0 ? (null, problems) : (TeamEnv.Serialize(result), problems);
    }

    /// <summary>What <see cref="SealStored"/> found: the teams whose values it sealed, and the unreadable values per team.</summary>
    public sealed record StoredState(int TeamsSealed, IReadOnlyDictionary<string, int> UnreadableByTeam);

    /// <summary>
    /// Seals every value that is stored unsealed, in every team: run at each start, before anything reads the
    /// variables. A database written by a version that stored the values as they are, or a restored backup of one, is
    /// sealed here. Sealed values are left as they are. When the credential store does not give or keep the key,
    /// nothing is changed and the log says so; the next start tries again.
    /// </summary>
    public StoredState SealStored(string connectionString)
    {
        using var c = Storage.Db.Open(connectionString);
        var teams = new List<(long Id, string Name, string Env)>();
        using (var read = c.CreateCommand())
        {
            read.CommandText = "SELECT id, name, env FROM teams WHERE env <> '{}' ORDER BY id;";
            using var r = read.ExecuteReader();
            while (r.Read()) teams.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        }
        var sealedTeams = 0;
        var unreadable = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name, storedEnv) in teams)
        {
            var env = storedEnv;
            var stored = TeamEnv.Parse(env);
            if (stored.Values.Any(v => !IsSealed(v)))
            {
                byte[] key;
                try { key = KeyOrNew(); }
                catch (Exception e) when (StoreFailure(e))
                {
                    log?.Invoke($"team environment: the values are not encrypted yet, because this computer's credential store did not give or keep the key ({e.Message})");
                    return new StoredState(sealedTeams, unreadable);
                }
                var json = TeamEnv.Serialize(stored.Select(e => new KeyValuePair<string, string>(e.Key, IsSealed(e.Value) ? e.Value : Seal(key, e.Key, e.Value))));
                using var write = c.CreateCommand();
                write.CommandText = "UPDATE teams SET env = $e WHERE id = $id;";
                write.Parameters.AddWithValue("$e", json);
                write.Parameters.AddWithValue("$id", id);
                write.ExecuteNonQuery();
                sealedTeams++;
                env = json;
            }
            var lost = Read(this, env).Unreadable.Count;
            if (lost > 0)
            {
                unreadable[name] = lost;
                log?.Invoke($"team environment: {lost} value(s) of team '{name}' cannot be read on this computer (the key that protects them is not in its credential store). Enter them again in Team settings > Environment.");
            }
        }
        if (sealedTeams > 0) log?.Invoke($"team environment: encrypted the values of {sealedTeams} team(s)");
        return new StoredState(sealedTeams, unreadable);
    }

    /// <summary>The key when the credential store has one; null when it has none or does not answer.</summary>
    private byte[]? KeyOrNull()
    {
        try { return Key(create: false); }
        catch (Exception e) when (StoreFailure(e))
        {
            log?.Invoke($"team environment: the credential store did not give the key ({e.Message})");
            return null;
        }
    }

    /// <summary>The key, made and kept in the credential store when it has none. Throws what the store throws.</summary>
    private byte[] KeyOrNew() => Key(create: true)!;

    private byte[]? Key(bool create)
    {
        lock (_gate)
        {
            if (_key is not null) return _key;
            if (store.Get(KeyName) is { } text && Decode(text) is { } found) return _key = found;
            if (!create) return null;
            var fresh = RandomNumberGenerator.GetBytes(KeyBytes);
            store.Set(KeyName, Convert.ToBase64String(fresh));
            return _key = fresh;
        }
    }

    private static byte[]? Decode(string text)
    {
        try { return Convert.FromBase64String(text) is { Length: KeyBytes } key ? key : null; }
        catch (FormatException) { return null; }
    }

    private static bool StoreFailure(Exception e) => e is CredentialStoreException or IOException or UnauthorizedAccessException;

    private static string Seal(byte[] key, string name, string value)
    {
        var plain = Encoding.UTF8.GetBytes(value);
        var packed = new byte[NonceBytes + TagBytes + plain.Length];
        var nonce = packed.AsSpan(0, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plain, packed.AsSpan(NonceBytes + TagBytes), packed.AsSpan(NonceBytes, TagBytes), Encoding.UTF8.GetBytes(name));
        return SealedPrefix + Convert.ToBase64String(packed);
    }

    private static string? Open(byte[] key, string name, string stored)
    {
        try
        {
            var packed = Convert.FromBase64String(stored[SealedPrefix.Length..]);
            if (packed.Length < NonceBytes + TagBytes) return null;
            var plain = new byte[packed.Length - NonceBytes - TagBytes];
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(packed.AsSpan(0, NonceBytes), packed.AsSpan(NonceBytes + TagBytes), packed.AsSpan(NonceBytes, TagBytes), plain, Encoding.UTF8.GetBytes(name));
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return null;
        }
    }
}
