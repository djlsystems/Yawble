using Microsoft.Data.Sqlite;
using Yawble.Core.Gateway;
using Yawble.Core.Launch;
using Yawble.Core.Storage;
using Xunit;

namespace Yawble.Core.Tests.Gateway;

/// <summary>
/// Migration 0050 adds gateway_events.count (default 1). Migration 0051 deletes successful model forwards written
/// one row per request, keeping each run's first per model vhost and the first after each credential problem or rate
/// limit there; every other row of gateway_events stays. What 0051 keeps is what GatewayLog.Write keeps.
/// </summary>
public class GatewayModelForwardsMigrationTests
{
    private const string Migration = "0051_gateway_model_forwards";

    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void KeepsEachRunsFirstSuccessfulForwardPerModelVhostAndNothingElseChanges()
    {
        var dir = Directory.CreateTempSubdirectory("yawble-db-").FullName;
        var cs = $"Data Source={Path.Combine(dir, "t.db")};Pooling=False";
        try
        {
            Assert.Contains(Migration, SchemaMigrator.Migrate(cs));

            // Seed rows one per request, then let the migrator apply 0051 again.
            using var c = new SqliteConnection(cs);
            c.Open();
            Exec(c, $"DELETE FROM schema_migrations WHERE name = '{Migration}';");

            var kept = new List<long>();
            var dropped = new List<long>();
            foreach (var vhost in Vhosts.ProductionUpstreams.Keys)
            {
                // Run 1: three successful forwards; only the first stays.
                kept.Add(Insert(c, 1, GatewayEventKinds.Forward, vhost, 200));
                dropped.Add(Insert(c, 1, GatewayEventKinds.Forward, vhost, 200));
                dropped.Add(Insert(c, 1, GatewayEventKinds.Forward, vhost, 204));
                // Run 2's first is its own, even after run 1's.
                kept.Add(Insert(c, 2, GatewayEventKinds.Forward, vhost, 201));
                dropped.Add(Insert(c, 2, GatewayEventKinds.Forward, vhost, 200));
                // Not 2xx: kept, and does not count as the first.
                kept.Add(Insert(c, 3, GatewayEventKinds.Forward, vhost, 429));
                kept.Add(Insert(c, 3, GatewayEventKinds.Forward, vhost, 500));
                kept.Add(Insert(c, 3, GatewayEventKinds.Forward, vhost, 302));
                kept.Add(Insert(c, 3, GatewayEventKinds.Forward, vhost, 200));
                dropped.Add(Insert(c, 3, GatewayEventKinds.Forward, vhost, 200));
                // Other kinds on a model vhost: kept.
                kept.Add(Insert(c, 1, GatewayEventKinds.PathDenied, vhost, 403));
                kept.Add(Insert(c, 1, GatewayEventKinds.RateLimited, vhost, 429));
                kept.Add(Insert(c, 1, GatewayEventKinds.CredentialProblem, vhost, 200));
                // No run: kept.
                kept.Add(Insert(c, null, GatewayEventKinds.Forward, vhost, 200));
                kept.Add(Insert(c, null, GatewayEventKinds.Forward, vhost, 200));
            }
            // The vhost in another case is the same vhost (run 2 already has its first on model).
            dropped.Add(Insert(c, 2, GatewayEventKinds.Forward, "MODEL.yawble.internal", 200));
            // Not a model vhost: every row kept.
            kept.Add(Insert(c, 1, GatewayEventKinds.Forward, Vhosts.Bus, 200));
            kept.Add(Insert(c, 1, GatewayEventKinds.Forward, Vhosts.Bus, 200));
            kept.Add(Insert(c, 1, GatewayEventKinds.Forward, "registry.npmjs.org", 200));
            kept.Add(Insert(c, 1, GatewayEventKinds.Forward, "registry.npmjs.org", 200));
            kept.Add(Insert(c, 1, GatewayEventKinds.ConnectAllowed, null, null));
            kept.Add(Insert(c, 1, GatewayEventKinds.ConnectAllowed, null, null));
            kept.Add(Insert(c, 1, GatewayEventKinds.Bus, null, 200));
            kept.Add(Insert(c, 1, GatewayEventKinds.Bus, null, 200));

            Assert.Contains(Migration, SchemaMigrator.Migrate(cs));

            var left = Ids(c);
            Assert.Equal(kept.Order(), left);
            Assert.Empty(dropped.Intersect(left));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void KeepsTheFirstSuccessAfterEachCredentialProblemOrRateLimitOnThatRunsVhost()
    {
        // The live rule (GatewayLog.Write) writes the first 2xx forward after a credential problem or rate
        // limit on the vhost again, and GatewayVerdicts.From reads it as the problem cleared; 0051 must keep it too.
        var dir = Directory.CreateTempSubdirectory("yawble-db-").FullName;
        var cs = $"Data Source={Path.Combine(dir, "t.db")};Pooling=False";
        try
        {
            Assert.Contains(Migration, SchemaMigrator.Migrate(cs));
            using var c = new SqliteConnection(cs);
            c.Open();
            Exec(c, $"DELETE FROM schema_migrations WHERE name = '{Migration}';");

            const string model = "model.yawble.internal";
            const string grok = "grok.yawble.internal";
            const string F = GatewayEventKinds.Forward, Cred = GatewayEventKinds.CredentialProblem, Rate = GatewayEventKinds.RateLimited;
            var kept = new List<long>();
            var dropped = new List<long>();

            kept.Add(Insert(c, 10, F, model, 200));
            dropped.Add(Insert(c, 10, F, model, 200));
            // A credential problem: the next success is kept, the one after it is not.
            kept.Add(Insert(c, 10, Cred, model, 401));
            kept.Add(Insert(c, 10, F, model, 200));
            dropped.Add(Insert(c, 10, F, model, 204));
            // Two rate limits in a row: one success kept after them.
            kept.Add(Insert(c, 10, Rate, model, 429));
            kept.Add(Insert(c, 10, Rate, model, 429));
            kept.Add(Insert(c, 10, F, model, 201));
            dropped.Add(Insert(c, 10, F, model, 200));
            // A failed forward after the problem is kept and is not the success that answers it.
            kept.Add(Insert(c, 10, Cred, model, 401));
            kept.Add(Insert(c, 10, F, model, 401));
            kept.Add(Insert(c, 10, F, model, 200));
            dropped.Add(Insert(c, 10, F, model, 200));
            // A problem on another vhost, or in another run, does not reopen model in run 10.
            kept.Add(Insert(c, 10, Rate, grok, 429));
            kept.Add(Insert(c, 11, Cred, model, 401));
            dropped.Add(Insert(c, 10, F, model, 200));
            // Grok and run 11 keep their own first success, once.
            kept.Add(Insert(c, 10, F, grok, 200));
            dropped.Add(Insert(c, 10, F, grok, 200));
            kept.Add(Insert(c, 11, F, model, 200));
            dropped.Add(Insert(c, 11, F, model, 200));
            // The vhost's case does not matter, for the problem or the forward.
            kept.Add(Insert(c, 10, Cred, "Model.Yawble.Internal", 401));
            kept.Add(Insert(c, 10, F, "MODEL.yawble.internal", 200));
            dropped.Add(Insert(c, 10, F, model, 200));
            // A problem before the run's first success: that success is kept once.
            kept.Add(Insert(c, 12, Rate, model, 429));
            kept.Add(Insert(c, 12, F, model, 200));
            dropped.Add(Insert(c, 12, F, model, 200));
            // No run: kept, and a no-run problem reopens nothing.
            kept.Add(Insert(c, null, Cred, model, 401));
            kept.Add(Insert(c, null, F, model, 200));
            dropped.Add(Insert(c, 12, F, model, 200));

            Assert.Contains(Migration, SchemaMigrator.Migrate(cs));

            var left = Ids(c);
            Assert.Equal(kept.Order(), left);
            Assert.Empty(dropped.Intersect(left));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AFreshDatabase_AppliesBothMigrations_AndTheCountColumnDefaultsToOne()
    {
        var dir = Directory.CreateTempSubdirectory("yawble-db-").FullName;
        var cs = $"Data Source={Path.Combine(dir, "fresh.db")};Pooling=False";
        try
        {
            var applied = SchemaMigrator.Migrate(cs);
            Assert.Contains("0050_gateway_event_count", applied);
            Assert.Contains("0051_gateway_model_forwards", applied);
            Assert.Equal(SchemaMigrator.Available, applied);
            Assert.Equal(1, Scalar(cs, "SELECT count(*) FROM pragma_table_info('gateway_events') WHERE name = 'count' AND \"notnull\" = 1 AND dflt_value = '1';"));
            Exec(cs, "INSERT INTO gateway_events (run_id, member, at, kind, guardrail) VALUES (1, 'a/b', '2026-11-18T00:00:00Z', 'bus', 0);");
            Assert.Equal(1, Scalar(cs, "SELECT count FROM gateway_events;"));
            Assert.Equal(SchemaMigrator.Available, SchemaMigrator.Migrate(cs)); // again: nothing new, no error
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ADatabaseAtMigration0049_GainsCount_KeepsEachRunsFirstForwardPerModelVhost_RestUntouched()
    {
        var dir = Directory.CreateTempSubdirectory("yawble-db-").FullName;
        var cs = $"Data Source={Path.Combine(dir, "old.db")};Pooling=False";
        try
        {
            // A database at migration 0049: every migration before 0050, applied the migrator's way.
            using (var c = new SqliteConnection(cs))
            {
                c.Open();
                Exec(c, "PRAGMA foreign_keys=ON; CREATE TABLE schema_migrations (name TEXT PRIMARY KEY NOT NULL, applied_at TEXT NOT NULL) STRICT;");
                foreach (var name in SchemaMigrator.Available.Where(n => string.CompareOrdinal(n, "0050") < 0))
                {
                    using var tx = c.BeginTransaction();
                    Exec(c, ReadMigration(name), tx);
                    Exec(c, $"INSERT INTO schema_migrations (name, applied_at) VALUES ('{name}', '2026-11-01T00:00:00Z');", tx);
                    tx.Commit();
                }
                Assert.Equal(0, Scalar(c, "SELECT count(*) FROM pragma_table_info('gateway_events') WHERE name = 'count';"));
            }

            var kept = new List<long>();
            var gone = new List<long>();
            using (var c = new SqliteConnection(cs))
            {
                c.Open();
                // Run 1: 20 successful model requests on model, 3 on grok, a credential problem and a 401 in between.
                kept.Add(Insert(c, 1, GatewayEventKinds.Forward, Vhosts.Model, 200));
                for (var i = 0; i < 9; i++) gone.Add(Insert(c, 1, GatewayEventKinds.Forward, Vhosts.Model, 200));
                kept.Add(Insert(c, 1, GatewayEventKinds.Forward, Vhosts.Model, 401));
                kept.Add(Insert(c, 1, GatewayEventKinds.CredentialProblem, Vhosts.Model, 401));
                kept.Add(Insert(c, 1, GatewayEventKinds.Forward, Vhosts.Model, 200)); // the success after the problem
                for (var i = 0; i < 9; i++) gone.Add(Insert(c, 1, GatewayEventKinds.Forward, Vhosts.Model, 200));
                kept.Add(Insert(c, 1, GatewayEventKinds.Forward, Vhosts.Grok, 200));
                gone.Add(Insert(c, 1, GatewayEventKinds.Forward, Vhosts.Grok, 200));
                gone.Add(Insert(c, 1, GatewayEventKinds.Forward, Vhosts.Grok, 200));
                // Run 2: its own first, then repeats.
                kept.Add(Insert(c, 2, GatewayEventKinds.Forward, Vhosts.Model, 200));
                gone.Add(Insert(c, 2, GatewayEventKinds.Forward, Vhosts.Model, 200));
                // Host denials written one row each, bus calls, connects, non-model forwards: all kept.
                for (var i = 0; i < 3; i++) kept.Add(Insert(c, 1, GatewayEventKinds.HostDenied, "denied.example", 403));
                kept.Add(Insert(c, 1, GatewayEventKinds.Bus, Vhosts.Bus, 200));
                kept.Add(Insert(c, 1, GatewayEventKinds.ConnectAllowed, null, null));
                kept.Add(Insert(c, 1, GatewayEventKinds.Forward, "registry.npmjs.org", 200));
                kept.Add(Insert(c, 1, GatewayEventKinds.Forward, "registry.npmjs.org", 200));
            }

            var applied = SchemaMigrator.Migrate(cs);
            Assert.Equal(SchemaMigrator.Available, applied);

            using (var c = new SqliteConnection(cs))
            {
                c.Open();
                Assert.Equal(kept.Order(), Ids(c));
                Assert.Equal(kept.Count, Scalar(c, "SELECT count(*) FROM gateway_events WHERE count = 1;"));
            }
            // The verdict over what is left: run 1's credential problem reads as cleared.
            Assert.False(GatewayVerdicts.From(new GatewayLog(cs).Read(1)).CredentialRejected);

            // The app writes on the migrated database: a new denial is counted on its own row, the one-row-each denials stay.
            var log = new GatewayLog(cs, new FakeClock(T0));
            for (var i = 0; i < 4; i++) log.Write(new GatewayEvent(3, "beta/Tess", T0, GatewayEventKinds.HostDenied, false, "denied.example", "POST", "/v1/messages", 403, null));
            Assert.Equal(4, Assert.Single(log.Read(3)).Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// 0051 against the live rule: random logs with every event a row, taken through the migration, give exactly the rows
    /// GatewayLog.Write keeps for the same events. Runs, mixed-case model vhosts, non-model vhosts, non-2xx forwards,
    /// credential problems and rate limits on either vhost, and rows with no run.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Migration0051_KeepsExactlyTheRowsTheLiveGatewayLogKeeps_OnRandomLogs(int seed)
    {
        var rnd = new Random(seed);
        string[] vhosts = [Vhosts.Model, Vhosts.Model.ToUpperInvariant(), Vhosts.Grok, Vhosts.Xai, "registry.npmjs.org"];
        long?[] runs = [1, 2, 3, null];
        var events = new List<GatewayEvent>();
        for (var i = 0; i < 400; i++)
        {
            var run = runs[rnd.Next(runs.Length)];
            var vhost = vhosts[rnd.Next(vhosts.Length)];
            var roll = rnd.Next(20);
            events.Add(roll switch
            {
                0 => new(run, "beta/Tess", T0, GatewayEventKinds.CredentialProblem, false, vhost, "POST", "/v1/messages", 401, null),
                1 => new(run, "beta/Tess", T0, GatewayEventKinds.RateLimited, false, vhost, "POST", "/v1/messages", 429, null),
                2 => new(run, "beta/Tess", T0, GatewayEventKinds.Forward, false, vhost, "POST", "/v1/messages", 500, null),
                _ => new(run, "beta/Tess", T0, GatewayEventKinds.Forward, false, vhost, "POST", "/v1/messages", 200 + rnd.Next(2), null),
            });
        }

        var dir = Directory.CreateTempSubdirectory("yawble-db-").FullName;
        try
        {
            // Live: GatewayLog.Write on a fresh database.
            var live = $"Data Source={Path.Combine(dir, "live.db")};Pooling=False";
            SchemaMigrator.Migrate(live);
            var log = new GatewayLog(live, new FakeClock(T0));
            foreach (var e in events) log.Write(e);

            // Migrated: every event a row, then 0051.
            var old = $"Data Source={Path.Combine(dir, "old.db")};Pooling=False";
            SchemaMigrator.Migrate(old);
            using (var c = new SqliteConnection(old))
            {
                c.Open();
                Exec(c, "DELETE FROM schema_migrations WHERE name = '0051_gateway_model_forwards';");
                foreach (var e in events) InsertEvent(c, e);
            }
            SchemaMigrator.Migrate(old);
            Assert.Equal(1, Scalar(old, "SELECT count(*) FROM schema_migrations WHERE name = '0051_gateway_model_forwards';"));

            var a = Rows(live);
            var b = Rows(old);
            Assert.True(b.Count < events.Count); // the migration did delete
            Assert.Equal(a, b);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }

        static List<string> Rows(string cs)
        {
            using var c = new SqliteConnection(cs);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT ifnull(run_id, -1) || '|' || kind || '|' || ifnull(vhost, '') || '|' || ifnull(status, '') FROM gateway_events ORDER BY id;";
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    private static long Insert(SqliteConnection c, long? run, string kind, string? vhost, int? status)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO gateway_events (run_id, member, at, kind, guardrail, vhost, method, target, status)
            VALUES ($r, 'DeveloperA', '2026-11-18T10:00:00.0000000+00:00', $k, 0, $v, 'POST', '/v1/messages', $s);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$r", (object?)run ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$v", (object?)vhost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", (object?)status ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    private static List<long> Ids(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM gateway_events ORDER BY id;";
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    private static string ReadMigration(string name)
    {
        using var stream = typeof(SchemaMigrator).Assembly.GetManifestResourceStream("Yawble.Core.Migrations." + name + ".sql")!;
        return new StreamReader(stream).ReadToEnd();
    }

    private static void InsertEvent(SqliteConnection c, GatewayEvent e)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO gateway_events (run_id, member, at, kind, guardrail, vhost, method, target, status)
            VALUES ($r, 'beta/Tess', '2026-11-10T10:00:00.0000000+00:00', $k, 0, $v, 'POST', '/v1/messages', $s);
            """;
        cmd.Parameters.AddWithValue("$r", (object?)e.RunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$k", e.Kind);
        cmd.Parameters.AddWithValue("$v", (object?)e.Vhost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", (object?)e.Status ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(string cs, string sql)
    {
        using var c = new SqliteConnection(cs);
        c.Open();
        return Scalar(c, sql);
    }

    private static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void Exec(string cs, string sql)
    {
        using var c = new SqliteConnection(cs);
        c.Open();
        Exec(c, sql);
    }

    private static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
