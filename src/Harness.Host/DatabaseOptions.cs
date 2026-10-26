using Microsoft.Extensions.Configuration;

namespace Harness.Host;

/// <summary>Which database engine the Host runs on.</summary>
public enum DatabaseProvider
{
    Sqlite,
    SqlServer,
}

/// <summary>
/// THE DATABASE THE HOST RUNS ON: <c>HARNESS_DATABASE</c> (or the section key <c>Database:Provider</c>)
/// is <c>sqlite</c>, the default, or <c>sqlserver</c>; <c>HARNESS_DATABASE_CONNECTION</c> (or
/// <c>Database:Connection</c>) is the SqlClient connection string that <c>sqlserver</c> needs. Anything
/// else is refused with a sentence naming the value and the key it was read from, because a misspelt
/// provider that fell back to SQLite would start an instance on an empty file beside the database its
/// operator meant.
///
/// <para>
/// THE PREFIXED KEY FIRST, AND NEVER A BARE <c>Database</c>. Every environment variable is in the
/// configuration, and keys compare without case, so a bare <c>Database</c> key would read an unrelated
/// <c>DATABASE=mydb</c> that some other tool set and refuse a SQLite start that was never asked to
/// change. A section key (<c>Database:Provider</c>, <c>Database__Provider</c> in the environment) is one
/// no such variable can set. A blank value counts as unset and falls through to the next key.
/// </para>
///
/// In Harness.Host rather than Contracts: it reads <see cref="IConfiguration"/>, and Contracts
/// references nothing.
/// </summary>
public sealed record DatabaseOptions(DatabaseProvider Provider, string? Connection)
{
    /// <summary>The provider's keys, in the order they are read.</summary>
    public static readonly IReadOnlyList<string> ProviderKeys = ["HARNESS_DATABASE", "Database:Provider"];

    /// <summary>The connection string's keys, in the order they are read.</summary>
    public static readonly IReadOnlyList<string> ConnectionKeys = ["HARNESS_DATABASE_CONNECTION", "Database:Connection"];

    public static DatabaseOptions From(IConfiguration configuration)
    {
        var (name, nameKey) = First(configuration, ProviderKeys);
        var (connection, _) = First(configuration, ConnectionKeys);

        if (name is null || name.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return new DatabaseOptions(DatabaseProvider.Sqlite, connection);
        }

        if (!name.Equals("sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The database '{name}' (from {nameKey}) is not known: set {nameKey} to 'sqlite' (the default) or 'sqlserver'.");
        }

        return connection is null
            ? throw new ArgumentException(
                $"The database 'sqlserver' (from {nameKey}) needs a connection: set HARNESS_DATABASE_CONNECTION to a SQL Server connection string.")
            : new DatabaseOptions(DatabaseProvider.SqlServer, connection);
    }

    // The first key holding a value that is not blank, trimmed, with the key it came from.
    private static (string? Value, string? Key) First(IConfiguration configuration, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            if (configuration[key] is { } raw && !string.IsNullOrWhiteSpace(raw)) return (raw.Trim(), key);
        }

        return (null, null);
    }
}
