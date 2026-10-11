import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  clampTerminalDisplay,
  DefaultTerminalDisplay,
  FontSizeBounds,
  readTerminalDisplay,
  WindowWidthBounds,
  WindowHeightBounds,
  writeTerminalDisplay,
} from '../terminalDisplay';

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

describe('clampTerminalDisplay', () => {
  it('fills in whatever was not given', () => {
    expect(clampTerminalDisplay({})).toEqual(DefaultTerminalDisplay);
    expect(clampTerminalDisplay({ fontSize: 16 })).toEqual({
      ...DefaultTerminalDisplay,
      fontSize: 16,
    });
  });

  it('clamps fontSize at both ends', () => {
    expect(clampTerminalDisplay({ fontSize: 5 }).fontSize).toBe(FontSizeBounds.min);
    expect(clampTerminalDisplay({ fontSize: 99 }).fontSize).toBe(FontSizeBounds.max);
  });

  it('clamps window dimensions at both ends', () => {
    expect(clampTerminalDisplay({ width: 10 }).width).toBe(WindowWidthBounds.min);
    expect(clampTerminalDisplay({ width: 99999 }).width).toBe(WindowWidthBounds.max);
    expect(clampTerminalDisplay({ height: 1 }).height).toBe(WindowHeightBounds.min);
    expect(clampTerminalDisplay({ height: 99999 }).height).toBe(WindowHeightBounds.max);
  });

  it('treats a value that is not a finite number as absent', () => {
    expect(clampTerminalDisplay({ fontSize: Number.NaN }).fontSize).toBe(DefaultTerminalDisplay.fontSize);
    expect(clampTerminalDisplay({ fontSize: Number.POSITIVE_INFINITY }).fontSize).toBe(FontSizeBounds.max);
    expect(clampTerminalDisplay(JSON.parse('{"fontSize":null}')).fontSize).toBe(DefaultTerminalDisplay.fontSize);
    expect(clampTerminalDisplay({ width: Number.NaN }).width).toBe(DefaultTerminalDisplay.width);
  });

  it('rounds dimensions, because fractional pixels are not sizes anybody chose', () => {
    expect(clampTerminalDisplay({ fontSize: 13.6 }).fontSize).toBe(14);
    expect(clampTerminalDisplay({ width: 960.4 }).width).toBe(960);
  });

  it('accepts only supported font families', () => {
    expect(clampTerminalDisplay({ fontFamily: '"Cascadia Mono", Consolas, monospace' }).fontFamily).toBe(
      '"Cascadia Mono", Consolas, monospace'
    );
    expect(clampTerminalDisplay({ fontFamily: '"IBM Plex Mono", Consolas, monospace' }).fontFamily).toBe(
      '"IBM Plex Mono", Consolas, monospace'
    );
    expect(clampTerminalDisplay({ fontFamily: 'bad-font' }).fontFamily).toBe(DefaultTerminalDisplay.fontFamily);
  });

  it('treats maximised as a boolean', () => {
    expect(clampTerminalDisplay({ maximised: true }).maximised).toBe(true);
    expect(clampTerminalDisplay({ maximised: false }).maximised).toBe(false);
    expect(clampTerminalDisplay({ maximised: 'yes' as any }).maximised).toBe(DefaultTerminalDisplay.maximised);
  });

  it('handles left/top position as finite numbers', () => {
    expect(clampTerminalDisplay({ left: 100, top: 50 })).toEqual({
      ...DefaultTerminalDisplay,
      left: 100,
      top: 50,
    });
    expect(clampTerminalDisplay({ left: Number.NaN, top: Number.POSITIVE_INFINITY }).left).toBe(
      DefaultTerminalDisplay.left
    );
  });
});

describe('readTerminalDisplay', () => {
  it('answers the defaults when nothing has been stored', () => {
    useStorage();

    expect(readTerminalDisplay()).toEqual(DefaultTerminalDisplay);
  });

  it('answers the defaults when the stored value is not usable', () => {
    for (const bad of ['', 'not json', '[]', '{"fontSize":"big"}', 'null']) {
      useStorage({ 'harness.terminalDisplay': bad });

      expect(readTerminalDisplay()).toEqual(DefaultTerminalDisplay);
    }
  });

  it('clamps what it reads, not only what it writes', () => {
    useStorage({
      'harness.terminalDisplay': JSON.stringify({
        fontSize: 99,
        width: 1,
        fontFamily: '"Cascadia Mono", Consolas, monospace',
      }),
    });

    const result = readTerminalDisplay();
    expect(result.fontSize).toBe(FontSizeBounds.max);
    expect(result.width).toBe(WindowWidthBounds.min);
  });

  it('survives storage throwing', () => {
    vi.stubGlobal('localStorage', {
      getItem: () => {
        throw new Error('blocked');
      },
      setItem: () => {
        throw new Error('blocked');
      },
    });

    expect(readTerminalDisplay()).toEqual(DefaultTerminalDisplay);
    expect(() => writeTerminalDisplay(DefaultTerminalDisplay)).not.toThrow();
  });
});

describe('writeTerminalDisplay', () => {
  it('round-trips through storage', () => {
    useStorage();

    writeTerminalDisplay({ fontSize: 16, width: 800, height: 600 });

    const result = readTerminalDisplay();
    expect(result.fontSize).toBe(16);
    expect(result.width).toBe(800);
    expect(result.height).toBe(600);
  });

  it('stores the clamped value rather than what it was handed', () => {
    const store = useStorage();

    writeTerminalDisplay({ fontSize: 99, width: 99999 });

    const stored = JSON.parse(store['harness.terminalDisplay']!);
    expect(stored.fontSize).toBe(FontSizeBounds.max);
    expect(stored.width).toBe(WindowWidthBounds.max);
  });
});
