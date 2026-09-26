import { describe, expect, it } from 'vitest'
import {
  budgetBar,
  BudgetWarningFraction,
  budgetLine,
  budgetFraction,
  isUnlimited,
  budgetInForce,
  budgetFieldValue,
  budgetFieldIsLegal,
  budgetFieldRefusal,
  BudgetFieldRefusal,
  BudgetFieldFractionRefusal,
  spendAgainstBudget,
} from '../teamBudget'

describe('isUnlimited', () => {
  /**
   * NULL AND 0 ARE THE SAME ANSWER, a product decision and a deliberate departure from this codebase's usual rule that absent and empty differ. A person who clears the
   * field and one who types 0 both mean "do not stop this team".
   */
  it('reads null, undefined and 0 as the same answer', () => {
    expect(isUnlimited(null)).toBe(true)
    expect(isUnlimited(undefined)).toBe(true)
    expect(isUnlimited(0)).toBe(true)
  })

  it('reads a real number as a bound', () => {
    expect(isUnlimited(50_000_000)).toBe(false)
    expect(isUnlimited(1)).toBe(false)
  })
})

describe('budgetFraction', () => {
  /**
   * NULL FOR UNLIMITED, NEVER 0. A bar at zero says "barely started", which is a different claim
   * from "there is no bound here", and only the first is one a reader acts on.
   */
  it('is null when there is no bound to be a fraction of', () => {
    expect(budgetFraction(1_000, null)).toBeNull()
    expect(budgetFraction(1_000, 0)).toBeNull()
  })

  it('is null when spend was never measured', () => {
    expect(budgetFraction(null, 100)).toBeNull()
    expect(budgetFraction(undefined, 100)).toBeNull()
  })

  it('answers the fraction, and keeps going past 1', () => {
    expect(budgetFraction(50, 100)).toBe(0.5)

    // 39.2M against 30M is a real case, and a bar that clamped at
    // 1 would render it identically to a workflow that stopped exactly on its limit.
    expect(budgetFraction(39_215_406, 30_000_000)).toBeCloseTo(1.307, 3)
  })
})

describe('budgetLine', () => {
  /** An unbounded team is the ordinary case, and a line that always shows is a line nobody reads. */
  it('shows nothing when there is no budget', () => {
    expect(budgetLine(1_000, null)).toBeNull()
    expect(budgetLine(1_000, 0)).toBeNull()
  })

  it('shows spend against the budget while under it', () => {
    expect(budgetLine(12_000_000, 50_000_000)?.text).toBe('12,000,000 of 50,000,000')
    expect(budgetLine(12_000_000, 50_000_000)?.over).toBe(false)
  })

  /**
   * IT STOPS COUNTING ONCE IT IS OVER. Past the budget the pump refuses every wake under this
   * workflow, so a figure that kept ticking would suggest work still being done - the team is
   * STOPPED, and that is the thing to say.
   */
  it('stops counting and says so once spend passes the budget', () => {
    const line = budgetLine(39_215_406, 30_000_000)

    expect(line?.text).toBe('> limit')
    expect(line?.over).toBe(true)
  })

  it('is not over at exactly the budget, because the bound refuses ABOVE it', () => {
    expect(budgetLine(50_000_000, 50_000_000)?.over).toBe(false)
  })

  /** Spend not measured is not spend of zero. The bound is still worth naming. */
  it('names the budget when spend was never measured', () => {
    expect(budgetLine(null, 50_000_000)?.text).toBe('limit 50,000,000')
    expect(budgetLine(null, 50_000_000)?.over).toBe(false)
  })
})

