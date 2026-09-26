import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  clampBoardSize,
  DefaultBoardSize,
  FeedDepthBounds,
  HeightBounds,
  readBoardSize,
  WidthBounds,
  writeBoardSize,
} from '../boardSize';

/** A stand-in for `localStorage`, which does not exist in this project's test environment. */
function useStorage(initial: Record<string, string> = {}) {
  const store = { ...initial };

  vi.stubGlobal('localStorage', {
    getItem: (k: string) => (k in store ? store[k] : null),
    setItem: (k: string, v: string) => {
      store[k] = v;
    },
    removeItem: (k: string) => {
      delete store[k];
    },
  });

  return store;
}

beforeEach(() => vi.unstubAllGlobals());

describe('clampBoardSize', () => {
  it('fills in whatever was not given', () => {
    expect(clampBoardSize({})).toEqual(DefaultBoardSize);
    expect(clampBoardSize({ width: 500 })).toEqual({ ...DefaultBoardSize, width: 500 });
  });

  it('clamps rather than refusing, at both ends', () => {
    // Clamping, not rejecting: this is a slider-shaped preference, and a dialog that refuses a
    // number rather than moving it to the nearest legal one makes a person guess the bounds.
    expect(clampBoardSize({ width: 10 }).width).toBe(WidthBounds.min);
    expect(clampBoardSize({ width: 99999 }).width).toBe(WidthBounds.max);
    expect(clampBoardSize({ height: 1 }).height).toBe(HeightBounds.min);
    expect(clampBoardSize({ height: 99999 }).height).toBe(HeightBounds.max);
  });

  it('treats a value that is not a finite number as absent', () => {
    // A number input yields NaN for an empty box, and NaN survives every comparison silently -
    // Math.min(NaN, x) is NaN, so an unguarded clamp would write NaN into storage and the card
    // would take `width: NaNpx`, which the browser drops. The card then has no width at all.
    expect(clampBoardSize({ width: Number.NaN }).width).toBe(DefaultBoardSize.width);
    expect(clampBoardSize({ height: Number.POSITIVE_INFINITY }).height).toBe(HeightBounds.max);
    // Through JSON.parse, which is how a missing or null field REALLY arrives - from
    // hand-edited storage, where the type system was never consulted. Writing it as a
    // literal is untypeable under `exactOptionalPropertyTypes` and a cast would be
    // asserting something less true than the real path.
    expect(clampBoardSize(JSON.parse('{"width":null}')).width).toBe(DefaultBoardSize.width);
  });

  it('rounds, because a fractional pixel is not a size anybody chose', () => {
    expect(clampBoardSize({ width: 380.6 }).width).toBe(381);
  });
});

describe('readBoardSize', () => {
  it('answers the defaults when nothing has been stored', () => {
    useStorage();

    expect(readBoardSize()).toEqual(DefaultBoardSize);
  });

  it('answers the defaults when the stored value is not usable', () => {
    // Corrupt storage is not a crash. Somebody else's key, a half-written value, or a shape from
    // a future build all land here, and a board that will not render is a far worse outcome than
    // a preference that quietly resets.
    for (const bad of ['', 'not json', '[]', '{"width":"wide"}', 'null']) {
      useStorage({ 'harness.boardSize': bad });

      expect(readBoardSize()).toEqual(DefaultBoardSize);
    }
  });

  it('clamps what it reads, not only what it writes', () => {
    // Storage is editable by hand and shared with older builds, so a value that was legal when it
    // was written may not be now. Trusting it because it came from us is how a 4000px card gets
    // rendered.
    useStorage({ 'harness.boardSize': JSON.stringify({ width: 99999, height: 1 }) });

    expect(readBoardSize()).toEqual({
      width: WidthBounds.max,
      height: HeightBounds.min,

      // Absent from the stored object entirely, because it was written by a build that had no such
      // field. Falling back to the default rather than NaN is the same read-side clamp the width
      // above is exercising, meeting the case that actually reaches it: an older browser's storage.
      feedDepth: DefaultBoardSize.feedDepth,
      feedWindow: DefaultBoardSize.feedWindow,
    });
  });

  it('survives storage throwing', () => {
    // A private window with cookies blocked throws on ACCESS, not on read - the same reason the
    // active-team preference is wrapped.
    vi.stubGlobal('localStorage', {
      getItem: () => {
        throw new Error('blocked');
      },
      setItem: () => {
        throw new Error('blocked');
      },
    });

    expect(readBoardSize()).toEqual(DefaultBoardSize);
    expect(() => writeBoardSize(DefaultBoardSize)).not.toThrow();
  });
});

describe('feedDepth', () => {
  it('defaults to 100', () => {
    // A member calling `harness progress` steadily burns twenty lines in a couple of minutes, so a
    // smaller window closes faster than a person can read it.
    expect(DefaultBoardSize.feedDepth).toBe(100);
  });

  it('is clamped on read like every other field', () => {
    useStorage({
      'harness.boardSize': JSON.stringify({ width: 380, height: 520, feedDepth: 99999 }),
    });

    expect(readBoardSize().feedDepth).toBe(FeedDepthBounds.max);
  });

  it('treats a non-number as absent rather than storing NaN', () => {
    // NaN survives every comparison silently, so an unguarded clamp would write it to storage and
    // the card would slice to NaN - which returns an EMPTY array, blanking every feed on the board.
    useStorage({
      'harness.boardSize': JSON.stringify({ width: 380, height: 520, feedDepth: 'lots' }),
    });

    expect(readBoardSize().feedDepth).toBe(DefaultBoardSize.feedDepth);
  });
});

describe('writeBoardSize', () => {
  it('round-trips through storage', () => {
    useStorage();

    writeBoardSize({ width: 420, height: 600, feedDepth: 250, feedWindow: 800 });

    expect(readBoardSize()).toEqual({
      width: 420,
      height: 600,
      feedDepth: 250,
      feedWindow: 800,
    });
  });

  it('stores the clamped value rather than what it was handed', () => {
    const store = useStorage();

    writeBoardSize({ width: 99999, height: 600 });

    expect(JSON.parse(store['harness.boardSize']!).width).toBe(WidthBounds.max);
  });
});

describe('the bounds themselves', () => {
  it('start narrow enough for a phone', () => {
    // The card is `min(width, 100%)`, so the viewport already wins on a narrow screen and this is
    // belt and braces - but a minimum wider than a phone would make the setting look broken to
    // anyone who tried to shrink it there.
    expect(WidthBounds.min).toBeLessThanOrEqual(320);
  });

  it('default to something that fits two across a laptop', () => {
    expect(DefaultBoardSize.width).toBeLessThanOrEqual(420);
  });
});
