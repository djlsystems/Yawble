using System.Data;
using Harness.Contracts;
using Harness.Messaging;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Ledger;

/// <summary>
/// <see cref="ILedger"/> on SQL Server: the ledger as a query over the messages table, SQLite's
/// <c>SqliteLedger</c> clause for clause. That class's remarks are the argument - the floor, the
/// workflow scope applied inside the bound, the recent end of the window, the exclusions from
/// <see cref="EventCatalog"/> and the installed plugins, each a parameter and never spliced text.
/// Read-only by construction.
/// </summary>
public sealed class SqlServerLedger : ILedger
{
    private readonly string _connectionString;

    private readonly IPluginEventRegistry? _plugins;

    private static readonly string[] ExcludedTypes = [.. EventCatalog.ExcludedFromLedger];

    /// <param name="plugins">The installed plugins, whose events declared out of the ledger stay out
    /// of it as the platform's do, read on every query. Null: the platform's alone.</param>
    public SqlServerLedger(string connectionString, IPluginEventRegistry? plugins = null)
    {
        _connectionString = SqlServerOpen.ConnectionString(connectionString);
        _plugins = plugins;
    }

    public async Task<IReadOnlyList<Message>> ReadAsync(
        ContainerId container, long sinceSeq, long? correlation, long beforeSeq, int max,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(container);

        if (max <= 0) return [];

        string[] pluginExcluded = _plugins is null
            ? []
            : [.. _plugins.Events.Where(e => !e.InLedger).Select(e => e.Type).Distinct(StringComparer.Ordinal)];

        return await SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();

                var exclusion = NotIn(command, "excl", ExcludedTypes);
                var pluginClause = NotIn(command, "pexcl", pluginExcluded);

                command.CommandText =
                    $"""
                     SELECT {MessageRows.Columns} FROM (
                         SELECT TOP (@max) {MessageRows.Columns} FROM messages
                         WHERE seq > @since AND seq < @before
                           AND (source = @container COLLATE {SqlServerSql.NoCase} OR type = @instruction)
                           {exclusion}
                           {pluginClause}
                           AND (@correlation IS NULL OR correlation_id = @correlation)
                         ORDER BY seq DESC
                     ) recent
                     ORDER BY seq
                     """;

                command.Parameters.AddWithValue("@since", sinceSeq);
                command.Parameters.AddWithValue("@before", beforeSeq);
                command.Parameters.AddWithValue("@container", container.ToString());
                command.Parameters.AddWithValue("@instruction", MessageTypes.InstructionFor(container));
                command.Parameters.AddWithValue("@max", max);
                command.Parameters.Add("@correlation", SqlDbType.BigInt).Value = (object?)correlation ?? DBNull.Value;

                return await MessageRows.ReadAllAsync(command, token);
            },
            ct);
    }

    /// <summary>`AND type NOT IN (...)` over parameters this code names (<c>@excl0</c>, ...), or nothing
    /// for an empty list, never a malformed <c>NOT IN ()</c>.</summary>
    private static string NotIn(SqlCommand command, string prefix, IReadOnlyList<string> types)
    {
        if (types.Count == 0) return string.Empty;

        var names = new string[types.Count];
        for (var i = 0; i < types.Count; i++)
        {
            names[i] = $"@{prefix}{i}";
            command.Parameters.AddWithValue(names[i], types[i]);
        }

        return $"AND type NOT IN ({string.Join(", ", names)})";
    }
}
