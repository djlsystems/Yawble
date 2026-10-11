import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { useTerminalDisplayStore } from '../terminalDisplay';
import { DefaultTerminalDisplay, FontSizeBounds } from '../../lib/terminalDisplay';

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

describe('the terminal display store', () => {
  it('hydrates from storage rather than from the defaults', () => {
    useStorage({ 'harness.terminalDisplay': JSON.stringify({ fontSize: 16, width: 800 }) });
    setActivePinia(createPinia());

    const display = useTerminalDisplayStore();

    expect(display.fontSize).toBe(16);
    expect(display.width).toBe(800);
  });

  it('starts with defaults when nothing has been stored', () => {
    useStorage();
    setActivePinia(createPinia());

    const display = useTerminalDisplayStore();

    // Check individual fields rather than object equality, since the store may have extra properties
    expect(display.fontSize).toBe(DefaultTerminalDisplay.fontSize);
    expect(display.fontFamily).toBe(DefaultTerminalDisplay.fontFamily);
    expect(display.maximised).toBe(DefaultTerminalDisplay.maximised);
    expect(display.width).toBe(DefaultTerminalDisplay.width);
    expect(display.height).toBe(DefaultTerminalDisplay.height);
  });

  it('clamps and persists what it is given', () => {
    const storage = useStorage();
    setActivePinia(createPinia());

    const display = useTerminalDisplayStore();
    display.set({ fontSize: 99 });

    expect(display.fontSize).toBe(FontSizeBounds.max);
    expect(JSON.parse(storage['harness.terminalDisplay']!).fontSize).toBe(FontSizeBounds.max);
  });

  it('leaves the other dimensions alone when only one is set', () => {
    useStorage();
    setActivePinia(createPinia());

    const display = useTerminalDisplayStore();
    display.set({ height: 640 });

    // The dialog edits one field at a time. Sending the untouched one back is how an unedited box
    // quietly overwrites something.
    expect(display.width).toBe(DefaultTerminalDisplay.width);
    expect(display.height).toBe(640);
  });

  it('resets to the defaults, and persists that too', () => {
    const storage = useStorage({
      'harness.terminalDisplay': JSON.stringify({ fontSize: 20, width: 1200, height: 800 }),
    });
    setActivePinia(createPinia());

    const display = useTerminalDisplayStore();
    display.reset();

    expect(display.fontSize).toBe(DefaultTerminalDisplay.fontSize);
    // Check the stored value
    const stored = JSON.parse(storage['harness.terminalDisplay']!);
    expect(stored.fontSize).toBe(DefaultTerminalDisplay.fontSize);
    expect(stored.width).toBe(DefaultTerminalDisplay.width);
  });

  it('accepts font family changes', () => {
    const storage = useStorage();
    setActivePinia(createPinia());

    const display = useTerminalDisplayStore();
    display.set({ fontFamily: '"IBM Plex Mono", Consolas, monospace' });

    expect(display.fontFamily).toBe('"IBM Plex Mono", Consolas, monospace');
    expect(JSON.parse(storage['harness.terminalDisplay']!).fontFamily).toBe(
      '"IBM Plex Mono", Consolas, monospace'
    );
  });

  it('accepts maximised state', () => {
    const storage = useStorage();
    setActivePinia(createPinia());

    const display = useTerminalDisplayStore();
    display.set({ maximised: true });

    expect(display.maximised).toBe(true);
    expect(JSON.parse(storage['harness.terminalDisplay']!).maximised).toBe(true);
  });
});

describe('initializeGeometryIfNeeded', () => {
  it('computes initial geometry from viewport when storage is empty', () => {
    const storage = useStorage();
    setActivePinia(createPinia());

    const display = useTerminalDisplayStore();
    display.initializeGeometryIfNeeded(1920, 1080);

    // Should compute ~960x560 in bottom-right, not default 0,0
    expect(display.width).toBeLessThanOrEqual(960);
    expect(display.height).toBeLessThanOrEqual(560);
    expect(display.left).toBeGreaterThan(0); // Not 0, offset from right
    expect(display.top).toBeGreaterThan(0); // Not 0, offset from bottom

    // Should be persisted
    const stored = JSON.parse(storage['harness.terminalDisplay']!);
    expect(stored.left).toBe(display.left);
    expect(stored.top).toBe(display.top);
  });

  it('keeps user-placed window at 0,0 (top-left corner)', () => {
    const storage = useStorage({
      'harness.terminalDisplay': JSON.stringify({ left: 0, top: 0, width: 960, height: 560 }),
    });
    setActivePinia(createPinia());

    const display = useTerminalDisplayStore();
    display.initializeGeometryIfNeeded(1920, 1080);

    // User-placed at 0,0 should stay there
    expect(display.left).toBe(0);
    expect(display.top).toBe(0);
    expect(display.width).toBe(960);
    expect(display.height).toBe(560);
  });

  it('treats storage with missing left/top as unplaced', () => {
    const storage = useStorage({
      'harness.terminalDisplay': JSON.stringify({ fontSize: 16, width: 960, height: 560 }),
    });
    setActivePinia(createPinia());

    const display = useTerminalDisplayStore();
    display.initializeGeometryIfNeeded(1920, 1080);

    // Missing left/top keys means unplaced - should compute new position
    expect(display.left).toBeGreaterThan(0);
    expect(display.top).toBeGreaterThan(0);
  });

  it('clamps overflowing stored rect without jumping to default', () => {
    // Store a rect that would overflow a smaller viewport
    const storage = useStorage({
      'harness.terminalDisplay': JSON.stringify({ left: 300, top: 200, width: 960, height: 560 }),
    });
    setActivePinia(createPinia());

    const display = useTerminalDisplayStore();
    // Smaller viewport: 768 wide
    display.initializeGeometryIfNeeded(768, 1080);

    // Should clamp to fit, not jump to default bottom-right
    // left(300) + width should fit within 768
    expect(display.left + display.width).toBeLessThanOrEqual(768);
    expect(display.top + display.height).toBeLessThanOrEqual(1080);

    // Should preserve approximate left/top position (clamped), not reset
    expect(display.left).toBeLessThanOrEqual(300);
  });

  it('treats storage that is completely unset as needing initialization', () => {
    const storage = useStorage();
    setActivePinia(createPinia());

    const display = useTerminalDisplayStore();
    // First call to store - everything is defaults
    expect(display.left).toBe(0);
    expect(display.top).toBe(0);

    display.initializeGeometryIfNeeded(1920, 1080);

    // Should compute new position, not stay at 0,0
    expect(display.left).toBeGreaterThan(0);
    expect(display.top).toBeGreaterThan(0);
  });
});
