/**
 * WHAT THE ONE SPEND BOUND SAYS ON A SCREEN — as pure functions, which is the only place a test can
 * reach the decision.
 *
 * **THERE ARE TWO BOUNDS AND THIS MODULE RESOLVES NEITHER.**
 *
 * Measured spend on items that look comparable when they are written varies by an order of
 * magnitude, so no number a person invents is right. The box is therefore PREFILLED with the
 * figure already in force, so nobody is asked to invent anything and editing it is a deliberate
 * act; and a workflow that reaches its figure PAUSES rather than being destroyed, which costs a
 * person a click and nothing else. **Exactly ONE of the two bounds applies**, the team's figure
 * when it has chosen one — ABOVE the instance's own if that is what was typed — and the
 * instance's only when it has not.
 *
 * **AND THE RESOLUTION IS THE SERVER'S.** Everything here takes ONE figure — `Team.
 * effectiveWorkflowBudget`, which the server resolved, where `null` means unlimited and 0 never
 * appears — and nothing here may read `Team.budgetTokens`, which is the stored CHOICE and carries
 * a `0` meaning unlimited and a `null` meaning "chosen nothing". Two resolvers is how a bar and a
 * pump come to answer the same question separately.
 *
 * THE BAR EXISTS so that a ceiling in force is visible before it refuses anything. A tile showing a
 * comfortable fraction of a bound that is not the one operating would hide the real ceiling until
 * the refusal. **A bar that reassures is worse than no bar**, so the one thing this module may never measure against is a
 * figure that is not in force.
 */

/**
 * THE FIGURE IN FORCE FOR ONE WORKFLOW ON A TEAM, which is the SERVER's answer or nothing.
 *
 * **THIS IS NOT A RESOLVER AND MUST NOT BECOME ONE.** `Team.effectiveWorkflowBudget` already IS
 * the resolution — the server read the team's stored choice and the instance figure and answered
 * one number, with `null` meaning unlimited. All this does is handle the one case the server
 * cannot: a Host that does not send the field at all.
 *
 * **ABSENT AND `null` ARE DIFFERENT ANSWERS, AND A SINGLE `??` COLLAPSES THEM.** `null` is this
 * Host saying UNLIMITED — a team that deliberately chose no bound — and falling back there would
 * paint a bar over a workflow that has none, a bar that reassures. `undefined`
 * is a Host with nothing to say, and such a Host applies its instance figure to every team, so
 * that figure is the one in force.
 *
 * Takes the two numbers rather than a `Team`, so it is a pure function a test can reach without
 * building a team record — the same rule the rest of this module follows.
 */
export function budgetInForce(
  effectiveWorkflowBudget: number | null | undefined,
  instanceWorkflowSpendLimit: number | null,
): number | null {
  return effectiveWorkflowBudget === undefined ? instanceWorkflowSpendLimit : effectiveWorkflowBudget
}

/**
 * WHICH SPEND THE BAR MEASURES — the window the server's guard decided on, never the cumulative
 * total.
 *
 * **WHY NOT THE CUMULATIVE TOTAL.** `TeamWorkflowTiming.spend` is filled from
 * `GetWorkflowSpendAsync` — the WHOLE workflow, which never comes down — while the pump refuses or
 * allows wakes on `GetSpendSinceNudgeAsync`, whose window RESETS at every nudge. A bar drawn from
 * the total would keep saying `> limit` about a workflow nudged back into life, or resumed after a
 * pause, that the server had already released. A bar that contradicts the decision is the same
 * fault as a bar that reassures: both measure something that is not in force.
 *
 * **TWO QUESTIONS, TWO FIELDS.** `spend` stays cumulative because `BacklogExecutionRecord` reads it
 * for what a backlog item COST, which is genuinely a whole-workflow question. `spendSinceNudge` is
 * the windowed one, and it is the only one this answer may prefer.
 *
 * **A WINDOW OF ZERO IS A MEASUREMENT** — it is exactly what the instant after a nudge looks like —
 * so this is `??` and never `||`, which would fall back to the cumulative figure in exactly that
 * state.
 *
 * FALLING BACK ON AN UNMEASURED WINDOW IS SAFE, and deliberately unlike {@link budgetInForce},
 * which must keep `null` and `undefined` apart. There, `null` is a real answer (UNLIMITED) that
 * differs from the fallback. Here the two fields appear and disappear together — a Host that sends
 * a null window sends a null total — so the fallback answers null anyway, and the only Host the
 * arm actually serves is one that does not send the window and would otherwise lose its bar
 * entirely.
 *
 * Takes the two numbers rather than the two `WorkflowSpend` records, the same rule the rest of this
 * module follows.
 */
export function spendAgainstBudget(
  spendSinceNudgeTokens: number | null | undefined,
  spendTokens: number | null | undefined,
): number | null {
  return spendSinceNudgeTokens ?? spendTokens ?? null
}

