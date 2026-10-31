/**
 * ONE OUTCOME, ONE COLOUR, EVERYWHERE. The single place an outcome's chart colour is decided: the
 * teams list's Cost column, the team page's Cost tile, the Cost dialog (chart, legend, table) and any
 * later cost view all ask here, so the same outcome is never two hues on one screen.
 *
 * THE FIXED ORDER IS INSTANCE-WIDE: No outcome first, then outcomes by when they were created (oldest
 * first, ties by id). The Host sends each outcome's 1-based place in that order as `rank`, the same on
 * every team and in both cost routes. An outcome's colour is its rank's slot in the categorical
 * palette (`--os-series-1..8`, stepped for dark mode by the theme), never its position among whatever
 * one team happens to list.
 *
 * - No outcome (id null) is neutral gray.
 * - A rank past the palette's last slot, or a missing rank, shares the recessive Other colour: a hue is
 *   never generated or cycled.
 */

/** The categorical palette's slots (`--os-series-1..8`). A rank past the last folds into Other. */
export const OutcomePaletteSlots = 8

/** "No outcome": neutral gray, stacked first (at the bottom). */
export const NoOutcomeColour = 'var(--os-ink-faint)'

/** Outcomes past the palette's last slot share one recessive colour. */
export const OtherOutcomeColour = 'var(--os-rule-strong)'

/** What the colour is decided from: an outcome entry of either cost route. */
export interface RankedOutcome {
  /** The outcome's id; null for No outcome. */
  id: string | null
  /** Its 1-based place in the instance-wide fixed order; null for No outcome. */
  rank: number | null
}

/** The palette colour for a rank in the fixed order: slots 1..8, then Other. */
export function colourForRank(rank: number | null | undefined): string {
  return rank != null && Number.isInteger(rank) && rank >= 1 && rank <= OutcomePaletteSlots
    ? `var(--os-series-${rank})`
    : OtherOutcomeColour
}

/** One outcome's colour: neutral gray for No outcome, otherwise its rank's palette slot. */
export function outcomeColour(outcome: RankedOutcome): string {
  return outcome.id === null ? NoOutcomeColour : colourForRank(outcome.rank)
}

/**
 * Outcome id -> colour for every outcome given, keyed `null` for No outcome. Pass outcomes from one team
 * or from every team (the all-teams route): ranks are global, so the map agrees wherever it is built.
 */
export function outcomeColours(outcomes: Iterable<RankedOutcome>): Map<string | null, string> {
  const colours = new Map<string | null, string>()
  for (const outcome of outcomes) {
    if (!colours.has(outcome.id)) colours.set(outcome.id, outcomeColour(outcome))
  }
  return colours
}
