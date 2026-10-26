using Harness.Contracts;
using Harness.SqlServer;
using Microsoft.Data.SqlClient;

namespace Harness.Host.SqlServer;

/// <summary>
/// The SQL Server version of <see cref="IPluginMemberSettingsStore"/>: <c>team_member_config</c>, SQLite's
/// <see cref="SqlitePluginMemberSettings"/> statement for statement, its three JSON columns written and
/// read by <see cref="PluginMemberSettingsRows"/>, shared with it. A save with tenant rows lands with
/// them in one transaction, through <see cref="SqlServerTenantAuditRow"/>.
///
/// <para>
/// A TEAM AND A MEMBER FOLD CASE, AS SQLITE'S <c>COLLATE NOCASE</c> COLUMNS DO: the columns carry the
/// case-insensitive collation. Every lookup also matches the length (<see cref="SqlServerSql.Same"/>), so
/// a trailing space finds nothing here as it finds nothing on SQLite.
/// </para>
///
/// <para>
/// LOCK ORDER: THE <c>teams</c> ROW, THE <c>team_members</c> ROW, THE SETTINGS ROW, THEN
/// <c>tenant_events</c> (<see cref="LockMemberAsync"/>). The settings row references its member, which
/// references its team; a team's or a member's deletion locks the parent first and then cascades to this
/// row, while a write of this row would otherwise lock it first and then check its parent for the
/// foreign key - the two orders that deadlock. So every write takes both parents under
/// <c>UPDLOCK, HOLDLOCK</c> first, the order <c>SqlServerTeamStore</c> takes for every child of a team,
/// and the upsert is one <c>MERGE ... HOLDLOCK</c>, so two first saves of one member make one row.
/// </para>
/// </summary>
public sealed class SqlServerPluginMemberSettings(string connectionString) : IPluginMemberSettingsStore
{
    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    /// <summary>The member's row, matched exactly: see the class remarks.</summary>
    internal static readonly string MemberMatch =
        $"{SqlServerSql.Same("team", "@team")} AND {SqlServerSql.Same("name", "@name")}";

    public Task<PluginMemberSettings> ForAsync(ContainerId member, CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT config_json, secrets_json, connections_json FROM team_member_config WHERE {MemberMatch}";
                BindMember(command, member);

                await using var reader = await command.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token)) return PluginMemberSettings.None;

                return PluginMemberSettingsRows.Read(reader.GetString(0), reader.GetString(1), reader.GetString(2));
            },
            ct);

    public Task SaveAsync(ContainerId member, PluginMemberSettings settings, CancellationToken ct = default) =>
        SaveAsync(member, settings, [], ct);

    public Task SaveAsync(ContainerId member, PluginMemberSettings settings, TriggerAudit audit, CancellationToken ct = default) =>
        SaveAsync(member, settings, [audit], ct);

    public Task SaveAsync(ContainerId member, PluginMemberSettings settings, IReadOnlyList<TriggerAudit> audits, CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                await LockMemberAsync(connection, transaction, member, token);
                await UpsertAsync(connection, transaction, member, settings, token);

                foreach (var audit in audits) await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);

                await SqlServerOpen.CommitAsync(transaction, token);
                return true;
            },
            ct);

    public async Task<IReadOnlySet<string>> ConnectionsBoundOnAsync(string team, CancellationToken ct = default) =>
        await BoundOnAsync("connections_json", team, ct);

    public async Task<IReadOnlySet<string>> KeysBoundOnAsync(string team, CancellationToken ct = default) =>
        await BoundOnAsync("secrets_json", team, ct);

    /// <summary>Every value <paramref name="column"/> binds on <paramref name="team"/>'s members.</summary>
    private Task<HashSet<string>> BoundOnAsync(string column, string team, CancellationToken ct) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {column} FROM team_member_config WHERE {SqlServerSql.Same("team", "@team")}";
                command.Parameters.AddWithValue("@team", team);

                var values = new HashSet<string>(StringComparer.Ordinal);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) values.UnionWith(PluginMemberSettingsRows.Bound(reader.GetString(0)));

                return values;
            },
            ct);

    /// <summary>
    /// The member's parents, the <c>teams</c> row and then its <c>team_members</c> row, held under
    /// <c>UPDLOCK, HOLDLOCK</c> for the rest of <paramref name="transaction"/>: the first locks every write
    /// of a member's settings or secrets takes. See the class remarks.
    /// </summary>
    internal static Task LockMemberAsync(SqlConnection connection, SqlTransaction transaction, ContainerId member, CancellationToken ct) =>
        LockMemberAsync(connection, transaction, member.Team, member.Name, ct);

    /// <summary><see cref="LockMemberAsync(SqlConnection, SqlTransaction, ContainerId, CancellationToken)"/> of a member
    /// as its rows spell it.</summary>
    internal static async Task LockMemberAsync(SqlConnection connection, SqlTransaction transaction, string team, string name, CancellationToken ct)
    {
        await using var hold = connection.CreateCommand();
        hold.Transaction = transaction;
        hold.CommandText =
            """
            SELECT COUNT(*) FROM teams WITH (UPDLOCK, HOLDLOCK) WHERE id = @team;
            SELECT COUNT(*) FROM team_members WITH (UPDLOCK, HOLDLOCK) WHERE team = @team AND name = @name;
            """;
        BindMember(hold, team, name);
        await hold.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The settings row, inserted or replaced whole, inside the caller's transaction after <see cref="LockMemberAsync"/>.</summary>
    internal static async Task UpsertAsync(
        SqlConnection connection, SqlTransaction transaction, ContainerId member, PluginMemberSettings settings, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            MERGE team_member_config WITH (HOLDLOCK) AS t
            USING (SELECT @team AS team, @name AS name) AS n
            ON t.team = n.team AND t.name = n.name
            WHEN MATCHED THEN UPDATE SET
                config_json = @config,
                secrets_json = @secrets,
                connections_json = @connections
            WHEN NOT MATCHED THEN
                INSERT (team, name, config_json, secrets_json, connections_json)
                VALUES (@team, @name, @config, @secrets, @connections);
            """;
        BindMember(command, member);
        command.Parameters.AddWithValue("@config", PluginMemberSettingsRows.ConfigJson(settings));
        command.Parameters.AddWithValue("@secrets", PluginMemberSettingsRows.SecretsJson(settings));
        command.Parameters.AddWithValue("@connections", PluginMemberSettingsRows.ConnectionsJson(settings));
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static void BindMember(SqlCommand command, ContainerId member) => BindMember(command, member.Team, member.Name);

    internal static void BindMember(SqlCommand command, string team, string name)
    {
        command.Parameters.AddWithValue("@team", team);
        command.Parameters.AddWithValue("@name", name);
    }
}
