using Yawble.Core.Bus;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>The person's hold on a workflow, and the app keeping workflows from being forgotten (Yawble's workflow control).</summary>
public class WorkflowControlTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(TempDb Db, BusEngine Engine)> Team(ScriptedRunner runner, BusOptions? options = null)
    {
        var db = new TempDb();
        var engine = new BusEngine(db.OpenStore(), runner, options ?? new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(200) });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        return (db, engine);
    }

    [Fact]
    public async Task AnOwnerThatEndsItsRunWithTheWorkflowOpenAndNobodyWorkingIt_IsToldOnce()
    {
        var runner = new ScriptedRunner();
        var (db, engine) = await Team(runner);
        using (db)
        await using (engine)
        {
            await engine.StartAsync(Ct);
            var root = await engine.PersonTellAsync("Alpha", "Ada", "look into the flaky test", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");

            // Ada ran and ended without declaring, which woke her Manager; the Manager ended too, with nobody else working
            // it: the Manager (who may declare on her behalf) was told once, ran again, ended again: not told a second time.
            Assert.Equal(["Alpha/Ada", "Alpha/Manager", "Alpha/Manager"], runner.AdmittedNames());
            using var store = db.OpenStore();
            var offers = store.Workflow(root.Seq).Where(m => m.Payload.Contains(BusEngine.IdleOfferMarker)).ToArray();
            var offer = Assert.Single(offers);
            Assert.Equal(MessageTypes.HostSource, offer.Source);
            Assert.Contains("nobody is working it", Payload.Field(offer, "text"));
        }
    }

    [Fact]
    public async Task TheIdleOffer_GetsAFreshChanceAfterThePersonSaysSomethingInTheWorkflow()
    {
        // The Manager keeps ending its runs without declaring or asking. One offer per word from the person: the
        // workflow the person picks up again cannot stall silently because the first offer was already spent.
        var runner = new ScriptedRunner();
        var (db, engine) = await Team(runner);
        using (db)
        await using (engine)
        {
            await engine.StartAsync(Ct);
            var root = await engine.PersonTellAsync("Alpha", "Manager", "deploy it", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            int Offers() { using var s = db.OpenStore(); return s.Workflow(root.Seq).Count(m => m.Payload.Contains(BusEngine.IdleOfferMarker)); }
            Assert.Equal(1, Offers());

            await engine.PersonTellAsync("Alpha", "Manager", "carry on with step 2", root.Seq, Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet again");
            Assert.Equal(2, Offers());
        }
    }

    [Fact]
    public async Task NoIdleOffer_WhileTheWorkflowWaitsOnAnUnansweredQuestion()
    {
        // The Manager asks the person and ends its run: the workflow waits on the person, so it is not told "nobody is
        // working it" (which only made it ask the same question again as "blocked").
        var runner = new ScriptedRunner().On("Alpha/Manager", async (run, ct) =>
        {
            await run.NeedsDecisionAsync("Approve this plan?", ct);
            return RunResult.Ok;
        });
        var (db, engine) = await Team(runner);
        using (db)
        await using (engine)
        {
            await engine.StartAsync(Ct);
            var root = await engine.PersonTellAsync("Alpha", "Manager", "make a plan", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            Assert.Equal(["Alpha/Manager"], runner.AdmittedNames());
            using var store = db.OpenStore();
            Assert.DoesNotContain(store.Workflow(root.Seq), m => m.Payload.Contains(BusEngine.IdleOfferMarker));
        }
    }

    [Fact]
    public async Task AManagerWokenAgainInTheWorkflow_IsToldTheQuestionItAlreadyAsked()
    {
        // The Manager tells Ada and asks the person; Ada's hand-back wakes it again before the person answers. That run
        // must know the question is open, so it does not ask it a second time.
        var managerRuns = 0;
        IReadOnlyList<(long Seq, string Question)>? secondRunSaw = null;
        var runner = new ScriptedRunner()
            .On("Alpha/Manager", async (run, ct) =>
            {
                if (++managerRuns == 1)
                {
                    await run.TellAsync("Ada", "do part one", ct);
                    await run.NeedsDecisionAsync("How should the build reach the private repo?\n1. token 2. public", ct);
                }
                else secondRunSaw ??= run.OpenQuestions;
                return RunResult.Ok;
            })
            .On("Alpha/Ada", async (run, ct) => { await run.HandbackAsync("part one done", ct); return RunResult.Ok; });
        var (db, engine) = await Team(runner);
        using (db)
        await using (engine)
        {
            await engine.StartAsync(Ct);
            await engine.PersonTellAsync("Alpha", "Manager", "plan the deploy", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            Assert.True(managerRuns >= 2, $"manager runs: {managerRuns}");
            var open = Assert.Single(secondRunSaw!);
            Assert.StartsWith("How should the build reach the private repo?", open.Question);
            var lines = string.Join('\n', Yawble.Core.Teams.TeamPrompt.OpenQuestionLines(secondRunSaw!));
            Assert.Contains("Do NOT ask it again", lines);
            Assert.Contains($"- question {open.Seq}: How should the build reach the private repo?", lines);
        }
    }

    [Fact]
    public async Task AnsweringAQuestion_AlsoSettlesTheSameQuestionAskedEarlier()
    {
        var runs = 0;
        var runner = new ScriptedRunner().On("Alpha/Manager", async (run, ct) =>
        {
            if (++runs > 1) return RunResult.Ok; // asks in its first run only
            await run.NeedsDecisionAsync("How should the build reach the private repo?", ct);
            await run.NeedsDecisionAsync("How should the image build get the private repo?", ct); // asked twice
            return RunResult.Ok;
        });
        var (db, engine) = await Team(runner);
        using (db)
        await using (engine)
        {
            await engine.StartAsync(Ct);
            await engine.PersonTellAsync("Alpha", "Manager", "plan the deploy", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            var open = await engine.OpenDecisionsAsync("Alpha", Ct);
            Assert.Equal(2, open.Count);

            // The person answers the later one: both are settled.
            await engine.PersonTellAsync("Alpha", "Manager", "Use the registry", open[1].Seq, Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet again");
            Assert.Empty(await engine.OpenDecisionsAsync("Alpha", Ct));
        }
    }

    [Fact]
    public async Task StandingApprovals_AreGivenToTheManagersRuns_AndListedInItsPrompt()
    {
        IReadOnlyList<string>? seen = null;
        IReadOnlyList<string>? adas = null;
        var runner = new ScriptedRunner()
            .On("Alpha/Manager", async (run, ct) => { seen = run.Approvals; await run.WorkflowCompleteAsync("ok", ct); return RunResult.Ok; })
            .On("Alpha/Ada", async (run, ct) => { adas = run.Approvals; await run.WorkflowCompleteAsync("ok", ct); return RunResult.Ok; });
        var (db, engine) = await Team(runner);
        using (db)
        await using (engine)
        {
            await engine.StartAsync(Ct);
            var added = await engine.AddTeamApprovalAsync("Alpha", "hiring members for an approved plan", "person", Ct);
            await Assert.ThrowsAsync<BusRefusal>(() => engine.AddTeamApprovalAsync("Alpha", "  ", "person", Ct));
            await engine.PersonTellAsync("Alpha", "Manager", "plan it", ct: Ct);
            await engine.PersonTellAsync("Alpha", "Ada", "do it", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            Assert.Equal(["hiring members for an approved plan"], seen);
            Assert.Empty(adas!); // members are not told: only the Manager decides what to ask
            Assert.Contains("- hiring members for an approved plan", string.Join('\n', Yawble.Core.Teams.TeamPrompt.ApprovalLines(seen!)));

            await engine.RemoveTeamApprovalAsync("Alpha", added.Id, Ct);
            Assert.Empty(await engine.TeamApprovalsAsync("Alpha", Ct));
        }
    }

    [Fact]
    public async Task NoIdleOffer_WhenTheOwnerDeclaresTheWorkflow_OrTheOptionIsOff()
    {
        var runner = new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
        {
            await run.WorkflowCompleteAsync("fixed it", ct);
            return RunResult.Ok;
        });
        var (db, engine) = await Team(runner);
        using (db)
        await using (engine)
        {
            await engine.StartAsync(Ct);
            await engine.PersonTellAsync("Alpha", "Ada", "fix it", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            Assert.Equal(["Alpha/Ada", "Alpha/Manager"], runner.AdmittedNames()); // her end wakes the Manager, as always
        }

        var quiet = new ScriptedRunner();
        var (db2, engine2) = await Team(quiet, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(200), IdleOffer = false });
        using (db2)
        await using (engine2)
        {
            await engine2.StartAsync(Ct);
            await engine2.PersonTellAsync("Alpha", "Ada", "look", ct: Ct);
            await Wait.UntilAsync(() => engine2.IsQuiescentAsync(Ct), "quiet");
            Assert.Equal(["Alpha/Ada", "Alpha/Manager"], quiet.AdmittedNames());
        }
    }

    [Fact]
    public async Task ClosingAWorkflowEndsIt_CancelsItsWakes_AndANudgeReopensItThroughTheManager()
    {
        var runner = new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
        {
            if (run.Items.Count == 1 && run.Items[0].Source == MessageTypes.PersonSource && !run.Items[0].Payload.Contains("nudge"))
                await run.WakeMeAsync(60, "check the build again", ct);
            return RunResult.Ok;
        });
        var (db, engine) = await Team(runner);
        using (db)
        await using (engine)
        {
            await engine.StartAsync(Ct);
            var root = await engine.PersonTellAsync("Alpha", "Ada", "watch the build", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            using (var store = db.OpenStore()) Assert.Equal(1, store.OpenWakes(root.Seq));

            var list = await engine.WorkflowsAsync("Alpha", ct: Ct);
            var w = Assert.Single(list);
            Assert.Equal(("open", "Ada", "watch the build"), (w.State, w.Owner, w.Title));
            Assert.False(w.Undeclared); // a wake is set: somebody will look again

            await engine.CloseWorkflowAsync("Alpha", root.Seq, "not needed any more", Ct);
            using (var store = db.OpenStore())
            {
                Assert.True(store.WorkflowEnded(root.Seq));
                Assert.Equal(0, store.OpenWakes(root.Seq));
            }
            var closed = Assert.Single(await engine.WorkflowsAsync("Alpha", ct: Ct));
            Assert.Equal(("closed", "not needed any more"), (closed.State, closed.Ending));
            var again = await Assert.ThrowsAsync<BusRefusal>(() => engine.CloseWorkflowAsync("Alpha", root.Seq, null, Ct));
            Assert.Equal("already_ended", again.Code);

            var nudge = await engine.NudgeWorkflowAsync("Alpha", root.Seq, "the release needs it after all", Ct);
            Assert.Equal(MessageTypes.InstructionPrefix + "Alpha/Manager", nudge.Type);
            Assert.Equal(root.Seq, nudge.CorrelationId);
            Assert.Contains("the release needs it after all", Payload.Field(nudge, "text"));
            using (var store = db.OpenStore()) Assert.False(store.WorkflowEnded(root.Seq)); // reopened
        }
    }

    [Fact]
    public async Task AWorkflowIsOnlyItsTeamsToClose_AndWorkflowShowOnlyShowsTheCallersTeam()
    {
        IReadOnlyList<Message>? seenOwn = null, seenOther = null;
        long otherRoot = 0;
        var runner = new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
        {
            seenOwn = await run.WorkflowShowAsync(run.Correlation, ct);
            seenOther = await run.WorkflowShowAsync(otherRoot, ct);
            await run.WorkflowCompleteAsync("done", ct);
            return RunResult.Ok;
        });
        var (db, engine) = await Team(runner);
        using (db)
        await using (engine)
        {
            await engine.CreateTeamAsync("Beta", ct: Ct);
            await engine.AddMemberAsync("Beta", "Bo", ct: Ct);
            otherRoot = (await engine.PersonTellAsync("Beta", "Bo", "secret plans", ct: Ct)).Seq;
            var refused = await Assert.ThrowsAsync<BusRefusal>(() => engine.CloseWorkflowAsync("Alpha", otherRoot, null, Ct));
            Assert.Equal("no_such_workflow", refused.Code);

            await engine.StartAsync(Ct);
            var root = await engine.PersonTellAsync("Alpha", "Ada", "tidy up", ct: Ct);
            await Wait.UntilAsync(() => seenOwn is not null && seenOther is not null, "Ada's run");
            Assert.Contains(seenOwn!, m => m.Seq == root.Seq);
            Assert.Empty(seenOther!);
        }
    }

    [Fact]
    public async Task StoppingAWorkflowStopsItsRuns()
    {
        var release = new TaskCompletionSource<RunResult>();
        var runner = new ScriptedRunner().On("Alpha/Ada", async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return RunResult.Ok;
        });
        var (db, engine) = await Team(runner);
        using (db)
        await using (engine)
        {
            await engine.StartAsync(Ct);
            var root = await engine.PersonTellAsync("Alpha", "Ada", "long job", ct: Ct);
            await Wait.UntilAsync(() => runner.Admitted.Count == 1, "Ada running");
            Assert.Equal(1, await engine.StopWorkflowAsync("Alpha", root.Seq, Ct));
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            using var store = db.OpenStore();
            var failed = Assert.Single(store.Workflow(root.Seq), m => m.Type == MessageTypes.Failed);
            Assert.Equal(FailureClasses.Stopped, Payload.Field(failed, "failureClass"));
        }
    }

    [Fact]
    public async Task AManagerWhoseRunTheStopCutOff_IsWokenOnceAtTheNextStart()
    {
        var db = new TempDb();
        using (db)
        {
            long root;
            var blocking = new ScriptedRunner().On("Alpha/Manager", async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return RunResult.Ok;
            });
            await using (var first = new BusEngine(db.OpenStore(), blocking, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(200) }))
            {
                await first.CreateTeamAsync("Alpha", ct: Ct);
                await first.StartAsync(Ct);
                root = (await first.PersonTellAsync("Alpha", "Manager", "plan the release", ct: Ct)).Seq;
                await Wait.UntilAsync(() => blocking.Admitted.Count == 1, "the Manager running");
            } // the app stops mid-run: the run is left open, as a process exit leaves it

            var after = new ScriptedRunner();
            await using var second = new BusEngine(db.OpenStore(), after, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(200), IdleOffer = false });
            await second.StartAsync(Ct);
            await Wait.UntilAsync(() => second.IsQuiescentAsync(Ct), "quiet after the restart");

            using var store = db.OpenStore();
            var wake = Assert.Single(store.Workflow(root), m => m.Payload.Contains(BusEngine.RestartWakeMarker));
            Assert.Equal(MessageTypes.InstructionPrefix + "Alpha/Manager", wake.Type);
            Assert.Contains("cut off", Payload.Field(wake, "text"));
            Assert.Equal(["Alpha/Manager"], after.AdmittedNames());
        }
    }
}
