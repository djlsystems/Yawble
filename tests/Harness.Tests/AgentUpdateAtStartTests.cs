using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// THE START'S CLI UPDATE GOES THROUGH CONTROL'S ONE UPDATE GATE. A worker's start installs only
/// missing CLIs; the update a start used to make is asked of the gate when the first worker joins after
/// control started, once per launched command, as a person's request from the Agents screen is.
/// <c>HARNESS_UPDATE_AGENTS=0</c> turns it off; a Host that runs its runs itself, or a control outside
/// the control image (whose workers still update at their start), asks nothing here.
/// </summary>
public sealed class AgentUpdateAtStartTests
{
    private static readonly AgentCatalog Catalog = new(
    [
        new AgentDefinition("claude", AgentMode.Headless, new AgentLaunch("claude", ["-p"])),
        new AgentDefinition("claude-interactive", AgentMode.Interactive, new AgentLaunch("claude", [])),
        new AgentDefinition("codex", AgentMode.Headless, new AgentLaunch("codex", ["exec"])),
    ]);

    [Fact]
    public void The_first_worker_to_join_after_start_asks_the_gate_once_per_installed_cli()
    {
        var (join, asked) = Wire(control: true, updateAgents: null, out var wired);

        Assert.True(wired);
        Assert.Empty(asked);
        join(new WorkerId("w1"));

        Assert.Equal(["claude", "codex"], asked);
    }

    [Fact]
    public void The_first_worker_to_join_after_start_asks_nothing_with_agent_updates_off()
    {
        var (join, asked) = Wire(control: true, updateAgents: "0", out var wired);

        Assert.False(wired);
        Assert.Null(join);
        Assert.Empty(asked);
    }

    [Fact]
    public void The_first_worker_to_join_after_start_a_second_worker_joining_asks_nothing_more()
    {
        var (join, asked) = Wire(control: true, updateAgents: "1", out _);

        join(new WorkerId("w1"));
        join(new WorkerId("w2"));
        join(new WorkerId("w1"));

        Assert.Equal(["claude", "codex"], asked);
    }

    [Fact]
    public void A_host_that_runs_its_runs_itself_asks_nothing_at_a_join()
    {
        var (join, asked) = Wire(control: false, updateAgents: null, out var wired);

        Assert.False(wired);
        Assert.Null(join);
        Assert.Empty(asked);
    }

    [Fact]
    public void A_control_outside_the_control_image_asks_nothing_at_a_join()
    {
        foreach (var image in new[] { null, "", "worker" })
        {
            var (join, asked) = Wire(control: true, updateAgents: null, out var wired, image);

            Assert.False(wired);
            Assert.Null(join);
            Assert.Empty(asked);
        }
    }

    [Fact]
    public void A_request_that_throws_does_not_stop_the_others()
    {
        var asked = new List<string>();
        Action<WorkerId>? join = null;
        AgentUpdatesAtStart.Wire(true, "control", null, handler => join = handler, () => AgentUpdatesAtStart.OnePerCommand(Catalog), agent =>
        {
            asked.Add(agent);
            if (agent == "claude") throw new InvalidOperationException("no worker");
        });

        join!(new WorkerId("w1"));

        Assert.Equal(["claude", "codex"], asked);
    }

    private static (Action<WorkerId> Join, List<string> Asked) Wire(bool control, string? updateAgents, out bool wired, string? image = "control")
    {
        var asked = new List<string>();
        Action<WorkerId>? join = null;
        wired = AgentUpdatesAtStart.Wire(
            control, image, updateAgents, handler => join = handler, () => AgentUpdatesAtStart.OnePerCommand(Catalog), asked.Add);
        return (join!, asked);
    }
}
