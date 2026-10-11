using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// AN AGENT MEMBER: adapts a coding-agent CLI to <see cref="IMemberRunner"/>.
///
/// Everything a coding agent needs that a plugin does not lives here or below it, and the member
/// runtime knows none of it:
/// - the member's ledger HISTORY, built through <see cref="IContextBuilder"/> above the floor;
/// - the PROMPT, rendered from the batch by <see cref="MessageText"/> plus the card's worktree
///   line - the exact text this platform has always handed an agent;
/// - the system prompt, handed through as the runtime's opaque standing instructions;
/// - the agent's own FAILURE WORDS and their precedence: its launch error, then
///   <see cref="AgentResult.DidNothing"/>, then "The agent exited N"; and the class default,
///   <see cref="FailureClasses.AgentFault"/> for a run that never reached the platform.
///
/// Below it, unchanged, is the <see cref="IAgentRunner"/> stack Program.cs composes:
/// <see cref="CredentialUseRunner"/> over <see cref="ProcessAgentRunner"/>, which owns the catalog,
/// argument tokens, MCP, usage, envelopes and the live view. The step-0 goldens in
/// <c>MemberGoldenTests</c> pin that an agent member's prompt, context, environment and rows are
/// byte-identical to what the runtime produced when it did this itself.
/// </summary>
/// <remarks>
/// THE RUN'S CREDENTIAL IS DECIDED HERE, at run start, through <see cref="IRunCredentials"/>, and
/// travels on <see cref="AgentInvocation.Credential"/>: the runner below only applies it. Null
/// <paramref name="credentials"/> leaves it to the runner, which asks the same resolver.
/// </remarks>
public sealed class AgentMemberRunner(IAgentRunner agent, IContextBuilder? context = null, IRunCredentials? credentials = null)
    : IMemberRunner
{
    /// <summary>The agent stack below this adapter.</summary>
    public IAgentRunner Agent => agent;

    public async Task<MemberResult> RunAsync(MemberInvocation invocation, CancellationToken ct = default)
    {
        var first = invocation.Work[0];
        var run = invocation.Context;

        // Built from the LEDGER, before the run. Without it an invocation would receive the system
        // prompt and the waking message and nothing else - no history at all. On the run's own
        // token: a Stop during context building must take effect, and this read is not instant on
        // a member with history.
        var history = context is null
            ? string.Empty
            : await context.BuildAsync(invocation.Member, first, run.Limits, run.FloorSeq, ct);

        var prompt = run.Worktrees.Count == 0 || run.BranchHint is null
            ? MessageText.Of(invocation.Work)
            : MessageText.Of(invocation.Work) + "\n" + WorktreeText(run.BranchHint, run.Worktrees);

        var credential = credentials is null
            ? null
            : await credentials.ResolveAsync(invocation.Implementation, null, ct);

        var result = await agent.RunAsync(
            new AgentInvocation(
                invocation.Member,
                run.Instructions,
                prompt,
                invocation.WorkingDirectory,
                invocation.Environment,
                history,
                invocation.Implementation,
                run.UnreachableRoot,
                credential),
            ct);

        return ToMemberResult(result);
    }

    /// <summary>
    /// An agent's result, in the runtime's terms, with today's precedence exactly.
    ///
    /// THE REASON: THREE ARMS, AND THE ORDER IS THE PRECEDENCE. `launchError` first, because it is
    /// the sentence that says what actually went wrong. A run stopped by the timeout, by Stop, or
    /// by a host restart ALSO made no CLI call, and it already carries a reason that says which of
    /// those it was; putting `DidNothing` above them would replace a sentence that helps with one
    /// that is true and vaguer. Every one of those paths sets `LaunchError`, so the first arm covers
    /// them all and the middle arm only ever speaks for a run that reached the agent, came back
    /// clean, and did nothing.
    ///
    /// THE CLASS, WHOSE PRECEDENCE IS NOT THE REASON'S: what the runner found; then `DidNothing` is
    /// agent-fault, because a run whose tools were denied exits 0 and reaches nothing and re-running
    /// it spends again to fail the same way; then null, which the runtime files as unknown.
    /// </summary>
    public static MemberResult ToMemberResult(AgentResult result) =>
        new(
            result.Succeeded,
            result.ExitCode,
            result.Output,
            result.LaunchError,
            FailureReason: result.Succeeded
                ? null
                : !string.IsNullOrWhiteSpace(result.LaunchError)
                    ? result.LaunchError
                    : result.ReachedThePlatform == false
                        ? AgentResult.DidNothing
                        : $"This run did not complete. The agent exited {result.ExitCode}.",
            FailureClass: result.FailureClass
                ?? (result.ReachedThePlatform == false ? FailureClasses.AgentFault : null),
            RetryAfter: result.RetryAfter,
            ProcessId: result.ProcessId,
            Usage: result.Usage,
            Transcript: result.AgentTranscript is { } transcript
                ? new RunTranscript(transcript.Path, transcript.Format)
                : null);

    /// <summary>
    /// The instruction's own statement of where the work goes, appended after the messages: the
    /// same paths the environment carries, and one line per repository when there are several.
    /// </summary>
    public static string WorktreeText(string branchHint, IReadOnlyList<RepoWorktree> trees)
    {
        var lines = new List<string>(trees.Count + 3) { string.Empty };

        if (trees.Count == 1)
        {
            lines.Add(
                $"Your worktree for this card is {trees[0].Path} (${MemberRuntime.WorktreeVariable}), cut from the "
                + $"clone at {trees[0].ClonePath}. Create it if it does not exist, otherwise work in it.");
        }
        else
        {
            lines.Add(
                $"Your worktrees for this card, one per repository (${MemberRuntime.WorktreeVariable} is the first). "
                + "Create each you need if it does not exist, otherwise work in it:");
            lines.AddRange(trees.Select(t => $"- {t.Repo}: {t.Path}, cut from the clone at {t.ClonePath}"));
        }

        lines.Add($"Suggested branch: {branchHint} (${MemberRuntime.BranchHintVariable}).");

        return string.Join("\n", lines);
    }
}
