using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.DataProtection;

namespace Harness.Tests.Contracts;

/// <summary>
/// A SECRET SEALED ON ONE PROVIDER OPENS ON THE OTHER, BYTE FOR BYTE: a plugin key, a member's own value
/// and its named secrets, and a connection's tokens, client secret, mailbox password and PKCE verifier are Data Protection
/// ciphertext the shared classes seal (<see cref="PluginSecretStore"/>, <see cref="PluginMemberSecretStore"/>,
/// <see cref="ConnectionCipher"/>), and each provider's rows keep that text exactly as given. Each row is
/// written through one provider's store, copied raw, column for column, into the other's tables past the
/// stores, read back identical, and opened through the other's store with the same key ring.
/// </summary>
public sealed class HostSecretPortabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly CredentialActor Person = new(null, "person@example.test");

    private static readonly DateTimeOffset At = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    private static readonly (string Table, string Columns)[] Rows =
    [
        ("plugin_secrets", "[key], value_protected, set_by, set_at"),
        ("plugin_member_secrets", "team, name, field, value_protected, set_by, set_at"),
        ("plugin_member_named_secrets", "team, name, secret, value_protected, set_by, set_at"),
        ("oauth_providers", "id, kind, name, client_id, client_secret_protected, tenant, authorize_url, token_url, userinfo_url, default_scopes, updated_at"),
        ("connections", "id, name, provider, account, scopes, refresh_token_protected, access_token_protected, access_expires_at, connected_at, refreshed_at, status, status_reason, connected_by, tenant"),
        ("imap_connections", "id, username, imap_host, imap_port, imap_security, smtp_host, smtp_port, smtp_security, preset, password_protected"),
        ("connection_flows", "state, user_id, provider, scopes, name, reconnect_id, redirect_uri, loopback, verifier_protected, issued_at, used_at, tenant"),
    ];

    [Fact]
    public async Task Every_secret_sealed_on_sqlite_opens_on_sql_server()
    {
        await using var sqlServer = await SqlServerIdentityDatabase.CreateAsync();
        await using var sqlite = new SqliteIdentityDatabase();

        await RoundTripAsync(sqlite, sqlServer);
    }

    [Fact]
    public async Task Every_secret_sealed_on_sql_server_opens_on_sqlite()
    {
        await using var sqlServer = await SqlServerIdentityDatabase.CreateAsync();
        await using var sqlite = new SqliteIdentityDatabase();

        await RoundTripAsync(sqlServer, sqlite);
    }

    private static async Task RoundTripAsync(IdentityDatabase from, IdentityDatabase to)
    {
        var keys = new EphemeralDataProtectionProvider();
        var member = new ContainerId("Alpha", "Dev");
        foreach (var database in new[] { from, to })
        {
            await database.Teams().SaveTeamAsync(new PersistedTeam("Alpha", null), Ct);
            await database.Teams().SaveMemberAsync(new PersistedMember("Alpha", "Dev", null, "plugin:fixture", null, [], [], 0, null), Ct);
        }

        // SEALED ON ONE.
        await new PluginSecretStore(from.SecretRows(), keys, new NoEnvironment()).SetAsync("ACME_KEY", "plugin-key-value-1234", Person, Ct);
        await new PluginMemberSecretStore(from.MemberSecretRows(), keys).SetAsync(member, "Dev", "fixture", "token", "member-value-1234", Person, Ct);
        await new PluginMemberSecretStore(from.MemberSecretRows(), keys).ChangeNamedAsync(
            member, "Dev", "fixture", new Dictionary<string, string?> { ["ApiKey"] = "named-value-1234" }, Person, Ct);
        var connections = from.Connections(keys);
        await connections.SaveProviderAsync(
            new StoredProvider("custom-acme", "custom", "Acme", "client", false, null, "https://auth.example.test/a", "https://auth.example.test/t", null, ["openid"]),
            "client-secret-1234", Audit("custom-acme"), Ct);
        await connections.InsertAsync(
            new ConnectionRecord("conn-a", "Work", "custom-acme", "person@example.test", ["openid"], At, null, ConnectionRecord.Ok, null),
            new ConnectionTokens("refresh-token-1234", "access-token-1234", At.AddHours(1)), "person", Audit("conn-a"), Ct);
        await connections.InsertMailboxAsync(
            new ConnectionRecord("conn-mail", "Mail", MailboxSettings.Kind, "mail@example.test", [], At, At, ConnectionRecord.Ok, null),
            new MailboxSettings("mail", new MailServer("imap.example.test", 993, MailServer.Tls), new MailServer("smtp.example.test", 587, MailServer.StartTls), null, true),
            "app-password-1234", "person", Audit("conn-mail"), Ct);
        await connections.InsertFlowAsync(
            new ConnectionFlow("state-1", "person", "custom-acme", ["openid"], null, null, "http://127.0.0.1:5000/", true, "verifier-1234", At, null), Ct);

        // COPIED RAW, AND IDENTICAL.
        foreach (var (table, columns) in Rows)
        {
            var written = await from.RowsAsync($"SELECT {columns} FROM {table}");
            Assert.NotEmpty(written);
            foreach (var row in written)
            {
                // Every value is an id, base64url ciphertext, a JSON list, a host, a port or an instant:
                // none holds a quote.
                var values = string.Join(", ", row.Select(v => v is null ? "NULL" : $"'{v}'"));
                await to.ExecuteAsync($"INSERT INTO {table} ({columns}) VALUES ({values})");
            }

            Assert.Equal(Text(written), Text(await to.RowsAsync($"SELECT {columns} FROM {table}")));
        }

        // OPENED ON THE OTHER.
        Assert.Equal("plugin-key-value-1234", new PluginSecretStore(to.SecretRows(), keys, new NoEnvironment()).TryGet("ACME_KEY"));
        Assert.Equal(from.SecretRows().Find("ACME_KEY"), to.SecretRows().Find("ACME_KEY"));
        Assert.Equal("member-value-1234", (await new PluginMemberSecretStore(to.MemberSecretRows(), keys).ValuesAsync(member, Ct))["token"]);
        Assert.Equal("named-value-1234", (await new PluginMemberSecretStore(to.MemberSecretRows(), keys).NamedValuesAsync(member, Ct))["ApiKey"]);
        var other = to.Connections(keys);
        Assert.Equal("client-secret-1234", await other.ClientSecretAsync("custom-acme", Ct));
        Assert.Equal(new ConnectionTokens("refresh-token-1234", "access-token-1234", At.AddHours(1)), await other.TokensAsync("conn-a", Ct));
        Assert.Equal("app-password-1234", await other.MailboxPasswordAsync("conn-mail", Ct));
        Assert.Equal("verifier-1234", (await other.ConsumeFlowAsync("state-1", At, "person", Ct))!.CodeVerifier);
    }

    /// <summary>Every value as its text, so a number read back as a long on one provider and an int on the other compares equal.</summary>
    private static string[] Text(IReadOnlyList<object?[]> rows) =>
        [.. rows.Select(row => string.Join(" | ", row.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "NULL"))).Order(StringComparer.Ordinal)];

    private static TriggerAudit Audit(string subject) => new(null, "person@example.test", TenantActions.ConnectionConnected, subject, subject, null);

    private sealed class NoEnvironment : ISecretStore
    {
        public string? TryGet(string logicalKey) => null;
    }
}
