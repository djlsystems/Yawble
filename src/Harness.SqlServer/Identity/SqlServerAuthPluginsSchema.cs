using Harness.Contracts;

namespace Harness.SqlServer.Identity;

/// <summary>
/// The identity module's plugin and connection tables on SQL Server, as SQLite's <c>AuthSchema</c> leaves
/// them after <c>auth-029</c>, in the module's third baseline step: a plugin member's settings
/// (<c>team_member_config</c>) and own secret values (<c>plugin_member_secrets</c>), the plugin keys a
/// person set (<c>plugin_secrets</c>), and the OAuth providers, connections, sign-in flows and mailboxes.
/// That class's remarks are the argument for each table and column; this one says only where the dialect
/// makes a difference. It follows <c>auth.sqlserver-baseline-1</c> (<see cref="SqlServerAuthSchema"/>):
/// the two member tables reference <c>team_members</c>.
///
/// <para>
/// Types follow the first baseline's rules: BIGINT for INTEGER, NVARCHAR(40) for an instant,
/// NVARCHAR(MAX) for text no key holds, and every column SQLite declares <c>COLLATE NOCASE</c> takes
/// <see cref="SqlServerSql.NoCase"/>. Every <c>*_protected</c> column is NVARCHAR(MAX): Data Protection's
/// base64url ciphertext, stored and read back as the same text SQLite holds. Keyed text is as wide as
/// what bounds it, and no wider:
/// </para>
/// <list type="bullet">
///   <item>THE MEMBER TABLES' <c>team</c> AND <c>name</c> ARE <c>team_members</c>' OWN NVARCHAR(200), which
///     a foreign key requires. <c>plugin_member_secrets.field</c> is NVARCHAR(64): a manifest's secret
///     name (<c>PluginManifest.IsConfigName</c>, at most 64), checked against the manifest before every
///     write (<c>PluginMemberSecretStore.ChangesRefusal</c>, the hire's binding check). Its key is 928
///     bytes, past the 900 a clustered key may hold, so it is NONCLUSTERED (1,700).</item>
///   <item><c>plugin_secrets.key</c> NVARCHAR(128): a logical key, at most 128 characters
///     (<c>EnvironmentSecretStore.Refusal</c>, checked before every read and write). It is <c>[key]</c> in
///     every statement: a reserved word in T-SQL.</item>
///   <item><c>oauth_providers.id</c> NVARCHAR(64): <c>google</c>, <c>microsoft</c> or a custom id of at
///     most 40 (<c>ConnectionProviders.IsCustomId</c>).</item>
///   <item><c>connections.id</c> and <c>imap_connections.id</c> NVARCHAR(64): <c>conn-</c> and 12 hex
///     digits, minted by <c>Connections</c> alone. <c>connection_flows.state</c> NVARCHAR(64): 24 or 32
///     random bytes in base64url, 32 or 43 characters.</item>
/// </list>
///
/// <para>
/// No index SQLite lacks: a provider's connections and an instance's bindings are read by scan on both,
/// over tables of a few rows.
/// </para>
/// </summary>
public static class SqlServerAuthPluginsSchema
{
    /// <summary>A JSON column's defaults, as SQLite's <c>DEFAULT '{}'</c> and <c>DEFAULT '[]'</c>.</summary>
    private const string EmptyObject = "{}";

    private const string EmptyList = "[]";