/**
 * Null, undefined and 0 all mean "no bound" — one question, asked in one place.
 *
 * BELT AND BRACES. Its callers are given `effectiveWorkflowBudget`, which the server
 * has already resolved to `null` for unlimited and never answers 0, so the `<= 0` arm does not
 * fire on anything the wire sends. It stays because this is a PURE function over a nullable
 * number and the cost of it being wrong is a bar drawn over nothing.
 */
export function isUnlimited(limitTokens: number | null | undefined): boolean {
  return limitTokens === null || limitTokens === undefined || limitTokens <= 0
}

/**
 * WHAT THE BOX ON THE TWO DIALOGS MEANS, as the one function both of them call.
 *
 * A `q-input type="number"` hands back the EMPTY STRING for a cleared box, never `null` - and NaN
 * for anything it cannot parse, which survives every comparison it is put through. Both have to
 * become the one thing the server understands, and doing it twice is how the New Team dialog and
 * Team settings come to disagree about what an empty box means.
 *
 * **EMPTY IS `null`, WHICH IS "THIS TEAM CHOOSES NOTHING"** - it runs on the instance's own
 * `WorkflowSpendLimit`, and the box is prefilled with exactly that figure so clearing it is a
 * deliberate act rather than a default.
 *
 * **A TYPED `0` IS `0`, WHICH IS "THIS TEAM CHOOSES UNLIMITED"**, and is a different answer.
 * Collapsing 0 to null at the write would, under the one-of rule, silently turn a person's
 * unlimited into the instance figure, which is the single easiest thing to get wrong on this
 * feature.
 *
 * **NOTHING ELSE IS TOUCHED.** A figure above the instance's own comes out exactly as typed: this
 * function normalises the absence of a number and never the value of one.
 */
export function budgetFieldValue(typed: number | string | null | undefined): number | null {
  if (typed === null || typed === undefined) return null
  if (typeof typed === 'string' && typed.trim() === '') return null

  const value = typeof typed === 'number' ? typed : Number(typed)

  return Number.isFinite(value) ? value : null
}

/**
 * Whether what is in the box can be submitted at all.
 *
 * TWO THINGS ARE REFUSED, AND NEITHER IS A CLAMP. A clamp silently substitutes a
 * different number for the one a person typed; this refuses to submit and says so on the field,
 * which is the visible half of the same honesty. **A figure above the instance's own is legal, an
 * empty box is legal, and a typed 0 is legal** - this function must never start eroding those
 * three.
 *
 * A NEGATIVE, because the route answers 400 for one regardless, so a button that sent it would
 * only produce a worse version of the same message.
 *
 * A FRACTION, because `BudgetTokens` on the route is a `long?` and a fractional JSON number never
 * reaches the handler at all - the person gets the framework's complaint about the shape of the
 * request instead of a sentence about their budget. **The arm has to be here rather than on
 * `step=`**, which is browser-dependent and which a paste defeats outright; `step="1"` on the two
 * inputs is the spinner behaving, not the guard.
 */
export function budgetFieldIsLegal(typed: number | string | null | undefined): boolean {
  return budgetFieldRefusal(typed) === null
}

/** What the field says when it refuses, or null when there is nothing to refuse. THE ONE PLACE
 *  THE WORDING LIVES - the dialogs render what this returns and neither writes its own. */
export function budgetFieldRefusal(typed: number | string | null | undefined): string | null {
  const value = budgetFieldValue(typed)

  if (value === null) return null
  if (value < 0) return BudgetFieldRefusal
  if (!Number.isInteger(value)) return BudgetFieldFractionRefusal

  return null
}

/** What the field says about a negative. THE SIGN IS NAMED FIRST for a negative fraction, which is
 *  both: it is the larger mistake and the one a person can act on without re-reading the box. */
export const BudgetFieldRefusal = 'A budget cannot be negative. Leave it empty for no choice, or 0 for unlimited.'

/** What the field says about a fraction. A SECOND SENTENCE BECAUSE ONE CANNOT HONESTLY COVER BOTH:
 *  "cannot be negative" says nothing at all to somebody who typed 1.5. */
export const BudgetFieldFractionRefusal =
  'A budget is a whole number of tokens. Leave it empty for no choice, or 0 for unlimited.'

/**
 * How far through the limit a workflow is, or null when there is nothing to be a fraction of.
 *
 * NULL FOR UNLIMITED, never 0 — a progress bar at zero says "barely started", which is a different
 * claim from "there is no bound here at all", and the first is the one a reader acts on.
 */
export function budgetFraction(
  spentTokens: number | null | undefined,
  limitTokens: number | null | undefined,
): number | null {
  if (isUnlimited(limitTokens)) return null
  if (typeof spentTokens !== 'number' || !Number.isFinite(spentTokens) || spentTokens < 0) return null

  return spentTokens / (limitTokens as number)
}

/** What the tile shows under the spend figure, or null when there is no bound to show. */
export interface BudgetLine {
  readonly text: string

  /** Spend has passed the limit. The pump is refusing wakes under this workflow, so the team is
   *  STOPPED rather than merely close to one — which is why this is a tone, not a shade. */
  readonly over: boolean
}

