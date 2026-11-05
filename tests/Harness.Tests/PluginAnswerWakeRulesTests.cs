using System.Text.Json;
using Harness.Contracts;

namespace Harness.Tests;

/// <summary>
/// WHO A PLUGIN'S ANSWER WAKES, at the pump. A plugin member's `completed` or `failed` row closing a
/// `tell` from a member of its own team names that member (<see cref="PayloadFields.AnswersTell"/>);
/// the pump wakes it, though it holds neither type, and passes over the Manager, which did not ask -
/// except on a `needs-person` failure, which still reaches the Manager. An agent member's row, and a
/// plugin's row answering a person, carry no such key and wake as they always did.
/// </summary>
public sealed class PluginAnswerWakeRulesTests
{
    private static readonly ContainerId Manager = new("Team", "Manager");
    private static readonly ContainerId Dev = new("Team", "Dev");
    private static readonly ContainerId Plugin = new("Team", "Echo");
    private static readonly ContainerId Agent = new("Team", "Tester");

    private static readonly string[] ManagerTypes = [MessageTypes.Completed, MessageTypes.Failed, MessageTypes.Handback];

    /// <summary>A plugin's runner, as the pump sees one: its result says it answers its asker, as
    /// every `PluginMemberRunner` result does.</summary>
    private sealed class ScriptedPlugin(MemberResult result) : IMemberRunner
    {
        public Task<MemberResult> RunAsync(MemberInvocation invocation, CancellationToken ct = default) =>
            Task.FromResult(result with { AnswersAsker = true });
    }

    private static async Task<ContainerTestBed> BedAsync(MemberResult pluginResult)
    {
        var bed = new ContainerTestBed();
        await bed.AddAsync(Manager, ManagerTypes);
        await bed.AddAsync(Dev);
        await bed.AddAsync(Agent);
        await bed.Host.AddAsync(ContainerTestBed.Definition(Plugin) with { Agent = MemberRef.ForPlugin("fake") }, new ScriptedPlugin(pluginResult));
        return bed;
    }

    /// <summary>Tells <paramref name="to"/> as <paramref name="from"/>, then pumps until its run's
    /// terminal row is written and nothing further happens. Returns that row.</summary>
    private static async Task<Message> TellAndSettleAsync(ContainerTestBed bed, ContainerId to, string from)
    {
        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(to), JsonSerializer.Serialize(new { instruction = "check it" }), from, null));

        Message? terminal = null;
        Assert.True(await bed.PumpUntilAsync(async () =>
        {
            terminal = (await bed.OfTypeAsync(MessageTypes.Completed)).Concat(await bed.OfTypeAsync(MessageTypes.Failed))
                .FirstOrDefault(m => m.Source == to.ToString());
            return terminal is not null;
        }));
        await bed.SettleAsync();
        return terminal!;
    }

    /// <summary>How many runs of <paramref name="who"/> <paramref name="row"/> woke. A Manager is still
    /// woken by the asker's OWN run ending, as before; that is another row.</summary>
    private static async Task<int> WokenByAsync(ContainerTestBed bed, ContainerId who, Message row) =>
        (await bed.OfTypeAsync(MessageTypes.Completed)).Concat(await bed.OfTypeAsync(MessageTypes.Failed))
            .Count(m => m.Source == who.ToString() && m.CausationSeq == row.Seq);

    private static string? AnswersTell(Message row) =>
        JsonDocument.Parse(row.Payload).RootElement.TryGetProperty(PayloadFields.AnswersTell, out var value) ? value.GetString() : null;

    [Fact]
    public async Task A_needs_person_failure_answering_a_member_wakes_the_member_and_still_reaches_the_manager()
    {
        await using var bed = await BedAsync(MemberResult.NotRun("Set the token first.", FailureClasses.NeedsPerson));

        var row = await TellAndSettleAsync(bed, Plugin, Dev.ToString());

        Assert.Equal(MessageTypes.Failed, row.Type);
        Assert.Equal(Dev.ToString(), AnswersTell(row));
        Assert.Equal(1, await WokenByAsync(bed, Dev, row));
        Assert.Equal(1, await WokenByAsync(bed, Manager, row));
    }

    [Fact]
    public async Task An_ordinary_failure_answering_a_member_wakes_the_member_and_not_the_manager()
    {
        await using var bed = await BedAsync(new MemberResult(false, 1, "", FailureReason: "asked to fail"));

        var row = await TellAndSettleAsync(bed, Plugin, Dev.ToString());

        Assert.Equal(MessageTypes.Failed, row.Type);
        Assert.Equal(1, await WokenByAsync(bed, Dev, row));
        Assert.Equal(0, await WokenByAsync(bed, Manager, row));
    }

    [Fact]
    public async Task A_completion_answering_a_member_wakes_the_member_and_not_the_manager()
    {
        await using var bed = await BedAsync(new MemberResult(true, 0, "ok"));

        var row = await TellAndSettleAsync(bed, Plugin, Dev.ToString());

        Assert.Equal(MessageTypes.Completed, row.Type);
        Assert.Equal(1, await WokenByAsync(bed, Dev, row));
        Assert.Equal(0, await WokenByAsync(bed, Manager, row));
    }

    [Fact]
    public async Task A_plugin_answering_a_person_names_nobody_and_wakes_the_manager_as_before()
    {
        await using var bed = await BedAsync(new MemberResult(true, 0, "ok"));

        var row = await TellAndSettleAsync(bed, Plugin, "console");

        Assert.Null(AnswersTell(row));
        Assert.Equal(0, bed.Agent.RunsFor(Dev));
        Assert.Equal(1, await WokenByAsync(bed, Manager, row));
    }

    [Fact]
    public async Task An_agent_members_run_for_another_member_names_nobody_and_wakes_only_the_manager()
    {
        await using var bed = await BedAsync(new MemberResult(true, 0, "ok"));

        var row = await TellAndSettleAsync(bed, Agent, Dev.ToString());

        Assert.Null(AnswersTell(row));
        Assert.Equal(1, bed.Agent.RunsFor(Agent));
        Assert.Equal(0, bed.Agent.RunsFor(Dev));
        Assert.Equal(1, await WokenByAsync(bed, Manager, row));
    }
}
