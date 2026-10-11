/**
 * FLIP, reduced to the part that can be tested.
 *
 * First, Last, Invert, Play: measure where every card was, let the board re-render, measure where
 * each one is now, and start each card at its OLD position with a transform that then transitions
 * to nothing. The card appears to travel from the lane it left to the lane it arrived in, while the
 * DOM only ever knew about the new layout.
 *
 * Only the arithmetic lives here. The measuring and the two style writes stay in the component,
 * because mounted specs exist now and this is still the half that can be wrong: a sign error
 * inverts every move, which looks like an animation and is exactly backwards.
 */

/** Where a card was, in viewport coordinates. `DOMRect` satisfies this structurally. */
export interface CardBox {
  left: number
  top: number
}

/** How far to shift a card so it starts where it used to be. */
export interface FlipShift {
  dx: number
  dy: number
}

/**
 * The invert step, for every card present in both frames and actually moved.
 *
 * OLD MINUS NEW. The card is already laid out at its new place, so the transform has to carry it
 * BACK to the old one before the transition returns it - the opposite sign is the classic mistake
 * and the reason this is a function with a test rather than two lines in a `.vue` file.
 *
 * A card that did not move is absent rather than present with a zero shift: the caller uses the
 * map's membership to decide what to touch, and touching an unmoved card forces a needless style
 * write and a reflow on every refetch.
 */
export function flipShifts(
  before: ReadonlyMap<string, CardBox>,
  after: ReadonlyMap<string, CardBox>,
): Map<string, FlipShift> {
  const shifts = new Map<string, FlipShift>()

  for (const [id, was] of before) {
    const now = after.get(id)
    if (!now) continue

    const dx = was.left - now.left
    const dy = was.top - now.top

    if (dx === 0 && dy === 0) continue

    shifts.set(id, { dx, dy })
  }

  return shifts
}

/** The transform for one shift. A string, so the component has no formatting decision to make. */
export function flipTransform(shift: FlipShift): string {
  return `translate(${shift.dx}px, ${shift.dy}px)`
}

/** How long a card takes to travel. Long enough to follow with your eye, short enough to ignore. */
export const FlipDurationMs = 320
