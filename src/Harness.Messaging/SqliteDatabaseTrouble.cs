using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>The SQLite version of <see cref="IDatabaseTrouble"/>: <see cref="SqliteTrouble.KindOf"/>, unchanged.</summary>
public sealed class SqliteDatabaseTrouble : IDatabaseTrouble
{
    /// <summary>The one instance; it holds nothing.</summary>
    public static SqliteDatabaseTrouble Instance { get; } = new();

    public string? KindOf(Exception? exception) => SqliteTrouble.KindOf(exception);
}