describe('budgetBar', () => {
  /** Same gate as the line above it: an unbounded team is the ordinary case and draws nothing. */
  it('is null when there is no bound to be a fraction of', () => {
    expect(budgetBar(1_000, null)).toBeNull()
    expect(budgetBar(1_000, 0)).toBeNull()
  })

  /**
   * UNMEASURED SPEND DRAWS NOTHING, where the LINE still names the budget. A bar at zero claims
   * "barely started", which is a measurement; `(unknown)` is the absence of one, and this codebase
   * refuses to paint invented spend anywhere else.
   */
  it('is null when spend was never measured', () => {
    expect(budgetBar(null, 100)).toBeNull()
    expect(budgetBar(undefined, 100)).toBeNull()
  })

  it('fills to the fraction spent', () => {
    expect(budgetBar(25, 100)?.fraction).toBe(0.25)
    expect(budgetBar(0, 100)?.fraction).toBe(0)
  })

  /**
   * **CLAMPED AT FULL, WHERE `budgetFraction` IS DELIBERATELY NOT.** A bar cannot be 130% wide - it
   * would overflow its tile or, worse, be silently cut and read as exactly full. The OVERSHOOT is
   * not lost: the line beneath says `> budget` in words, which is where that fact belongs.
   */
  it('clamps at full rather than overflowing the tile', () => {
    expect(budgetBar(39_215_406, 30_000_000)?.fraction).toBe(1)
  })

  /**
   * RED WITHIN 10% OF THE BUDGET. The point of a warning is
   * that it arrives while there is still something to do about it - at 100% the pump has already
   * stopped waking the team, so a bar that only turned red then would be reporting history.
   */
  it('turns near at ninety per cent and not before', () => {
    expect(budgetBar(89, 100)?.near).toBe(false)
    expect(budgetBar(90, 100)?.near).toBe(true)
  })

  it('stays near once past the budget', () => {
    expect(budgetBar(150, 100)?.near).toBe(true)
  })

  /** The threshold is a named constant so the bar and anything that explains it cannot drift. */
  it('warns at the fraction it publishes', () => {
    expect(BudgetWarningFraction).toBe(0.9)
    expect(budgetBar(BudgetWarningFraction * 100, 100)?.near).toBe(true)
  })
})

describe('budgetLine and budgetBar against the one bound', () => {
  /**
   * A REAL-SCALE CASE: team budget 100,000,000, instance ceiling 30,000,000, spend 37,850,774. Reading
   * `37,850,774 of 100,000,000` with an orange bar at 38% would hide that the pump is refusing
   * every wake. It has to read as OVER, and say whose bound did it.
   */
  it('measures against the instance ceiling when that is what will stop the team', () => {
    const line = budgetLine(37_850_774, 30_000_000)

    expect(line?.over).toBe(true)
  })

  it('and the bar is full and red rather than 38% and orange', () => {
    const bar = budgetBar(37_850_774, 30_000_000)

    expect(bar?.fraction).toBe(1)
    expect(bar?.near).toBe(true)
  })

  /** When the TEAM's figure is the lower one, nothing is named - the number on the screen is the
   *  team's own and saying so would be noise. */
  it('says nothing about the instance when the team bound is the lower', () => {
    expect(budgetLine(1_000, 100_000_000)?.text).not.toContain('instance')
  })

  /** A team with no budget of its own is still bounded by the ceiling, and the tile must show it -
   *  rendering nothing would let an unbounded-looking team be stopped. */
  it('shows the ceiling for a team that set no budget', () => {
    const line = budgetLine(5_000_000, 30_000_000)

    expect(line).not.toBeNull()
    expect(line?.text).toContain('30,000,000')
  })

  /** And with neither bound there is still nothing to draw. */
  it('stays absent when nothing bounds the workflow', () => {
    expect(budgetLine(5_000, null)).toBeNull()
    expect(budgetBar(5_000, null)).toBeNull()
  })
})

describe('budgetInForce', () => {
  /**
   * THE SERVER'S ANSWER WINS WHENEVER IT GAVE ONE, and that is the whole rule on this
   * side: the browser does not resolve the team's stored choice against the instance figure.
   */
  it('takes the figure the server resolved', () => {
    expect(budgetInForce(200_000_000, 50_000_000)).toBe(200_000_000)
  })

  /** INCLUDING ONE ABOVE THE INSTANCE'S OWN. A team's number is exactly what was typed; the
   *  instance figure does not override a higher one. */
  it('does not lower a team figure to the instance one', () => {
    expect(budgetInForce(900_000_000, 100_000_000)).toBe(900_000_000)
  })

  /**
   * `null` IS THE SERVER SAYING UNLIMITED and stays unlimited. A `??` here would fall through to
   * the instance figure and paint a bar over a workflow that has no bound - a bound that is not
   * operating.
   */
  it('keeps an explicit null as unlimited rather than falling back', () => {
    expect(budgetInForce(null, 50_000_000)).toBeNull()
  })

  /** ABSENT IS A HOST THAT NEVER SAID. Such a Host applies its instance figure to every team, so
   *  that figure is the one in force - the opposite answer from `null`, on purpose. */
  it('falls back to the instance figure when the Host sent no field', () => {
    expect(budgetInForce(undefined, 50_000_000)).toBe(50_000_000)
  })

  /** And to nothing at all when that Host had no figure either. */
  it('is unlimited when neither side named a number', () => {
    expect(budgetInForce(undefined, null)).toBeNull()
  })
})

