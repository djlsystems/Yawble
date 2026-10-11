using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// THE LEDGER'S START, ON THE REAL HOST. The start that applies <c>outcome-001</c> backfills the
/// ledger from every terminal row and every completion or close still on the log, once; and
/// <c>instance.id</c> is written once and never changed. See <see cref="LedgerStart"/>.
/// </summary>
public sealed class LedgerStartTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-ledger-start-{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Database => Path.Combine(_dataRoot, "messages.db");

    public LedgerStartTests() => Directory.CreateDirectory(_dataRoot);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task The_backfill_recovers_every_terminal_row_and_close_once_and_records_when_the_ledger_began()
    {
        // A volume from before the ledger: every step but outcome-001 (and outcome-005, which indexes
        // its table), and a log holding two workflows' runs - a batched run, an unmeasured run, a
        // plugin's run - a completion, a close, and a second completion of the same workflow.
        await new SchemaMigrator(Database).ApplyAsync(
            [.. SchemaModules.All.Where(s => s.Id is not ("outcome-001" or "outcome-005"))], ct: Ct);

        var root = await InsertAsync(MessageTypes.InstructionFor(new ContainerId("Alpha", "Dev")), """{"instruction":"go"}""", "console", null);
        await InsertAsync(MessageTypes.Started, "{}", "Alpha/Dev", root);
        var measured = await InsertAsync(MessageTypes.Completed, """{"tokensIn":100,"tokensOut":40,"tokensCachedIn":50,"tokensSource":"claude"}""", "Alpha/Dev", root);
        await InsertAsync(MessageTypes.Completed, $$"""{"tokensIn":null,"tokensOut":null,"usageCountedOn":{{root}}}""", "Alpha/Dev", root);
        var second = await InsertAsync(MessageTypes.InstructionFor(new ContainerId("Alpha", "Dev")), """{"instruction":"more"}""", "console", root);
        await InsertAsync(MessageTypes.Failed, """{"tokensIn":null,"tokensOut":null}""", "Alpha/Dev", second);
        var echo = await InsertAsync(MessageTypes.InstructionFor(new ContainerId("Alpha", "Echo")), """{"instruction":"echo"}""", "console", root);
        await InsertAsync(MessageTypes.Completed, """{"tokensSource":"none"}""", "Alpha/Echo", echo);
        await InsertAsync(MessageTypes.WorkflowCompleted, "{}", "Alpha/Manager", measured);
        await InsertAsync(MessageTypes.WorkflowClosed, """{"team":"Alpha"}""", "person-1", root);
        await InsertAsync(MessageTypes.WorkflowCompleted, "{}", "Alpha/Manager", measured);

        var terminalRows = 3;
        var closes = 3;

        // First start: outcome-001 is applied and the ledger is backfilled.
        await StartAsync();

        Assert.Equal(terminalRows, await CountAsync("SELECT COUNT(*) FROM usage_ledger WHERE backfilled = 1"));
        Assert.Equal(closes, await CountAsync("SELECT COUNT(*) FROM workflow_ledger WHERE backfilled = 1"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM usage_ledger WHERE backfilled = 0"));

        var startedAt = await SettingAsync(LedgerStart.LedgerStartedAtName);
        Assert.NotNull(startedAt);
        Assert.Equal(terminalRows.ToString(System.Globalization.CultureInfo.InvariantCulture), await SettingAsync(LedgerStart.LedgerBackfilledRunsName));

        // Each setting was written with its tenant row, as every settings write is.
        Assert.Equal(1, await CountAsync(
            $"SELECT COUNT(*) FROM tenant_events WHERE action = '{TenantActions.TenantSettingChanged}' AND subject = '{LedgerStart.LedgerStartedAtName}'"));

        // A recovered row is built as a live one is: the batched run's extra row wrote nothing,
        // the measured run is weighted, unmeasured is null.
        Assert.Equal(100 + 40 + (50 / 10), await CountAsync($"SELECT billable FROM usage_ledger WHERE run_seq = {measured}"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM usage_ledger WHERE billable IS NULL AND measured = 0"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM usage_ledger WHERE member_kind = 'plugin' AND billable = 0 AND tokens_in IS NULL"));

        // Second start: nothing is recovered twice and the settings are as they were.
        await StartAsync();

        Assert.Equal(terminalRows, await CountAsync("SELECT COUNT(*) FROM usage_ledger"));
        Assert.Equal(closes, await CountAsync("SELECT COUNT(*) FROM workflow_ledger"));
        Assert.Equal(startedAt, await SettingAsync(LedgerStart.LedgerStartedAtName));
        Assert.Equal(1, await CountAsync(
            $"SELECT COUNT(*) FROM tenant_events WHERE subject = '{LedgerStart.LedgerBackfilledRunsName}'"));

        // And a direct second pass over the same log adds nothing either: the keys hold.
        Assert.Null((await LedgerStart.RunAsync(Database, Ct)).BackfilledRuns);
    }

    [Fact]
    public async Task The_instance_id_is_created_once_and_unchanged_across_restarts()
    {
        var first = await StartAsync();

        var stored = await SettingAsync(LedgerStart.InstanceIdName);
        Assert.NotNull(stored);
        Assert.True(Guid.TryParse(stored, out _));
        Assert.Equal(stored, first);

        var second = await StartAsync();

        Assert.Equal(stored, second);
        Assert.Equal(stored, await SettingAsync(LedgerStart.InstanceIdName));
        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM tenant_events WHERE subject = '{LedgerStart.InstanceIdName}'"));

        // Not the cookie's instance identity, which is a hash of the data root's path.
        Assert.DoesNotContain(stored, InstanceIdentity.CookieNameFor(_dataRoot), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Starts the real Host over this data root, and stops it; answers the instance id the
    /// Host holds.</summary>
    private async Task<string?> StartAsync()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning"));

        return factory.Services.GetRequiredService<LedgerIdentity>().InstanceId;
    }

    private async Task<long> InsertAsync(string type, string payload, string source, long? causation)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync(Ct);

        long correlation = 0;
        var depth = 0;
        if (causation is { } cause)
        {
            await using var parent = connection.CreateCommand();
            parent.CommandText = $"SELECT correlation_id, depth FROM messages WHERE seq = {cause}";
            await using var reader = await parent.ExecuteReaderAsync(Ct);
            await reader.ReadAsync(Ct);
            correlation = reader.GetInt64(0);
            depth = reader.GetInt32(1) + 1;
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO messages (type, payload, source, correlation_id, causation_seq, depth, occurred_at)
            VALUES ($type, $payload, $source, $correlation, $causation, $depth, $at)
            RETURNING seq
            """;
        insert.Parameters.AddWithValue("$type", type);
        insert.Parameters.AddWithValue("$payload", payload);
        insert.Parameters.AddWithValue("$source", source);
        insert.Parameters.AddWithValue("$correlation", correlation);
        insert.Parameters.AddWithValue("$causation", (object?)causation ?? DBNull.Value);
        insert.Parameters.AddWithValue("$depth", depth);
        insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        var seq = (long)(await insert.ExecuteScalarAsync(Ct))!;

        if (causation is null)
        {
            await using var settle = connection.CreateCommand();
            settle.CommandText = $"UPDATE messages SET correlation_id = {seq} WHERE seq = {seq}";
            await settle.ExecuteNonQueryAsync(Ct);
        }

        return seq;
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<string?> SettingAsync(string name)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM tenant_settings WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        return await command.ExecuteScalarAsync(Ct) as string;
    }
}
