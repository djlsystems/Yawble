import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  clampGeometry,
  clampNumber,
  clampToViewport,
  initialGeometry,
  isPlaced,
  readStored,
  readWindowGeometry,
  writeStored,
  writeWindowGeometry,
  type WindowBounds,
} from '../windowGeometry';
import { framed } from '../useWindowFrame';

/**
 * THE WINDOW RULES THE CONCIERGE AND THE DOCUMENTS EXPLORER SHARE. Storage is a stand-in: this
 * spec runs in node, where there is none, and several cases need one that throws.
 */
function useStorage(initial: Record<string, string> = {}) {
  const store = { ...initial };

  vi.stubGlobal('localStorage', {
    getItem: (k: string) => (k in store ? store[k] : null),
    setItem: (k: string, v: string) => {
      store[k] = v;
    },
  });

  return store;
}

const Bounds: WindowBounds = { width: { min: 480, max: 2400 }, height: { min: 320, max: 1600 } };
const Key = 'harness.test.window';

beforeEach(() => vi.unstubAllGlobals());

describe('clampNumber and clampGeometry', () => {
  it('treats NaN, strings and missing values as absent', () => {
    expect(clampNumber(Number.NaN, Bounds.width, 600)).toBe(600);
    expect(clampNumber('700', Bounds.width, 600)).toBe(600);
    expect(clampNumber(undefined, Bounds.width, 600)).toBe(600);
  });

  it('clamps at both ends and rounds', () => {
    expect(clampNumber(100, Bounds.width, 600)).toBe(480);
    expect(clampNumber(9000, Bounds.width, 600)).toBe(2400);
    expect(clampNumber(700.6, Bounds.width, 600)).toBe(701);
  });

  it('clamps each field on its own, so one bad field costs nothing else', () => {
    expect(clampGeometry({ left: Number.POSITIVE_INFINITY, top: 40, width: 800, height: 'x' as never }, Bounds, { left: 5, top: 6, width: 500, height: 400 }))
      .toEqual({ left: 5, top: 40, width: 800, height: 400 });
  });
});

describe('clampToViewport', () => {
  it('moves an off-screen window back on to the screen', () => {
    expect(clampToViewport({ left: 5000, top: -300, width: 800, height: 500 }, Bounds, 1440, 900))
      .toEqual({ left: 1440 - 800 - 10, top: 0, width: 800, height: 500 });
  });

  it('shrinks a window bigger than the viewport to fit it', () => {
    expect(clampToViewport({ left: 0, top: 0, width: 2000, height: 1400 }, Bounds, 1440, 900))
      .toEqual({ left: 0, top: 0, width: 1420, height: 880 });
  });
});

describe('initialGeometry', () => {
  it('places the preferred size bottom-right when it fits', () => {
    expect(initialGeometry(1920, 1080, Bounds, { width: 960, height: 560 }, 'bottom-right'))
      .toEqual({ width: 960, height: 560, left: 1920 - 960 - 20, top: 1080 - 560 - 20 });
  });

  it('centres when asked, and shrinks to the screen less a margin', () => {
    expect(initialGeometry(800, 600, Bounds, { width: 960, height: 700 }, 'centre'))
      .toEqual({ width: 760, height: 560, left: 20, top: 20 });
  });
});

describe('isPlaced', () => {
  it('is an object with its own left and top, including both at zero', () => {
    expect(isPlaced({ left: 0, top: 0 })).toBe(true);
    expect(isPlaced({ left: 10 })).toBe(false);
    expect(isPlaced(null)).toBe(false);
    expect(isPlaced([1, 2])).toBe(false);
    expect(isPlaced('{"left":1,"top":1}')).toBe(false);
  });
});