/**
 * The small line under a workflow's token spend.
 *
 * ABSENT WHEN THERE IS NO LIMIT, because a line that always shows is a line nobody reads — the same
 * rule the install badge follows.
 *
 * IT STOPS COUNTING ONCE IT IS OVER, which is also the truth: past the limit the pump refuses every
 * wake under this workflow, so the figure would sit there climbing only by whatever was already in
 * flight. "> limit" is the state, and a number that kept ticking would suggest work still being done.
 *
 * NOT THE TEAM TOTAL. The limit bounds ONE workflow and the token tile beside this is a TEAM TOTAL
 * across every workflow the team has ever run. Putting a per-workflow bound under a cumulative
 * figure would compare two different quantities, which is the mistake the Workflows dialog already
 * warns about for elapsed time.
 */
export function budgetLine(
  spentTokens: number | null | undefined,
  limitTokens: number | null | undefined,
): BudgetLine | null {
  if (isUnlimited(limitTokens)) return null

  const limit = limitTokens as number

  if (typeof spentTokens !== 'number' || !Number.isFinite(spentTokens) || spentTokens < 0) {
    // Spend not measured is not spend of zero — `(unknown)` is a real state here too, and a bar at
    // zero would claim this workflow had barely started.
    return { text: `limit ${limit.toLocaleString()}`, over: false }
  }

  return spentTokens > limit
    ? { text: '> limit', over: true }
    : { text: `${spentTokens.toLocaleString()} of ${limit.toLocaleString()}`, over: false }
}

/**
 * How close to the limit a workflow may get before the bar turns from spend to warning.
 *
 * NAMED RATHER THAN INLINE, so the bar and anything that ever explains the bar read one number:
 * within 10%.
 *
 * A WARNING HAS TO ARRIVE WHILE SOMETHING CAN STILL BE DONE. At 100% the pump has already stopped
 * waking the team, so a bar that first turned red there would be reporting history rather than
 * warning of anything.
 */
export const BudgetWarningFraction = 0.9

/** The thin bar across the top of the workflow tile, or null when there is nothing to draw. */
export interface BudgetBar {
  /**
   * How far along, 0 to 1, CLAMPED — where {@link budgetFraction} is deliberately not.
   *
   * A bar cannot be 130% wide: it would overflow its tile, or be silently cut and read as exactly
   * full. The overshoot is not lost, it is just not the BAR's to carry — {@link budgetLine} says
   * `> limit` in words directly beneath.
   */
  readonly fraction: number

  /** Within {@link BudgetWarningFraction} of the limit, or past it. */
  readonly near: boolean
}

/**
 * The bar, or null when there is no bound or no measurement.
 *
 * NULL FOR UNMEASURED SPEND, where {@link budgetLine} still names the limit in words. A bar at zero
 * is a MEASUREMENT — "barely started" — and `(unknown)` is the absence of one; painting invented
 * spend is what this codebase refuses everywhere else, and a bar is the easiest place to do it by
 * accident.
 *
 * COLOUR IS NEVER THE ONLY SIGNAL HERE, which is what makes a bar acceptable at all under the
 * text-plus-icon rule: it sits directly above a line that says the same thing in numbers and words,
 * so a reader who cannot tell orange from red loses nothing.
 */
export function budgetBar(
  spentTokens: number | null | undefined,
  limitTokens: number | null | undefined,
): BudgetBar | null {
  const fraction = budgetFraction(spentTokens, limitTokens)

  if (fraction === null) return null

  return {
    fraction: Math.min(1, Math.max(0, fraction)),
    near: fraction >= BudgetWarningFraction,
  }
}

/**
 * A token budget as a person reads it: "no limit", or a figure rounded to millions or billions
 * ("100 million tokens") rather than nine digits nobody counts. A rounded figure says "about", so
 * the words never claim a precision the box does not hold. Takes the RESOLVED figure, where `null`
 * and 0 both mean no bound - see {@link isUnlimited}.
 */
export function budgetInWords(limitTokens: number | null | undefined): string {
  if (isUnlimited(limitTokens)) return 'no limit'

  const limit = limitTokens as number
  const scales: readonly [number, string][] = [
    [1_000_000_000, 'billion'],
    [1_000_000, 'million'],
    [1_000, 'thousand'],
  ]

  for (const [at, [size, word]] of scales.entries()) {
    if (limit < size) continue
    const tenths = Math.round((limit / size) * 10)
    // 999,960 rounds to 1,000 thousand: say it with the scale above instead.
    const larger = scales[at - 1]
    if (tenths >= 10_000 && larger) return inScale(limit, larger)
    return inScale(limit, [size, word])
  }

  return limit === 1 ? '1 token' : `${limit} tokens`
}

function inScale(limit: number, [size, word]: readonly [number, string]): string {
  const tenths = Math.round((limit / size) * 10)
  const about = (tenths * size) / 10 === limit ? '' : 'about '
  return `${about}${(tenths / 10).toLocaleString('en')} ${word} tokens`
}