    public static IReadOnlyList<MigrationStep> Steps { get; } =
    [
        new MigrationStep(
            "auth.sqlserver-baseline-3",
            $"""
             CREATE TABLE team_member_config (
                 team             NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL,
                 name             NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL,
                 config_json      NVARCHAR(MAX) NOT NULL DEFAULT N'{EmptyObject}',
                 secrets_json     NVARCHAR(MAX) NOT NULL DEFAULT N'{EmptyObject}',
                 connections_json NVARCHAR(MAX) NOT NULL DEFAULT N'{EmptyObject}',
                 CONSTRAINT pk_team_member_config PRIMARY KEY (team, name),
                 CONSTRAINT fk_team_member_config_member FOREIGN KEY (team, name)
                     REFERENCES team_members(team, name) ON DELETE CASCADE
             );

             CREATE TABLE plugin_member_secrets (
                 team            NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL,
                 name            NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL,
                 field           NVARCHAR(64)  NOT NULL,
                 value_protected NVARCHAR(MAX) NOT NULL,
                 set_by          NVARCHAR(MAX) NOT NULL,
                 set_at          NVARCHAR(40)  NOT NULL,
                 CONSTRAINT pk_plugin_member_secrets PRIMARY KEY NONCLUSTERED (team, name, field),
                 CONSTRAINT fk_plugin_member_secrets_member FOREIGN KEY (team, name)
                     REFERENCES team_members(team, name) ON DELETE CASCADE
             );

             CREATE TABLE plugin_secrets (
                 [key]           NVARCHAR(128) NOT NULL CONSTRAINT pk_plugin_secrets PRIMARY KEY,
                 value_protected NVARCHAR(MAX) NOT NULL,
                 set_by          NVARCHAR(MAX) NOT NULL,
                 set_at          NVARCHAR(40)  NOT NULL
             );

             CREATE TABLE oauth_providers (
                 id                      NVARCHAR(64)  NOT NULL CONSTRAINT pk_oauth_providers PRIMARY KEY,
                 kind                    NVARCHAR(MAX) NOT NULL,
                 name                    NVARCHAR(MAX) NULL,
                 client_id               NVARCHAR(MAX) NULL,
                 client_secret_protected NVARCHAR(MAX) NULL,
                 tenant                  NVARCHAR(MAX) NULL,
                 authorize_url           NVARCHAR(MAX) NULL,
                 token_url               NVARCHAR(MAX) NULL,
                 userinfo_url            NVARCHAR(MAX) NULL,
                 default_scopes          NVARCHAR(MAX) NOT NULL DEFAULT N'{EmptyList}',
                 updated_at              NVARCHAR(40)  NOT NULL
             );

             CREATE TABLE connections (
                 id                      NVARCHAR(64)  NOT NULL CONSTRAINT pk_connections PRIMARY KEY,
                 name                    NVARCHAR(MAX) NOT NULL,
                 provider                NVARCHAR(MAX) NOT NULL,
                 account                 NVARCHAR(MAX) NOT NULL,
                 scopes                  NVARCHAR(MAX) NOT NULL DEFAULT N'{EmptyList}',
                 refresh_token_protected NVARCHAR(MAX) NULL,
                 access_token_protected  NVARCHAR(MAX) NULL,
                 access_expires_at       NVARCHAR(40)  NULL,
                 connected_at            NVARCHAR(40)  NOT NULL,
                 refreshed_at            NVARCHAR(40)  NULL,
                 status                  NVARCHAR(MAX) NOT NULL DEFAULT N'ok',
                 status_reason           NVARCHAR(MAX) NULL,
                 connected_by            NVARCHAR(MAX) NULL,
                 tenant                  NVARCHAR(MAX) NULL
             );

             CREATE TABLE connection_flows (
                 state              NVARCHAR(64)  NOT NULL CONSTRAINT pk_connection_flows PRIMARY KEY,
                 user_id            NVARCHAR(MAX) NOT NULL,
                 provider           NVARCHAR(MAX) NOT NULL,
                 scopes             NVARCHAR(MAX) NOT NULL DEFAULT N'{EmptyList}',
                 name               NVARCHAR(MAX) NULL,
                 reconnect_id       NVARCHAR(MAX) NULL,
                 redirect_uri       NVARCHAR(MAX) NOT NULL,
                 loopback           BIGINT        NOT NULL DEFAULT 0,
                 verifier_protected NVARCHAR(MAX) NOT NULL,
                 issued_at          NVARCHAR(40)  NOT NULL,
                 used_at            NVARCHAR(40)  NULL,
                 tenant             NVARCHAR(MAX) NULL
             );

             CREATE TABLE imap_connections (
                 id                 NVARCHAR(64)  NOT NULL CONSTRAINT pk_imap_connections PRIMARY KEY,
                 username           NVARCHAR(MAX) NOT NULL,
                 imap_host          NVARCHAR(MAX) NOT NULL,
                 imap_port          BIGINT        NOT NULL,
                 imap_security      NVARCHAR(MAX) NOT NULL,
                 smtp_host          NVARCHAR(MAX) NOT NULL,
                 smtp_port          BIGINT        NOT NULL,
                 smtp_security      NVARCHAR(MAX) NOT NULL,
                 preset             NVARCHAR(MAX) NULL,
                 password_protected NVARCHAR(MAX) NOT NULL
             );
             """),

        // `AuthSchema`'s auth-030: a plugin member's named secrets. `secret` is NVARCHAR(64) and folds
        // case: a name is at most 64 characters (`PluginNamedSecrets.IsName`, checked before every
        // write), and one member holds one `ApiKey` whatever its spelling. The key is 928 bytes, so
        // NONCLUSTERED, as plugin_member_secrets'.
        new MigrationStep(
            "auth-030",
            $"""
             CREATE TABLE plugin_member_named_secrets (
                 team            NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL,
                 name            NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL,
                 secret          NVARCHAR(64)  COLLATE {SqlServerSql.NoCase} NOT NULL,
                 value_protected NVARCHAR(MAX) NOT NULL,
                 set_by          NVARCHAR(MAX) NOT NULL,
                 set_at          NVARCHAR(40)  NOT NULL,
                 CONSTRAINT pk_plugin_member_named_secrets PRIMARY KEY NONCLUSTERED (team, name, secret),
                 CONSTRAINT fk_plugin_member_named_secrets_member FOREIGN KEY (team, name)
                     REFERENCES team_members(team, name) ON DELETE CASCADE
             );
             """),

        // `AuthSchema`'s auth-031: a credential connection's preset, fields and sealed values, and every
        // connection's name made unique, the first connected keeping it. `id` is NVARCHAR(64), as
        // `connections.id`. Names compare in the NOCASE collation, as SQLite's do.
        new MigrationStep(
            "auth-031",
            $"""
             CREATE TABLE credential_connections (
                 id                NVARCHAR(64)  NOT NULL CONSTRAINT pk_credential_connections PRIMARY KEY,
                 preset            NVARCHAR(MAX) NULL,
                 fields            NVARCHAR(MAX) NOT NULL DEFAULT N'{EmptyList}',
                 secrets_protected NVARCHAR(MAX) NULL
             );

             UPDATE c SET name = c.name + N' (' + c.id + N')'
             FROM connections c
             WHERE EXISTS (
                 SELECT 1 FROM connections o
                 WHERE o.name COLLATE {SqlServerSql.NoCase} = c.name COLLATE {SqlServerSql.NoCase}
                   AND (o.connected_at < c.connected_at OR (o.connected_at = c.connected_at AND o.id < c.id)));
             """),
    ];
}
