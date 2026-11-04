using Harness.Contracts;

namespace Harness.Tests;

/// <summary>
/// THE CLOSED SET OF INTERRUPTION REASONS and its rules, in one place: the eleven codes, the failure
/// mapping for every <see cref="FailureClasses"/> value, and the store's stamp that keeps a known code,
/// refuses any other and gives a row that names none its type's default.
/// </summary>
public sealed class InterruptionReasonsTests
{
    [Fact]
    public void The_set_is_the_eleven_codes_and_nothing_else()
    {
        Assert.Equal(
            ["token_cap", "workflow_budget", "auth", "permission", "approval_required", "decision_required",
             "paused_by_person", "team_archived", "busy", "admission", "failure"],
            InterruptionReasons.All);

        Assert.Equal(
            [MessageTypes.Blocked, MessageTypes.NeedsDecision, MessageTypes.Rejected, MessageTypes.WorkflowPaused, MessageTypes.ScheduleSkipped],
            InterruptionReasons.CarriedOn);
        Assert.False(InterruptionReasons.IsInterruption(MessageTypes.WorkflowResumed));
        Assert.False(InterruptionReasons.IsInterruption(MessageTypes.Failed));
    }

    /// <summary>Every class this build knows, and the two readings of no class: needs-person waits for a
    /// person to set a key or a connection; every other class is a generic failure.</summary>
    [Fact]
    public void Every_failure_class_maps_to_one_code_from_the_set()
    {
        var expected = new Dictionary<string, string>
        {
            [FailureClasses.Quota] = InterruptionReasons.Failure,
            [FailureClasses.Rate] = InterruptionReasons.Failure,
            [FailureClasses.Transport] = InterruptionReasons.Failure,
            [FailureClasses.AgentFault] = InterruptionReasons.Failure,
            [FailureClasses.LaunchMissing] = InterruptionReasons.Failure,
            [FailureClasses.OutOfMemory] = InterruptionReasons.Failure,
            [FailureClasses.Crashed] = InterruptionReasons.Failure,
            [FailureClasses.Timeout] = InterruptionReasons.Failure,
            [FailureClasses.Interrupted] = InterruptionReasons.Failure,
            [FailureClasses.WorkerLost] = InterruptionReasons.Failure,
            [FailureClasses.NeedsPerson] = InterruptionReasons.Auth,
            [FailureClasses.Unknown] = InterruptionReasons.Failure,
        };

        Assert.Equal(FailureClasses.All.Order(), expected.Keys.Order());
        foreach (var failureClass in FailureClasses.All)
        {
            Assert.Equal(expected[failureClass], InterruptionReasons.ForFailureClass(failureClass));
        }

        Assert.Equal(InterruptionReasons.Failure, InterruptionReasons.ForFailureClass(null));
        Assert.Equal(InterruptionReasons.Failure, InterruptionReasons.ForFailureClass("a-class-from-a-newer-build"));
        Assert.Equal(InterruptionReasons.Auth, InterruptionReasons.Of(MessageTypes.Failed, """{"failureClass":"needs-person"}"""));
        Assert.Equal(InterruptionReasons.Failure, InterruptionReasons.Of(MessageTypes.Failed, "{}"));
    }

    [Fact]
    public void A_code_outside_the_set_is_refused_with_one_sentence()
    {
        foreach (var code in new[] { null, "", "Failure", "because", "token-cap" })
        {
            var refused = Assert.Throws<ArgumentException>(() => InterruptionReasons.Require(code));
            Assert.StartsWith($"'{code}' is not an interruption reason; it must be one of token_cap,", refused.Message);
        }

        foreach (var code in InterruptionReasons.All) Assert.Equal(code, InterruptionReasons.Require(code));
    }

    [Fact]
    public void The_stamp_keeps_a_known_code_refuses_any_other_and_defaults_a_missing_one()
    {
        const string kept = """{"reason":"x","reasonCode":"busy"}""";
        Assert.Same(kept, InterruptionReasons.Stamped(MessageTypes.ScheduleSkipped, kept));

        Assert.Throws<ArgumentException>(() => InterruptionReasons.Stamped(MessageTypes.Rejected, """{"reasonCode":"nope"}"""));
        Assert.Throws<ArgumentException>(() =>
            InterruptionReasons.Stamped(MessageTypes.Rejected, """{"reasonCode":"busy","reasonCode":"busy"}"""));

        Assert.Equal("""{"reasonCode":"decision_required"}""", InterruptionReasons.Stamped(MessageTypes.NeedsDecision, "{}"));
        Assert.Equal("""{"reasonCode":"failure"}""", InterruptionReasons.Stamped(MessageTypes.Blocked, ""));
        Assert.Equal(
            """{"reasonCode":"workflow_budget","limitSource":"instance"}""",
            InterruptionReasons.Stamped(MessageTypes.WorkflowPaused, """{"limitSource":"instance"}"""));

        // Not an interruption: untouched, whatever it holds.
        const string other = """{"reasonCode":"nope"}""";
        Assert.Same(other, InterruptionReasons.Stamped(MessageTypes.WorkflowResumed, other));
        Assert.Same(other, InterruptionReasons.Stamped(MessageTypes.Failed, other));
    }

    [Fact]
    public void A_row_read_back_without_a_known_code_reads_as_its_types_default()
    {
        Assert.Equal(InterruptionReasons.DecisionRequired, InterruptionReasons.Of(MessageTypes.NeedsDecision, """{"question":"q"}"""));
        Assert.Equal(InterruptionReasons.Failure, InterruptionReasons.Of(MessageTypes.Blocked, """{"reason":"token cap"}"""));
        Assert.Equal(InterruptionReasons.Busy, InterruptionReasons.Of(MessageTypes.ScheduleSkipped, """{"reasonCode":"busy"}"""));
        Assert.Null(InterruptionReasons.Of(MessageTypes.WorkflowResumed, "{}"));
        Assert.Null(InterruptionReasons.Of(MessageTypes.Completed, "{}"));
    }
}
