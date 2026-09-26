using Harness.Contracts;
using Harness.Host;
using Harness.Kanban;

namespace Harness.Tests;

/// <summary>
/// A workflow may not be declared complete over an unfinished card. Declaring moves every card of
/// the workflow to Done, so without this check an interrupted or never-started card reads as
/// delivered.
/// </summary>
public sealed class WorkflowLooseEndsTests
{
    [Fact]
    public async Task A_card_whose_member_has_not_handed_back_is_a_loose_end_until_it_does()
    {
        await using var bed = new ContainerTestBed();
        var ct = TestContext.Current.CancellationToken;
        var worker = new ContainerId("Alpha", "DeveloperRowan");

        var root = await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(new ContainerId("Alpha", "Manager")),
            """{"instruction":"build it"}""", "console"), ct);
        var told = await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(worker), """{"instruction":"write hello.py"}""",
            "Alpha/Manager", root.Seq), ct);

        var before = await WorkflowLooseEnds.DescribeAsync(
            new KanbanStore(bed.Store), "Alpha", root.CorrelationId, "Manager");

        Assert.Single(before);
        Assert.Contains("DeveloperRowan", before[0], StringComparison.Ordinal);

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.Handback, """{"delivered":"hello.py at abc123"}""",
            worker.ToString(), told.Seq), ct);

        var after = await WorkflowLooseEnds.DescribeAsync(
            new KanbanStore(bed.Store), "Alpha", root.CorrelationId, "Manager");

        Assert.Empty(after);
    }

    [Fact]
    public async Task An_interrupted_run_is_a_loose_end()
    {
        await using var bed = new ContainerTestBed();
        var ct = TestContext.Current.CancellationToken;
        var worker = new ContainerId("Alpha", "DeveloperRowan");

        var root = await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(new ContainerId("Alpha", "Manager")),
            """{"instruction":"build it"}""", "console"), ct);
        var told = await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(worker), """{"instruction":"write hello.py"}""",
            "Alpha/Manager", root.Seq), ct);
        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.Failed,
            """{"exitCode":null,"output":"Interrupted by a host restart; this run did not complete.","failureClass":"interrupted"}""",
            worker.ToString(), told.Seq), ct);

        var looseEnds = await WorkflowLooseEnds.DescribeAsync(
            new KanbanStore(bed.Store), "Alpha", root.CorrelationId, "Manager");

        Assert.Single(looseEnds);
        Assert.Contains("failed", looseEnds[0], StringComparison.Ordinal);
    }
}
