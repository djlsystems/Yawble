using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// The quiet-team sweep's view of which workflows have ended is read from the log once and then only
/// from the rows written since: a sweep that runs every few minutes for days does not read every
/// ending row of the log's whole history each time. A purge or a reset that may have removed an ending
/// row starts the view over.
/// </summary>
public sealed class WorkflowEndingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"harness-endings-{Guid.NewGuid():N}");
    private readonly SqliteMessageStore _store;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public WorkflowEndingsTests()
    {
        Directory.CreateDirectory(_directory);
        var database = Path.Combine(_directory, "messages.db");
        new SchemaMigrator(database).ApplyAsync(SchemaModules.All).GetAwaiter().GetResult();
        _store = new SqliteMessageStore(database);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_second_read_takes_only_the_rows_written_since_and_a_reset_starts_over()
    {
        var first = await WorkflowAsync("Alpha");
        await _store.AppendAsync(new NewMessage(MessageTypes.WorkflowCompleted, "{}", "Alpha/Manager", first), Ct);

        var log = CountingProxy.Over<IMessageLog>(_store);
        var endings = new WorkflowEndings(log);

        var state = await endings.ReadAsync(Ct);
        Assert.Equal([first], state["Alpha"].EndedWorkflowCorrelations);
        Assert.Equal([first], state["Alpha"].DeclaredWorkflowCorrelations);
        var reads = CountingProxy.Of(log).CallsTo("ReadAfterAsync");
        Assert.True(reads >= 1);

        // NOTHING NEW: one look past the last row, and the same answer.
        state = await endings.ReadAsync(Ct);
        Assert.Equal(reads + 1, CountingProxy.Of(log).CallsTo("ReadAfterAsync"));
        Assert.Equal([first], state["Alpha"].EndedWorkflowCorrelations);

        // A WORKFLOW CLOSED SINCE is ended and not declared, and is read without the first one again.
        var second = await WorkflowAsync("Alpha");
        await _store.AppendAsync(new NewMessage(MessageTypes.WorkflowClosed, """{"team":"Alpha","reason":"done"}""", "person-1", second), Ct);

        state = await endings.ReadAsync(Ct);
        Assert.Equal(new HashSet<long> { first, second }, state["Alpha"].EndedWorkflowCorrelations);
        Assert.Equal([first], state["Alpha"].DeclaredWorkflowCorrelations);

        // A RESET STARTS OVER, so an ending row a purge removed does not stay ended here.
        var before = CountingProxy.Of(log).CallsTo("ReadAfterAsync");
        endings.Invalidate();
        state = await endings.ReadAsync(Ct);
        Assert.True(CountingProxy.Of(log).CallsTo("ReadAfterAsync") > before);
        Assert.Equal(new HashSet<long> { first, second }, state["Alpha"].EndedWorkflowCorrelations);
    }

    private async Task<long> WorkflowAsync(string team) =>
        (await _store.AppendAsync(new NewMessage(MessageTypes.InstructionFor(new ContainerId(team, "Manager")), """{"text":"go"}""", "person-1"), Ct)).Seq;
}
