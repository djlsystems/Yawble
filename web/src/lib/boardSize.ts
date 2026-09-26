/**
 * How big a member's card is, as a per-viewer preference.
 *
 * A DISPLAY preference rather than team data: it describes this browser's view of a board, never
 * anything the server or another viewer needs, so it lives in `localStorage` beside
 * `harness.activeTeam` and follows that key's wrapper discipline exactly - storage throws in a
 * private window with cookies blocked, and a card size is not worth a crash.
 *
 * The rules live here rather than in the store or the component because mounting is possible now
 * and still the worse home for this rule: a spec that greps a component's own source is what let a
 * Critical through 116 green tests once already. Pure functions are what a test can actually
 * exercise.
 */

export interface BoardSize {
  width: number;
  height: number;

  /**
   * How many activity lines ONE card keeps.
   *
   * A viewer preference for the same reason width is: it describes this browser's view, costs
   * nothing but memory, and two people watching one team may reasonably want different amounts of
   * it. What it is NOT is a promise of history - the card is a rolling window and the message log
   * is the audit. Raising this does not bring back lines already dropped; only new ones accumulate.
   */
  feedDepth: number;

  /**
   * How many of the TEAM'S most recent events the board fetches, before cards filter them to their
   * own. A different thing from `feedDepth` beside it, and the distinction matters: that one trims
   * what a card keeps, this one decides what arrives at all.
   *
   * Raising `feedDepth` cannot rescue a member the fetch never returned. The server caps this
   * BEFORE filtering to the teams a caller may see, so a member that has been quiet while its
   * colleagues were busy falls out of the window entirely and its card renders empty - which reads
   * as "never did anything" rather than "further back than we looked".
   */
  feedWindow: number;
}

/** Inclusive, and clamped to on both read and write. */
export const WidthBounds = { min: 260, max: 900 } as const;
export const HeightBounds = { min: 220, max: 1400 } as const;

/**
 * The ceiling is memory, not the wire. `GET /api/messages` answers the newest 200 rows across the
 * WHOLE team per poll, so a card cannot fill a deep feed in one fetch however high this goes - it
 * accumulates as messages arrive. 500 is generous enough that nobody meets it and low enough that
 * a chatty team cannot grow the store without bound.
 */
export const FeedDepthBounds = { min: 5, max: 500 } as const;

/** The server clamps to the same ceiling. Declaring it here too is a duplicate that fails in the
 *  safe direction: the dialog can only ever be optimistic, and the server decides. */
export const FeedWindowBounds = { min: 50, max: 2000 } as const;

/**
 * Narrow enough that two sit side by side on a laptop and three on a wide monitor, tall enough to
 * show roughly a dozen feed rows before scrolling. Chosen by looking at a real board rather than
 * derived - the same way the Braille banner's 64-column floor was.
 */
export const DefaultBoardSize: BoardSize = { width: 380, height: 520, feedDepth: 100, feedWindow: 200 };

const StorageKey = 'harness.boardSize';

function clamp(value: unknown, bounds: { min: number; max: number }, fallback: number): number {
  // NOT `typeof value === 'number'` alone. A number input yields NaN for an empty box, and NaN
  // survives every comparison silently - `Math.min(NaN, x)` is NaN - so an unguarded clamp writes
  // NaN into storage and the card takes `width: NaNpx`, which the browser DROPS. The card then has
  // no width at all, which looks like a layout bug rather than a bad value.
  if (typeof value !== 'number' || Number.isNaN(value)) return fallback;

  return Math.round(Math.min(bounds.max, Math.max(bounds.min, value)));
}

export function clampBoardSize(partial: Partial<BoardSize>): BoardSize {
  return {
    width: clamp(partial.width, WidthBounds, DefaultBoardSize.width),
    height: clamp(partial.height, HeightBounds, DefaultBoardSize.height),
    feedDepth: clamp(partial.feedDepth, FeedDepthBounds, DefaultBoardSize.feedDepth),
    feedWindow: clamp(partial.feedWindow, FeedWindowBounds, DefaultBoardSize.feedWindow),
  };
}

export function readBoardSize(): BoardSize {
  try {
    const stored = localStorage.getItem(StorageKey);

    if (!stored) return { ...DefaultBoardSize };

    const parsed: unknown = JSON.parse(stored);

    // `typeof null === 'object'`, so the null check is not redundant - and an array passes
    // `typeof === 'object'` too, which is why the shape is probed by reading the fields rather
    // than by trusting the type.
    if (typeof parsed !== 'object' || parsed === null) return { ...DefaultBoardSize };

    // CLAMPED ON READ, not only on write. Storage is editable by hand and shared with older
    // builds, so a value that was legal when it was written may not be now. Trusting it because it
    // came from us is how a 4000px card reaches the screen.
    return clampBoardSize(parsed as Partial<BoardSize>);
  } catch {
    // Unreadable storage, unparseable JSON, or access refused outright. A board that will not
    // render is a far worse outcome than a preference that quietly resets.
    return { ...DefaultBoardSize };
  }
}

export function writeBoardSize(size: Partial<BoardSize>): void {
  try {
    localStorage.setItem(StorageKey, JSON.stringify(clampBoardSize(size)));
  } catch {
    // Nothing to do and nothing worth saying: the size still applies for this session.
  }
}