describe('reading and writing a remembered window', () => {
  it('reads nothing back from storage that throws, and writing does not throw', () => {
    vi.stubGlobal('localStorage', {
      getItem: () => {
        throw new Error('blocked');
      },
      setItem: () => {
        throw new Error('blocked');
      },
    });

    expect(readStored(Key)).toBeNull();
    expect(readWindowGeometry(Key, Bounds, 1440, 900)).toBeNull();
    expect(() => writeStored(Key, { a: 1 })).not.toThrow();
    expect(() => writeWindowGeometry(Key, Bounds, { left: 1, top: 1, width: 600, height: 400, maximised: false })).not.toThrow();
  });

  it.each([
    ['bad JSON', '{not json'],
    ['null', 'null'],
    ['an array', '[100, 100, 600, 400]'],
    ['no left and top', '{"width":600,"height":400}'],
  ])('is unplaced for %s', (_name, raw) => {
    useStorage({ [Key]: raw });

    expect(readWindowGeometry(Key, Bounds, 1440, 900)).toBeNull();
  });

  it('reads numbers-as-strings and NaN as their fallbacks, never as sizes', () => {
    useStorage({ [Key]: JSON.stringify({ left: 10, top: 20, width: '900', height: null }) });

    expect(readWindowGeometry(Key, Bounds, 1440, 900)).toEqual({ left: 10, top: 20, width: 480, height: 320, maximised: false });
  });

  it('clamps what it reads to the bounds', () => {
    useStorage({ [Key]: JSON.stringify({ left: 10, top: 20, width: 100, height: 99999 }) });

    expect(readWindowGeometry(Key, Bounds, 3000, 3000)).toEqual({ left: 10, top: 20, width: 480, height: 1600, maximised: false });
  });

  it('brings an off-screen window back on to this screen', () => {
    useStorage({ [Key]: JSON.stringify({ left: 3000, top: 2000, width: 800, height: 500 }) });

    expect(readWindowGeometry(Key, Bounds, 1440, 900)).toEqual({ left: 630, top: 390, width: 800, height: 500, maximised: false });
  });

  it('shrinks a window remembered on a bigger screen to fit this one', () => {
    useStorage({ [Key]: JSON.stringify({ left: 0, top: 0, width: 2200, height: 1400 }) });

    expect(readWindowGeometry(Key, Bounds, 1440, 900)).toEqual({ left: 0, top: 0, width: 1420, height: 880, maximised: false });
  });

  it('is unplaced when even its minimum does not fit the viewport', () => {
    useStorage({ [Key]: JSON.stringify({ left: 0, top: 0, width: 600, height: 400 }) });

    expect(readWindowGeometry(Key, Bounds, 390, 844)).toBeNull();
  });

  it('restores a stored maximised, and round-trips what it wrote', () => {
    useStorage();
    writeWindowGeometry(Key, Bounds, { left: 40, top: 50, width: 700, height: 500, maximised: true });

    expect(readWindowGeometry(Key, Bounds, 1440, 900)).toEqual({ left: 40, top: 50, width: 700, height: 500, maximised: true });
  });
});

describe('framed: one pointer step', () => {
  const at = { left: 100, top: 100, width: 600, height: 400 };

  it('moves and resizes from each edge', () => {
    expect(framed(at, 'move', 10, -5)).toEqual({ left: 110, top: 95, width: 600, height: 400 });
    expect(framed(at, 'resize-e', 20, 0)).toEqual({ ...at, width: 620 });
    expect(framed(at, 'resize-s', 0, 30)).toEqual({ ...at, height: 430 });
    expect(framed(at, 'resize-se', 5, 6)).toEqual({ ...at, width: 605, height: 406 });
    expect(framed(at, 'resize-w', -10, 0)).toEqual({ ...at, left: 90, width: 610 });
    expect(framed(at, 'resize-n', 0, -10)).toEqual({ ...at, top: 90, height: 410 });
  });

  it('stops a left or top resize at the minimum instead of pushing the window', () => {
    expect(framed(at, 'resize-w', 500, 0, { width: 480, height: 320 })).toEqual({ ...at, left: 220, width: 480 });
    expect(framed(at, 'resize-n', 0, 500, { width: 480, height: 320 })).toEqual({ ...at, top: 180, height: 320 });
  });
});
