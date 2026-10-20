using Microsoft.Data.Sqlite;
using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// The ledger as a query over the messages table.
///
/// Opens its own connections against the same file the store owns, exactly as the store does, and
/// the driver's pooling makes that free. Read-only by construction: there is no write path in this
/// class at all, which is what makes "immutable" a property of the design rather than a promise
/// someone has to keep.
/// </summary>
public sealed class SqliteLedger : ILedger
{
    private readonly string _connectionString;

    private readonly IPluginEventRegistry? _plugins;

    // THE EXCLUSION LIST COMES FROM `EventCatalog`, which is its ONE store.
    //
    // Hand-written `type <>` clauses here would make "which types stay out of an agent's context"
    // a second copy of what the catalog says. The catalog is the store and this reads it.
    //
    // PARAMETERISED, NEVER INTERPOLATED, exactly as the rest of the statement below is: the names
    // here are ordinals THIS code chooses ($excl0, $excl1, ...), never a value from the catalog
    // spliced into SQL text. A type name reaches the command only as a parameter value.
    private static readonly string[] ExcludedTypes = [.. EventCatalog.ExcludedFromLedger];

    private static readonly string[] ExclusionParameterNames =
        [.. Enumerable.Range(0, ExcludedTypes.Length).Select(i => $"$excl{i}")];

    // STILL AT THE PROJECTION rather than at the write. Excluding here keeps it reversible - the
    // rows are on the log, in the activity feed and in a workflow view. `LedgerExclusionTests` pins
    // both halves. Handles the empty case: no excluded types produces an empty clause, never a
    // malformed `type NOT IN ()`.
    private static readonly string ExclusionClause = ExcludedTypes.Length == 0
        ? string.Empty
        : $"AND type NOT IN ({string.Join(", ", ExclusionParameterNames)})";

    /// <summary>
    /// The generated exclusion clause, exposed for <c>LedgerExclusionTests</c> only - proving the
    /// clause names ordinals rather than a catalog value spliced into SQL text. There is no
    /// <c>InternalsVisibleTo</c> between this project and its test project, so this is public rather
    /// than adding assembly-level plumbing for one assertion.
    /// </summary>
    public static string ExclusionClauseForTests => ExclusionClause;

    /// <param name="plugins">The installed plugins, whose events declared out of the ledger (a
    /// <c>highVolume</c> one) stay out of it as the platform's do. Read on every query, so a plugin
    /// installed or upgraded while the Host runs is seen on the next run. Null: the platform's alone.</param>
    public SqliteLedger(string databasePath, IPluginEventRegistry? plugins = null)
    {
        _connectionString = SqliteOpen.ConnectionString(databasePath);
        _plugins = plugins;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await SqlitePragmas.ApplyAsync(connection, ct: ct);
        return connection;
    }

    public async Task<IReadOnlyList<Message>> ReadAsync(
        ContainerId container, long sinceSeq, long? correlation, long beforeSeq, int max,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(container);

        if (max <= 0) return [];

        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();

        // THE INSTALLED PLUGINS' EXCLUSIONS, parameterised the same way under their own ordinals. A
        // plugin type is never a platform type, so the two lists cannot overlap.
        string[] pluginExcluded = _plugins is null
            ? []
            : [.. _plugins.Events.Where(e => !e.InLedger).Select(e => e.Type).Distinct(StringComparer.Ordinal)];
        var pluginClause = pluginExcluded.Length == 0
            ? string.Empty
            : $"AND type NOT IN ({string.Join(", ", pluginExcluded.Select((_, i) => $"$pexcl{i}"))})";

        // Newest-first inside, oldest-first outside. The bound has to apply to the RECENT end -- a
        // LIMIT on an ascending scan would keep the oldest entries and drop everything that just
        // happened, which is the opposite of a memory.
        //
        // Two halves: what this container was TOLD (its addressed instruction type) and what it
        // PUBLISHED (its own source). Parameterised, never interpolated -- a container name arrives
        // over the API and must not be able to reach the SQL text.
        //
        // `seq > $since` is the isolation floor: a container remembers nothing from before it
        // existed. Without it a new container inherits every same-named predecessor's history.
        command.CommandText =
            $"""
             SELECT {MessageRows.Columns} FROM (
                 SELECT {MessageRows.Columns} FROM messages
                 WHERE seq > $since AND seq < $before AND (source = $container OR type = $instruction)
                   {ExclusionClause}
                   {pluginClause}
                   AND ($correlation IS NULL OR correlation_id = $correlation)
                 ORDER BY seq DESC
                 LIMIT $max
             )
             ORDER BY seq
             """;

        command.Parameters.AddWithValue("$since", sinceSeq);
        command.Parameters.AddWithValue("$before", beforeSeq);
        command.Parameters.AddWithValue("$container", container.ToString());
        command.Parameters.AddWithValue("$instruction", MessageTypes.InstructionFor(container));
        command.Parameters.AddWithValue("$max", max);

        for (var i = 0; i < ExcludedTypes.Length; i++)
        {
            command.Parameters.AddWithValue(ExclusionParameterNames[i], ExcludedTypes[i]);
        }

        for (var i = 0; i < pluginExcluded.Length; i++)
        {
            command.Parameters.AddWithValue($"$pexcl{i}", pluginExcluded[i]);
        }

        // INSIDE THE SUBQUERY, ABOVE THE LIMIT, AND THAT PLACEMENT IS THE WHOLE FIX. A predicate
        // applied to the OUTER select would read the newest $max rows across every workflow this
        // container holds and then discard the ones belonging to other threads - so a chatty
        // workflow would still consume the window and a quiet one would still come back empty. The
        // filter would be doing visible work and fixing nothing, and every existing test would pass.
        //
        // NULL READS EVERYTHING, which is what an audit wants. `$correlation IS NULL OR ...` rather
        // than composing two SQL strings: one statement, one plan to read, and the parameter carries
        // the decision.
        command.Parameters.AddWithValue("$correlation", (object?)correlation ?? DBNull.Value);

        return await MessageRows.ReadAllAsync(command, ct);
    }
}
