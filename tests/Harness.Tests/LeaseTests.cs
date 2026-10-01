using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Tests.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// THE HEAVY LEASE, ACROSS EVERY TEAM: at most <c>leases.heavy.holders</c> hold it, the rest queue
/// in order naming who holds it, a queued run's silence clock is paused, and a run's end releases
/// whatever it held however it ended. An unknown name is refused with a sentence.
/// </summary>
public sealed class LeaseTests
{
    private static readonly ContainerId Dev = new("alpha", "DeveloperRowan");
    private static readonly LeaseOwner Alpha = LeaseOwner.For(new ContainerId("alpha", "Developer"));
    private static readonly LeaseOwner Beta = LeaseOwner.For(new ContainerId("beta", "Tester"));
    private static readonly LeaseOwner Gamma = LeaseOwner.For(new ContainerId("gamma", "Manager"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void It_grants_N_holders_and_queues_the_rest_with_position_and_holder()
    {
        var holders = 2;
        var leases = new InstanceLeases(() => holders);

        Assert.Equal(LeaseOutcome.Granted, leases.Acquire("heavy", Alpha).Outcome);
        Assert.Equal(LeaseOutcome.Granted, leases.Acquire("heavy", Beta).Outcome);

        var queued = leases.Acquire("heavy", Gamma);
        Assert.Equal(LeaseOutcome.Queued, queued.Outcome);
        Assert.Equal(1, queued.Position);
        Assert.True(queued.NewlyQueued);
        Assert.Equal(["alpha/Developer", "beta/Tester"], queued.HeldBy!.Select(h => $"{h.Team}/{h.Member}"));
        Assert.StartsWith("Queued, position 1", queued.Sentence, StringComparison.Ordinal);
        Assert.Contains("Developer on team alpha", queued.Sentence, StringComparison.Ordinal);
        Assert.Contains("Tester on team beta", queued.Sentence, StringComparison.Ordinal);

        // Calling again is how it waits: still position 1, and not announced a second time.
        var again = leases.Acquire("heavy", Gamma);
        Assert.Equal(1, again.Position);
        Assert.False(again.NewlyQueued);

        var state = Assert.Single(leases.Leases());
        Assert.Equal(2, state.Limit);
        Assert.Equal(2, state.Holders.Count);
        Assert.Equal("gamma", Assert.Single(state.Queue).Team);

        // A release hands the head of the queue the lease.
        var released = leases.Release("heavy", Alpha);
        Assert.Equal(LeaseOutcome.Released, released.Outcome);
        Assert.Equal([Gamma], released.Promoted!);
        Assert.Equal(LeaseOutcome.Granted, leases.Acquire("heavy", Gamma).Outcome);
    }

    [Fact]
    public void The_default_is_one_holder_and_the_setting_is_read_on_every_call()
    {
        var holders = 1;
        var leases = new InstanceLeases(() => holders);

        Assert.Equal(LeaseOutcome.Granted, leases.Acquire("heavy", Alpha).Outcome);
        Assert.Equal(LeaseOutcome.Queued, leases.Acquire("heavy", Beta).Outcome);
        Assert.Equal(2, leases.Acquire("heavy", Gamma).Position);

        holders = 2;
        Assert.Equal(LeaseOutcome.Granted, leases.Acquire("heavy", Beta).Outcome);
        Assert.Equal(1, leases.Acquire("heavy", Gamma).Position);
    }

    [Fact]
    public void The_default_holder_count_setting_is_one()
    {
        var definition = Assert.Single(
            new TenantSettings(null!, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), 4, 0)
                .Definitions,
            d => d.Name == TenantSettings.LeasesHeavyHoldersName);
        Assert.Equal("1", definition.BuiltInDefault);
        Assert.Equal(1, definition.Min);
    }

    [Fact]
    public void An_unknown_name_is_refused_with_a_sentence_and_nothing_is_taken()
    {
        var leases = new InstanceLeases(() => 1);

        var refused = leases.Acquire("gpu", Alpha);

        Assert.Equal(LeaseOutcome.Unknown, refused.Outcome);
        Assert.Equal(
            "There is no lease named 'gpu'. The only lease is `heavy`: take it before running anything "
            + "you know to be heavy, such as the repository's full test command or a full build.",
            refused.Sentence);
        Assert.Equal(LeaseOutcome.Unknown, leases.Release("gpu", Alpha).Outcome);
        Assert.Empty(Assert.Single(leases.Leases()).Holders);
    }

    /// <summary>
    /// The queued run's clock is PAUSED: a one-second silence window passes twice over while it
    /// waits, and its card reads "waiting for a heavy-work slot". Handed the lease, its clock runs
    /// again and silence stops it as before.
    /// </summary>
    [Fact]
    public async Task A_queued_runs_silence_clock_is_paused_and_runs_again_when_it_is_granted()
    {
        var heartbeat = new RunHeartbeat();
        var reports = new RecordedReports();
        var actions = new LeaseActions(new InstanceLeases(() => 1), heartbeat, reports);

        Assert.Equal(LeaseOutcome.Granted, (await actions.AcquireAsync("heavy", Alpha, Ct)).Outcome);

        using var clock = ChildProcess.Clock(heartbeat, Dev, 1, Ct);
        var queued = await actions.AcquireAsync("heavy", LeaseOwner.For(Dev), Ct);
        Assert.Equal(LeaseOutcome.Queued, queued.Outcome);
        Assert.Equal([(Dev, LeaseActions.WaitingWords)], reports.Lines);

        // A progress report while queued does not restart a countdown that is not running.
        heartbeat.Touch(Dev);
        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);
        Assert.False(clock.Expired);

        await actions.ReleaseAsync("heavy", Alpha, Ct);
        Assert.Equal((Dev, LeaseActions.GrantedWords), reports.Lines[^1]);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!clock.Expired && DateTime.UtcNow < deadline) await Task.Delay(50, Ct);
        Assert.True(clock.Expired);
    }

    [Fact]
    public async Task A_clock_left_running_with_no_lease_still_expires()
    {
        // The control for the pause above: the same window with nothing queued stops the run.
        using var clock = ChildProcess.Clock(new RunHeartbeat(), Dev, 1, Ct);
        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);
        Assert.True(clock.Expired);
    }

    public enum RunEnd
    {
        Completed,
        Failed,
        Threw,
        Stopped,
    }

    /// <summary>
    /// NO LEASE OUTLIVES ITS RUN: the run takes the lease, another team's member queues behind it,
    /// and however the run ends the lease is released and the waiter holds it.
    /// </summary>
    [Theory]
    [InlineData(RunEnd.Completed)]
    [InlineData(RunEnd.Failed)]
    [InlineData(RunEnd.Threw)]
    [InlineData(RunEnd.Stopped)]
    public async Task A_lease_is_released_when_its_run_ends_however_it_ends(RunEnd end)
    {
        var heartbeat = new RunHeartbeat();
        var leases = new InstanceLeases(() => 1);
        LeaseActions actions = null!;

        // Wired as Program.cs wires it: the run's end reaches the lease first.
        await using var bed = new ContainerTestBed(onRunEnding: async (member, _, _, ct) =>
        {
            await actions.EndedAsync(LeaseOwner.For(member), ct);
            return false;
        });
        actions = new LeaseActions(leases, heartbeat, new MemberReports(bed.Host, bed.Store, heartbeat));

        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new Scripted(async (_, ct) =>
        {
            Assert.Equal(LeaseOutcome.Granted, (await actions.AcquireAsync("heavy", LeaseOwner.For(Dev), ct)).Outcome);
            holding.TrySetResult();
            if (end != RunEnd.Stopped) await finish.Task.WaitAsync(ct);

            return end switch
            {
                RunEnd.Completed => new AgentResult(0, "done"),
                RunEnd.Failed => new AgentResult(1, "", "the build broke"),
                RunEnd.Threw => throw new InvalidOperationException("the runner fell over"),
                _ => await Forever(ct),
            };
        });
        var member = await bed.Host.AddAsync(ContainerTestBed.Definition(Dev), bed.AsMember(agent), Ct);

        await bed.Store.AppendAsync(
            new NewMessage(MessageTypes.InstructionFor(Dev), """{"instruction":"build it"}""", "console"), Ct);
        Assert.True(await bed.PumpUntilAsync(() => holding.Task.IsCompleted));

        Assert.Equal(LeaseOutcome.Queued, (await actions.AcquireAsync("heavy", Beta, Ct)).Outcome);
        Assert.Equal("alpha", Assert.Single(Assert.Single(leases.Leases()).Holders).Team);
        if (end == RunEnd.Stopped) Assert.True(member.Stop());
        else finish.TrySetResult();

        Assert.True(await bed.PumpUntilAsync(async () =>
            (await bed.Store.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct)).Count == 1));

        var state = Assert.Single(leases.Leases());
        Assert.Equal("beta", Assert.Single(state.Holders).Team);
        Assert.Empty(state.Queue);
    }

    /// <summary>A queued run that ends leaves the queue, so it never holds the head of it.</summary>
    [Fact]
    public void A_run_that_ends_while_queued_leaves_the_queue()
    {
        var leases = new InstanceLeases(() => 1);
        leases.Acquire("heavy", Alpha);
        leases.Acquire("heavy", Beta);
        leases.Acquire("heavy", Gamma);

        var ended = leases.Ended(Beta);

        Assert.Equal([Beta], ended.Withdrawn!);
        Assert.Equal(1, leases.Acquire("heavy", Gamma).Position);
    }

    private static async Task<AgentResult> Forever(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return new AgentResult(0, "");
    }

    private sealed class Scripted(Func<AgentInvocation, CancellationToken, Task<AgentResult>> behaviour) : IAgentRunner
    {
        public Task<AgentResult> RunAsync(AgentInvocation invocation, CancellationToken ct = default) =>
            behaviour(invocation, ct);
    }

    private sealed class RecordedReports : IMemberReports
    {
        public List<(ContainerId, string)> Lines { get; } = [];

        public Task<MemberReportOutcome> ProgressAsync(ContainerId member, string status, CancellationToken ct = default)
        {
            lock (Lines) Lines.Add((member, status));
            return Task.FromResult(MemberReportOutcome.Ok);
        }

        public Task<MemberReportOutcome> BlockedAsync(ContainerId member, string reason, int? item = null, CancellationToken ct = default) =>
            Task.FromResult(MemberReportOutcome.Ok);

        public Task<MemberReportOutcome> DeferAsync(ContainerId member, int item, string reason, CancellationToken ct = default) =>
            Task.FromResult(MemberReportOutcome.Ok);

        public Task<MemberReportOutcome> NeedsDecisionAsync(ContainerId member, string question, CancellationToken ct = default) =>
            Task.FromResult(MemberReportOutcome.Ok);

        public Task<MemberReportOutcome> HandbackAsync(ContainerId member, string delivered, CancellationToken ct = default) =>
            Task.FromResult(MemberReportOutcome.Ok);

        public Task<MemberReportOutcome> PublishAsync(ContainerId member, string type, string payload, CancellationToken ct = default) =>
            Task.FromResult(MemberReportOutcome.Ok);
    }
}

