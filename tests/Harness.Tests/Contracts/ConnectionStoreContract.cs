using System.Data.Common;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="IConnectionStore"/>, as each provider must keep it. From <c>FreshVolumeTests</c>, same values:
/// the mailbox round trip and the directory (tenant) round trip. The rest are <see cref="ConnectionStore"/>'s
/// remarks, which the SQLite suite reaches only through the Host's routes: providers (a kept, cleared and
/// replaced client secret; a removal refused while a connection of it exists), connections (tokens sealed,
/// a refresh keeping an unrotated refresh token, a refused refresh, a rename, a removal refused while a
/// member binds it), mailboxes, and flows (consumed once, by their own person). Then the audit atomicity,
/// the case and trailing-space map of an id, long text, and concurrency: two first saves of a provider,
/// two callbacks with one state, and two refreshes.
/// </summary>
public abstract class ConnectionStoreContract : HostStoreContract
{
    private static readonly DateTimeOffset At = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private async Task<IConnectionStore> StoreAsync() => (await DatabaseAsync()).Connections(Protection);

    private static TriggerAudit Row(string action, string subject) => Audit(action, subject);

    private static ConnectionRecord Record(string id, string name, string provider = "google", string? tenant = null) =>
        new(id, name, provider, $"{id}@example.test", ["openid", "email"], At, null, ConnectionRecord.Ok, null, Tenant: tenant);

    private static StoredProvider Provider(string id, string? name = null) =>
        new(id, ConnectionProviders.KindOf(id), name, "client-id", false, null,
            id.StartsWith("custom-", StringComparison.Ordinal) ? "https://auth.example.test/authorize" : null,
            id.StartsWith("custom-", StringComparison.Ordinal) ? "https://auth.example.test/token" : null, null, ["openid"]);

    private Task InsertAsync(IConnectionStore store, string id, string name, ConnectionTokens? tokens = null, string provider = "google") =>
        store.InsertAsync(Record(id, name, provider), tokens ?? new ConnectionTokens("refresh-1", "access-1", At.AddHours(1)), "person",
            Row(TenantActions.ConnectionConnected, id), Ct);

    private static MailboxSettings Mailbox => new(
        "mailbox", new MailServer("imap.mail.me.com", 993, MailServer.Tls), new MailServer("smtp.mail.me.com", 587, MailServer.StartTls), "icloud", true);

    /// <summary>FROM <c>FreshVolumeTests.MailboxRoundTripAsync</c>, same values.</summary>
    [Fact]
    public async Task A_mailbox_round_trips_with_its_servers_and_its_password_sealed()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync();

        await store.InsertMailboxAsync(
            new ConnectionRecord("conn-mail", "Mail", MailboxSettings.Kind, "mailbox@icloud.example", [], At, At, ConnectionRecord.Ok, null),
            Mailbox, "app-password-value", "person", Row(TenantActions.ConnectionConnected, "conn-mail"), Ct);

