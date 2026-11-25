using Microsoft.Data.Sqlite;
using Yawble.Core.Gateway;
using Yawble.Core.Storage;
using Xunit;

namespace Yawble.Core.Tests.Gateway;

/// <summary>
/// Migration 0051 deletes the old one-row-per-request successful model forwards, keeping each run's
/// first per model vhost and the first after each credential problem or rate limit there, and leaves everything else in gateway_events as it was.
/// </summary>
public class GatewayModelForwardsMigrationTests
{
    private const string Migration = "0051_gateway_model_forwards";

    [Fact]
    public void KeepsEachRunsFirstSuccessfulForwardPerModelVhostAndNothingElseChanges()
    {
        var dir = Directory.CreateTempSubdirectory("yawble-db-").FullName;
        var cs = $"Data Source={Path.Combine(dir, "t.db")};Pooling=False";
        try
        {
            Assert.Contains(Migration, SchemaMigrator.Migrate(cs));

            // Seed as a database from before 0051 would hold it, then let the migrator apply 0051 again.
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

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
