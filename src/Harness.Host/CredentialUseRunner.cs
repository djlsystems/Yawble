using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// Answers, for one run, whether the agent reached the platform at all - and hands the answer back
/// on the result so the container can refuse to call it a success.
///
/// WHY THIS EXISTS. An agent CLI can deny its own shell tools through a fault of its own, so the
/// member never calls `skills_get member`, never loads its role skill, writes nothing - and exits
/// 0. Without this check the platform publishes `container.completed`, the card goes green, and
/// the Manager sends corrections into further empty runs, each billed in full, until a person
/// notices the ABSENCE of commits.
///
/// THE SIGNAL IS THE MEMBER'S OWN CREDENTIAL. Every seeded prompt opens with
/// `skills_get &lt;role&gt;`, and the MCP tools are the only way a member reaches the system at
/// all, so a run that served no request made no tool call that could have mattered. It is
/// structural: it cannot be faked by output text, and it does not care which CLI failed or how.
///
/// NOT A DENYLIST OF SENTENCES. Matching the agent's output for "permission denied" is refused:
/// such a list is only as current as the last CLI anyone looked at, and it fires on an
/// agent legitimately REPORTING that a command was refused - which is a thing a good agent does.
///
/// A DECORATOR RATHER THAN A PARAMETER ON <see cref="ProcessAgentRunner"/>, and that shape is
/// chosen. The runner is about spawning a process and reading what it wrote; this is about what the
/// SERVER saw while it ran. Folding it in would give that class a store it uses for nothing else and
/// would reach all of its test constructions. The cost is that the composition in
/// `Program.cs` becomes load-bearing - if nothing wraps the runner, every result reports null and
/// nothing is ever detected, silently, which is why
/// <c>AgentRunnerCompositionTests</c> asserts the Host's registered IAgentRunner is this type.
///
/// NO INSTANCE STATE, deliberately. One of these serves every container on the host, so anything
/// remembered between the two reads would be raced by concurrent runs - including whether the
/// store answered, which is why <c>Probe</c> carries that alongside the answer.
/// </summary>
public sealed class CredentialUseRunner(
    IAgentRunner inner, IPrincipalStore principals, AgentCatalog catalog) : IAgentRunner
{
    /// <summary>The runner this one measures.</summary>
    public IAgentRunner Inner => inner;

    /// <summary>One read: the answer, and whether the store actually gave one.</summary>
    private readonly record struct Probe(DateTimeOffset? At, bool Answered);

    public async Task<AgentResult> RunAsync(
        AgentInvocation invocation, CancellationToken ct = default)
    {
        // RESOLVED ON EVERY INVOCATION, never cached on the container - a cache keyed on an Agent
        // Container drifts, changing nothing until restart. `For` answers null for an agent the
        // catalog cannot resolve (deleted, relabelled, never chosen); such a container is treated as a
        // language model, which is the strict arm.
        //
        // THE PREMISE OF THIS DECORATOR IS THAT A MEMBER CALLS THE PLATFORM, and a procedural preset
        // legitimately does not - an Agent Container can host an ordinary program, woken by a
        // message like any member, which does its work and never authenticates anything.
        //
        // NOT PROBED, rather than probed-and-forgiven. ReachedThePlatform stays NULL, which is "not
        // measured" and has never been a failure - `Succeeded` tests `!= false`, not `== true`,
        // precisely so a probe that cannot see does not convict. Reusing that is why this needs no
        // new success path.
        var command = catalog.For(invocation.Agent);
        if (command is { LanguageModel: false })
        {
            return await inner.RunAsync(invocation, ct);
        }

        // The container's principal id IS its qualified name - see AgentEnvironment, which mints
        // with `id.ToString()`. Read from the invocation rather than from the environment, so this
        // class stays ignorant of which variable carries the credential.
        var id = invocation.Container.ToString();

        var before = await ProbeAsync(id, ct);

        var result = await inner.RunAsync(invocation, ct);

        var after = await ProbeAsync(id, ct);

        // UNKNOWN STAYS UNKNOWN. If either read failed, this run is NOT MEASURED and reports null,
        // which AgentResult.Succeeded treats as a success. A probe that cannot see must not convict:
        // reading a store failure as "the agent did nothing" would fail every run on a machine whose
        // database hiccuped - a far worse outcome than the one this exists to catch.
        if (!before.Answered || !after.Answered) return result;

        // `after != before` also covers a credential used for the FIRST time, which moves from null
        // to a timestamp. `last_used_at` is stamped on every successful resolve, so any
        // authenticated request during the run moves it; none leaves the two reads equal, and two
        // nulls are equal.
        //
        // NOT "is it still null": MintAsync's upsert nulls the column, which makes a null check look
        // sufficient - but a container's credential is minted at CREATION and at RESTORE rather than
        // per invocation, so by its second run it is never null again.
        if (after.At != before.At)
        {
            return result with { ReachedThePlatform = true };
        }

        // The credential did not move. But a run that produced output or reported usage evidence
        // still ran and did something. Only when BOTH are absent is the run DidNothing.
        //
        // Evidence in order of trustworthiness:
        // - usage: a reported token count. A brand that reports usage and reports a non-zero figure
        //   has been to the model. Strongest signal.
        // - output: non-empty stdout. Weaker because a stand-in can echo, but a real agent with
        //   thousands of characters did something.
        var hasUsageEvidence = HasUsage(result);
        var hasOutputEvidence = !string.IsNullOrEmpty(result.Output);

        if (hasUsageEvidence || hasOutputEvidence)
        {
            // The run produced evidence of work. Leave ReachedThePlatform as null (not measured),
            // which is not a failure. The credential did not move, but the work is not "DidNothing".
            return result;
        }

        // No credential use, no usage, no output: the run did nothing.
        return result with { ReachedThePlatform = false };
    }

    /// <summary>
    /// Whether the result reports any token usage at all.
    /// </summary>
    private static bool HasUsage(AgentResult result)
    {
        if (result.Usage is null)
        {
            return false;
        }

        // Usage with combined total
        if (result.Usage.Total.HasValue && result.Usage.Total.Value > 0)
        {
            return true;
        }

        // Usage with split (input and/or output)
        var hasTokensIn = result.Usage.TokensIn.HasValue && result.Usage.TokensIn.Value > 0;
        var hasTokensOut = result.Usage.TokensOut.HasValue && result.Usage.TokensOut.Value > 0;

        return hasTokensIn || hasTokensOut;
    }

    private async Task<Probe> ProbeAsync(string id, CancellationToken ct)
    {
        try
        {
            return new Probe(await principals.LastUsedAtAsync(id, ct), Answered: true);
        }
        catch (Exception)
        {
            return new Probe(null, Answered: false);
        }
    }
}