/// <summary>
/// The route the <c>lease</c> tool calls: the owner is the credential's, a person has no lease, a
/// member not in a run is refused one, and an unknown name is refused with its sentence.
/// </summary>
public sealed class LeaseRouteTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_unknown_name_is_refused_with_a_sentence()
    {
        using var member = host.Container(host.AlphaContainerKey);

        var refused = await member.PostAsJsonAsync("/api/me/lease", new { action = "acquire", name = "gpu" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var error = (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString();
        Assert.Equal(InstanceLeases.UnknownName("gpu"), error);
    }

    [Fact]
    public async Task A_member_not_in_a_run_is_refused_a_lease_and_a_person_has_none()
    {
        // The team's Manager: a hosted member, idle between runs.
        var key = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(host.Alpha, TeamRegistry.DefaultManagerName).ToString(),
            PrincipalKind.Container, host.Alpha, Permits.All, ct: Ct);
        using var member = host.Container(key);
        var idle = await member.PostAsJsonAsync("/api/me/lease", new { action = "acquire", name = "heavy" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, idle.StatusCode);
        Assert.Empty(Assert.Single(host.Services.GetRequiredService<ILeaseState>().Leases()).Holders);

        using var person = await host.PersonAsync();
        var refused = await person.PostAsJsonAsync("/api/me/lease", new { action = "acquire", name = "heavy" }, Ct);
        Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
    }
}