describe('budgetFieldValue', () => {
  /**
   * AN EMPTY BOX IS `null`, WHICH IS "THIS TEAM CHOOSES NOTHING" - it runs on the instance figure.
   * A `q-input type="number"` hands back the empty string rather than null, and both dialogs call
   * this rather than each deciding what an empty box means.
   */
  it('reads an empty box as no choice', () => {
    expect(budgetFieldValue('')).toBeNull()
    expect(budgetFieldValue('   ')).toBeNull()
    expect(budgetFieldValue(null)).toBeNull()
    expect(budgetFieldValue(undefined)).toBeNull()
  })

  /**
   * A TYPED 0 IS `0`, WHICH IS A DIFFERENT ANSWER: this team chooses UNLIMITED. Collapsing 0 to
   * null at the write would, under the one-of rule, silently turn a person's unlimited into the
   * instance figure - the single easiest thing to get wrong here.
   */
  it('keeps an explicit zero, which is a choice and not an absence', () => {
    expect(budgetFieldValue(0)).toBe(0)
    expect(budgetFieldValue('0')).toBe(0)
  })

  /** EVERYTHING ELSE COMES OUT AS TYPED. This normalises the ABSENCE of a number, never the value
   *  of one - no clamp, no rounding, no ceiling. */
  it('passes a real figure through untouched, however large', () => {
    expect(budgetFieldValue(900_000_000)).toBe(900_000_000)
    expect(budgetFieldValue('250000000')).toBe(250_000_000)
  })

  /** NaN survives every comparison it is put through, so it is turned into the one answer that
   *  means something rather than being sent. */
  it('reads something unparseable as no choice rather than as NaN', () => {
    expect(budgetFieldValue(Number.NaN)).toBeNull()
    expect(budgetFieldValue('not a number')).toBeNull()
  })
})

describe('budgetFieldIsLegal', () => {
  /**
   * ONLY A NEGATIVE IS REFUSED, AND THIS IS NOT A CLAMP. A clamp silently
   * substitutes a different number for the one a person typed; this refuses and says so on the
   * field. A figure above the instance's own is perfectly legal here - that is the decision.
   */
  it('accepts a figure above the instance’s own', () => {
    expect(budgetFieldIsLegal(900_000_000)).toBe(true)
  })

  it('accepts zero and an empty box, which are both real answers', () => {
    expect(budgetFieldIsLegal(0)).toBe(true)
    expect(budgetFieldIsLegal('')).toBe(true)
  })

  it('refuses a negative, which the route refuses with 400 anyway', () => {
    expect(budgetFieldIsLegal(-1)).toBe(false)
    expect(budgetFieldIsLegal('-250')).toBe(false)
  })

  /**
   * A FRACTION IS REFUSED BY THE FIELD, NOT BY THE DESERIALISER. `BudgetTokens` on the route is a
   * `long?`, so a fractional JSON number never reaches the handler that would say something
   * useful about it - the person gets a framework message about the shape of the request instead
   * of the one sentence this field exists to give them. The arm has to be HERE rather than on
   * `step=`, because `step` is browser-dependent and a paste defeats it outright.
   */
  it('refuses a fraction, which the route cannot deserialise into a long', () => {
    expect(budgetFieldIsLegal(1.5)).toBe(false)
    expect(budgetFieldIsLegal('2.5')).toBe(false)
    expect(budgetFieldIsLegal(0.5)).toBe(false)
  })

  /** A WHOLE NUMBER WRITTEN WITH A DECIMAL POINT IS STILL A WHOLE NUMBER. `2.0` parses to `2`,
   *  which a `long` takes; refusing it would be refusing the value over its spelling. */
  it('accepts a whole number however it was spelled', () => {
    expect(budgetFieldIsLegal('2.0')).toBe(true)
    expect(budgetFieldIsLegal(250_000_000.0)).toBe(true)
  })
})

