using Microsoft.Data.Sqlite;
using Xunit;
using Yawble.Core.Storage;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;

namespace Yawble.Core.Tests.Storage;

/// <summary>
/// The one place connections to the app's database are opened: what every connection is set to, the lighter flush for
/// logs, and the statistics refresh. Fakes only: a temp SQLite file.
/// </summary>
public sealed class DbTests : IDisposable
{
    private readonly TempDb _db = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _db.Dispose();
    }

    private static long Pragma(SqliteConnection c, string name)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA {name};";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void Open_sets_the_connection_for_the_apps_state()
    {
        using var c = Db.Open(_db.ConnectionString);

        Assert.Equal(1, Pragma(c, "foreign_keys"));
        Assert.Equal(5000, Pragma(c, "busy_timeout"));
        Assert.Equal(67108864, Pragma(c, "journal_size_limit"));
        Assert.Equal(268435456, Pragma(c, "mmap_size"));
        Assert.Equal(2, Pragma(c, "synchronous")); // FULL
    }

    [Fact]
    public void OpenLog_flushes_at_checkpoints_and_is_otherwise_the_same()
    {
        using var c = Db.OpenLog(_db.ConnectionString);

        Assert.Equal(1, Pragma(c, "foreign_keys"));
        Assert.Equal(5000, Pragma(c, "busy_timeout"));
        Assert.Equal(67108864, Pragma(c, "journal_size_limit"));
        Assert.Equal(1, Pragma(c, "synchronous")); // NORMAL
    }

    /// <summary>A pooled connection goes from a log to a store: the store still gets the full flush.</summary>
    [Fact]
    public void A_connection_a_log_used_is_set_back_for_the_apps_state()
    {
        var pooled = $"Data Source={_db.Path}"; // pooling on, as the app opens it
        using (var log = Db.OpenLog(pooled)) Assert.Equal(1, Pragma(log, "synchronous"));

        using var c = Db.Open(pooled);

        Assert.Equal(2, Pragma(c, "synchronous"));
    }

    [Fact]
    public void OpenBare_applies_no_settings()
    {
        using var c = Db.OpenBare(_db.ConnectionString);

        Assert.Equal(0, Pragma(c, "busy_timeout"));
        Assert.Equal(0, Pragma(c, "mmap_size"));
    }

    [Fact]
    public void Optimize_gives_the_query_planner_statistics_for_tables_with_rows()
    {
        using (var c = Db.Open(_db.ConnectionString))
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO messages (type, payload, source, depth, occurred_at) VALUES ('a', '{}', 'person', 0, 'now');";
            for (var i = 0; i < 20; i++) cmd.ExecuteNonQuery();
        }

        Db.Optimize(_db.ConnectionString);

        using var check = Db.Open(_db.ConnectionString);
        using var q = check.CreateCommand();
        q.CommandText = "SELECT count(*) FROM sqlite_stat1 WHERE tbl = 'messages';";
        Assert.True((long)q.ExecuteScalar()! > 0);
    }

    [Fact]
    public void Optimize_never_throws()
    {
        var lines = new List<string>();

        Db.Optimize($"Data Source={Path.Combine(_db.Dir, "no-such-folder", "x.db")};Pooling=False", lines.Add);

        Assert.Single(lines);
    }

    /// <summary>Every store opens its connection through <see cref="Db"/>, so none misses a setting.</summary>
    [Fact]
    public void No_other_source_file_opens_a_connection_itself()
    {
        var src = Path.Combine(Binaries.RepoRoot, "core", "src", "Yawble.Core");
        var own = Path.Combine(src, "Storage", "Db.cs");
        var others = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !string.Equals(f, own, StringComparison.OrdinalIgnoreCase))
            .Where(f => File.ReadAllText(f).Contains("new SqliteConnection(", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(src, f))
            .ToList();

        Assert.Empty(others);
    }
}
