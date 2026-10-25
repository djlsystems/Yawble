using Harness.Contracts;
using Harness.SqlServer.Messaging;

namespace Harness.SqlServer;

/// <summary>
/// Every SQL Server schema module's steps, concatenated in ONE place and in the order the SQLite
/// list (<c>SchemaModules</c> in the Host) applies its own: messages, auth, skills, backlog, outcome.
///
/// One list for the same reason the SQLite one is: the migrator treats a database carrying a step
/// the list does not name as one written by a newer build, so a second, shorter copy would make an
/// operator command refuse an instance a running Host had already migrated. A module's baseline
/// belongs here the moment it exists.
/// </summary>
public static class SqlServerSchemaModules
{
    public static IReadOnlyList<MigrationStep> All { get; } = [.. SqlServerMessagingSchema.Steps];
}
