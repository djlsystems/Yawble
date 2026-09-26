import { describe, expect, it } from 'vitest';
import { firstPaintNudge } from '../concierge-first-paint';

/**
 * A REAL TEST OF A PURE FUNCTION, and deliberately not the source-reading kind that sits beside it
 * in `concierge-socket.spec.ts`. This project can mount a component now, and the rule is still
 * that the DECISION lives in `lib/` where it can be exercised and the component only renders it.
 */
describe('firstPaintNudge', () => {
  /**
   * The child paints its banner the moment it starts, before anything can tell it a size it does
   * not already have. A TUI that has painted only repaints on a size CHANGE - sending the size it
   * already holds is a no-op to it - so the nudge has to be a real change and then a real change
   * back.
   */
  it('sends a different size first, then the true one', () => {
    expect(firstPaintNudge({ cols: 99, rows: 54 })).toEqual([
      { cols: 98, rows: 54 },
      { cols: 99, rows: 54 },
    ]);
  });

  /**
   * THE INVARIANT THAT MATTERS. Whatever this returns, the LAST size is the terminal's real one -
   * a nudge that left the child one column short would trade a scrambled first paint for a
   * permanently wrong width, which is worse because nothing would ever correct it.
   */
  it('always ends on the size it was given', () => {
    for (const cols of [2, 3, 80, 99, 240]) {
      const nudge = firstPaintNudge({ cols, rows: 40 });

      expect(nudge).not.toBeNull();
      expect(nudge![nudge!.length - 1]).toEqual({ cols, rows: 40 });
    }
  });

  /**
   * Nothing to shrink. One column cannot go to zero - a zero-width terminal is not a smaller
   * terminal, it is an invalid one, and some PTYs treat it as "use the default" which is exactly
   * the wrong size to leave a child at.
   */
  it('refuses a terminal too narrow to shrink', () => {
    expect(firstPaintNudge({ cols: 1, rows: 40 })).toBeNull();
    expect(firstPaintNudge({ cols: 0, rows: 40 })).toBeNull();
  });

  /** A terminal with no rows has not been measured yet; nudging it would send that nonsense on. */
  it('refuses a size that has not been measured', () => {
    expect(firstPaintNudge({ cols: 99, rows: 0 })).toBeNull();
    expect(firstPaintNudge({ cols: Number.NaN, rows: 54 })).toBeNull();
    expect(firstPaintNudge({ cols: 99, rows: Number.NaN })).toBeNull();
  });
});
