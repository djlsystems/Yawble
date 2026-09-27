namespace Harness.Contracts;

/// <summary>
/// The one value <c>tokensSource</c> may hold that is NOT a measurement.
///
/// A row carrying it holds characters ÷ 4 over the system prompt, the ledger context, the waking
/// prompt and stdout — which sounds like an approximation of a run's token usage and is a different
/// quantity. A headless CLI is an agentic loop: every internal turn re-sends the whole growing
/// conversation, so one run can consume hundreds of thousands of prompt tokens across dozens of
/// loops where such an estimate counts the opening prompt once. Wrong by orders of magnitude,
/// understated, and worst exactly where a run is most expensive.
///
/// THERE IS NO ESTIMATE AND NO FALLBACK. Usage is reported where a brand measured it and
/// <c>(unknown)</c> everywhere else. A fallback that exists is one something quietly uses.
///
/// This constant exists ONLY so the projection can EXCLUDE rows carrying it. The message log is
/// append-only and those numbers cannot be re-derived, so they are counted as runs without usage
/// rather than summed. It is never written to a new row.
/// </summary>
public static class UsageSource
{
    public const string ExcludedEstimate = "estimated";
}

public sealed record InvocationUsage
{
    public InvocationUsage(
        int tokensIn,
        int tokensOut,
        string source,
        int? cachedIn = null,
        int? reasoning = null,
        int? cacheCreation = null)
    {
        if (tokensIn < 0 || tokensOut < 0 || cachedIn is < 0 || cacheCreation is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tokensIn), "Token counts cannot be negative.");
        }

        // TokensIn is UNCACHED input only. Cache reads are a separate quantity and are often
        // larger than the uncached input; folding them in at full weight inflates a workflow's
        // figure many times over. BillableTokens weights a cache read at 1/10 and a cache write
        // at 5/4, which is Claude's published price ratio. The spend SQL must use the same weights.
        TokensIn = tokensIn;
        TokensOut = tokensOut;
        Source = source;
        CachedIn = cachedIn;
        Reasoning = reasoning;
        CacheCreation = cacheCreation;
    }

    private InvocationUsage(int total, string source)
    {
        Total = total;
        Source = source;
    }

    /// <summary>
    /// A brand that reports ONE FIGURE for a run and no split - codex prints `tokens used N` and
    /// says nothing about which way they went.
    ///
    /// THE SPLIT STAYS NULL AND IS NOT INVENTED. Halving the total, or filing all of it as input,
    /// would put a number on screen that no CLI ever reported - an estimate by another name, from a new
    /// source of numbers. Null is "not measured", which is true, and <see cref="Total"/> carries
    /// what actually was.
    ///
    /// It is deliberately NOT set for a brand that reports a split: in + out is derivable from
    /// those two, and a stored copy is a second store of one fact waiting to disagree with them.
    /// </summary>
    public static InvocationUsage Combined(int total, string source) => new(total, source);

    /// <summary>Null when this brand reports only a combined total.</summary>
    public int? TokensIn { get; }

    /// <summary>Null when this brand reports only a combined total.</summary>
    public int? TokensOut { get; }

    /// <summary>Set ONLY where the brand gave one figure and no split. Null otherwise.</summary>
    public int? Total { get; }

    public string Source { get; }
    public int? CachedIn { get; }
    public int? Reasoning { get; }

    /// <summary>Tokens written into the prompt cache. Null when the brand did not report them.</summary>
    public int? CacheCreation { get; }

    /// <summary>
    /// What a spend limit should count. Cache reads are not full-price input.
    /// A combined total (no split) is returned as reported. Integer arithmetic matches the SQL
    /// projection: cache read / 10, cache write * 5 / 4.
    /// </summary>
    public long BillableTokens()
    {
        if (Total is { } combined) return combined;

        var cache = CachedIn ?? 0;
        var created = CacheCreation ?? 0;
        return (TokensIn ?? 0) + (TokensOut ?? 0) + (cache / 10) + ((created * 5) / 4);
    }
}

/// <summary>
/// One container's usage as summed from completed/failed payloads on the log.
///
/// A PROJECTION, not a second store. The payload written by
/// <c>MemberRuntime.RunOneAsync</c> is the record. Rows that predate capture (no
/// <c>tokensIn</c>/<c>tokensOut</c> keys) are counted in <see cref="RunsWithoutUsage"/>
/// rather than summed as zeros, so a dashboard cannot paint invented spend.
/// </summary>
/// <param name="TokensTotal">
/// The sum of the COMBINED figures this container reported, from a brand that gives one number and
/// no split. Null where none did. Never folded into <paramref name="TokensIn"/> or
/// <paramref name="TokensOut"/>: nothing knows which way those tokens went, and a run carrying only
/// this still counts in <paramref name="RunsWithoutUsage"/> so the in/out totals stay honest about
/// what they leave out.
/// </param>
public sealed record ContainerUsageRow(
    string Source,
    long TokensIn,
    long TokensOut,
    int RunsWithUsage,
    int RunsWithoutUsage,
    long? TokensTotal = null,
    long TokensCachedIn = 0,
    long TokensCacheCreation = 0,
    long TokensBillable = 0);

