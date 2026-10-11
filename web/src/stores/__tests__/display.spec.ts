import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { useDisplayStore } from '../display';
import { DefaultBoardSize, WidthBounds } from '../../lib/boardSize';

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

beforeEach(() => {
  vi.unstubAllGlobals();
  setActivePinia(createPinia());
});

describe('the display store', () => {
  it('hydrates from storage rather than from the defaults', () => {
    useStorage({ 'harness.boardSize': JSON.stringify({ width: 500, height: 700 }) });
    setActivePinia(createPinia());

    const display = useDisplayStore();

    expect(display.width).toBe(500);
    expect(display.height).toBe(700);
  });

  it('renders CSS variables the board container carries', () => {
    useStorage();
    setActivePinia(createPinia());

    // One place, read through the cascade. Every card sizes off these rather than binding its own
    // width, which is what makes a live preview free - change the variable, the board re-lays out.
    expect(useDisplayStore().cssVars).toEqual({
      '--os-card-w': `${DefaultBoardSize.width / 16}rem`,
      '--os-card-h': `${DefaultBoardSize.height / 16}rem`,
    });
  });

  it('clamps and persists what it is given', () => {
    const store = useStorage();
    setActivePinia(createPinia());

    const display = useDisplayStore();
    display.set({ width: 99999 });

    expect(display.width).toBe(WidthBounds.max);
    expect(JSON.parse(store['harness.boardSize']!).width).toBe(WidthBounds.max);
  });

  it('leaves the other dimension alone when only one is set', () => {
    useStorage();
    setActivePinia(createPinia());

    const display = useDisplayStore();
    display.set({ height: 640 });

    // The dialog edits one field at a time. Sending the untouched one back is how an unedited box
    // quietly overwrites something - the same trap the member PATCH's three prompt states exist
    // to avoid.
    expect(display.width).toBe(DefaultBoardSize.width);
    expect(display.height).toBe(640);
  });

  it('resets to the defaults, and persists that too', () => {
    const store = useStorage({
      'harness.boardSize': JSON.stringify({ width: 700, height: 900 }),
    });
    setActivePinia(createPinia());

    const display = useDisplayStore();
    display.reset();

    expect(display.width).toBe(DefaultBoardSize.width);
    expect(JSON.parse(store['harness.boardSize']!)).toEqual(DefaultBoardSize);
  });
});
