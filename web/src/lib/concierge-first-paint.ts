/** A terminal geometry in characters. */
export interface TerminalSize {
  cols: number;
  rows: number;
}

/**
 * The sizes to send once, after a Concierge session first attaches, so the child repaints at the
 * geometry the browser is actually showing.
 *
 * WHY THIS EXISTS. The child prints its banner the instant it starts — before anything could tell
 * it a size it does not already hold — and a TUI only repaints its whole screen when the size
 * CHANGES. Sending the size it already has is a no-op to it. So where the child's idea of the
 * width and the browser's disagree, the first paint stays wrong: two frames at two widths on top of
 * each other, characters dropped mid-word where a line wrapped and the next absolute cursor move
 * landed somewhere else. Reported twice from a live instance, with a screenshot each time, and
 * cleared completely by the person resizing the panel by hand — which is this nudge, performed
 * manually.
 *
 * **THE CAUSE IS UPSTREAM OF HERE AND IS NOT FIXED BY THIS.** `ConciergePanel` measures the
 * terminal and sends `cols`/`rows` on the connect precisely so the child is spawned at the right
 * size; when that works there is nothing to repair. Something makes the two disagree on some
 * machines and it did not reproduce on the one this was written on — 99×54 measured, 99×54 sent,
 * no resize frame, a clean banner, at two window sizes. So this is deliberate handling of a
 * condition that is real, reported and not yet explained, NOT a diagnosis. Finding the cause is still
 * worth doing: this makes the symptom go away, and a symptom that goes away is one nobody
 * investigates.
 *
 * **THE LAST SIZE IS ALWAYS THE REAL ONE.** A nudge that left the child a column short would trade
 * a scrambled first paint for a permanently wrong width, and nothing downstream would ever correct
 * it. That invariant has its own test, over a range of widths, rather than being read off this
 * sentence.
 *
 * Returns null where there is nothing safe to do: a terminal one column wide cannot be shrunk, and
 * zero is not a smaller terminal but an invalid one — some PTYs read it as "use the default", which
 * is the one size a child must not be left at. An unmeasured terminal is refused for the same
 * reason.
 */
export function firstPaintNudge(size: TerminalSize): [TerminalSize, TerminalSize] | null {
  const { cols, rows } = size;

  if (!Number.isFinite(cols) || !Number.isFinite(rows)) return null;
  if (cols < 2 || rows < 1) return null;

  return [
    { cols: cols - 1, rows },
    { cols, rows },
  ];
}
