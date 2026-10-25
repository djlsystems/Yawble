using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Harness.Contracts;
using Microsoft.AspNetCore.DataProtection;

namespace Harness.Host;

/// <summary>
/// WHAT A CONNECTION ROW HOLDS AND HOW IT READS BACK, for every provider's <see cref="IConnectionStore"/>:
/// the column lists both dialects share, the values each credential column is given and the readers
/// over a <see cref="DbDataReader"/>. No SQL that differs between providers lives here; the statements
/// are each store's own. Every column in a list is read by position, so a store's SELECT names them in
/// exactly this order.
/// </summary>
public static class ConnectionRows
{
    /// <summary>
    /// A provider's columns after its secret, which each store selects as a 0-or-1 integer in position
    /// 4 (SQLite: <c>client_secret_protected IS NOT NULL</c>).
    /// </summary>
    public const string ProviderTail = "tenant, authorize_url, token_url, userinfo_url, default_scopes";

    /// <summary>A connection and its mailbox columns, from <c>connections c LEFT JOIN imap_connections m</c>;
    /// position 17 is the store's own 0-or-1 for whether a mailbox password is set.</summary>
    public const string ConnectionHead =
        "c.id, c.name, c.provider, c.account, c.scopes, c.connected_at, c.refreshed_at, c.status, c.status_reason, "
        + "m.username, m.imap_host, m.imap_port, m.imap_security, m.smtp_host, m.smtp_port, m.smtp_security, m.preset";

    /// <summary>A flow's columns, in the order <see cref="ReadFlow"/> reads them.</summary>
    public const string FlowColumns =
        "state, user_id, provider, scopes, name, reconnect_id, redirect_uri, loopback, verifier_protected, issued_at, used_at, tenant";

    public static StoredProvider ReadProvider(DbDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), NullableText(reader, 2), NullableText(reader, 3),
        reader.GetInt64(4) != 0, NullableText(reader, 5), NullableText(reader, 6), NullableText(reader, 7),
        NullableText(reader, 8), List(reader.GetString(9)));

    public static ConnectionRecord ReadConnection(DbDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), List(reader.GetString(4)),
        Time(reader.GetString(5)), NullableText(reader, 6) is { } refreshed ? Time(refreshed) : null,
        reader.GetString(7), NullableText(reader, 8),
        reader.IsDBNull(9) ? null : new MailboxSettings(
            reader.GetString(9),
            new MailServer(reader.GetString(10), checked((int)reader.GetInt64(11)), reader.GetString(12)),
            new MailServer(reader.GetString(13), checked((int)reader.GetInt64(14)), reader.GetString(15)),
            NullableText(reader, 16), reader.GetInt64(17) != 0),
        NullableText(reader, 18));

    /// <summary>A flow as its row holds it, its verifier opened by <paramref name="unprotect"/>.</summary>
    public static ConnectionFlow ReadFlow(DbDataReader reader, Func<string?, string?> unprotect) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), List(reader.GetString(3)),
        NullableText(reader, 4), NullableText(reader, 5), reader.GetString(6), reader.GetInt64(7) != 0,
        unprotect(reader.GetString(8)) ?? "", Time(reader.GetString(9)),
        NullableText(reader, 10) is { } used ? Time(used) : null, NullableText(reader, 11));

    /// <summary>The three token values a row is given: ciphertext, or null for none.</summary>
    public static (object Refresh, object Access, object Expires) Tokens(ConnectionCipher cipher, ConnectionTokens tokens) => (
        cipher.ProtectOrNull(tokens.RefreshToken),
        cipher.ProtectOrNull(tokens.AccessToken),
        tokens.AccessExpiresAt is { } expires ? Instant.Stamp(expires) : DBNull.Value);

    /// <summary>The tokens a row holds, decrypted.</summary>
    public static ConnectionTokens ReadTokens(DbDataReader reader, ConnectionCipher cipher) => new(
        cipher.Unprotect(NullableText(reader, 0)), cipher.Unprotect(NullableText(reader, 1)),
        NullableText(reader, 2) is { } expires ? Time(expires) : null);

    /// <summary>Every binding in one <c>team_member_config</c> row: connection id and its user, by slot.</summary>
    public static IEnumerable<(string Id, ConnectionUse Use)> Bindings(string team, string member, string connectionsJson) =>
        (JsonSerializer.Deserialize<Dictionary<string, string>>(connectionsJson) ?? [])
            .Select(binding => (binding.Value, new ConnectionUse(team, member, binding.Key)));

    /// <summary>A mailbox's verified time as it is first written, round-trip text as SQLite's has always been.</summary>
    public static string MailboxVerified(ConnectionRecord record) =>
        (record.RefreshedAt ?? record.ConnectedAt).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>The issue time before which a flow is swept as a new one arrives: a day.</summary>
    public static string FlowCutoff(ConnectionFlow flow) =>
        flow.IssuedAt.AddDays(-1).ToString("O", CultureInfo.InvariantCulture);

    public static string Scopes(IReadOnlyList<string> scopes) => JsonSerializer.Serialize(scopes);

    public static string? NullableText(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static IReadOnlyList<string> List(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];

    public static DateTimeOffset Time(string text) => Instant.Parse(text);
}

/// <summary>
/// THE ONE CIPHER EVERY CONNECTION CREDENTIAL COLUMN IS WRITTEN WITH, whichever database holds it: Data
/// Protection under the purpose <see cref="Purpose"/> and the instance's key ring, so a row one provider
/// wrote opens through the other's store.
/// </summary>
public sealed class ConnectionCipher(IDataProtectionProvider protection)
{
    public const string Purpose = "Harness.Connections.v1";

    private readonly IDataProtector _protector = protection.CreateProtector(Purpose);

    public string Protect(string value) => _protector.Protect(value);

    /// <summary>Ciphertext of a value, or <see cref="DBNull.Value"/> for none or empty.</summary>
    public object ProtectOrNull(string? value) => value is { Length: > 0 } text ? _protector.Protect(text) : DBNull.Value;

    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;

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
}
