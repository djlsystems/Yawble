using Microsoft.Extensions.Configuration;

namespace Harness.Host;

/// <summary>Which database engine the Host runs on.</summary>
public enum DatabaseProvider
{
    Sqlite,
    SqlServer,
}

/// <summary>
/// THE DATABASE THE HOST RUNS ON: `Database` (or `HARNESS_DATABASE`) is `sqlite`, the default, or
/// `sqlserver`; `DatabaseConnection` (or `HARNESS_DATABASE_CONNECTION`) is the SqlClient connection
/// string that `sqlserver` needs. Anything else is refused with a sentence naming the value, because a
/// misspelt provider that fell back to SQLite would start an instance on an empty file beside the
/// database its operator meant.
///
/// In Harness.Host rather than Contracts: it reads <see cref="IConfiguration"/>, and Contracts
/// references nothing.
/// </summary>
public sealed record DatabaseOptions(DatabaseProvider Provider, string? Connection)
{
    public static DatabaseOptions From(IConfiguration configuration)
    {
        var name = Value(configuration["Database"] ?? configuration["HARNESS_DATABASE"]);
        var connection = Value(configuration["DatabaseConnection"] ?? configuration["HARNESS_DATABASE_CONNECTION"]);

        if (name is null || name.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return new DatabaseOptions(DatabaseProvider.Sqlite, connection);
        }

        if (!name.Equals("sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The database '{name}' is not known: set HARNESS_DATABASE to 'sqlite' (the default) or 'sqlserver'.");
        }

        return connection is null
            ? throw new ArgumentException(
                "The database 'sqlserver' needs a connection: set HARNESS_DATABASE_CONNECTION to a SQL Server connection string.")
            : new DatabaseOptions(DatabaseProvider.SqlServer, connection);
    }

    private static string? Value(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
}
