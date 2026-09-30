using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;

namespace Harness.Tests;

/// <summary>
/// THE LEDGER IS A PROJECTION OF THE MESSAGE LOG, AND A RUN'S CONTEXT COMES FROM IT. What the
/// container published last run is in the next run's context; a log row the projection leaves out
/// (`progress`) is not. Context is read per workflow, so the second instruction joins the first's.
/// </summary>
public sealed class LedgerContextTests
{
    [Fact]
    public async Task The_next_runs_context_holds_the_last_runs_output_and_not_its_progress()
    {
        await using var bed = new ContainerTestBed();
        var dev = new ContainerId("Alpha", "Dev");
        var container = await bed.AddAsync(dev);
        var ct = TestContext.Current.CancellationToken;

        bed.Agent.Behaviour = async invocation =>
        {
            if (bed.Agent.RunsFor(dev) == 1)
            {
                await bed.Store.AppendAsync(new NewMessage(
                    MessageTypes.Progress, """{"status":"progress-sentinel"}""", dev.ToString(),
                    container.CurrentCausation), ct);
                return new AgentResult(0, "output-sentinel");
            }

            return new AgentResult(0, "done");
        };

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(dev), """{"instruction":"first"}""", "console"), ct);
        Assert.True(await bed.PumpUntilAsync(async () => (await bed.OfTypeAsync(MessageTypes.Completed)).Count == 1));

        // The same workflow: context is read per correlation.
        var first = Assert.Single(await bed.OfTypeAsync(MessageTypes.Completed));
        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(dev), """{"instruction":"second"}""", "console", first.Seq), ct);
        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(dev) == 2));

        var second = bed.Agent.Invocations.Last(i => i.Container == dev);
        Assert.Contains("output-sentinel", second.Context, StringComparison.Ordinal);
        Assert.DoesNotContain("progress-sentinel", second.Context, StringComparison.Ordinal);
    }
}

/// <summary>
/// `container.progress` STAYS ON THE LOG AND OUT OF THE LEDGER; `container.blocked` STAYS IN IT. A
/// chatty member would otherwise fill its own context with its status lines, and a member that
/// forgot it was blocked would cheerfully retry what it just abandoned.
/// </summary>
public sealed class LedgerExclusionTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"harness-ledger-{Guid.NewGuid():N}");

    private readonly string _database;

    public LedgerExclusionTests()
    {
        Directory.CreateDirectory(_directory);
        _database = Path.Combine(_directory, "messages.db");
        new SchemaMigrator(_database).ApplyAsync(SchemaModules.All).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Progress_stays_on_the_log_and_blocked_stays_in_the_ledger()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new SqliteMessageStore(_database);
        var dev = new ContainerId("Alpha", "Dev");

        await store.AppendAsync(new NewMessage(MessageTypes.Progress, """{"status":"working"}""", dev.ToString()), ct);
        await store.AppendAsync(new NewMessage(MessageTypes.Blocked, """{"reason":"stuck"}""", dev.ToString()), ct);

        var ledger = await new SqliteLedger(_database).ReadAsync(dev, 0, null, long.MaxValue, 100, ct);
        Assert.DoesNotContain(ledger, m => m.Type == MessageTypes.Progress);
        Assert.Contains(ledger, m => m.Type == MessageTypes.Blocked);

        // On the log all the same: the board and the feed read it there.
        Assert.Single(await store.ReadAfterAsync(0, [MessageTypes.Progress], 10, ct));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}

/// <summary>
/// A ROW WITH NO TEAM WAKES NOBODY. A container is woken only by its own team, and a row whose team
/// cannot be read fails closed rather than reaching every team's subscriber.
/// </summary>
public sealed class NullTeamTests
{
    [Fact]
    public async Task A_row_whose_team_cannot_be_read_wakes_nobody()
    {
        await using var bed = new ContainerTestBed();
        var manager = new ContainerId("Alpha", "Manager");
        await bed.AddAsync(manager, [MessageTypes.Completed]);
        var ct = TestContext.Current.CancellationToken;

        // An unqualified source: no team.
        await bed.Store.AppendAsync(new NewMessage(MessageTypes.Completed, """{"output":"x"}""", "DeveloperRowan"), ct);
        await bed.SettleAsync();
        Assert.Equal(0, bed.Agent.RunsFor(manager));

        // The control: the same row from inside the team does wake it.
        await bed.Store.AppendAsync(new NewMessage(MessageTypes.Completed, """{"output":"x"}""", "Alpha/DeveloperRowan"), ct);
        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(manager) == 1));
    }
}

/// <summary>
/// THE COMPLETION PAYLOAD FIELD IS NAMED `output`. MessageText and the browser's card feed both
/// read it; renaming it blanks the agent-facing text and the feed together.
/// </summary>
public sealed class CompletionPayloadTests
{
    [Fact]
    public async Task A_finished_run_publishes_its_output_under_output()
    {
        await using var bed = new ContainerTestBed();
        var dev = new ContainerId("Alpha", "Dev");
        await bed.AddAsync(dev);
        bed.Agent.Behaviour = _ => Task.FromResult(new AgentResult(0, "hello"));

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(dev), """{"instruction":"go"}""", "console"), TestContext.Current.CancellationToken);
        Assert.True(await bed.PumpUntilAsync(async () => (await bed.OfTypeAsync(MessageTypes.Completed)).Count == 1));

        var completed = Assert.Single(await bed.OfTypeAsync(MessageTypes.Completed));
        using var payload = JsonDocument.Parse(completed.Payload);
        Assert.Equal("hello", payload.RootElement.GetProperty("output").GetString());
        Assert.Equal("output", PayloadFields.Output);
    }
}

/// <summary>
/// THE CONTAINER SUBSCRIBES, NOT THE AGENT. The agent exits at the end of every run; the container
/// still holds the subscription, so the next instruction wakes it again.
/// </summary>
public sealed class ContainerSubscriptionTests
{
    [Fact]
    public async Task A_container_whose_agent_has_exited_is_woken_by_the_next_instruction()
    {
        await using var bed = new ContainerTestBed();
        var dev = new ContainerId("Alpha", "Dev");
        await bed.AddAsync(dev);
        var ct = TestContext.Current.CancellationToken;

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(dev), """{"instruction":"first"}""", "console"), ct);
        Assert.True(await bed.PumpUntilAsync(async () => (await bed.OfTypeAsync(MessageTypes.Completed)).Count == 1));

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(dev), """{"instruction":"second"}""", "console"), ct);
        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(dev) == 2));
    }
}