        var read = (await store.GetAsync("conn-mail", Ct))!;
        Assert.Equal(Mailbox, read.Mailbox);
        Assert.Equal(At, read.RefreshedAt);
        Assert.Equal("app-password-value", await store.MailboxPasswordAsync("conn-mail", Ct));
        Assert.DoesNotContain("app-password-value", await database.ColumnAsync("SELECT password_protected FROM imap_connections"));
    }

    /// <summary>FROM <c>FreshVolumeTests.DirectoryRoundTripAsync</c>, same values.</summary>
    [Fact]
    public async Task A_connection_keeps_the_directory_it_signed_in_through()
    {
        var store = await StoreAsync();

        await store.InsertAsync(
            new ConnectionRecord("conn-dir", "Directory", "microsoft", "person@example.test", ["openid"], At, null, ConnectionRecord.Ok, null,
                Tenant: "example.onmicrosoft.com"),
            new ConnectionTokens(null, null, null), "person",
            new TriggerAudit(null, "person@example.test", TenantActions.ConnectionConnected, "conn-dir", "Directory", null), Ct);

        Assert.Equal("example.onmicrosoft.com", (await store.GetAsync("conn-dir", Ct))!.Tenant);
    }

    [Fact]
    public async Task A_provider_round_trips_and_its_client_secret_is_kept_cleared_or_replaced()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync();
        var custom = Provider("custom-acme", "Acme") with { UserinfoUrl = "https://auth.example.test/me", Tenant = "acme" };

        await store.SaveProviderAsync(custom, "client-secret-1", Row(TenantActions.ConnectionProviderSaved, "custom-acme"), Ct);
        await store.SaveProviderAsync(Provider("google"), null, Row(TenantActions.ConnectionProviderSaved, "google"), Ct);

        Assert.Equal(["custom-acme", "google"], (await store.ProvidersAsync(Ct)).Select(p => p.Id));
        Assert.Equal(custom with { ClientSecretSet = true }, await store.ProviderAsync("custom-acme", Ct), new ProviderComparer());
        Assert.Equal("client-secret-1", await store.ClientSecretAsync("custom-acme", Ct));
        Assert.DoesNotContain("client-secret-1", await database.ColumnAsync("SELECT client_secret_protected FROM oauth_providers"));
        Assert.False((await store.ProviderAsync("google", Ct))!.ClientSecretSet);
        Assert.Null(await store.ProviderAsync("custom-none", Ct));

        // Null keeps the secret, a new one replaces it, "" clears it.
        await store.SaveProviderAsync(custom with { Name = "Acme 2" }, null, Row(TenantActions.ConnectionProviderSaved, "custom-acme"), Ct);
        Assert.Equal(("Acme 2", "client-secret-1"), ((await store.ProviderAsync("custom-acme", Ct))!.Name, await store.ClientSecretAsync("custom-acme", Ct)));
        await store.SaveProviderAsync(custom, "client-secret-2", Row(TenantActions.ConnectionProviderSaved, "custom-acme"), Ct);
        Assert.Equal("client-secret-2", await store.ClientSecretAsync("custom-acme", Ct));
        await store.SaveProviderAsync(custom, "", Row(TenantActions.ConnectionProviderSaved, "custom-acme"), Ct);
        Assert.Null(await store.ClientSecretAsync("custom-acme", Ct));
        Assert.False((await store.ProviderAsync("custom-acme", Ct))!.ClientSecretSet);

        Assert.Equal(5, await database.LongAsync($"SELECT COUNT(*) FROM tenant_events WHERE action = '{TenantActions.ConnectionProviderSaved}'"));
    }

    [Fact]
    public async Task A_provider_is_not_removed_while_a_connection_of_it_exists_and_names_them()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync();
        await store.SaveProviderAsync(Provider("custom-acme"), null, Row(TenantActions.ConnectionProviderSaved, "custom-acme"), Ct);
        await InsertAsync(store, "conn-b", "beta", provider: "custom-acme");
        await InsertAsync(store, "conn-a", "Alpha", provider: "custom-acme");

        var refused = await store.DeleteProviderAsync("custom-acme", Row(TenantActions.ConnectionProviderRemoved, "custom-acme"), Ct);
        Assert.False(refused.Removed);
        Assert.Equal(["Alpha", "beta"], refused.Connections);
        Assert.NotNull(await store.ProviderAsync("custom-acme", Ct));

        await store.DeleteAsync("conn-a", Row(TenantActions.ConnectionDisconnected, "conn-a"), Ct);
        await store.DeleteAsync("conn-b", Row(TenantActions.ConnectionDisconnected, "conn-b"), Ct);
        var removed = await store.DeleteProviderAsync("custom-acme", Row(TenantActions.ConnectionProviderRemoved, "custom-acme"), Ct);

        Assert.Equal((true, 0), (removed.Removed, removed.Connections.Count));
        Assert.Null(await store.ProviderAsync("custom-acme", Ct));
        Assert.Equal(1, await database.LongAsync($"SELECT COUNT(*) FROM tenant_events WHERE action = '{TenantActions.ConnectionProviderRemoved}'"));
    }

    [Fact]
    public async Task A_connection_round_trips_and_its_tokens_are_sealed()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync();

        await InsertAsync(store, "conn-a", "Work mail", new ConnectionTokens("refresh-secret", "access-secret", At.AddHours(1)));

        var read = (await store.GetAsync("conn-a", Ct))!;
        Assert.Equal(Record("conn-a", "Work mail") with { Scopes = read.Scopes }, read);
        Assert.Equal(["openid", "email"], read.Scopes);
        Assert.Equal(new ConnectionTokens("refresh-secret", "access-secret", At.AddHours(1)), await store.TokensAsync("conn-a", Ct));
        var columns = (await database.RowsAsync("SELECT refresh_token_protected, access_token_protected FROM connections")).Single();
        Assert.DoesNotContain(columns, c => ((string)c!).Contains("secret", StringComparison.Ordinal));
        Assert.Null(await store.TokensAsync("conn-none", Ct));
        Assert.Null(await store.GetAsync("conn-none", Ct));

        // No token at all is stored as none.
        await InsertAsync(store, "conn-b", "Empty", new ConnectionTokens(null, "", null));
        Assert.Equal(new ConnectionTokens(null, null, null), await store.TokensAsync("conn-b", Ct));
    }

    [Fact]
    public async Task The_list_is_in_name_order_whatever_the_case()
    {
        var store = await StoreAsync();
        await InsertAsync(store, "conn-3", "beta");
        await InsertAsync(store, "conn-2", "Alpha");
        await InsertAsync(store, "conn-1", "alpha two");
        await InsertAsync(store, "conn-4", "a-z");
        await InsertAsync(store, "conn-5", "a_z");

        Assert.Equal(["conn-4", "conn-5", "conn-2", "conn-1", "conn-3"], (await store.ListAsync(Ct)).Select(c => c.Id));
    }

    [Fact]
    public async Task A_reconnect_a_refresh_and_a_rotation_keep_an_unreissued_refresh_token()
    {
        var store = await StoreAsync();
        await InsertAsync(store, "conn-a", "A", new ConnectionTokens("refresh-1", "access-1", At.AddHours(1)));
        await store.MarkNeedsReconnectAsync("conn-a", "invalid_grant", Row(TenantActions.ConnectionNeedsReconnect, "conn-a"), Ct);

        var needs = (await store.GetAsync("conn-a", Ct))!;
        Assert.Equal((ConnectionRecord.NeedsReconnect, "invalid_grant"), (needs.Status, needs.StatusReason));
        Assert.Equal(new ConnectionTokens("refresh-1", null, null), await store.TokensAsync("conn-a", Ct));

        await store.ReconnectAsync("conn-a", ["mail"], new ConnectionTokens(null, "access-2", At.AddHours(2)), Row(TenantActions.ConnectionReconnected, "conn-a"), Ct, tenant: "contoso");
        var back = (await store.GetAsync("conn-a", Ct))!;
        Assert.Equal((ConnectionRecord.Ok, null, "contoso"), (back.Status, back.StatusReason, back.Tenant));
        Assert.Equal(["mail"], back.Scopes);
        Assert.NotNull(back.RefreshedAt);
        Assert.Equal(new ConnectionTokens("refresh-1", "access-2", At.AddHours(2)), await store.TokensAsync("conn-a", Ct));

        await store.StoreRefreshAsync("conn-a", new ConnectionTokens("refresh-3", "access-3", At.AddHours(3)), Ct);
        Assert.Equal(new ConnectionTokens("refresh-3", "access-3", At.AddHours(3)), await store.TokensAsync("conn-a", Ct));
        await store.StoreRefreshAsync("conn-a", new ConnectionTokens(null, "access-4", null), Ct);
        Assert.Equal(new ConnectionTokens("refresh-3", "access-4", null), await store.TokensAsync("conn-a", Ct));

        await store.StoreRotatedRefreshAsync("conn-a", null, Ct);
        Assert.Equal("refresh-3", (await store.TokensAsync("conn-a", Ct))!.RefreshToken);
        await store.StoreRotatedRefreshAsync("conn-a", "refresh-5", Ct);
        Assert.Equal(new ConnectionTokens("refresh-5", "access-4", null), await store.TokensAsync("conn-a", Ct));
    }

    [Fact]
    public async Task A_rename_answers_whether_the_connection_exists_and_writes_its_row_only_then()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync();
        await InsertAsync(store, "conn-a", "A");

        Assert.True(await store.RenameAsync("conn-a", "Renamed", Row(TenantActions.ConnectionRenamed, "conn-a"), Ct));
        Assert.False(await store.RenameAsync("conn-none", "Nobody", Row(TenantActions.ConnectionRenamed, "conn-none"), Ct));

        Assert.Equal("Renamed", (await store.GetAsync("conn-a", Ct))!.Name);
        Assert.Equal(["conn-a"], await database.ColumnAsync($"SELECT subject FROM tenant_events WHERE action = '{TenantActions.ConnectionRenamed}'"));
    }

    /// <summary>
    /// A DELETE ASKS FIRST, THEN UNBINDS. Unconfirmed (no <c>unbind</c>), a connection a member binds is
    /// kept and its users are named. Confirmed, the connection goes with every member's binding of it -
    /// the other slots kept - and the connection's row and each member's row land in the same transaction.
    /// </summary>
    [Fact]
    public async Task A_used_connection_is_named_first_then_deleted_with_every_binding_of_it_and_their_rows()
    {
        var database = await DatabaseAsync();
        await HireAsync(database, "Beta", "Dev");
        await HireAsync(database, "Alpha", "Ops", "Dev");
        var store = await StoreAsync();
        var settings = database.MemberSettings();
        await InsertAsync(store, "conn-a", "A");
        await InsertAsync(store, "conn-b", "B");
        foreach (var (team, member) in new[] { ("Beta", "Dev"), ("Alpha", "Ops"), ("Alpha", "Dev") })
        {
            await settings.SaveAsync(new ContainerId(team, member), Bind(("mail", "conn-a"), ("files", "conn-b")), Ct);
        }

        var asked = await store.DeleteAsync("conn-a", Row(TenantActions.ConnectionDisconnected, "conn-a"), Ct);

        IReadOnlyList<ConnectionUse> users = [new("Alpha", "Dev", "mail"), new("Alpha", "Ops", "mail"), new("Beta", "Dev", "mail")];
        Assert.False(asked.Deleted);
        Assert.Equal(users, asked.UsedBy);
        Assert.Equal(users, await store.UsedByAsync("conn-a", Ct));
        Assert.NotNull(await store.GetAsync("conn-a", Ct));
        Assert.Equal(0, await database.LongAsync($"SELECT COUNT(*) FROM tenant_events WHERE action = '{TenantActions.ConnectionDisconnected}'"));

        IReadOnlyList<ConnectionUse>? handed = null;
        var deleted = await store.DeleteAsync("conn-a", Row(TenantActions.ConnectionDisconnected, "conn-a"), Ct, found =>
        {
            handed = found;
            return [.. found.Select(u => Row(TenantActions.MemberConnectionsChanged, $"{u.Team}/{u.Member}"))];
        });

        Assert.True(deleted.Deleted);
        Assert.Equal(users, deleted.UsedBy);
        Assert.Equal(users, handed);
        Assert.Null(await store.GetAsync("conn-a", Ct));
        Assert.Empty(await store.UsedByAsync("conn-a", Ct));
        foreach (var (team, member) in new[] { ("Beta", "Dev"), ("Alpha", "Ops"), ("Alpha", "Dev") })
        {
            var left = (await settings.ForAsync(new ContainerId(team, member), Ct)).Connections;
            Assert.Equal(new Dictionary<string, string> { ["files"] = "conn-b" }, left);
        }

        Assert.Equal(3, (await store.AllUsesAsync(Ct))["conn-b"].Count());
        Assert.Equal(1, await database.LongAsync($"SELECT COUNT(*) FROM tenant_events WHERE action = '{TenantActions.ConnectionDisconnected}'"));
        Assert.Equal(
            ["Alpha/Dev", "Alpha/Ops", "Beta/Dev"],
            (await database.ColumnAsync($"SELECT subject FROM tenant_events WHERE action = '{TenantActions.MemberConnectionsChanged}'")).Order(StringComparer.Ordinal));
    }

    /// <summary>A confirmed delete whose tenant row cannot be written changes nothing: the connection,
    /// its credentials and every binding of it stay.</summary>
    [Fact]
    public async Task A_confirmed_delete_whose_tenant_row_cannot_land_changes_nothing()
    {
        var database = await DatabaseAsync();
        await HireAsync(database, "Beta", "Dev");
        var store = await StoreAsync();
        var settings = database.MemberSettings();
        await InsertCredentialAsync(store, "conn-a", "A");
        await settings.SaveAsync(new ContainerId("Beta", "Dev"), Bind(("login", "conn-a")), Ct);
        await database.ExecuteAsync("DROP TABLE tenant_events");

        await Assert.ThrowsAnyAsync<DbException>(() => store.DeleteAsync(
            "conn-a", Row(TenantActions.ConnectionDisconnected, "conn-a"), Ct,
            found => [.. found.Select(u => Row(TenantActions.MemberConnectionsChanged, $"{u.Team}/{u.Member}"))]));

        Assert.NotNull(await store.GetAsync("conn-a", Ct));
        Assert.Equal("password-value-1", (await store.CredentialSecretsAsync("conn-a", Ct))!["password"]);
        Assert.Equal([new ConnectionUse("Beta", "Dev", "login")], await store.UsedByAsync("conn-a", Ct));
    }

    private static CredentialSettings Login(string username, bool passwordSet = true) => new(null,
    [
        new CredentialField("username", "Username", false, username, true),
        new CredentialField("password", "Password", true, null, passwordSet),
        new CredentialField("url", "URL", false, null, false),
    ]);

    private Task InsertCredentialAsync(IConnectionStore store, string id, string name, string password = "password-value-1") =>
        store.InsertCredentialAsync(
            new ConnectionRecord(id, name, ConnectionKinds.Login, "user-1", [], At, null, ConnectionRecord.Ok, null, Credential: Login("user-1")),
            new Dictionary<string, string> { ["password"] = password }, "person", Row(TenantActions.ConnectionConnected, id), Ct);

    /// <summary>A CREDENTIAL CONNECTION round-trips its kind, preset and fields; its masked values are
    /// one ciphertext no column shows, replaced whole by an edit, and gone with the connection.</summary>
    [Fact]
    public async Task A_credential_connection_round_trips_its_fields_and_seals_its_masked_values()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync();
        var key = new CredentialSettings("twilio",
        [
            new CredentialField("accountSid", "Account SID", false, "AC-0001", true),
            new CredentialField("apiKeySid", "API key SID", false, "SK-0001", true),
            new CredentialField("apiKeySecret", "API key secret", true, null, true),
        ]);
        await store.InsertCredentialAsync(
            new ConnectionRecord("conn-key", "Twilio", ConnectionKinds.ApiKey, "AC-0001", [], At, null, ConnectionRecord.Ok, null, Credential: key),
            new Dictionary<string, string> { ["apiKeySecret"] = "secret-value-xyz" }, "person", Row(TenantActions.ConnectionConnected, "conn-key"), Ct);
        await InsertCredentialAsync(store, "conn-login", "Login");

        var read = (await store.GetAsync("conn-key", Ct))!;
        Assert.Equal((ConnectionKinds.ApiKey, "twilio", "AC-0001"), (read.Kind, read.Credential!.Preset, read.Account));
        Assert.Equal(key.Fields, read.Credential.Fields);
        Assert.Null(read.Mailbox);
        Assert.Equal("secret-value-xyz", (await store.CredentialSecretsAsync("conn-key", Ct))!["apiKeySecret"]);
        foreach (var column in new[] { "SELECT secrets_protected FROM credential_connections", "SELECT fields FROM credential_connections" })
        {
            Assert.All(await database.ColumnAsync(column), v => Assert.DoesNotContain("secret-value-xyz", v));
        }

        Assert.True(await store.UpdateCredentialAsync(
            "conn-login", "user-2", Login("user-2"), new Dictionary<string, string> { ["password"] = "password-value-2" },
            Row(TenantActions.ConnectionCredentialsChanged, "conn-login"), Ct));
        Assert.False(await store.UpdateCredentialAsync(
            "conn-none", "x", Login("x"), new Dictionary<string, string>(), Row(TenantActions.ConnectionCredentialsChanged, "conn-none"), Ct));
        var edited = (await store.GetAsync("conn-login", Ct))!;
        Assert.Equal(("Login", "user-2", "user-2"), (edited.Name, edited.Account, edited.Credential!.Fields[0].Value));
        Assert.Equal("password-value-2", (await store.CredentialSecretsAsync("conn-login", Ct))!["password"]);
        Assert.Equal(["conn-login"], await database.ColumnAsync($"SELECT subject FROM tenant_events WHERE action = '{TenantActions.ConnectionCredentialsChanged}'"));

        Assert.True((await store.DeleteAsync("conn-key", Row(TenantActions.ConnectionDisconnected, "conn-key"), Ct)).Deleted);
        Assert.Null(await store.CredentialSecretsAsync("conn-key", Ct));
        Assert.Equal(1, await database.LongAsync("SELECT COUNT(*) FROM credential_connections"));
    }

    /// <summary>THE NAME IS THE KEY: no write gives a connection a name another has, ignoring case and
    /// the spaces around it, and a refused write lands nothing. A rename to its own name is no clash.</summary>
    [Fact]
    public async Task Every_write_refuses_a_name_another_connection_has_and_lands_nothing()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync();
        await InsertAsync(store, "conn-a", "Work mail");
        await InsertAsync(store, "conn-b", "Files");

        Assert.True(await store.NameTakenAsync(" WORK MAIL ", null, Ct));
        Assert.False(await store.NameTakenAsync("Work mail", "conn-a", Ct));
        Assert.False(await store.NameTakenAsync("Other", null, Ct));

        await Assert.ThrowsAsync<ConnectionNameTakenException>(() => InsertAsync(store, "conn-c", "work mail"));
        await Assert.ThrowsAsync<ConnectionNameTakenException>(() => store.InsertMailboxAsync(
            Record("conn-d", "WORK MAIL", MailboxSettings.Kind), Mailbox, "app-password-value", null, Row(TenantActions.ConnectionConnected, "conn-d"), Ct));
        await Assert.ThrowsAsync<ConnectionNameTakenException>(() => InsertCredentialAsync(store, "conn-e", "Work Mail"));
        await Assert.ThrowsAsync<ConnectionNameTakenException>(() => store.RenameAsync("conn-b", "work mail", Row(TenantActions.ConnectionRenamed, "conn-b"), Ct));
        Assert.True(await store.RenameAsync("conn-a", "WORK MAIL", Row(TenantActions.ConnectionRenamed, "conn-a"), Ct));

        Assert.Equal([("conn-b", "Files"), ("conn-a", "WORK MAIL")], (await store.ListAsync(Ct)).Select(c => (c.Id, c.Name)));
        Assert.Equal(0, await database.LongAsync("SELECT COUNT(*) FROM imap_connections"));
        Assert.Equal(0, await database.LongAsync("SELECT COUNT(*) FROM credential_connections"));
        Assert.Equal(2, await database.LongAsync($"SELECT COUNT(*) FROM tenant_events WHERE action = '{TenantActions.ConnectionConnected}'"));
    }

    /// <summary>A reconnect as another account keeps the id and name and takes the account; one with no
    /// account given keeps the stored one.</summary>
    [Fact]
    public async Task A_reconnect_may_take_another_account_under_the_same_id_and_name()
    {
        var store = await StoreAsync();
        await InsertAsync(store, "conn-a", "Work");

        await store.ReconnectAsync("conn-a", ["openid"], new ConnectionTokens("refresh-2", "access-2", null), Row(TenantActions.ConnectionReconnected, "conn-a"), Ct);
        Assert.Equal("conn-a@example.test", (await store.GetAsync("conn-a", Ct))!.Account);

        await store.ReconnectAsync(
            "conn-a", ["openid"], new ConnectionTokens("refresh-3", "access-3", null), Row(TenantActions.ConnectionReconnected, "conn-a"), Ct,
            account: "other@example.test");
        var read = (await store.GetAsync("conn-a", Ct))!;
        Assert.Equal(("conn-a", "Work", "other@example.test"), (read.Id, read.Name, read.Account));
        Assert.Equal("refresh-3", (await store.TokensAsync("conn-a", Ct))!.RefreshToken);
    }

    [Fact]
    public async Task A_removal_takes_the_connection_and_its_mailbox_and_writes_its_row()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync();
        await store.InsertMailboxAsync(Record("conn-mail", "Mail", MailboxSettings.Kind), Mailbox, "app-password-value", null, Row(TenantActions.ConnectionConnected, "conn-mail"), Ct);

        Assert.Equal((true, 0), Deleted(await store.DeleteAsync("conn-mail", Row(TenantActions.ConnectionDisconnected, "conn-mail"), Ct)));
        Assert.Equal((false, 0), Deleted(await store.DeleteAsync("conn-mail", Row(TenantActions.ConnectionDisconnected, "conn-mail"), Ct)));

        Assert.Equal(0, await database.LongAsync("SELECT COUNT(*) FROM imap_connections"));
        Assert.Null(await store.MailboxPasswordAsync("conn-mail", Ct));
        Assert.Equal(1, await database.LongAsync($"SELECT COUNT(*) FROM tenant_events WHERE action = '{TenantActions.ConnectionDisconnected}'"));
    }

    [Fact]
    public async Task A_mailbox_password_update_and_a_verified_login_set_the_status_and_the_time()
    {
        var store = await StoreAsync();
        await store.InsertMailboxAsync(Record("conn-mail", "Mail", MailboxSettings.Kind), Mailbox, "app-password-value", null, Row(TenantActions.ConnectionConnected, "conn-mail"), Ct);
        await store.MarkNeedsReconnectAsync("conn-mail", "login refused", Row(TenantActions.ConnectionNeedsReconnect, "conn-mail"), Ct);

        // Not while it needs a person.
        await store.StoreMailboxVerifiedAsync("conn-mail", At.AddDays(1), Ct);
        Assert.Equal(At, (await store.GetAsync("conn-mail", Ct))!.RefreshedAt);

        Assert.False(await store.UpdateMailboxPasswordAsync("conn-none", "other-password", At, Row(TenantActions.ConnectionPasswordUpdated, "conn-none"), Ct));
        Assert.True(await store.UpdateMailboxPasswordAsync("conn-mail", "new-app-password", At.AddDays(2), Row(TenantActions.ConnectionPasswordUpdated, "conn-mail"), Ct));
        var updated = (await store.GetAsync("conn-mail", Ct))!;
        Assert.Equal((ConnectionRecord.Ok, null, At.AddDays(2)), (updated.Status, updated.StatusReason, updated.RefreshedAt));
        Assert.Equal("new-app-password", await store.MailboxPasswordAsync("conn-mail", Ct));

        await store.StoreMailboxVerifiedAsync("conn-mail", At.AddDays(3), Ct);
        Assert.Equal(At.AddDays(3), (await store.GetAsync("conn-mail", Ct))!.RefreshedAt);
    }

    [Fact]
    public async Task A_refused_test_needs_a_person_with_its_sentence_and_a_passed_one_clears_it()
    {
        var store = await StoreAsync();
        await InsertAsync(store, "conn-a", "Work", new ConnectionTokens("refresh-1", "access-1", At.AddHours(1)));

        await store.RecordTestAsync("conn-a", "Stripe refused the secret key (HTTP 401).", At.AddDays(1), Row(TenantActions.ConnectionTested, "conn-a"), Ct);
        var refused = (await store.GetAsync("conn-a", Ct))!;
        Assert.Equal((ConnectionRecord.NeedsReconnect, "Stripe refused the secret key (HTTP 401)."), (refused.Status, refused.StatusReason));
        Assert.Equal(new ConnectionTokens("refresh-1", null, null), await store.TokensAsync("conn-a", Ct));

        await store.RecordTestAsync("conn-a", null, At.AddDays(2), Row(TenantActions.ConnectionTested, "conn-a"), Ct);
        var passed = (await store.GetAsync("conn-a", Ct))!;
        Assert.Equal((ConnectionRecord.Ok, null, At.AddDays(2)), (passed.Status, passed.StatusReason, passed.RefreshedAt));
        Assert.Equal("refresh-1", (await store.TokensAsync("conn-a", Ct))!.RefreshToken);
    }

    [Fact]
    public async Task A_flow_is_consumed_once_by_its_own_person_and_old_flows_go_as_new_ones_arrive()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync();
        var old = Flow("state-old", At.AddDays(-2));
        var flow = Flow("state-new", At) with { Tenant = "contoso", Name = "Work", ReconnectId = "conn-a" };
        await store.InsertFlowAsync(old, Ct);
        await store.InsertFlowAsync(flow, Ct);

        Assert.Equal(["state-new"], await database.ColumnAsync("SELECT state FROM connection_flows"));
        Assert.DoesNotContain("the-verifier", await database.ColumnAsync("SELECT verifier_protected FROM connection_flows"));

        // Another person's sign-in is not theirs to spend, and is left unconsumed.
        Assert.Null(await store.ConsumeFlowAsync("state-new", At.AddMinutes(1), "someone-else", Ct));

        var first = (await store.ConsumeFlowAsync("state-new", At.AddMinutes(2), "person", Ct))!;
        Assert.Equal(flow, first, new FlowComparer());
        Assert.Null(first.UsedAt);

        var second = (await store.ConsumeFlowAsync("state-new", At.AddMinutes(3), null, Ct))!;
        Assert.Equal(At.AddMinutes(2), second.UsedAt);
        Assert.Null(await store.ConsumeFlowAsync("state-never", At, null, Ct));
    }

    [Fact]
    public async Task A_write_whose_tenant_row_cannot_land_changes_nothing()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync();
        await InsertAsync(store, "conn-a", "Kept");
        await database.ExecuteAsync("DROP TABLE tenant_events");

        await Assert.ThrowsAnyAsync<DbException>(() => InsertAsync(store, "conn-b", "New"));
        await Assert.ThrowsAnyAsync<DbException>(() => store.RenameAsync("conn-a", "Changed", Row(TenantActions.ConnectionRenamed, "conn-a"), Ct));
        await Assert.ThrowsAnyAsync<DbException>(() => store.DeleteAsync("conn-a", Row(TenantActions.ConnectionDisconnected, "conn-a"), Ct));
        await Assert.ThrowsAnyAsync<DbException>(() => store.SaveProviderAsync(Provider("google"), "secret", Row(TenantActions.ConnectionProviderSaved, "google"), Ct));
        await Assert.ThrowsAnyAsync<DbException>(() => store.MarkNeedsReconnectAsync("conn-a", "x", Row(TenantActions.ConnectionNeedsReconnect, "conn-a"), Ct));
        await Assert.ThrowsAnyAsync<DbException>(() => store.RecordTestAsync("conn-a", "refused", At, Row(TenantActions.ConnectionTested, "conn-a"), Ct));

        Assert.Equal([("conn-a", "Kept", ConnectionRecord.Ok)], (await store.ListAsync(Ct)).Select(c => (c.Id, c.Name, c.Status)));
        Assert.Empty(await store.ProvidersAsync(Ct));
    }

    /// <summary>THE CASE MAP: an id is exact, as SQLite's BINARY keys are.</summary>
    [Fact]
    public async Task Ids_differing_only_in_case_are_other_ids()
    {
        var store = await StoreAsync();
        await InsertAsync(store, "conn-a", "A");
        await store.SaveProviderAsync(Provider("custom-acme"), null, Row(TenantActions.ConnectionProviderSaved, "custom-acme"), Ct);
        await store.InsertFlowAsync(Flow("State-X", At), Ct);

        Assert.Null(await store.GetAsync("CONN-A", Ct));
        Assert.Null(await store.TokensAsync("Conn-A", Ct));
        Assert.Null(await store.ProviderAsync("CUSTOM-ACME", Ct));
        Assert.Null(await store.ConsumeFlowAsync("state-x", At, null, Ct));
        Assert.False(await store.RenameAsync("CONN-A", "x", Row(TenantActions.ConnectionRenamed, "CONN-A"), Ct));
    }

    /// <summary>THE TRAILING-SPACE MAP: an id from a request with a trailing space finds nothing.</summary>
    [Fact]
    public async Task An_id_with_a_trailing_space_finds_nothing()
    {
        var store = await StoreAsync();
        await InsertAsync(store, "conn-a", "A");
        await store.SaveProviderAsync(Provider("custom-acme"), "client-secret", Row(TenantActions.ConnectionProviderSaved, "custom-acme"), Ct);
        await store.InsertFlowAsync(Flow("state-x", At), Ct);

        Assert.Null(await store.GetAsync("conn-a ", Ct));
        Assert.Null(await store.TokensAsync("conn-a ", Ct));
        Assert.Null(await store.ProviderAsync("custom-acme ", Ct));
        Assert.Null(await store.ClientSecretAsync("custom-acme ", Ct));
        Assert.Null(await store.ConsumeFlowAsync("state-x ", At, null, Ct));
        Assert.False(await store.RenameAsync("conn-a ", "x", Row(TenantActions.ConnectionRenamed, "conn-a "), Ct));
        Assert.Equal((false, 0), Deleted(await store.DeleteAsync("conn-a ", Row(TenantActions.ConnectionDisconnected, "conn-a "), Ct)));
        Assert.Equal((true, 0), (await store.DeleteProviderAsync("custom-acme ", Row(TenantActions.ConnectionProviderRemoved, "custom-acme "), Ct)) is var r ? (r.Removed, r.Connections.Count) : default);

        Assert.NotNull(await store.ProviderAsync("custom-acme", Ct));
        Assert.Equal("A", (await store.GetAsync("conn-a", Ct))!.Name);
        Assert.Null((await store.ConsumeFlowAsync("state-x", At, null, Ct))!.UsedAt);
    }

    [Fact]
    public async Task Long_and_unicode_text_round_trips()
    {
        var store = await StoreAsync();
        var name = LongUnicode("name");
        var token = LongUnicode("token");

        await store.InsertAsync(Record("conn-a", name) with { Account = "O'Brien 🚀 café@example.test" }, new ConnectionTokens(token, token, null), "person", Row(TenantActions.ConnectionConnected, "conn-a"), Ct);

        var read = (await store.GetAsync("conn-a", Ct))!;
        Assert.Equal((name, "O'Brien 🚀 café@example.test"), (read.Name, read.Account));
        Assert.Equal(new ConnectionTokens(token, token, null), await store.TokensAsync("conn-a", Ct));
    }

    /// <summary>REVIEW FOCUS 2: eight people save one provider nobody has saved yet, at once: every save
    /// lands, one row stands, and each has its tenant row.</summary>
    [Fact]
    public async Task Concurrent_first_saves_of_one_provider_make_one_row()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync();

        for (var round = 0; round < 12; round++)
        {
            var id = $"custom-raced{round}";
            await RaceAsync(8, async i =>
            {
                await store.SaveProviderAsync(Provider(id, $"Name {i}"), $"secret-{i}", Row(TenantActions.ConnectionProviderSaved, id), Ct);
                return i;
            });

            var saved = (await store.ProviderAsync(id, Ct))!;
            Assert.Equal(saved.Name!["Name ".Length..], (await store.ClientSecretAsync(id, Ct))!["secret-".Length..]);
        }

        Assert.Equal(12, (await store.ProvidersAsync(Ct)).Count);
        Assert.Equal(12 * 8, await database.LongAsync("SELECT COUNT(*) FROM tenant_events"));
    }

    /// <summary>REVIEW FOCUS 2: eight callbacks with one state, at once: exactly one finds it unused.</summary>
    [Fact]
    public async Task Concurrent_consumes_of_one_flow_let_one_win()
    {
        var store = await StoreAsync();

        for (var round = 0; round < 12; round++)
        {
            var state = $"state-{round}";
            await store.InsertFlowAsync(Flow(state, At), Ct);

            var flows = await RaceAsync(8, i => store.ConsumeFlowAsync(state, At.AddSeconds(i), "person", Ct));

            Assert.Single(flows, f => f!.UsedAt is null);
        }
    }

    /// <summary>Two refreshes of one connection at once (each a new access token and a rotated refresh
    /// token): the row holds one refresh's pair whole, never one's access token beside the other's.</summary>
    [Fact]
    public async Task Concurrent_refreshes_leave_one_refresh_whole()
    {
        var store = await StoreAsync();
        await InsertAsync(store, "conn-a", "A");

        for (var round = 0; round < 12; round++)
        {
            await RaceAsync(4, async i =>
            {
                await store.StoreRefreshAsync("conn-a", new ConnectionTokens($"refresh-{round}-{i}", $"access-{round}-{i}", At.AddMinutes(i)), Ct);
                return i;
            });

            var tokens = (await store.TokensAsync("conn-a", Ct))!;
            Assert.Equal(tokens.RefreshToken!["refresh-".Length..], tokens.AccessToken!["access-".Length..]);
        }
    }

    private static (bool, int) Deleted((bool Deleted, IReadOnlyList<ConnectionUse> UsedBy) result) => (result.Deleted, result.UsedBy.Count);

    private static PluginMemberSettings Bind(params (string Slot, string Id)[] bindings) =>
        new(new Dictionary<string, JsonElement>(), new Dictionary<string, string>())
        {
            Connections = bindings.ToDictionary(b => b.Slot, b => b.Id),
        };

    private static ConnectionFlow Flow(string state, DateTimeOffset issued) =>
        new(state, "person", "google", ["openid"], null, null, "http://127.0.0.1:53121/", true, "the-verifier", issued, null);

    /// <summary>Records holding lists compare by reference; these compare them by their items.</summary>
    private sealed class ProviderComparer : IEqualityComparer<StoredProvider?>
    {
        public bool Equals(StoredProvider? x, StoredProvider? y) =>
            x is not null && y is not null && x with { DefaultScopes = [] } == y with { DefaultScopes = [] } && x.DefaultScopes.SequenceEqual(y.DefaultScopes);

        public int GetHashCode(StoredProvider? obj) => 0;
    }

    private sealed class FlowComparer : IEqualityComparer<ConnectionFlow?>
    {
        public bool Equals(ConnectionFlow? x, ConnectionFlow? y) =>
            x is not null && y is not null && x with { Scopes = [] } == y with { Scopes = [] } && x.Scopes.SequenceEqual(y.Scopes);

        public int GetHashCode(ConnectionFlow? obj) => 0;
    }
}

public sealed class SqliteConnectionStoreTests : ConnectionStoreContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerConnectionStoreTests : ConnectionStoreContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}
