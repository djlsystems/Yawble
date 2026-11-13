using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Integrations;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>Deleting a team removes its integration state and records its pending asks as cancelled; archiving keeps it.</summary>
public class TeamCleanupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static long Count(string connectionString, string sql)
    {
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void Run(string connectionString, string sql)
    {
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Alpha and Beta both offered a CLI and an MCP server; Alpha has choices, rules, pending asks, a watch and a host task.</summary>
    private static async Task<(long Alpha, long Beta)> Seed(BusEngine engine, string cs)
    {
        var alpha = await engine.CreateTeamAsync("Alpha", ct: Ct);
        var beta = await engine.CreateTeamAsync("Beta", ct: Ct);
        Run(cs, $"""
            INSERT INTO integrations (id, kind, name, enabled_all, created_at, updated_at) VALUES (1, 'cli', 'node', 0, 'x', 'x');
            INSERT INTO mcp_servers (id, name, command, created_at, updated_at) VALUES (1, 'fake', 'node', 'x', 'x');
            INSERT INTO integration_teams (integration_id, team_id) VALUES (1, {alpha}), (1, {beta});
            INSERT INTO mcp_server_teams (server_id, team_id) VALUES (1, {alpha}), (1, {beta});
            INSERT INTO integration_rules (integration_id, team_id, pattern, decision, created_at) VALUES (1, {alpha}, 'a*', 'allow', 'x'), (1, NULL, 'b*', 'ask', 'x');
            INSERT INTO mcp_rules (server_id, team_id, pattern, decision, created_at) VALUES (1, {alpha}, 'echo', 'allow', 'x');
            INSERT INTO integration_choices (integration_id, team_id, path, decision, chosen_at) VALUES (1, {alpha}, 'run', 'allow', 'x'), (1, NULL, 'run', 'ask', 'x'), (1, {beta}, 'run', 'deny', 'x');
            INSERT INTO mcp_choices (server_id, team_id, path, decision, chosen_at) VALUES (1, {alpha}, 'echo', 'allow', 'x'), (1, NULL, 'echo', 'ask', 'x');
            INSERT INTO integration_asks (at, integration, team_id, member, args, reason, workflow, causation_seq) VALUES
                ('x', 'node', {alpha}, 'Alpha/Ada', '["run","one"]', 'why', 0, 0),
                ('x', 'mcp:fake', {alpha}, 'Alpha/Ada', '["fake__echo"]', 'why', 0, 0),
                ('x', 'node', {beta}, 'Beta/Bo', '["run","two"]', 'why', 0, 0);
            INSERT INTO watches (team_id, name, integration, args, key_field, event_name, interval_sec, created_at, updated_at)
                VALUES ({alpha}, 'prs', 'node', '[]', 'number', 'web.pr', 60, 'x', 'x');
            INSERT INTO host_tasks (id, team_id, name, repo, program, args, workdir, timeout_s, env_names, watch, pin_mode, created_at, updated_at)
                VALUES (1, {alpha}, 'build', 'r', 'p', '[]', '.', 60, '[]', '[]', 'files', 'x', 'x');
            INSERT INTO host_task_approvals (task_id, definition_hash, files_hash, definition_json, files_json, commit_sha, approved_at)
                VALUES (1, 'h', 'h', '[]', '[]', 'c', 'x');
            INSERT INTO host_task_runs (task_id, team_id, task_name, repo_path, started_at, state) VALUES (1, {alpha}, 'build', 'r', 'x', 'done');
            """);
        return (alpha, beta);
    }

    [Fact]
    public async Task DeletingATeam_RemovesItsIntegrationState_AndRecordsCancelledAsks()
    {
        using var db = new TempDb();
        await using var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false });
        await using var integrations = new IntegrationService(db.ConnectionString, engine);
        engine.Integrations = integrations;
        var (alpha, beta) = await Seed(engine, db.ConnectionString);
        Assert.Equal(2, integrations.PendingCount(alpha));

        await engine.DeleteTeamAsync("Alpha", Ct);

        foreach (var table in new[] { "integration_asks", "integration_choices", "integration_rules", "integration_teams", "mcp_choices", "mcp_rules",
                     "mcp_server_teams", "watches", "host_tasks" })
            Assert.Equal(0, Count(db.ConnectionString, $"SELECT count(*) FROM {table} WHERE team_id = {alpha}"));
        Assert.Equal(0, Count(db.ConnectionString, "SELECT count(*) FROM host_task_approvals"));
        Assert.Equal(1, Count(db.ConnectionString, "SELECT count(*) FROM host_task_runs")); // history stays
        // The default choices, the default rule and the integrations remain; so does Beta's.
        Assert.Equal(1, Count(db.ConnectionString, "SELECT count(*) FROM integration_choices WHERE team_id IS NULL"));
        Assert.Equal(1, Count(db.ConnectionString, "SELECT count(*) FROM mcp_choices WHERE team_id IS NULL"));
        Assert.Equal(1, Count(db.ConnectionString, "SELECT count(*) FROM integration_rules WHERE team_id IS NULL"));
        Assert.Equal(1, Count(db.ConnectionString, "SELECT count(*) FROM integrations"));
        Assert.Equal(1, Count(db.ConnectionString, "SELECT count(*) FROM mcp_servers"));
        Assert.Equal(1, Count(db.ConnectionString, $"SELECT count(*) FROM integration_teams WHERE team_id = {beta}"));
        Assert.Equal(1, Count(db.ConnectionString, $"SELECT count(*) FROM mcp_server_teams WHERE team_id = {beta}"));
        Assert.Equal(1, Count(db.ConnectionString, $"SELECT count(*) FROM integration_choices WHERE team_id = {beta}"));
        Assert.Equal(1, Count(db.ConnectionString, $"SELECT count(*) FROM integration_asks WHERE team_id = {beta}"));

        var calls = (JsonArray)(await integrations.DispatchAsync("integrations.calls", new JsonObject(), Ct))!;
        var cancelled = calls.Where(x => x!["decision"]!.GetValue<string>() == "cancelled").ToList();
        Assert.Equal(2, cancelled.Count);
        Assert.All(cancelled, x => { Assert.Equal("team deleted", x!["rule"]!.GetValue<string>()); Assert.Equal("Alpha", x["team"]!.GetValue<string>()); });
        Assert.Contains(cancelled, x => x!["integration"]!.GetValue<string>() == "mcp:fake");
        Assert.Contains(cancelled, x => x!["args"]!.GetValue<string>().Contains("one"));
        Assert.Equal(1, integrations.PendingCount()); // only Beta's ask is left
        Assert.Equal(0, integrations.PendingCount(alpha));
    }

    [Fact]
    public async Task ArchivingATeamKeepsItsIntegrationState()
    {
        using var db = new TempDb();
        await using var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false });
        await using var integrations = new IntegrationService(db.ConnectionString, engine);
        engine.Integrations = integrations;
        var (alpha, _) = await Seed(engine, db.ConnectionString);

        await engine.ArchiveTeamAsync("Alpha", Ct);

        Assert.Equal(1, Count(db.ConnectionString, $"SELECT count(*) FROM integration_teams WHERE team_id = {alpha}"));
        Assert.Equal(1, Count(db.ConnectionString, $"SELECT count(*) FROM integration_choices WHERE team_id = {alpha}"));
        Assert.Equal(1, Count(db.ConnectionString, $"SELECT count(*) FROM watches WHERE team_id = {alpha}"));
        Assert.Equal(2, integrations.PendingCount(alpha));
        Assert.Equal(0, Count(db.ConnectionString, "SELECT count(*) FROM integration_calls WHERE decision = 'cancelled'"));
    }

    /// <summary>Removing a CLI or an MCP server records its pending asks as cancelled in the call history and deletes them; others stay.</summary>
    [Theory]
    [InlineData("integrations.remove", "node", "node", "CLI removed")]
    [InlineData("integrations.mcpRemove", "fake", "mcp:fake", "MCP server removed")]
    public async Task RemovingAnIntegration_CancelsItsPendingAsks(string method, string name, string integration, string why)
    {
        using var db = new TempDb();
        await using var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false });
        await using var integrations = new IntegrationService(db.ConnectionString, engine);
        engine.Integrations = integrations;
        var (alpha, beta) = await Seed(engine, db.ConnectionString);
        var mine = Count(db.ConnectionString, $"SELECT count(*) FROM integration_asks WHERE integration = '{integration}'");
        var others = Count(db.ConnectionString, $"SELECT count(*) FROM integration_asks WHERE integration <> '{integration}'");
        Assert.True(mine > 0);

        await integrations.DispatchAsync(method, new JsonObject { ["name"] = name }, Ct);

        Assert.Equal(0, Count(db.ConnectionString, $"SELECT count(*) FROM integration_asks WHERE integration = '{integration}'"));
        Assert.Equal(others, Count(db.ConnectionString, $"SELECT count(*) FROM integration_asks"));
        Assert.Equal(mine, Count(db.ConnectionString,
            $"SELECT count(*) FROM integration_calls WHERE integration = '{integration}' AND decision = 'cancelled' AND rule = '{why}'"));
        Assert.Equal(1, Count(db.ConnectionString, $"SELECT count(*) FROM integration_calls WHERE integration = '{integration}' AND team = 'Alpha' AND member = 'Alpha/Ada'"));
        _ = (alpha, beta);
    }
}