describe('budgetFieldRefusal', () => {
  /** SILENT WHEN THERE IS NOTHING TO SAY - the three legal answers each get no sentence at all.
   *  This is the same guard as the `budgetFieldIsLegal` cases above, read from the other end:
   *  empty (chose nothing), 0 (chose unlimited), and a figure ABOVE the instance's own. */
  it('says nothing about an empty box, an explicit zero, or a figure above the instance’s own', () => {
    expect(budgetFieldRefusal('')).toBeNull()
    expect(budgetFieldRefusal(0)).toBeNull()
    expect(budgetFieldRefusal(900_000_000)).toBeNull()
  })

  /**
   * TWO SENTENCES, BOTH IN THIS FILE, BECAUSE ONE CANNOT HONESTLY COVER BOTH. "cannot be negative"
   * says nothing to somebody who typed 1.5, and a dialog writing its own wording for the second
   * case is how the two dialogs come to disagree about what the field refuses.
   */
  it('names the fraction when a fraction is what was typed', () => {
    expect(budgetFieldRefusal(1.5)).toBe(BudgetFieldFractionRefusal)
    expect(budgetFieldRefusal('2.5')).toBe(BudgetFieldFractionRefusal)
  })

  it('names the negative when a negative is what was typed', () => {
    expect(budgetFieldRefusal(-1)).toBe(BudgetFieldRefusal)
    expect(budgetFieldRefusal('-250')).toBe(BudgetFieldRefusal)
  })

  /** A negative fraction is both, and is told about the sign first: it is the larger mistake and
   *  the one a person can act on without re-reading the box. */
  it('tells a negative fraction about its sign', () => {
    expect(budgetFieldRefusal(-1.5)).toBe(BudgetFieldRefusal)
  })
})

describe('spendAgainstBudget', () => {
  /**
   * **THE DISPLAY MUST NOT CONTRADICT THE DECISION.** `spend` is what the server fills from
   * `GetWorkflowSpendAsync` — the WHOLE workflow, which never comes down — while the guard decides
   * on `GetSpendSinceNudgeAsync`, which resets at every nudge. A bar drawn from `spend` would keep
   * saying a workflow nudged back into life was over, about a workflow the pump had already
   * released.
   */
  it('measures the window the guard decides on, not the cumulative total', () => {
    expect(spendAgainstBudget(2_000_000, 98_000_000)).toBe(2_000_000)
  })

  /** A window of ZERO is a measurement — the instant after a nudge — and not an absence. `||`
   *  would fall straight back to the cumulative figure here, contradicting the guard. */
  it('keeps a window of zero rather than falling back to the total', () => {
    expect(spendAgainstBudget(0, 98_000_000)).toBe(0)
  })

  /**
   * A HOST THAT DOES NOT SEND THE FIELD must still get a bar rather than nothing. Its `spend` is
   * the only measurement it has, and a tile with no bar would lose it for every such Host.
   */
  it('falls back to the cumulative total when the Host never sent the window', () => {
    expect(spendAgainstBudget(undefined, 12_000_000)).toBe(12_000_000)
  })

  /**
   * THE TWO APPEAR AND DISAPPEAR TOGETHER — the server makes `spendSinceNudge` null
   * under exactly the condition `spend` is null, no completed or failed runs. So there is nothing
   * to fall back TO, and unmeasured spend stays unmeasured: `budgetLine` names the limit in words
   * and `budgetBar` draws nothing, which is the existing treatment of `(unknown)`.
   */
  it('is null when neither figure was measured', () => {
    expect(spendAgainstBudget(null, null)).toBeNull()
    expect(spendAgainstBudget(undefined, undefined)).toBeNull()
  })
})