/// <summary>Team-wide usage totals, the honest answer a header card can show.</summary>
public sealed record TeamTokenTotals(
    bool Available,
    long TokensIn,
    long TokensOut,

    // NO `Estimated`. Nothing estimated is summed, so there is nothing to flag - and a flag that
    // is always false is a question readers keep asking. `Partial` carries what is left: some runs
    // on this team have no usage, whether because their rows carry none or because their Agent
    // reports none.
    bool Partial,
    int RunsWithUsage,
    int RunsWithoutUsage,
    string? Missing,
    IReadOnlyList<MemberTokenTotals> Members,
    long TokensCachedIn = 0,
    long TokensCacheCreation = 0,

    // Weighted as InvocationUsage.BillableTokens weights one run; combined totals included.
    long TokensBillable = 0);

/// <summary>
/// One member's line in the "who consumed what" dialog.
///
/// THREE DIFFERENT REASONS A NUMBER CAN BE ABSENT, AND THEY HAVE DIFFERENT REMEDIES - which is why
/// this record carries two facts beside the totals rather than leaving every empty cell reading
/// `(unknown)`:
///
/// - <see cref="Runs"/> is 0: this member has never finished a run. Nothing is missing; there is
///   nothing yet. A member mid-first-run is listed here too.
/// - <see cref="BrandReportsUsage"/> is false: its Agent reports no usage AT ALL, because the
///   preset carries no <c>AgentLaunch.UsageFormat</c>. `codex-headless` is the seeded example.
///   This is a PERMANENT answer, not a gap - the next run will not fill it either.
/// - Both are set and the totals are still null: the rows carry no usage keys, or a parse failed.
///   That one genuinely is a gap and may close on its own.
/// </summary>
/// <param name="BrandReportsUsage">
/// Whether this member's CURRENT Agent carries a usage format. Null when the Agent cannot be
/// resolved at all - a container the registry no longer holds - because "not measured" and "we
/// could not tell" are the same distinction one level up, and a probe that cannot see must not
/// convict.
/// </param>
/// <param name="Runs">
/// Completed and failed rows this member has on the log, above the team's floor. Counts ROWS, so a
/// member listed here for completeness with no rows of its own reads 0 and adds nothing to the
/// team's own run counts.
/// </param>
public sealed record MemberTokenTotals(
    string Member,
    string Brand,
    long? TokensIn,
    long? TokensOut,
    bool? BrandReportsUsage = null,
    int Runs = 0,

    /// <summary>What this member's runs cost where its Agent reports ONE figure and no split.
    /// Null for a brand that splits, and null for one that reports nothing at all.</summary>
    long? TokensTotal = null,
    long? TokensCachedIn = null,
    long? TokensCacheCreation = null,
    long? TokensBillable = null)
{
    /// <summary>
    /// One member's line from its projected row. The split figures are null unless a run reported
    /// a split; billable is null unless a run reported a split or a combined total.
    /// </summary>
    public static MemberTokenTotals FromUsage(
        string member, string brand, ContainerUsageRow row, bool? brandReportsUsage)
    {
        var split = row.RunsWithUsage > 0;

        return new(member, brand,
            split ? row.TokensIn : null,
            split ? row.TokensOut : null,
            brandReportsUsage,
            row.RunsWithUsage + row.RunsWithoutUsage,
            row.TokensTotal,
            split ? row.TokensCachedIn : null,
            split ? row.TokensCacheCreation : null,
            split || row.TokensTotal is not null ? row.TokensBillable : null);
    }
}

/// <summary>
/// One workflow's total spend as summed from completed/failed payloads on the log.
///
/// A PROJECTION, not a second store. The payload written by <c>MemberRuntime.RunOneAsync</c>
/// is the record. This is what a workflow spend limit reads to decide whether to refuse a dispatch.
///
/// Unmeasured spend never convicts: a workflow with zero measured runs will have RunsWithMeasuredUsage = 0,
/// and the refusal check has an explicit arm to make it impossible for the bound to refuse.
/// Partial measurement bounds only what was measured.
/// </summary>
/// <param name="TokensSpent">
/// The sum of tokens actually counted: tokensIn + tokensOut + tokensTotal, one figure.
/// tokensTotal is added (not folded into in/out): a brand that reports one number (codex) cannot
/// be added to an in total or an out total without deciding which way its tokens went, and nothing
/// knows that. ExcludedEstimate rows are excluded. If there are no measured runs, this is 0.
/// </param>
/// <param name="RunsWithMeasuredUsage">
/// Count of completed/failed runs under this correlation that have any usage recorded
/// (tokensIn + tokensOut present, or tokensTotal present), excluding ExcludedEstimate.
/// Zero when all runs are unmeasured.
/// </param>
/// <param name="RunsWithoutUsage">
/// Count of completed/failed runs under this correlation that had no usage to measure.
/// Includes rows with no tokensIn/tokensOut keys, ExcludedEstimate rows, and any
/// runs from a brand that reports no usage. Zero when every run was measured.
/// </param>
public sealed record WorkflowSpend(
    long TokensSpent,
    int RunsWithMeasuredUsage,
    int RunsWithoutUsage);
